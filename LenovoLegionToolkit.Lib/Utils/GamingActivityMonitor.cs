using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Utils;

// Only presence is inspected: no game handles, modules, hooks, or driver calls.
// Separate from automation listeners so read-only sessions never activate them.
public sealed class GamingActivityMonitor
{
    public static GamingActivityMonitor Instance { get; } = new();
    private readonly Func<bool> _isGamePresent;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _windowVisible;
    private bool _detectionAvailable;
    private bool _gameRunning;
    private volatile bool _optionalWorkPaused = true;
    public bool OptionalWorkPaused => _optionalWorkPaused;
    public bool GameRunning { get { lock (_stateLock) return _gameRunning; } }
    public event EventHandler? Changed;

    public GamingActivityMonitor() : this(IsValorantPresent, TimeSpan.FromSeconds(5)) { }
    // Injectable presence source supports managed-only lifecycle checks.
    public GamingActivityMonitor(Func<bool> isGamePresent, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(isGamePresent);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _isGamePresent = isGamePresent;
        _interval = interval;
    }
    private static bool IsValorantPresent()
    {
        var processes = Process.GetProcessesByName("VALORANT-Win64-Shipping");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public void SetWindowVisible(bool visible)
    {
        bool changed;
        lock (_stateLock)
        {
            _windowVisible = visible;
            changed = UpdatePauseState();
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }
    private bool UpdatePauseState()
    {
        var paused = !_detectionAvailable || _gameRunning || !_windowVisible;
        var changed = paused != _optionalWorkPaused;
        _optionalWorkPaused = paused;
        return changed;
    }
    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_worker is { IsCompleted: false }) return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _worker = Task.Run(() => RunAsync(token));
        }
        finally { _lifecycle.Release(); }
    }
    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool changed;
                try
                {
                    var running = _isGamePresent();
                    lock (_stateLock)
                    {
                        var gameStateChanged = _gameRunning != running;
                        _gameRunning = running;
                        _detectionAvailable = true;
                        // Tray consumers can remain visible while the main window is
                        // hidden. They still need game transitions when the optional
                        // main-window work was already paused.
                        changed = UpdatePauseState() || gameStateChanged;
                    }
                }
                catch (Exception ex)
                {
                    // Uncertain detection keeps optional work paused; it never
                    // disables required monitoring or assumes the game exited.
                    lock (_stateLock)
                    {
                        var wasAvailable = _detectionAvailable;
                        _detectionAvailable = false;
                        changed = UpdatePauseState();
                        if (wasAvailable) Log.Instance.Trace($"Game-presence check unavailable; optional monitoring paused.", ex);
                    }
                }
                if (changed) Changed?.Invoke(this, EventArgs.Empty);
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cts is null) return;
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_worker is not null) await _worker.ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
            _worker = null;
            bool changed;
            lock (_stateLock) { _detectionAvailable = false; changed = UpdatePauseState(); }
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _lifecycle.Release(); }
    }
}
