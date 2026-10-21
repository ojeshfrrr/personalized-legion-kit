using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Controllers.Sensors;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Overclocking.Amd;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;

if (args.Length == 3 && args[0] == "--approved-deathadder-reversible-side4-forward")
{
    await DeathAdderControlledButtonTest.RunAsync(args[1], args[2]);
    return;
}

if (args.Length == 3 && args[0] == "--approved-deathadder-buttons-read")
{
    await DeathAdderButtonsRead.RunAsync(args[1], args[2]);
    return;
}

if (args.Length == 3 && args[0] == "--approved-deathadder-reversible-polling-8000-during-warmup")
{
    await DeathAdderControlledPollingTest.RunAsync(args[1], args[2], allowRunningGame: true);
    return;
}

if (args.Length == 3 && args[0] == "--approved-deathadder-reversible-polling-8000")
{
    await DeathAdderControlledPollingTest.RunAsync(args[1], args[2]);
    return;
}

if (args.Length == 3 && args[0] == "--approved-deathadder-polling-read")
{
    await DeathAdderPollingRead.RunAsync(args[1], args[2]);
    return;
}

if (args.Length == 3 && args[0] == "--approved-deathadder-reversible-1650")
{
    await DeathAdderControlledDpiTest.RunAsync(args[1], args[2]);
    return;
}

if (args.Length == 3 && args[0] == "--approved-deathadder-baseline")
{
    await DeathAdderBaselineRead.RunAsync(args[1], args[2]);
    return;
}

if (args.Length == 1 && args[0].StartsWith("--approved-deathadder-dpi-read=", StringComparison.Ordinal))
{
    await DeathAdderLiveRead.RunAsync(args[0]["--approved-deathadder-dpi-read=".Length..]);
    return;
}

// Only this test double is registered. No production IoC module or hardware service is constructed.
var assertions = 0;
var profile = Path.Combine(Environment.CurrentDirectory, "BuildProjectLegion", "Phase7", "assertion-profile-" + Guid.NewGuid().ToString("N"));
CheckThrows(() => AppFlags.Initialize(["--qa-profile=" + profile]), "QA profile requires explicit read-only mode", typeof(ArgumentException));
Directory.CreateDirectory(profile);
var profileMarker = Path.Combine(profile, ".project-legion-qa-profile");
const string markerSentinel = "preexisting QA profile marker";
File.WriteAllText(profileMarker, markerSentinel);
var markerTimestamp = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
File.SetLastWriteTimeUtc(profileMarker, markerTimestamp);
AppFlags.Initialize(["--read-only", "--qa-profile=" + profile]);
Check(Folders.IsIsolatedProfile && Folders.AppData == profile, "QA settings use the isolated directory");
Check(Folders.IsReadOnlySession, "read-only mode also locks configuration persistence");
Check(File.ReadAllText(profileMarker) == markerSentinel && File.GetLastWriteTimeUtc(profileMarker) == markerTimestamp, "configuring an existing QA profile preserves its marker contents and timestamp");
Check(!File.Exists(Path.Combine(profile, "settings.json")), "QA does not migrate upstream settings");

AppFlags.Initialize(["--read-only"]);
Check(!Folders.IsIsolatedProfile, "a read-only initialization without a QA profile does not reuse a previous profile");
Check(Log.Instance.LogPath.Length == 0, "plain read-only mode does not create a user-profile log path");
Log.Instance.ErrorReport("read-only assertion", new InvalidOperationException("sentinel"));

AppFlags.Initialize(["--read-only", "--qa-profile=" + profile]);
var amdController = new AmdOverclockingController(new AmdOverclockingSettings());
var externalAmdProfile = Path.Combine(profile, "exported_amd_profile.json");
amdController.SaveShutdownInfo(new ShutdownInfo { Status = "Normal", AbnormalCount = 0 });
amdController.SaveDefaultProfile(new OverclockingProfile());
amdController.SaveProfile(new OverclockingProfile(), externalAmdProfile);
Check(!File.Exists(Path.Combine(profile, "system_status.json")) && !File.Exists(Path.Combine(profile, "default_amd_overclocking.json")) && !File.Exists(externalAmdProfile), "AMD status, default-profile, and explicit-profile writes are blocked in read-only mode");

var localizationSource = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Utils", "LocalizationHelper.cs"));
var languageSaveStart = localizationSource.IndexOf("private static Task SaveLanguageToFileAsync", StringComparison.Ordinal);
var languageGuard = languageSaveStart >= 0 ? localizationSource.IndexOf("if (Folders.IsReadOnlySession)", languageSaveStart, StringComparison.Ordinal) : -1;
var languageWrite = languageSaveStart >= 0 ? localizationSource.IndexOf("File.WriteAllTextAsync(LanguagePath", languageSaveStart, StringComparison.Ordinal) : -1;
Check(languageSaveStart >= 0 && languageGuard > languageSaveStart && languageWrite > languageGuard, "language fallback persistence checks read-only mode before writing");

var appSource = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "App.xaml.cs"));
Check(appSource.Contains("HardwareRuntime.ControlsEnabled && c.HasLightControlOwnership", StringComparison.Ordinal), "RGB ownership release is skipped while hardware controls are locked");

var resetSource = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Controls", "Settings", "SettingsAppBehaviorControl.xaml.cs"));
var resetStart = resetSource.IndexOf("private async void ResetSettings_Click", StringComparison.Ordinal);
var resetGuard = resetStart >= 0 ? resetSource.IndexOf("if (Folders.IsReadOnlySession)", resetStart, StringComparison.Ordinal) : -1;
var resetDelete = resetStart >= 0 ? resetSource.IndexOf("File.Delete(file)", resetStart, StringComparison.Ordinal) : -1;
Check(resetStart >= 0 && resetGuard > resetStart && resetDelete > resetGuard, "Settings reset checks read-only mode before deleting profile files");

var argumentSource = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Windows", "Utils", "ArgumentWindow.xaml.cs"));
var argumentSaveStart = argumentSource.IndexOf("private void Save_Click", StringComparison.Ordinal);
var argumentGuard = argumentSaveStart >= 0 ? argumentSource.IndexOf("if (Folders.IsReadOnlySession)", argumentSaveStart, StringComparison.Ordinal) : -1;
var argumentWrite = argumentSaveStart >= 0 ? argumentSource.IndexOf("File.WriteAllLines(argsFile, newArgs)", argumentSaveStart, StringComparison.Ordinal) : -1;
Check(argumentSaveStart >= 0 && argumentGuard > argumentSaveStart && argumentWrite > argumentGuard, "Argument editor checks read-only mode before its direct args.txt write");

var settingsPath = Path.Combine(profile, "settings.json");
var osdPath = Path.Combine(profile, "osd.json");
var rgbPath = Path.Combine(profile, "rgb_keyboard.json");
new ApplicationSettings().EnsureFileExists();
new OsdSettings().EnsureFileExists();
new RGBKeyboardSettings().EnsureFileExists();
new ApplicationSettings().Save();
new ApplicationSettings().SynchronizeStore();
new OsdSettings().Save();
new OsdSettings().SynchronizeStore();
new RGBKeyboardSettings().Save();
new RGBKeyboardSettings().SynchronizeStore();
Check(!File.Exists(settingsPath) && !File.Exists(osdPath) && !File.Exists(rgbPath), "Save, SynchronizeStore, and EnsureFileExists do not initialize settings in read-only mode");

const string sentinel = "{\"phase5Sentinel\":true}";
File.WriteAllText(settingsPath, sentinel);
File.WriteAllText(osdPath, sentinel);
File.WriteAllText(rgbPath, sentinel);
var appSettings = new ApplicationSettings();
appSettings.Store.WindowSize = new WindowSize(980, 680);
appSettings.Save();
appSettings.SynchronizeStore();
var osdSettings = new OsdSettings();
osdSettings.Save();
osdSettings.SynchronizeStore();
var rgbSettings = new RGBKeyboardSettings();
rgbSettings.Save();
rgbSettings.SynchronizeStore();
Check(File.ReadAllText(settingsPath) == sentinel && File.ReadAllText(osdPath) == sentinel && File.ReadAllText(rgbPath) == sentinel, "all three existing settings files survive direct save and synchronization calls byte-for-byte");

var backupWindowPath = Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Windows", "Settings", "SettingsBackupWindow.xaml.cs");
var backupWindowSource = File.ReadAllText(backupWindowPath);
var restoreHandlerStart = backupWindowSource.IndexOf("private async void RestoreButton_Click", StringComparison.Ordinal);
var restoreReadOnlyGuard = restoreHandlerStart >= 0 ? backupWindowSource.IndexOf("if (Folders.IsReadOnlySession)", restoreHandlerStart, StringComparison.Ordinal) : -1;
var restoreFileCopy = restoreHandlerStart >= 0 ? backupWindowSource.IndexOf("File.Copy(sourcePath, item.FullPath, true);", restoreHandlerStart, StringComparison.Ordinal) : -1;
Check(restoreHandlerStart >= 0 && restoreReadOnlyGuard > restoreHandlerStart && restoreFileCopy > restoreReadOnlyGuard, "Settings Backup restore source checks read-only mode before its direct profile file copy");

File.WriteAllText(osdPath, "{");
_ = new OsdSettings().Store;
Check(File.ReadAllText(osdPath) == "{", "corrupt settings are not replaced during read-only recovery");
Check(!Directory.EnumerateFiles(profile, "*_backup_*.json").Any(), "corrupt settings do not create recovery backups in read-only mode");
AppFlags.Instance.Minimized = true;
Check(!File.Exists(Path.Combine(profile, "args.txt")), "AppFlags changes do not write args.txt in read-only mode");
CheckThrows(() => AppFlags.Initialize(["--qa-exit-after-seconds=15"]), "timed QA requires read-only mode", typeof(ArgumentException));
CheckThrows(() => AppFlags.Initialize(["--read-only", "--qa-exit-after-seconds=1"]), "timed QA rejects out-of-range duration", typeof(ArgumentException));
AppFlags.Initialize(["--read-only", "--qa-profile=" + profile, "--qa-exit-after-seconds=15"]);
Check(AppFlags.Instance.QaExitAfterSeconds == 15, "bounded QA lifetime is session-only");
Check(!HardwareRuntime.ControlsEnabled, "timed QA leaves commands locked");
foreach (var flag in new[] { "--read-only", "--ui-preview" })
{
    AppFlags.Initialize([flag]);
    Check(Folders.IsReadOnlySession, $"{flag}: settings persistence remains locked");
    var builder = new ContainerBuilder();
    builder.Register<ProbeListener>().AutoActivateListener();
    using var container = builder.Build();
    var listener = container.Resolve<ProbeListener>();
    Check(listener.StartCount == 0, $"{flag}: automatic listener activation was suppressed");
    Check(!HardwareRuntime.ControlsEnabled, $"{flag}: commands remain locked");
    CheckThrows(HardwareRuntime.EnableControls, $"{flag}: command lock cannot be lifted");
    CheckThrows(HardwareRuntime.RequireControls, $"{flag}: command entry rejects a write");
    var gpu = new GPUController(); // Constructor only allocates locks; neither NVAPI nor IoC is resolved.
    CheckThrows(() => gpu.ApplyPStateAsync(0).GetAwaiter().GetResult(), $"{flag}: GPU P-state apply rejects before NVAPI access");
    CheckThrows(() => gpu.SetPStateAsync(0).GetAwaiter().GetResult(), $"{flag}: GPU P-state save rejects before settings access");
}
Console.WriteLine($"PASS: {assertions} safety/settings assertions; no devices or drivers were initialized.");

Check(SensorQueryRequirements.GetSupplementalQueries(new HashSet<SensorItem> { SensorItem.MemoryUtilization, SensorItem.Disk1Temperature }) == SupplementalSensorQueries.None,
    "memory/storage-only view does not request fan or battery queries");
foreach (var item in new[] { SensorItem.CpuFanSpeed, SensorItem.GpuFanSpeed, SensorItem.PchFanSpeed, SensorItem.PchTemperature })
    Check(SensorQueryRequirements.GetSupplementalQueries(new HashSet<SensorItem> { item }) == SupplementalSensorQueries.Lenovo,
        $"{item} retains its Lenovo query without requesting battery information");
foreach (var item in new[] { SensorItem.BatteryState, SensorItem.BatteryLevel })
    Check(SensorQueryRequirements.GetSupplementalQueries(new HashSet<SensorItem> { item }) == SupplementalSensorQueries.Battery,
        $"{item} retains its battery query without requesting Lenovo fan information");
Check(SensorQueryRequirements.GetSupplementalQueries(new HashSet<SensorItem>(Enum.GetValues<SensorItem>())) ==
      (SupplementalSensorQueries.Lenovo | SupplementalSensorQueries.Battery), "full view retains both supplemental data providers");

if (args.Length >= 2 && args[0] == "--measure-polling-lifecycle")
{
    // The override only waits on managed signals. No NVAPI, IoC, settings, or
    // device query is executed by this test of the real controller lifecycle.
    var polling = new PollingLifecycleProbe();
    await polling.StartAsync(delay: 0);
    await polling.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => polling.StartAsync(delay: 0)));
    var startsBeforeStop = polling.Starts;
    var stop = polling.StopAsync();
    await polling.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.WhenAny(stop, Task.Delay(50));
    var stopReturnedWhileWorkerActive = stop.IsCompleted;
    var restart = polling.StartAsync(delay: 0);
    await Task.Delay(50);
    polling.AllowExit.TrySetResult();
    await stop;
    await restart;
    await polling.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await polling.StopAsync();
    await polling.AllExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var metrics = new { StartsBeforeStop = startsBeforeStop, StopReturnedWhileWorkerActive = stopReturnedWhileWorkerActive,
        PeakConcurrentWorkers = polling.PeakWorkers, WorkersAfterStop = polling.ActiveWorkers };
    var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.GetFullPath(args[1]), json);
    Console.WriteLine(json);
    if (args.Contains("--assert"))
    {
        Check(startsBeforeStop == 1, "repeated GPU starts share one monitoring worker");
        Check(!stopReturnedWhileWorkerActive, "GPU stop awaits worker completion");
        Check(polling.PeakWorkers == 1 && polling.ActiveWorkers == 0, "GPU restart never overlaps a retiring worker and stop leaves none running");
    }
}

await FeatureStateConfirmationChecks.RunAsync(Check);
await GamingWorkloadChecks.RunAsync(Check);
await DeathAdderProtocolChecks.RunAsync(Check);
await DpiConfigurationChecks.RunAsync(Check);
await PollingConfigurationChecks.RunAsync(Check);
await MousePageChecks.RunAsync(Check);
await ButtonConfigurationChecks.RunAsync(Check);
await KeyboardAssignmentChecks.RunAsync(Check);
MouseSoftwareProfileChecks.Run(Check);
await MousePresetApplyChecks.RunAsync(Check);
Console.WriteLine($"PASS: {assertions} total safety and targeted assertions.");

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
    Console.WriteLine($"PASS: {message}");
}

void CheckThrows(Action action, string message, Type? expected = null)
{
    try { action(); }
    catch (Exception ex) when (ex.GetType() == (expected ?? typeof(InvalidOperationException))) { Check(true, message); return; }
    throw new InvalidOperationException(message);
}

internal sealed class ProbeListener : IListener<EventArgs>
{
    public int StartCount { get; private set; }
    public event EventHandler<EventArgs>? Changed { add { } remove { } }
    public Task StartAsync() { StartCount++; return Task.CompletedTask; }
    public Task StopAsync() => Task.CompletedTask;
}

internal sealed class PollingLifecycleProbe : GPUController
{
    public readonly TaskCompletionSource FirstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource SecondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource AllowExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource AllExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Starts;
    public int ActiveWorkers;
    public int PeakWorkers;
    private int _exited;
    protected override async Task RefreshLoopAsync(int delay, CancellationToken token)
    {
        var started = Interlocked.Increment(ref Starts);
        var active = Interlocked.Increment(ref ActiveWorkers);
        PeakWorkers = Math.Max(PeakWorkers, active);
        if (started == 1) FirstStarted.TrySetResult(); else SecondStarted.TrySetResult();
        try
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { CancellationObserved.TrySetResult(); }
            await AllowExit.Task;
        }
        finally
        {
            Interlocked.Decrement(ref ActiveWorkers);
            if (Interlocked.Increment(ref _exited) == 2) AllExited.TrySetResult();
        }
    }
}
