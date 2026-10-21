using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.AutoListeners;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Automation.Pipeline;
using LenovoLegionToolkit.Lib.Automation.Pipeline.Triggers;
using LenovoLegionToolkit.Lib.Automation.Utils;
using LenovoLegionToolkit.Lib.Controllers.GodMode;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Utils;
using NeoSmart.AsyncLock;

namespace LenovoLegionToolkit.Lib.Automation;

public class AutomationProcessor(
    AutomationSettings settings,
    DisplayConfigurationListener displayConfigurationListener,
    NativeWindowsMessageListener nativeWindowsMessageListener,
    PowerStateListener powerStateListener,
    PowerModeListener powerModeListener,
    GodModeController godModeController,
    BatteryAutoListener batteryAutoListener,
    GameAutoListener gameAutoListener,
    ProcessAutoListener processAutoListener,
    SessionLockUnlockListener sessionLockUnlockListener,
    TimeAutoListener timeAutoListener,
    UserInactivityAutoListener userInactivityAutoListener,
    WiFiAutoListener wifiAutoListener)
{
    private readonly AsyncLock _ioLock = new();
    private readonly AsyncLock _runLock = new();

    private List<AutomationPipeline> _pipelines = [];
    private CancellationTokenSource? _cts;
    private bool _listenersInitialized;
    private volatile bool _activeThisSession;

    private int _generation;
    private readonly PowerSourceTransitions _powerTransitions = new();
    private readonly object _statusGate = new();
    private AutomationRunStatus _status = new(null, "No automatic rule has run this session.");
    public sealed record AutomationRunStatus(PowerAdapterStatus? Source, string Result);
    public AutomationRunStatus Status { get { lock (_statusGate) return _status; } }
    public event EventHandler? StatusChanged;
    private void UpdateStatus(string result, PowerAdapterStatus? source = null, bool updateSource = false)
    {
        lock (_statusGate) _status = new(updateSource ? source : _status.Source, result);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task ReadPowerSourceAsync()
    {
        var source = await Power.TryGetPowerAdapterStatusAsync().ConfigureAwait(false);
        UpdateStatus(Status.Result, source, updateSource: true);
    }

    public bool IsEnabled => _activeThisSession && settings.Store.IsEnabled;

    public event EventHandler<List<AutomationPipeline>>? PipelinesChanged;

    #region Initialization / pipeline reloading

    public async Task InitializeAsync(bool startListeners = true)
    {
        using (await _ioLock.LockAsync().ConfigureAwait(false))
        {
            _pipelines = [.. settings.Store.Pipelines];
            RaisePipelinesChanged();
            if (!startListeners)
                return;

            HardwareRuntime.RequireControls();
            InitializeListeners();
            _activeThisSession = true;
            await UpdateListenersAsync().ConfigureAwait(false);
            await nativeWindowsMessageListener.EnsureInitializedAsync().ConfigureAwait(false);
        }
    }

    private void InitializeListeners()
    {
        if (_listenersInitialized)
            return;
            displayConfigurationListener.Changed += DisplayConfigurationListener_Changed;
            nativeWindowsMessageListener.Changed += NativeWindowsMessageListener_Changed;
            powerStateListener.Changed += PowerStateListener_Changed;
            powerModeListener.Changed += PowerModeListener_Changed;
            godModeController.PresetChanged += GodModeController_PresetChanged;
            sessionLockUnlockListener.Changed += SessionLockUnlockListener_Changed;
        _listenersInitialized = true;
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        HardwareRuntime.RequireControls();
        using (await _ioLock.LockAsync().ConfigureAwait(false))
        {
            InitializeListeners();
            Interlocked.Increment(ref _generation);
            if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
            var source = await Power.TryGetPowerAdapterStatusAsync().ConfigureAwait(false);
            _powerTransitions.Prime(source, Volatile.Read(ref _generation)); // Enabling establishes a baseline; it never applies a profile.
            _activeThisSession = enabled;
            settings.Store.IsEnabled = enabled;
            UpdateStatus(enabled ? "Enabled for future power transitions; no startup action applied." : "Automatic rules disabled.", source, true);
            settings.SynchronizeStore();

            await UpdateListenersAsync().ConfigureAwait(false);
        }
        if (!enabled) { using (await _runLock.LockAsync().ConfigureAwait(false)) { } }
    }

    public async Task ReloadPipelinesAsync(List<AutomationPipeline> pipelines)
    {
        HardwareRuntime.RequireControls();
        Log.Instance.Trace($"Pipelines reload pending...");

        using (await _ioLock.LockAsync().ConfigureAwait(false))
        {
            Log.Instance.Trace($"Pipelines reloading...");

            Interlocked.Increment(ref _generation);
            if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
            _powerTransitions.Prime(await Power.TryGetPowerAdapterStatusAsync().ConfigureAwait(false), Volatile.Read(ref _generation));
            _pipelines = pipelines.Select(p => p.DeepCopy()).ToList();

            settings.Store.Pipelines = pipelines;
            settings.SynchronizeStore();

            RaisePipelinesChanged();

            await UpdateListenersAsync().ConfigureAwait(false);

            UpdateStatus("Rules saved; waiting for a future transition. No profile applied by saving.");
            Log.Instance.Trace($"Pipelines reloaded.");
        }
        using (await _runLock.LockAsync().ConfigureAwait(false)) { }
    }

    public async Task<List<AutomationPipeline>> GetPipelinesAsync()
    {
        using (await _ioLock.LockAsync().ConfigureAwait(false))
            return _pipelines.Select(p => p.DeepCopy()).ToList();
    }

    public async Task RestartListenersAsync()
    {
        using (await _ioLock.LockAsync().ConfigureAwait(false))
        {
            await UpdateListenersAsync().ConfigureAwait(false);
        }
    }

    #endregion

    #region Run

    public void RunOnStartup()
    {
        if (!IsEnabled)
        {
            Log.Instance.Trace($"Not enabled. Pipeline run on startup ignored.");

            return;
        }

        Log.Instance.Trace($"Pipeline run on startup pending...");

        Task.Run(() => ProcessEvent(new StartupAutomationEvent()));
    }

    public async Task RunNowAsync(AutomationPipeline pipeline)
    {
        HardwareRuntime.RequireControls();
        await Task.Run(() => RunNowCoreAsync(pipeline)).ConfigureAwait(false);
    }

    private async Task RunNowCoreAsync(AutomationPipeline pipeline)
    {
        Log.Instance.Trace($"Pipeline run now pending...");

        using (await _runLock.LockAsync().ConfigureAwait(false))
        {
            Log.Instance.Trace($"Pipeline run starting...");

            try
            {
                List<AutomationPipeline> pipelines;
                using (await _ioLock.LockAsync().ConfigureAwait(false))
                {
                    pipelines = _pipelines.ToList();
                }

                var otherPipelines = pipelines.Where(p => p.Id != pipeline.Id).ToList();
                await pipeline.DeepCopy().RunAsync(otherPipelines).ConfigureAwait(false);

                Log.Instance.Trace($"Pipeline run finished successfully.");
            }
            catch (Exception ex)
            {
                Log.Instance.Trace($"Pipeline run failed.", ex);

                throw;
            }
        }
    }

    public async Task RunNowAsync(Guid pipelineId)
    {
        AutomationPipeline? pipeline;
        using (await _ioLock.LockAsync().ConfigureAwait(false))
            pipeline = _pipelines.Where(p => p.Trigger is null).FirstOrDefault(p => p.Id == pipelineId);

        if (pipeline is null)
            return;

        await RunNowAsync(pipeline).ConfigureAwait(false);
    }

    private async Task RunAsync(IAutomationEvent automationEvent, int generation)
    {
        await Task.Run(() => RunCoreAsync(automationEvent, generation)).ConfigureAwait(false);
    }

    private async Task RunCoreAsync(IAutomationEvent automationEvent, int generation)
    {
        Log.Instance.Trace($"Run pending...");

        using (await _runLock.LockAsync().ConfigureAwait(false))
        {
            Log.Instance.Trace($"Run starting...");

            if (_cts is not null)
                await _cts.CancelAsync().ConfigureAwait(false);

            if (!IsEnabled || generation != Volatile.Read(ref _generation))
                return;
            if (automationEvent is PowerStateAutomationEvent { HasSource: true } power &&
                (power.Source is null || await Power.TryGetPowerAdapterStatusAsync().ConfigureAwait(false) != power.Source))
            {
                UpdateStatus("Skipped a stale or unavailable power transition; no profile applied.");
                return;
            }

            List<AutomationPipeline> pipelines;
            using (await _ioLock.LockAsync().ConfigureAwait(false))
                pipelines = _pipelines.ToList();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            foreach (var pipeline in pipelines)
            {
                if (ct.IsCancellationRequested || !IsEnabled || generation != Volatile.Read(ref _generation))
                {
                    Log.Instance.Trace($"Run interrupted.");
                    break;
                }

                try
                {
                    if (automationEvent is StartupAutomationEvent &&
                        !pipeline.RunOnStartup &&
                        pipeline.Trigger is not OnStartupAutomationPipelineTrigger)
                    {
                        Log.Instance.Trace($"Pipeline configured to skip startup. [name={pipeline.Name}, trigger={pipeline.Trigger}, id={pipeline.Id}]");
                        continue;
                    }

                    if (pipeline.Trigger is null || !await pipeline.Trigger.IsMatchingEvent(automationEvent).ConfigureAwait(false))
                    {
                        Log.Instance.Trace($"Pipeline triggers not satisfied. [name={pipeline.Name}, trigger={pipeline.Trigger}, steps.Count={pipeline.Steps.Count}]");
                        continue;
                    }

                    HardwareRuntime.RequireControls();
                    UpdateStatus($"Running: {pipeline.Name ?? pipeline.Trigger.GetType().Name}");
                    Log.Instance.Trace($"Running pipeline... [name={pipeline.Name}, trigger={pipeline.Trigger}, steps.Count={pipeline.Steps.Count}]");

                    var otherPipelines = pipelines.Where(p => p.Id != pipeline.Id).ToList();
                    await pipeline.RunAsync(otherPipelines, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    UpdateStatus($"Completed: {pipeline.Name ?? pipeline.Trigger.GetType().Name}. Feature changes were read back.");

                    Log.Instance.Trace($"Pipeline completed successfully. [name={pipeline.Name}, trigger={pipeline.Trigger}]");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    UpdateStatus("Cancelled; no further automation steps will run. An in-flight write is drained without retry.");
                    break;
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Failed / not confirmed: {pipeline.Name ?? pipeline.Trigger?.GetType().Name}. {ex.Message}");
                    Log.Instance.Trace($"Pipeline run failed. [name={pipeline.Name}, trigger={pipeline.Trigger}]", ex);
                }

                if (pipeline.IsExclusive)
                {
                    Log.Instance.Trace($"Pipeline is exclusive. Breaking. [name={pipeline.Name}, trigger={pipeline.Trigger}, steps.Count={pipeline.Steps.Count}]");
                    break;
                }
            }

            Log.Instance.Trace($"Run finished successfully.");
        }
    }

    #endregion

    #region Listeners

    private async void DisplayConfigurationListener_Changed(object? sender, DisplayConfigurationListener.ChangedEventArgs args)
    {
        var e = new HDRAutomationEvent(args.HDR);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    private async void NativeWindowsMessageListener_Changed(object? sender, NativeWindowsMessageListener.ChangedEventArgs args)
    {
        var e = new NativeWindowsMessageEvent(args.Message, args.Data);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    private async void PowerStateListener_Changed(object? sender, PowerStateListener.ChangedEventArgs args)
    {
        var generation = Volatile.Read(ref _generation);
        try
        {
            var source = await Power.TryGetPowerAdapterStatusAsync().ConfigureAwait(false);
            UpdateStatus(Status.Result, source, true);
            if (generation != Volatile.Read(ref _generation)) return;
            if (args.PowerStateEvent == PowerStateEvent.StatusChange && args.PowerAdapterStateChanged)
            {
                if (source is null)
                {
                    UpdateStatus("Power source unavailable; no automatic profile applied.");
                    return;
                }
                if (!_powerTransitions.Accept(source, generation)) return;
                // Retire work for the old source immediately; a native write still drains.
                if (IsEnabled && _cts is { } previous) await previous.CancelAsync().ConfigureAwait(false);
            }
            var e = new PowerStateAutomationEvent(args.PowerStateEvent, args.PowerAdapterStateChanged, source, hasSource: true);
            await ProcessEvent(e, generation).ConfigureAwait(false);
        }
        catch (Exception ex) { UpdateStatus($"Power event unavailable: {ex.Message}"); }
    }

    private async void PowerModeListener_Changed(object? sender, PowerModeListener.ChangedEventArgs args)
    {
        var e = new PowerModeAutomationEvent(args.State);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    private async void GodModeController_PresetChanged(object? sender, Guid presetId)
    {
        var e = new CustomModePresetAutomationEvent(presetId);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    private async void GameAutoListener_Changed(object? sender, GameAutoListener.ChangedEventArgs args)
    {
        var e = new GameAutomationEvent(args.Running);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    private async void ProcessAutoListener_Changed(object? sender, ProcessAutoListener.ChangedEventArgs args)
    {
        var e = new ProcessAutomationEvent(args.Type, args.ProcessInfo);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    private async void SessionLockUnlockListener_Changed(object? sender, SessionLockUnlockListener.ChangedEventArgs args)
    {
        var e = new SessionLockUnlockAutomationEvent(args.Locked);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    private async void TimeAutoListener_Changed(object? sender, TimeAutoListener.ChangedEventArgs args)
    {
        var e = new TimeAutomationEvent(args.Time, args.Day);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    private async void UserInactivityAutoListener_Changed(object? sender, UserInactivityAutoListener.ChangedEventArgs args)
    {
        var e = new UserInactivityAutomationEvent(args.TimerResolution * args.TickCount);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    private async void WiFiAutoListener_Changed(object? sender, WiFiAutoListener.ChangedEventArgs args)
    {
        var e = new WiFiAutomationEvent(args.IsConnected, args.Ssid);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    private async void BatteryAutoListener_Changed(object? sender, BatteryAutoListener.ChangedEventArgs args)
    {
        var e = new BatteryPercentageAutomationEvent(args.Percentage);
        await ProcessEvent(e).ConfigureAwait(false);
    }

    #endregion

    #region Event processing

    private async Task ProcessEvent(IAutomationEvent e, int? eventGeneration = null)
    {
        var generation = eventGeneration ?? Volatile.Read(ref _generation);
        if (!IsEnabled || generation != Volatile.Read(ref _generation)) return;
        var triggerMatches = await Task.WhenAll(_pipelines.SelectMany(p => p.AllTriggers)
                .Select(async t => await t.IsMatchingEvent(e).ConfigureAwait(false)))
            .ConfigureAwait(false);

        if (!triggerMatches.Any(t => t))
        {
            return;
        }

        Log.Instance.Trace($"Processing event {e}... [type={e.GetType().Name}]");

        await RunAsync(e, generation).ConfigureAwait(false);
    }

    #endregion

    #region Helper methods

    public async Task StopAsync()
    {
        using (await _ioLock.LockAsync().ConfigureAwait(false))
        {
            _activeThisSession = false;
            Interlocked.Increment(ref _generation);
            if (_cts is not null)
                await _cts.CancelAsync().ConfigureAwait(false);
            await UpdateListenersAsync().ConfigureAwait(false);
            if (_listenersInitialized)
            {
            displayConfigurationListener.Changed -= DisplayConfigurationListener_Changed;
            nativeWindowsMessageListener.Changed -= NativeWindowsMessageListener_Changed;
            powerStateListener.Changed -= PowerStateListener_Changed;
            powerModeListener.Changed -= PowerModeListener_Changed;
            godModeController.PresetChanged -= GodModeController_PresetChanged;
            sessionLockUnlockListener.Changed -= SessionLockUnlockListener_Changed;
            _listenersInitialized = false;
            }
        }
        // Release _ioLock before draining work that may itself need that lock.
        using (await _runLock.LockAsync().ConfigureAwait(false)) { }
    }

    private async Task UpdateListenersAsync()
    {
        Log.Instance.Trace($"Stopping listeners...");
        var wasRunning = gameAutoListener.AreGamesRunning();
        Log.Instance.Trace($"Current Game Listener State: Running={wasRunning}");

        if (wasRunning)
        {
            gameAutoListener.PreserveStateOnRestart();
        }

        await batteryAutoListener.UnsubscribeChangedAsync(BatteryAutoListener_Changed).ConfigureAwait(false);
        await gameAutoListener.UnsubscribeChangedAsync(GameAutoListener_Changed).ConfigureAwait(false);
        await processAutoListener.UnsubscribeChangedAsync(ProcessAutoListener_Changed).ConfigureAwait(false);
        await timeAutoListener.UnsubscribeChangedAsync(TimeAutoListener_Changed).ConfigureAwait(false);
        await userInactivityAutoListener.UnsubscribeChangedAsync(UserInactivityAutoListener_Changed).ConfigureAwait(false);
        await wifiAutoListener.UnsubscribeChangedAsync(WiFiAutoListener_Changed).ConfigureAwait(false);

        Log.Instance.Trace($"Stopped listeners...");

        if (!IsEnabled)
        {
            Log.Instance.Trace($"Not enabled. Will not start listeners.");
            return;
        }

        Log.Instance.Trace($"Starting listeners...");

        var triggers = _pipelines.SelectMany(p => p.AllTriggers).ToArray();

        if (triggers.OfType<IGameAutomationPipelineTrigger>().Any())
        {
            Log.Instance.Trace($"Starting game listener...");

            await gameAutoListener.SubscribeChangedAsync(GameAutoListener_Changed).ConfigureAwait(false);
        }

        if (triggers.OfType<IProcessesAutomationPipelineTrigger>().Any())
        {
            Log.Instance.Trace($"Starting process listener...");

            await processAutoListener.SubscribeChangedAsync(ProcessAutoListener_Changed).ConfigureAwait(false);
        }

        if (triggers.OfType<ITimeAutomationPipelineTrigger>().Any() || triggers.OfType<ITimeRangeAutomationPipelineTrigger>().Any() || triggers.OfType<IPeriodicAutomationPipelineTrigger>().Any())
        {
            Log.Instance.Trace($"Starting time listener...");

            await timeAutoListener.SubscribeChangedAsync(TimeAutoListener_Changed).ConfigureAwait(false);
        }

        if (triggers.OfType<IUserInactivityPipelineTrigger>().Any())
        {
            Log.Instance.Trace($"Starting user inactivity listener...");

            await userInactivityAutoListener.SubscribeChangedAsync(UserInactivityAutoListener_Changed).ConfigureAwait(false);
        }

        if (triggers.OfType<IWiFiConnectedPipelineTrigger>().Any() || triggers.OfType<WiFiDisconnectedAutomationPipelineTrigger>().Any())
        {
            Log.Instance.Trace($"Starting WiFi listener...");

            await wifiAutoListener.SubscribeChangedAsync(WiFiAutoListener_Changed).ConfigureAwait(false);
        }

        if (triggers.OfType<IBatteryPercentageAutomationPipelineTrigger>().Any())
        {
            Log.Instance.Trace($"Starting battery listener...");

            await batteryAutoListener.SubscribeChangedAsync(BatteryAutoListener_Changed).ConfigureAwait(false);
        }

        Log.Instance.Trace($"Started relevant listeners.");
    }

    private void RaisePipelinesChanged()
    {
        PipelinesChanged?.Invoke(this, _pipelines.Select(p => p.DeepCopy()).ToList());
    }

    #endregion

}
