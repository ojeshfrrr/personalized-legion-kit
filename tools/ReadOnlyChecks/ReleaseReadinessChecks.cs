using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using LenovoLegionToolkit.Lib.Utils;

internal static class ReleaseReadinessChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        OptionalUiState State(bool active = true, bool minimized = false, bool visible = true, bool loaded = true, bool pauseForGaming = false, bool paused = true, bool game = true) =>
            OptionalUiPolicy.Evaluate(loaded, visible, true, active, minimized, true, pauseForGaming, paused, game);
        check(State() == OptionalUiState.Active, "foreground sensor policy permits gaming-time readings");
        check(State(active: false) == OptionalUiState.Inactive && State(minimized: true) == OptionalUiState.Minimized,
            "monitoring explains foreground loss and minimization distinctly");
        check(State(loaded: false) == OptionalUiState.Hidden && State(visible: false) == OptionalUiState.Hidden,
            "unloaded and hidden pages remain stopped");
        check(State(pauseForGaming: true) == OptionalUiState.GamingPaused && State(pauseForGaming: true, game: false) == OptionalUiState.CheckingGame,
            "game-blocked work distinguishes game presence from unavailable detection");
        check(Enum.GetValues<OptionalUiState>().All(state => OptionalUiPolicy.Describe(state).Length > 10), "each monitoring state has a visible explanation");
        var started = 0;
        var reads = new DemandReadSession<int>(_ => Task.FromResult(4), (_, _) => { }, _ => { }, readingStarted: () => started++);
        await reads.RefreshAsync(); check(started == 0, "inactive refresh does not claim initialization started");
        await reads.ActivateAsync(); await reads.DeactivateAsync(); check(started == 1, "active getter reports its actual start once");

        try { await PortableUpdateInstaller.PrepareAsync("missing.zip", new("", 1, ""), new Version(9, 0)); check(false, "read-only installation blocked"); }
        catch (InvalidOperationException) { check(true, "read-only install is blocked before file, administrator or process access"); }
        try { await PortableUpdateInstaller.StartRestartAsync(new("missing", "missing", new Version(9, 0))); check(false, "read-only restart blocked"); }
        catch (InvalidOperationException) { check(true, "read-only restart/rollback cannot start a helper"); }
        check(PortableUpdateInstaller.GetRollbackCandidate() is null, "read-only sessions do not expose a rollback mutation");

        var stage = typeof(PortableUpdateInstaller).GetMethod("StageCoreAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var directory = Path.Combine(Environment.CurrentDirectory, "BuildProjectLegion", "InstallerChecks", Guid.NewGuid().ToString("N"));
        var previous = Path.Combine(directory, "source"); var root = Path.Combine(directory, "versions");
        Directory.CreateDirectory(previous); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(previous, "Project Legion.exe"), "offline previous executable fixture");
        File.WriteAllText(Path.Combine(previous, "settings.json"), "private sentinel");
        File.WriteAllText(Path.Combine(previous, "secret.pfx"), "excluded sentinel");
        var assembly = Path.Combine(AppContext.BaseDirectory, "Project Legion.dll");
        var version = AssemblyName.GetAssemblyName(assembly).Version!;
        var zip = Path.Combine(directory, "valid.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(assembly, "Project Legion.dll");
            foreach (var name in new[] { "Project Legion.exe", "LenovoLegionToolkit.Lib.dll", "Project Legion.deps.json", "Project Legion.runtimeconfig.json", "LICENSE", "NOTICE" })
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("offline fixture");
        }
        var package = new PortableUpdatePackage($"https://github.com/ojeshfrrr/personalized-legion-kit/releases/download/v{version}/ProjectLegion-win-x64-framework-dependent.zip",
            new FileInfo(zip).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))));
        Task<PreparedUpdate> Stage(PortableUpdatePackage metadata, Action guard, CancellationToken token = default) =>
            (Task<PreparedUpdate>)stage.Invoke(null, [zip, metadata, version, root, previous, token, guard])!;
        var installed = await Stage(package, () => { });
        var transactionType = typeof(PortableUpdateInstaller).GetNestedType("Transaction", BindingFlags.NonPublic)!;
        check(JsonSerializer.Deserialize(File.ReadAllText(installed.Ticket), transactionType) is not null,
            "persisted handoff data round-trips through the production transaction type");
        var backup = Path.Combine(Path.GetDirectoryName(installed.Executable)!, "Previous");
        check(File.Exists(installed.Ticket) && File.Exists(installed.Executable) && File.Exists(Path.Combine(backup, "Project Legion.exe")),
            "offline verified package stages side by side with a retained previous executable");
        check(!File.Exists(Path.Combine(backup, "settings.json")) && !File.Exists(Path.Combine(backup, "secret.pfx")) &&
            File.ReadAllText(Path.Combine(previous, "settings.json")) == "private sentinel", "backup excludes configuration/private keys and never edits the original profile fixture");
        var count = Directory.GetDirectories(root).Length;
        try { await Stage(package with { Sha256 = new string('0', 64) }, () => { }); check(false, "tampered download rejected"); }
        catch (LenovoLegionToolkit.Lib.Utils.SecurityException) { check(Directory.GetDirectories(root).Length == count, "reverification rejects a changed ZIP before creating an installation"); }
        var calls = 0;
        try { await Stage(package, () => { if (++calls > 2) throw new OperationCanceledException(); }); check(false, "partial install cancelled"); }
        catch (OperationCanceledException) { check(Directory.GetDirectories(root).Length == count, "cancelled partial extraction removes only its new transaction directory"); }
        var observe = typeof(PortableUpdateInstaller).GetMethod("ObserveStartupAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Task<string> Observe(Func<bool> exited, Func<bool> healthy, CancellationToken token = default) => (Task<string>)observe.Invoke(null, [exited, healthy, token])!;
        check(await Observe(() => false, () => true) == "Healthy", "restart success requires an explicit startup acknowledgement");
        check(await Observe(() => true, () => false) == "ExitedBeforeReady", "early process failure is distinguished for rollback");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        check((await Observe(() => false, () => false, cancelled.Token)).StartsWith("Pending", StringComparison.Ordinal),
            "uncertain startup remains pending instead of killing an app or claiming success");
        var execute = typeof(PortableUpdateInstaller).GetMethod("ExecuteRestartPlanAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var first in new[] { "Healthy", "ExitedBeforeReady", "LaunchFailed", "Pending" })
        {
            var attempts = new System.Collections.Generic.List<string>();
            var verifications = new System.Collections.Generic.List<string>();
            var committed = "";
            Func<string, Task<string>> launch = path => { attempts.Add(path); return Task.FromResult(path == "new" ? first : "Healthy"); };
            var outcome = await (Task<string>)execute.Invoke(null, ["new", "old", launch, (Action<string>)(path => verifications.Add(path)), (Action<string>)(path => committed = path)])!;
            check(first switch
            {
                "Healthy" => outcome == "Healthy" && attempts.SequenceEqual(new[] { "new" }) && committed == "new",
                "Pending" => outcome == "Pending" && attempts.SequenceEqual(new[] { "new" }) && committed == "",
                _ => outcome == "PreviousVersionRestored" && attempts.SequenceEqual(new[] { "new", "old" }) && verifications.SequenceEqual(new[] { "new", "old" }) && committed == "old"
            }, "restart coordinator handles " + first + " without duplicate launches or premature shortcut changes");
        }
        var host = File.ReadAllText("LenovoLegionToolkit.UpdateHost/Program.cs");
        check(!host.Contains("IoC") || host.Contains("// No App, IoC"), "update host does not initialize the production service container");
        var installer = File.ReadAllText("LenovoLegionToolkit.Lib/Utils/PortableUpdateInstaller.cs");
        check(!installer.Contains(".Kill(") && !installer.Contains("ExecutionPolicy") && !installer.Contains("Verb = \"runas\""),
            "updater neither force-closes processes nor bypasses Windows elevation/script policies");
    }
}
