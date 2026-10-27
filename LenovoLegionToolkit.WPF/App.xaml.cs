using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Automation;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Controllers.GodMode;
using LenovoLegionToolkit.Lib.Controllers.Sensors;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Features.Hybrid;
using LenovoLegionToolkit.Lib.Features.Hybrid.Notify;
using LenovoLegionToolkit.Lib.Features.PanelLogo;
using LenovoLegionToolkit.Lib.Features.WhiteKeyboardBacklight;
using LenovoLegionToolkit.Lib.Integrations;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Macro;
using LenovoLegionToolkit.Lib.Messaging;
using LenovoLegionToolkit.Lib.Messaging.Messages;
using LenovoLegionToolkit.Lib.Overclocking.Amd;
using LenovoLegionToolkit.Lib.Services;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.SoftwareDisabler;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.CLI;
using LenovoLegionToolkit.WPF.Controls.Custom;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Station.Core;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows;
using LenovoLegionToolkit.WPF.Windows.Osd;
using LenovoLegionToolkit.WPF.Windows.Utils;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using WinFormsApp = System.Windows.Forms.Application;
using WinFormsHighDpiMode = System.Windows.Forms.HighDpiMode;

namespace LenovoLegionToolkit.WPF;

public partial class App
{
    #region P/Invoke

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    #endregion

    #region Constants & Fields

    private const string MUTEX_NAME = "ProjectLegion_Mutex_70ade129-b5d8-4d78-8c5f-63dbbfbb1942";
    private const string EVENT_NAME = "ProjectLegion_Event_70ade129-b5d8-4d78-8c5f-63dbbfbb1942";

    public Window? OsdWindow;

    private static readonly ConcurrentDictionary<string, string> TitleCache = new();

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _singleInstanceWaitHandle;
    private bool _showPawnIONotify;
    private Task? _shutdownTask;

    public new static App Current => (App)Application.Current;
    public static MainWindow? MainWindowInstance;

    public static bool IsRestoringSettings { get; set; }

    #endregion

    #region Startup & Exit Logic

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        var startupStopwatch = Stopwatch.StartNew();

        try
        {
            if (!await InitializeCoreEnvironmentAsync(e))
            {
                return;
            }

            ReadOnlyQaSession.Start(startupStopwatch);

            await InitializeSettingsAndCompatibilityAsync();
            if (!AppFlags.Instance.UiPreview)
                await GamingActivityMonitor.Instance.StartAsync();
            await InitializeUIAsync();
            ReadOnlyQaSession.Mark("UiInitialized");

            if (AppFlags.Instance.UiPreview)
            {
                MainWindowInstance!.ShowUiPreview();
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                return;
            }

            if (!AppFlags.Instance.Minimized)
            {
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                Log.Instance.Trace($"Startup shell rendered. [elapsed={startupStopwatch.ElapsedMilliseconds}ms]");
            }

            if (!await CheckCompatibilityAsyncWrapper(AppFlags.Instance))
            {
                return;
            }
            await Task.Run(InitializeHardwareAndFeaturesAsync);
            Log.Instance.Trace($"Hardware feature initialization stage finished. [elapsed={startupStopwatch.ElapsedMilliseconds}ms]");
            // Extension entry points may issue hardware commands. Load only on explicit opt-in.
            await MainWindowInstance!.InitializeAfterFeaturesAsync();
            Log.Instance.Trace($"Main window controls enabled. [elapsed={startupStopwatch.ElapsedMilliseconds}ms]");

            if (!AppFlags.Instance.Minimized && _showPawnIONotify && HardwareRuntime.ControlsEnabled)
            {
                PawnIOHelper.ShowPawnIONotify();
            }

            var deferredInitTask = StartBackgroundServicesAsync();
            await deferredInitTask;

            await Dispatcher.InvokeAsync(() =>
            {
                if (Log.Instance.IsTraceEnabled)
                {
                    Log.Instance.Trace($"{ProjectBranding.ProductName} Version {Assembly.GetEntryAssembly()?.GetName().Version}");
                }

                InitOsd();
                InitAppMessages();
                LogIdentityStatus();

                if (AppFlags.Instance.Debug)
                {
                    Console.WriteLine(@"[Startup] Startup Complete.");
                }

                Log.Instance.Trace($"Startup completed. [elapsed={startupStopwatch.ElapsedMilliseconds}ms]");
                ReadOnlyQaSession.Mark("StartupCompleted");
                PortableUpdateInstaller.ConfirmStartup(e.Args);
            });
            if (AppFlags.Instance.QaExitAfterSeconds > 0)
                _ = EndReadOnlyQaSessionAsync();

            _ = Task.Run(LogMachineInformationAsync);
        }
        catch (Exception ex)
        {
            if (AppFlags.Instance?.Debug == true)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(@$"CRITICAL EXCEPTION: {ex}");
                Console.ResetColor();
            }

            HandleCriticalStartupError(ex);
        }
    }

    private async Task<bool> InitializeCoreEnvironmentAsync(StartupEventArgs e)
    {
#if DEBUG
        if (Debugger.IsAttached)
        {
            Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)
                .Where(p => p.Id != Environment.ProcessId)
                .ToList()
                .ForEach(p =>
                {
                    p.Kill();
                    p.WaitForExit();
                });
        }
#endif

#if PROJECT_LEGION_PREVIEW
        AppFlags.Initialize(e.Args.Concat(["--ui-preview"]));
#else
        AppFlags.Initialize(e.Args);
#endif
        Log.Instance.IsTraceEnabled = AppFlags.Instance.IsTraceEnabled;

        if (AppFlags.Instance.Debug)
        {
            InitializeDebugConsole();
            Console.WriteLine(@"[Startup] Ensuring Single Instance...");
        }

        if (!EnsureSingleInstance())
        {
            return false;
        }

        SetupExceptionHandling();

        if (AppFlags.Instance.Debug)
        {
            Console.WriteLine(@$"[Startup] Parsing Flags complete. TraceEnabled: {AppFlags.Instance.IsTraceEnabled}");
            Console.WriteLine(@"[Startup] Initializing IoC Container...");
        }

        IoCContainer.Initialize(
            new Lib.IoCModule(),
            new Lib.Automation.IoCModule(),
            new Lib.Macro.IoCModule(),
            new IoCModule()
        );

        return true;
    }

    private async Task InitializeSettingsAndCompatibilityAsync()
    {
        if (AppFlags.Instance.Debug)
        {
            Console.WriteLine(@"[Startup] Setting Language and Preparing Settings...");
        }

        await LocalizationHelper.SetLanguageAsync(true);

        if (AppFlags.Instance.Debug)
        {
            Console.WriteLine(@"[Startup] Configuring Render Options...");
        }

        WinFormsApp.SetHighDpiMode(WinFormsHighDpiMode.PerMonitorV2);

        var settings = IoCContainer.Resolve<ApplicationSettings>();

        if (!settings.Store.EnableHardwareAcceleration)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        CardControl.IsCompact = settings.Store.CompactMode;

        ConfigureFeatureFlags();
    }

    private async Task InitializeHardwareAndFeaturesAsync()
    {
        if (AppFlags.Instance.Debug)
        {
            Console.WriteLine(@"[Startup] Initializing Features...");
        }

        Log.Instance.Trace($"Starting... [version={Assembly.GetEntryAssembly()?.GetName().Version}, build={Assembly.GetEntryAssembly()?.GetBuildDateTimeString()}, os={Environment.OSVersion}, dotnet={Environment.Version}]");

        var totalSw = Stopwatch.StartNew();

        var initTasks = new List<Task>
        {
            // MainWindow.UpdateIndicators already queries and traces the same four
            // software states. Do not repeat those service/process scans here.
            SafeInitAsync(InitAutomationProcessorAsync, "Automation Processor"),
        };

        foreach (var task in initTasks)
        {
            await task;
        }

        var postTasks = new List<Task>
        {
            SafeInitAsync(InitAutomationLocalization, "Automation Localization"),
        };

        await Task.WhenAll(postTasks);

        Log.Instance.Trace($"InitializeHardwareAndFeatures completed [elapsed={totalSw.ElapsedMilliseconds}ms]");
    }

    private Task StartBackgroundServicesAsync()
    {
        // Integrations must be explicitly enabled this session, never resumed from saved settings.
        return Task.CompletedTask;
    }

    private Task InitializeUIAsync()
    {
        if (AppFlags.Instance.Debug)
        {
            Console.WriteLine(@"[Startup] Creating MainWindow...");
        }

        var mainWindow = new MainWindow
        {
            ShowActivated = !Folders.IsIsolatedProfile,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            TrayTooltipEnabled = !AppFlags.Instance.DisableTrayTooltip,
            DisableConflictingSoftwareWarning = AppFlags.Instance.DisableConflictingSoftwareWarning
        };

        MainWindow = mainWindow;
        MainWindowInstance = mainWindow;

        IoCContainer.Resolve<ThemeManager>().Apply();
        InitSetLogIndicator();

        PawnIOHelper.RequestShowDialogAsync = state => state switch
        {
            PawnIOState.UpdateRequired => MessageBoxHelper.ShowAsync(Current.MainWindow!, Resource.Notify_PawnIO_Update_Title, string.Format(Resource.Notify_PawnIO_Update_Message, PawnIOHelper.GetInstalledVersion(), PawnIOHelper.RequiredPawnIOVersion), Resource.Yes, Resource.No),
            PawnIOState.ServiceNotRunning => MessageBoxHelper.ShowAsync(Current.MainWindow!, Resource.Notify_PawnIO_ServiceNotRunning_Title, Resource.Notify_PawnIO_ServiceNotRunning_Message, Resource.Yes, Resource.No),
            _ => MessageBoxHelper.ShowAsync(Current.MainWindow!, Resource.Warning, Resource.Notify_PawnIO_Warning_Message, Resource.Yes, Resource.No),
        };

        if (AppFlags.Instance.Minimized && !AppFlags.Instance.UiPreview)
        {
            var minimizeToTray = IoCContainer.Resolve<ApplicationSettings>().Store.MinimizeToTray;

            mainWindow.WindowState = WindowState.Minimized;
            mainWindow.Show();

            if (minimizeToTray)
                mainWindow.Hide();
        }
        else
        {
            mainWindow.Show();
        }

        DebugUIManager.Start();
        return Task.CompletedTask;
    }

    private void InitializeDebugConsole()
    {
        if (!AttachConsole(ATTACH_PARENT_PROCESS))
        {
            AllocConsole();
        }
    }

    private void ConfigureFeatureFlags()
    {
        IoCContainer.Resolve<HttpClientFactory>().SetProxy(AppFlags.Instance.ProxyUrl, AppFlags.Instance.ProxyUsername, AppFlags.Instance.ProxyPassword, AppFlags.Instance.ProxyAllowAllCerts);
        IoCContainer.Resolve<PowerModeFeature>().AllowAllPowerModesOnBattery = AppFlags.Instance.AllowAllPowerModesOnBattery;
        IoCContainer.Resolve<RGBKeyboardBacklightController>().ForceDisable = AppFlags.Instance.ForceDisableRgbKeyboardSupport;
        IoCContainer.Resolve<SpectrumKeyboardBacklightController>().ForceDisable = AppFlags.Instance.ForceDisableSpectrumKeyboardSupport;
        IoCContainer.Resolve<WhiteKeyboardLenovoLightingBacklightFeature>().ForceDisable = AppFlags.Instance.ForceDisableLenovoLighting;
        IoCContainer.Resolve<PanelLogoLenovoLightingBacklightFeature>().ForceDisable = AppFlags.Instance.ForceDisableLenovoLighting;
        IoCContainer.Resolve<PortsBacklightFeature>().ForceDisable = AppFlags.Instance.ForceDisableLenovoLighting;
        IoCContainer.Resolve<IGPUModeFeature>().ExperimentalGPUWorkingMode = AppFlags.Instance.ExperimentalGPUWorkingMode;
        IoCContainer.Resolve<DGPUNotify>().ExperimentalGPUWorkingMode = AppFlags.Instance.ExperimentalGPUWorkingMode;
        IoCContainer.Resolve<UpdateChecker>().Disable = AppFlags.Instance.DisableUpdateChecker ||
            Folders.IsReadOnlySession || !ProjectBranding.HasUpdateFeed;
    }

    private void HandleCriticalStartupError(Exception ex)
    {
        var errorMsg = $"CRITICAL STARTUP ERROR:\n{ex}";

        if (AppFlags.Instance is { Debug: true })
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(@$"\n{new string('=', 30)}\n{errorMsg}\n{new string('=', 30)}");
            Console.ResetColor();
            Console.WriteLine(@"\nPress ENTER to exit...");
            try
            {
                Console.ReadLine();
            }
            catch { /* Ignore */ }
        }
        else
        {
            try
            {
                MessageBox.Show(errorMsg, $"{ProjectBranding.ProductName} - Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* Ignore */ }
        }

        Environment.Exit(-1);
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        try { IoCContainer.Dispose(); }
        catch (Exception ex) { Log.Instance.Trace($"Resource disposal failed.", ex); }
        finally { ReadOnlyQaSession.Mark("ApplicationExit"); _singleInstanceMutex?.Close(); }
    }

    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        // Publish the shutdown task before window-close callbacks can re-enter it.
        await Task.Yield();
        await GamingActivityMonitor.Instance.StopAsync();
        if (HardwareRuntime.ControlsEnabled)
            await SafeExecuteAsync<AIController>(c => c.StopAsync());

        await SafeExecuteAsync<RGBKeyboardBacklightController>(async c =>
        {
            if (HardwareRuntime.ControlsEnabled && c.HasLightControlOwnership) await c.SetLightControlOwnerAsync(false);
        });

        await SafeExecuteAsync<SpectrumKeyboardBacklightController>(async c =>
        {
            if (HardwareRuntime.ControlsEnabled && await c.IsSupportedAsync()) await c.StopAuroraIfNeededAsync();
        });

        await SafeExecuteAsync<NativeWindowsMessageListener>(c => c.StopAsync());
        await SafeExecuteAsync<SessionLockUnlockListener>(c => c.StopAsync());
        await SafeExecuteAsync<HWiNFOIntegration>(c => c.StopAsync());
        await SafeExecuteAsync<IpcServer>(c => c.StopAsync());
        await SafeExecuteAsync<BatteryDischargeRateMonitorService>(c => c.StopAsync());
        await SafeExecuteAsync<ExtensionManager>(c => c.StopAsync());
        await SafeExecuteAsync<GPUController>(c => c.StopAsync(waitForFinish: true));
        await SafeExecuteAsync<AutomationProcessor>(c => c.StopAsync());

        var feature = IoCContainer.Resolve<AmdOverclockingController>();

        var cleanInfo = new ShutdownInfo
        {
            Status = "Normal",
            AbnormalCount = 0
        };

        feature.SaveShutdownInfo(cleanInfo);

        ReadOnlyQaSession.Mark("ShutdownServicesStopped");
        Dispatcher.Invoke(Shutdown);
    }

    private static async Task SafeExecuteAsync<T>(Func<T, Task> action) where T : class
    {
        try
        {
            if (IoCContainer.TryResolve<T>() is { } service)
            {
                await action(service);
            }
        }
        catch { /* Ignore */ }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try
        {
            var reason = e.ReasonSessionEnding == ReasonSessionEnding.Logoff ? "Logoff" : "Shutdown";
            Log.Instance.Trace($"System SessionEnding triggered. Reason: {reason}");

            ExecuteShutdownLogic();
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"CRITICAL ERROR during SessionEnding: {ex}");
        }

        base.OnSessionEnding(e);
    }

    private void ExecuteShutdownLogic()
    {
        var overclockController = IoCContainer.Resolve<AmdOverclockingController>();

        var cleanInfo = new ShutdownInfo
        {
            Status = "Normal",
            AbnormalCount = 0
        };

        overclockController.SaveShutdownInfo(cleanInfo);

        Log.Instance.Trace($"Shutdown info saved successfully.");
    }

    #endregion

    #region Compatibility Check

    private static async Task LogMachineInformationAsync()
    {
        try
        {
            await Compatibility.PrintMachineInfoAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to log machine information.", ex);
        }
    }

    private async Task<bool> CheckCompatibilityAsyncWrapper(AppFlags flags)
    {
        if (flags.SkipCompatibilityCheck)
        {
            return true;
        }

        try
        {
            var (isCompatible, mi) = await Task.Run(Compatibility.IsCompatibleAsync);

            if (isCompatible)
            {
                Log.Instance.Trace($"Compatibility check passed. [Vendor={mi.Vendor}, Model={mi.Model}, MachineType={mi.MachineType}, BIOS={mi.BiosVersion}]");
                return true;
            }

            Log.Instance.Trace($"Incompatible system detected. [Vendor={mi.Vendor}, Model={mi.Model}, MachineType={mi.MachineType}, BIOS={mi.BiosVersion}]");

            var unsupportedWindow = new UnsupportedWindow(mi);
            unsupportedWindow.Show();

            if (await unsupportedWindow.ShouldContinue)
            {
                Log.Instance.IsTraceEnabled = true;
                Log.Instance.Trace($"Compatibility check OVERRIDE. [Vendor={mi.Vendor}, Model={mi.Model}, MachineType={mi.MachineType}]");
                return true;
            }

            Shutdown(202);
            return false;
        }
        catch (Exception ex)
        {
            if (flags.Debug)
            {
                Console.WriteLine(@$"[Compatibility] Check failed: {ex.Message}");
            }

            Log.Instance.Trace($"Failed to check device compatibility", ex);
            MessageBox.Show(Resource.CompatibilityCheckError_Message, Resource.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(200);
            return false;
        }
    }

    #endregion

    #region Instance Management

    public async void RestartMainWindow()
    {
        if (MainWindow is MainWindow mw)
        {
            mw.SuppressClosingEventHandler = true;
            mw.Close();
        }

        MainWindow = MainWindowInstance = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        MainWindow.Show();

        try
        {
            if (AppFlags.Instance.UiPreview)
                MainWindowInstance.ShowUiPreview();
            else
                await MainWindowInstance.InitializeAfterFeaturesAsync();
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Main window reinitialization failed.", ex);
            SnackbarHelper.Show("Window could not be refreshed", ex.Message, SnackbarType.Error);
        }

        var restoreOsd = OsdWindow?.IsVisible == true;
        if (OsdWindow != null)
        {
            OsdWindow.Hide();
            OsdWindow.Close();
            OsdWindow = null;
        }

        var settingsStore = IoCContainer.Resolve<OsdSettings>().Store;

        if (!restoreOsd || !HardwareRuntime.ControlsEnabled || !IoCContainer.Resolve<ApplicationSettings>().Store.EnableHardwareSensors)
        {
            return;
        }

        OsdWindow = settingsStore.SelectedStyleIndex switch
        {
            1 => new OsdBarWindow(),
            _ => new OsdPanelWindow()
        };

        OsdWindow.Show();
    }

    private bool EnsureSingleInstance()
    {
        // Disposable QA profiles must not signal/activate the user's existing session.
        var profileSuffix = Folders.IsIsolatedProfile
            ? "_QA_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Folders.AppData.ToUpperInvariant())))[..16]
            : string.Empty;
        var mutexName = (AppFlags.Instance.UiPreview ? $"{MUTEX_NAME}_Preview" : MUTEX_NAME) + profileSuffix;
        var eventName = (AppFlags.Instance.UiPreview ? $"{EVENT_NAME}_Preview" : EVENT_NAME) + profileSuffix;
        _singleInstanceMutex = new Mutex(true, mutexName, out var isOwned);
        _singleInstanceWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        if (!isOwned)
        {
            _singleInstanceWaitHandle.Set();
            Shutdown();
            return false;
        }

        Task.Factory.StartNew(() =>
        {
            while (_singleInstanceWaitHandle.WaitOne())
            {
                Dispatcher.BeginInvoke(async () =>
                {
                    if (MainWindow is { } window)
                    {
                        window.BringToForeground();
                    }
                    else
                    {
                        await ShutdownAsync();
                    }
                });
            }
        }, TaskCreationOptions.LongRunning);

        return true;
    }

    private async Task EndReadOnlyQaSessionAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(AppFlags.Instance.QaExitAfterSeconds));
            ReadOnlyQaSession.Mark("ShutdownRequested");
            await Dispatcher.InvokeAsync(ShutdownAsync).Task.Unwrap();
        }
        catch (Exception ex) { Log.Instance.Trace($"Timed read-only QA shutdown failed.", ex); }
    }


    #endregion

    #region Exception Handling

    private void LogUnhandledException(Exception exception)
    {
        if (exception == null)
        {
            return;
        }

        if (AppFlags.Instance is { Debug: true })
        {
            Console.WriteLine(@$"[UnhandledException] {exception}");
        }

        Log.Instance.Trace($"Exception in LogUnhandledException {exception.Message}", exception);
        var userMessage = GetFriendlyErrorMessage(exception);

        if (Application.Current == null)
        {
            return;
        }

        var showSnackbarAction = () =>
        {
            SnackbarHelper.Show(Resource.UnexpectedException, userMessage, SnackbarType.Error);
        };

        if (Dispatcher.CheckAccess())
        {
            showSnackbarAction();
        }
        else
        {
            Dispatcher.BeginInvoke(showSnackbarAction);
        }
    }

    private Exception GetInnermostException(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: > 0 } aggEx)
        {
            return GetInnermostException(aggEx.InnerExceptions[0]);
        }

        if (ex.InnerException != null)
        {
            return GetInnermostException(ex.InnerException);
        }

        return ex;
    }

    private string GetFriendlyErrorMessage(Exception ex)
    {
        if (ex == null)
        {
            return "An unknown error occurred.";
        }

        var inner = GetInnermostException(ex);

        if (string.IsNullOrWhiteSpace(inner.Message))
        {
            return "An unexpected error occurred, please try again.";
        }
        else
        {
            return inner.Message;
        }
    }

    private bool ShouldIgnoreException(Exception ex)
    {
        return ex switch
        {
            ManagementException or OperationCanceledException or TaskCanceledException => true,
            AggregateException aggregateException => aggregateException.InnerExceptions.Any(ShouldIgnoreException),
            _ => ex.InnerException != null && ShouldIgnoreException(ex.InnerException)
        };
    }

    private void SetupExceptionHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (!ShouldIgnoreException((Exception)e.ExceptionObject))
            {
                LogUnhandledException((Exception)e.ExceptionObject);
            }
        };

        DispatcherUnhandledException += (s, e) =>
        {
            if (!ShouldIgnoreException(e.Exception))
            {
                LogUnhandledException(e.Exception);
            }

            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            if (!ShouldIgnoreException(e.Exception))
            {
                LogUnhandledException(e.Exception);
            }

            e.SetObserved();
        };
    }

    #endregion

    #region Feature Initialization

    private static Task<bool> InitITSModeFeatureAsync()
    {
        return IoCContainer.Resolve<ITSModeFeature>().RestoreStateAsync();
    }

    private static async Task<bool> InitPowerModeFeatureAsync()
    {
        try
        {
            var feature = IoCContainer.Resolve<PowerModeFeature>();

            if (await feature.IsSupportedAsync())
            {
                await feature.EnsureGodModeStateIsAppliedAsync(skipThrottle: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"InitPowerModeFeatureAsync failed.", ex);
            return false;
        }
    }

    private static async Task<bool> InitAIControllerAsync()
    {
        try
        {
            await IoCContainer.Resolve<AIController>().StartIfNeededAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"InitAIControllerAsync failed.", ex);
            return false;
        }
    }

    private static async Task<bool> InitHybridModeAsync()
    {
        try
        {
            await IoCContainer.Resolve<HybridModeFeature>().EnsureDGPUEjectedIfNeededAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Couldn't initialize hybrid mode.", ex);
            return false;
        }
    }

    private static async Task<bool> InitAutomationProcessorAsync()
    {
        try
        {
            var ap = IoCContainer.Resolve<AutomationProcessor>();
            await ap.InitializeAsync(startListeners: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Couldn't initialize automation processor.", ex);
            return false;
        }
    }

    private static async Task<bool> InitAutomationLocalization()
    {
        AutomationTranslator.GetTitleFunc = typeName =>
        {
            return TitleCache.GetOrAdd(typeName, name =>
            {
                return Resource.ResourceManager.GetString($"{name}Control_Title") ?? name.Replace("AutomationStep", string.Empty);
            });
        };

        return true;
    }

    private static void InitSetLogIndicator()
    {
        try
        {
            var settings = IoCContainer.Resolve<ApplicationSettings>();

            if (!settings.Store.EnableLogging || Current.MainWindow is not MainWindow mainWindow)
            {
                return;
            }

            Log.Instance.IsTraceEnabled = true;
            mainWindow._openLogIndicator.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Couldn't reapply parameters.", ex);
        }
    }

    private static async Task<bool> InitBatteryFeatureAsync()
    {
        try
        {
            var feature = IoCContainer.Resolve<BatteryFeature>();

            if (await feature.IsSupportedAsync())
            {
                await feature.EnsureCorrectBatteryModeIsSetAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Couldn't ensure correct battery mode.", ex);
            return false;
        }
    }

    private static async Task<bool> InitSensorsGroupControllerFeatureAsync()
    {
        var settings = IoCContainer.Resolve<ApplicationSettings>();
        var OsdSettings = IoCContainer.Resolve<OsdSettings>();

        try
        {
            if (settings.Store is { EnableHardwareSensors: false })
            {
                return false;
            }

            var state = await IoCContainer.Resolve<SensorsGroupController>().IsSupportedAsync();

            if (state is not (LibreHardwareMonitorInitialState.Initialized or LibreHardwareMonitorInitialState.Success))
            {
                Current._showPawnIONotify = true;
            }
            else if (PawnIOHelper.GetPawnIOState() is PawnIOState.UpdateRequired or PawnIOState.ServiceNotRunning)
            {
                Current._showPawnIONotify = true;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"InitSensorsGroupControllerFeatureAsync() raised exception:", ex);

            if (!ex.Message.Contains("LibreHardwareMonitor initialization failed"))
            {
                Current._showPawnIONotify = true;
            }

            return false;
        }
    }

    private static async Task<bool> InitRgbKeyboardControllerAsync()
    {
        try
        {
            var controller = IoCContainer.Resolve<RGBKeyboardBacklightController>();

            if (await controller.IsSupportedAsync())
            {
                await controller.SetLightControlOwnerAsync(true, true);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Couldn't set light control owner or current preset.", ex);
            return false;
        }
    }

    private static async Task<bool> InitSpectrumKeyboardControllerAsync()
    {
        try
        {
            var controller = IoCContainer.Resolve<SpectrumKeyboardBacklightController>();

            if (await controller.IsSupportedAsync())
            {
                await controller.StartAuroraIfNeededAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Couldn't start Aurora if needed.", ex);
            return false;
        }
    }

    private static async Task<bool> InitGpuOverclockControllerAsync()
    {
        try
        {
            var controller = IoCContainer.Resolve<GPUOverclockController>();

            if (await controller.IsSupportedAsync())
            {
                await controller.EnsureOverclockIsAppliedAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Couldn't overclock GPU.", ex);
            return false;
        }
    }

    private static async Task<bool> InitAMDOverclocking()
    {
        try
        {
            var feature = IoCContainer.Resolve<AmdOverclockingController>();

            if (feature.IsActive())
            {
                await feature.InitializeAsync().ConfigureAwait(false);
                Log.Instance.Trace($"AMD Overclocking Controller initialized.");

                return true;
            }

            return false;
        }
        catch (InvalidOperationException)
        {
            Log.Instance.Trace($"Profile apply has been canceled due to AC issue.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to apply profile on startup: {ex.Message}", ex);
            return false;
        }
    }

    private static async Task<bool> PostApplyAmdOverclockingProfileAsync()
    {
        var feature = IoCContainer.Resolve<AmdOverclockingController>();

        if (feature.IsActive() && !feature.DoNotApply && feature.IsSupported())
        {
            await feature.ApplyInternalProfileAsync().ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<bool> InitHWiNFOAsync()
    {
        await IoCContainer.Resolve<HWiNFOIntegration>().StartStopIfNeededAsync().ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> InitIpcServerAsync()
    {
        await IoCContainer.Resolve<IpcServer>().StartStopIfNeededAsync().ConfigureAwait(false);
        return true;
    }

    #endregion
    #region UI Helpers

    public void InitOsd()
    {
        MessagingCenter.Subscribe<OsdChangedMessage>(this, message =>
        {
            Dispatcher.Invoke(() =>
            {
                HandleOsdCommand(message.State, message.SetPreference);
            });
        });

        // A saved overlay preference is not session consent for continuous reads.
        // The existing OSD toggle/hotkey remains the explicit opt-in.
    }

    private void InitAppMessages()
    {
        MessagingCenter.Subscribe<ShowAppMessage>(this, _ =>
        {
            Dispatcher.Invoke(() =>
            {
                if (MainWindowInstance is { } window)
                {
                    window.BringToForeground();
                }
            });
        });
    }

    private void HandleOsdCommand(ToggleState command, bool setPreference = true)
    {
        var OsdSettings = IoCContainer.Resolve<OsdSettings>();
        bool shouldBeBar = OsdSettings.Store.SelectedStyleIndex == 1;

        bool show = command switch
        {
            ToggleState.On => true,
            ToggleState.Off => false,
            ToggleState.Toggle => !(OsdWindow?.IsVisible ?? false),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };

        if (show && !HardwareRuntime.ControlsEnabled) return;

        if (show)
        {
            EnsureCorrectOsdStyle(shouldBeBar);
            OsdWindow?.Show();
        }
        else
        {
            OsdWindow?.Hide();
        }

        if (setPreference)
        {
            OsdSettings.Store.ShowOsd = OsdWindow?.IsVisible ?? false;
            OsdSettings.SynchronizeStore();
        }
    }

    private void EnsureCorrectOsdStyle(bool shouldBeBar)
    {
        if (OsdWindow != null && (OsdWindow is OsdBarWindow) != shouldBeBar)
        {
            OsdWindow.Close();
            OsdWindow = null;
        }

        EnsureOsdWindowCreated(shouldBeBar);
    }

    private void EnsureOsdWindowCreated(bool isBar)
    {
        if (OsdWindow != null)
        {
            return;
        }

        OsdWindow = isBar ? new OsdBarWindow() : new OsdPanelWindow();
        OsdWindow.Closed += (s, e) =>
        {
            OsdWindow = null;
        };
    }

    private static async Task<bool> InitLampArrayControllerAsync()
    {
        try
        {
            if (!AppFlags.Instance.EnableLampArray)
            {
                return false;
            }

            var controller = IoCContainer.Resolve<LampArrayController>();
            var settings = IoCContainer.Resolve<LampArraySettings>();
            controller.SetScreenCaptureProvider(new SpectrumScreenCapture());
            await controller.InitializeAsync(settings).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"InitLampArrayControllerAsync failed.", ex);
            return false;
        }
    }

    private static Task SafeInitAsync(Func<Task<bool>> action, string taskName)
    {
        return Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var initialized = await action().ConfigureAwait(false);
                Log.Instance.Trace($"{taskName} initialization finished. [initialized={initialized}, elapsed={sw.ElapsedMilliseconds}ms]");
            }
            catch (Exception ex)
            {
                Log.Instance.Trace($"{taskName} initialization failed. [elapsed={sw.ElapsedMilliseconds}ms]", ex);
            }
        });
    }

    private void LogIdentityStatus()
    {
        try
        {
            var package = global::Windows.ApplicationModel.Package.Current;
            var aumid = $"{package.Id.FamilyName}!App";
            SetCurrentProcessExplicitAppUserModelID(aumid);
            Log.Instance.Trace($"Package Identity found and AUMID set: {aumid}");
        }
        catch (InvalidOperationException)
        {
            const string appUserModelId = "ProjectLegion.Desktop";
            SetCurrentProcessExplicitAppUserModelID(appUserModelId);
            Log.Instance.Trace($"Package Identity not found; using desktop AUMID: {appUserModelId}");
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to check Package Identity: {ex.Message}");
        }
    }

    #endregion
}
