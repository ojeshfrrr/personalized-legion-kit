using System;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Utils;

/// <summary>Coalesced page reads, with an optional active-owner telemetry cadence.
/// Retired native reads drain before replacement; their results cannot be published.</summary>
public sealed class DemandReadSession<T>(Func<CancellationToken, Task<T>> read,
    Action<T, DateTimeOffset> publish, Action<Exception> error, TimeSpan? refreshInterval = null,
    Func<CancellationToken, Task>? waitForNextTick = null, Action? readingStarted = null)
{
    private readonly object _gate = new();
    private readonly TimeSpan? _refreshInterval = ValidateInterval(refreshInterval);
    private CancellationTokenSource? _scheduleLifetime;
    private Task _scheduleTask = Task.CompletedTask;
    private bool _active;
    private CancellationTokenSource? _inFlight;
    private Task _pending = Task.CompletedTask;
    public bool IsActive { get { lock (_gate) return _active; } }
    public bool IsBusy { get { lock (_gate) return !_pending.IsCompleted; } }
    public Task ActivateAsync()
    {
        lock (_gate)
        {
            if (_active) return _pending;
            _active = true;
            var initial = Start();
            if (_refreshInterval is not null)
            {
                var lifetime = _scheduleLifetime = new CancellationTokenSource();
                _scheduleTask = ScheduleAsync(_scheduleTask, initial, lifetime);
            }
            return initial;
        }
    }
    public Task RefreshAsync()
    {
        lock (_gate)
            return !_active || !_pending.IsCompleted ? _pending : Start();
    }
    // A presentation selection may retire an old result without issuing a new query.
    public void InvalidatePendingRead()
    {
        lock (_gate) _inFlight?.Cancel();
    }
    public Task DeactivateAsync()
    {
        lock (_gate)
        {
            _active = false;
            _inFlight?.Cancel();
            _scheduleLifetime?.Cancel();
            return _refreshInterval is null ? _pending : Task.WhenAll(_pending, _scheduleTask);
        }
    }
    private static TimeSpan? ValidateInterval(TimeSpan? interval)
    {
        if (interval is { } value && value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        return interval;
    }
    private async Task ScheduleAsync(Task previous, Task initial, CancellationTokenSource lifetime)
    {
        await Task.Yield();
        try
        {
            await previous;
            lifetime.Token.ThrowIfCancellationRequested();
            // Exactly one timer per active owner. Missed ticks coalesce instead of queuing reads.
            using var timer = waitForNextTick is null ? new PeriodicTimer(_refreshInterval!.Value) : null;
            await initial;
            while (!lifetime.IsCancellationRequested)
            {
                if (waitForNextTick is not null) await waitForNextTick(lifetime.Token);
                else if (!await timer!.WaitForNextTickAsync(lifetime.Token)) break;
                lifetime.Token.ThrowIfCancellationRequested();
                await RefreshAsync();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_gate)
                if (_active && ReferenceEquals(_scheduleLifetime, lifetime) && !lifetime.IsCancellationRequested) error(ex);
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_scheduleLifetime, lifetime)) _scheduleLifetime = null;
            lifetime.Dispose();
        }
    }
    private Task Start()
    {
        var previous = _pending;
        var lifetime = new CancellationTokenSource();
        _inFlight = lifetime;
        return _pending = RunAsync(previous, lifetime);
    }
    private async Task RunAsync(Task previous, CancellationTokenSource lifetime)
    {
        // Publish the pending task before a synchronous getter can complete.
        await Task.Yield();
        try
        {
            await previous;
            lifetime.Token.ThrowIfCancellationRequested();
            lock (_gate)
                if (_active && ReferenceEquals(_inFlight, lifetime)) readingStarted?.Invoke();
            var value = await Task.Run(() => read(lifetime.Token), lifetime.Token);
            lock (_gate)
                if (_active && ReferenceEquals(_inFlight, lifetime) && !lifetime.IsCancellationRequested)
                    publish(value, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_gate)
                if (_active && ReferenceEquals(_inFlight, lifetime) && !lifetime.IsCancellationRequested) error(ex);
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_inFlight, lifetime)) _inFlight = null;
            lifetime.Dispose();
        }
    }
}
