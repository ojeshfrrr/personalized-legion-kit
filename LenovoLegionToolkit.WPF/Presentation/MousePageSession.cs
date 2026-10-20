using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;

namespace LenovoLegionToolkit.WPF.Presentation;

public sealed record ConfirmedMouseValue<T>(T Value, DateTimeOffset ReadAt);

public sealed class MousePageReading<T>
{
    public ConfirmedMouseValue<T>? Current { get; private set; }
    public ConfirmedMouseValue<T>? LastConfirmed { get; private set; }
    public string Message { get; private set; } = "Not read yet.";
    internal void Confirm(T value, string message)
    {
        Current = LastConfirmed = new(value, DateTimeOffset.Now);
        Message = message;
    }
    internal void Confirm(T value, DateTimeOffset readAt, string message)
    {
        Current = LastConfirmed = new(value, readAt);
        Message = message;
    }
    internal void Unavailable(string message) { Current = null; Message = message; }
}

/// <summary>
/// Mouse-page state, also exercised with a fake service without starting WPF.
/// One active operation, cancellation on navigation, no timer or configuration writes.
/// Calls resume on the caller's UI context; delayed work may not update a retired page.
/// </summary>
public sealed class MousePageSession(IMouseControlService service, Func<bool> canWrite)
{
    private CancellationTokenSource? _lifetime;
    private Task _pending = Task.CompletedTask;
    public MousePageReading<MouseDpi> Dpi { get; } = new();
    public MousePageReading<MousePollingRate> Polling { get; } = new();
    public bool IsActive => _lifetime is not null;
    public bool IsBusy { get; private set; }
    public bool CanEditDpi => IsActive && !IsBusy && canWrite() && Dpi.Current is not null;
    public bool CanEditPolling => IsActive && !IsBusy && canWrite() && Polling.Current is not null;
    public event Action? Changed;

    public void InvalidateForPreset()
    {
        Dpi.Unavailable("Preset in progress; previous DPI is historical.");
        Polling.Unavailable("Preset in progress; previous polling rate is historical.");
        Changed?.Invoke();
    }
    public void AcceptPresetResult(MousePresetApplyResult result)
    {
        if (!IsActive || IsBusy) return;
        var dpi = result.Steps.FirstOrDefault(s => s.Status is (MousePresetStepStatus.Confirmed or MousePresetStepStatus.Unchanged) && s.DpiReadback is not null && s.ReadAt.HasValue);
        var polling = result.Steps.FirstOrDefault(s => s.Status is (MousePresetStepStatus.Confirmed or MousePresetStepStatus.Unchanged) && s.PollingReadback.HasValue && s.ReadAt.HasValue);
        if (dpi is not null) Dpi.Confirm(dpi.DpiReadback!, dpi.ReadAt!.Value, "Preset DPI verified by device readback.");
        else Dpi.Unavailable("Preset DPI not verified. Read the device before another change.");
        if (polling is not null) Polling.Confirm(polling.PollingReadback!.Value, polling.ReadAt!.Value, "Preset polling verified by device readback.");
        else Polling.Unavailable("Preset polling not verified. Read the device before another change.");
        Changed?.Invoke();
    }

    public Task ActivateAsync()
    {
        if (_lifetime is not null) return _pending;
        var lifetime = _lifetime = new CancellationTokenSource();
        return Start(lifetime, () => RefreshCoreAsync(lifetime));
    }

    public void Deactivate()
    {
        var lifetime = _lifetime;
        _lifetime = null;
        lifetime?.Cancel();
        Dpi.Unavailable("Read the device to refresh its current DPI.");
        Polling.Unavailable("Read the device to refresh its polling configuration.");
        Changed?.Invoke();
        if (!IsBusy) lifetime?.Dispose();
    }

    public Task RefreshAsync()
    {
        if (_lifetime is null || IsBusy) return _pending;
        var lifetime = _lifetime;
        return Start(lifetime, () => RefreshCoreAsync(lifetime));
    }

    private async Task RefreshCoreAsync(CancellationTokenSource lifetime)
    {
        Dpi.Unavailable("Reading active DPI…");
        Polling.Unavailable("Reading device polling configuration…");
        Changed?.Invoke();
        var blocked = false;
        try
        {
            var dpi = await service.ReadActiveDpiAsync(lifetime.Token);
            if (!IsCurrent(lifetime)) return;
            Dpi.Confirm(dpi, "Read from the mouse. No setting was changed.");
        }
        catch (Exception ex)
        {
            if (!IsCurrent(lifetime)) return;
            Dpi.Unavailable("Unavailable · " + ex.Message);
            blocked = ex is Win32Exception { NativeErrorCode: 5 or 32 };
        }
        if (!IsCurrent(lifetime)) return;
        Changed?.Invoke();
        if (blocked)
        {
            Polling.Unavailable("Device access denied or in use; no further query attempted.");
            return;
        }
        try
        {
            var rate = await service.ReadPollingRateAsync(lifetime.Token);
            if (IsCurrent(lifetime)) Polling.Confirm(rate, "Profile 1 configuration read from the mouse. No rate was changed.");
        }
        catch (Exception ex) { if (IsCurrent(lifetime)) Polling.Unavailable("Unavailable · " + ex.Message); }
    }

    // Only explicit Apply handlers call these methods. Draft changes never enter the service.
    public Task ApplyDpiAsync(int x, int y)
    {
        _ = DeathAdderV3Protocol.ValidateDpi(x, y);
        if (!CanEditDpi) throw new InvalidOperationException("Read the device and enable controls before applying DPI.");
        var lifetime = _lifetime!;
        return Start(lifetime, async () =>
        {
            Dpi.Unavailable("Applying and verifying active DPI…");
            Changed?.Invoke();
            try
            {
                var result = await service.ApplyActiveDpiAsync(x, y, lifetime.Token);
                if (!IsCurrent(lifetime)) return;
                var message = (result.Success ? "Confirmed · " : "Not confirmed · ") + result.Message;
                if (result.Actual is not null) Dpi.Confirm(result.Actual, message);
                else Dpi.Unavailable(message + " Current DPI is unavailable; last confirmed reading is historical.");
            }
            catch (Exception ex) { if (IsCurrent(lifetime)) Dpi.Unavailable("Failed · " + ex.Message); }
        });
    }

    public Task ApplyPollingAsync(MousePollingRate rate)
    {
        _ = DeathAdderV3PollingProtocol.Encode(rate);
        if (!CanEditPolling) throw new InvalidOperationException("Read the device and enable controls before applying its polling rate.");
        var lifetime = _lifetime!;
        return Start(lifetime, async () =>
        {
            Polling.Unavailable("Applying and verifying polling configuration…");
            Changed?.Invoke();
            try
            {
                var result = await service.ApplyPollingRateAsync(rate, lifetime.Token);
                if (!IsCurrent(lifetime)) return;
                var message = (result.Success ? "Confirmed · " : "Not confirmed · ") + result.Message +
                    (result.Previous.HasValue ? $" Previous device setting: {(int)result.Previous.Value:N0} Hz." : "");
                if (result.Actual.HasValue) Polling.Confirm(result.Actual.Value, message);
                else Polling.Unavailable(message + " Current rate is unavailable; last confirmed reading is historical.");
            }
            catch (Exception ex) { if (IsCurrent(lifetime)) Polling.Unavailable("Failed · " + ex.Message); }
        });
    }

    private bool IsCurrent(CancellationTokenSource lifetime) => ReferenceEquals(_lifetime, lifetime) && !lifetime.IsCancellationRequested;
    private Task Start(CancellationTokenSource lifetime, Func<Task> action)
    {
        var previous = _pending;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = finished.Task;
        _ = RunAsync();
        return finished.Task;
        async Task RunAsync()
        {
            Exception? failure = null;
            try
            {
                await previous;
                if (!IsCurrent(lifetime)) return;
                IsBusy = true;
                Changed?.Invoke();
                await action();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                IsBusy = false;
                if (IsCurrent(lifetime)) Changed?.Invoke();
                else lifetime.Dispose();
                if (failure is not null) finished.TrySetException(failure);
                else finished.TrySetResult();
            }
        }
    }
}
