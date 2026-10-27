using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Utils;

public sealed record PreparedUpdate(string Ticket, string Executable, Version Version);

/// <summary>Explicit, side-by-side software installation. Never modifies the user profile or overwrites the running installation.</summary>
public static class PortableUpdateInstaller
{
    private const string AppExe = "Project Legion.exe";
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProjectLegionUpdates");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed class Transaction
    {
        public Transaction() { }
        public int Schema { get; set; } = 1;
        public string Origin { get; set; } = "";
        public string Target { get; set; } = "";
        public string Previous { get; set; } = "";
        public int ParentPid { get; set; }
        public long ParentStart { get; set; }
        public string State { get; set; } = "Prepared";
        public string WorkerToken { get; set; } = "";
        public Dictionary<string, string> TargetFiles { get; set; } = [];
        public Dictionary<string, string> PreviousFiles { get; set; } = [];
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void RequireWrite()
    {
        if (Folders.IsReadOnlySession) throw new InvalidOperationException("Installing updates is disabled in read-only mode.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Installation requires the application's normal administrator session. No elevation is requested automatically.");
    }
    private static void NoLinks(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Update paths cannot contain reparse points.");
    }
    private static void SecureRoot()
    {
        NoLinks(Root);
        Directory.CreateDirectory(Root);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(security);
    }
    public static async Task<PreparedUpdate> PrepareAsync(string zip, PortableUpdatePackage package, Version version, CancellationToken token = default)
    {
        RequireWrite();
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireWrite(); SecureRoot();
            if (version <= (Assembly.GetEntryAssembly()?.GetName().Version ?? new Version()))
                throw new InvalidOperationException("Choose a newer release; use the explicit rollback action for a previous version.");
            return await StageCoreAsync(zip, package, version, Root, AppContext.BaseDirectory, token, RequireWrite).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }
    private static async Task<PreparedUpdate> StageCoreAsync(string zip, PortableUpdatePackage package, Version version,
        string root, string previousDirectory, CancellationToken token, Action requireWrite)
    {
        requireWrite(); token.ThrowIfCancellationRequested();
        if (!ProjectReleaseFeed.IsPackageUrl(package.Url) || !ProjectReleaseFeed.IsSha256(package.Sha256) || package.Size <= 0 || package.Size > ProjectReleaseFeed.MaximumPackageSize)
            throw new ArgumentException("Invalid release package metadata.");
        if (!ProjectReleaseFeed.TryParseVersion(new Uri(package.Url).Segments[^2].TrimEnd('/'), out var packageVersion) || packageVersion != version)
            throw new ArgumentException("Release tag does not match the requested installation version.");
        // Lock the verified input against modification/replacement for the entire extraction.
        await using var input = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != package.Size || !Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Package changed after download verification.");
        PortableUpdateDownloader.VerifyArchive(zip);
        input.Position = 0;
        var directory = Path.Combine(Path.GetFullPath(root), "version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, true);
            foreach (var entry in archive.Entries)
            {
                requireWrite(); token.ThrowIfCancellationRequested();
                var parts = entry.FullName.Replace('\\', '/').TrimEnd('/').Split('/');
                if (parts.Any(p => p.Length == 0 || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(p.Split('.')[0], StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Archive has an invalid Windows path.");
                var relative = Path.Combine(parts);
                if (parts[0].Equals("Previous", StringComparison.OrdinalIgnoreCase) ||
                    (relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !relative.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) && !relative.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Release contains unexpected settings or transaction data.");
                var target = Path.GetFullPath(Path.Combine(directory, relative));
                if (!target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new SecurityException("Archive escaped installation directory.");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var content = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await content.CopyToAsync(output, token);
            }
            if (AssemblyName.GetAssemblyName(Path.Combine(directory, "Project Legion.dll")).Version != version)
                throw new InvalidDataException("Downloaded assembly version does not match the release.");
            if (package.SelfContained && !new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "PresentationFramework.dll" }.All(name => File.Exists(Path.Combine(directory, name))))
                throw new InvalidDataException("Self-contained update is missing its Desktop Runtime.");
            var targetFiles = Manifest(directory);
            var previous = Path.Combine(directory, "Previous");
            Directory.CreateDirectory(previous);
            var backupCount = 0; long backupBytes = 0;
            foreach (var file in BackupFiles(previousDirectory))
            {
                requireWrite(); token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(previousDirectory, file);
                var first = relative.Split(Path.DirectorySeparatorChar)[0];
                if (first.Equals("Previous", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).StartsWith("ProjectLegion.", StringComparison.Ordinal)) continue;
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (!(extension is ".dll" or ".exe" or ".dat" or ".bin" || (first == "Branding" && extension is ".svg" or ".png" or ".ico" or ".md") ||
                    file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file) is "LICENSE" or "NOTICE" or "RELEASE-NOTES.md")) continue;
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Previous installation contains a reparse point.");
                NoLinks(Path.GetDirectoryName(file)!);
                backupBytes = checked(backupBytes + new FileInfo(file).Length);
                if (++backupCount > 10000 || backupBytes > 2L * 1024 * 1024 * 1024) throw new IOException("Previous installation exceeds backup limits.");
                var destination = Path.Combine(previous, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, false);
                File.SetAttributes(destination, FileAttributes.Normal);
            }
            if (!File.Exists(Path.Combine(previous, AppExe))) throw new InvalidDataException("Previous executable is unavailable for rollback.");
            using var parent = Process.GetCurrentProcess();
            var transaction = new Transaction { Origin = Environment.ProcessPath!, Target = Path.Combine(directory, AppExe), Previous = Path.Combine(previous, AppExe),
                ParentPid = parent.Id, ParentStart = parent.StartTime.ToUniversalTime().Ticks, TargetFiles = targetFiles, PreviousFiles = Manifest(previous) };
            requireWrite(); token.ThrowIfCancellationRequested();
            var ticket = Path.Combine(directory, "install.json");
            Save(ticket, transaction);
            return new PreparedUpdate(ticket, transaction.Target, version);
        }
        catch
        {
            // Only the fresh, non-shared transaction directory can be removed.
            if (directory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
            throw;
        }
    }
    private static IEnumerable<string> BackupFiles(string root)
    {
        NoLinks(root);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Previous installation contains a reparse point.");
                if (item is DirectoryInfo)
                {
                    if (!item.Name.Equals("Previous", StringComparison.OrdinalIgnoreCase)) pending.Push(item.FullName);
                }
                else yield return item.FullName;
            }
    }
    private static Dictionary<string, string> Manifest(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            bytes = checked(bytes + new FileInfo(file).Length);
            if (result.Count >= 10000 || bytes > 2L * 1024 * 1024 * 1024) throw new IOException("Installation exceeds backup limits.");
            result.Add(Path.GetRelativePath(directory, file), Hash(file));
        }
        return result;
    }
    private static void VerifyFiles(string executable, Dictionary<string, string> files)
    {
        if (files.Count == 0 || !files.ContainsKey(AppExe)) throw new InvalidDataException("Executable manifest missing.");
        var root = Path.GetDirectoryName(executable)!;
        NoLinks(root);
        foreach (var pair in files)
        {
            var file = Path.GetFullPath(Path.Combine(root, pair.Key));
            NoLinks(Path.GetDirectoryName(file)!);
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) || Hash(file) != pair.Value)
                throw new SecurityException("Installed files failed verification.");
        }
    }
    private static Transaction Load(string ticket)
    {
        ticket = Path.GetFullPath(ticket);
        var relative = Path.GetRelativePath(Root, ticket).Split(Path.DirectorySeparatorChar);
        if (relative.Length != 2 || !relative[0].StartsWith("version-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(relative[0][8..], "N", out _) || relative[1] != "install.json") throw new SecurityException("Unrecognized update transaction.");
        NoLinks(Path.GetDirectoryName(ticket)!);
        if (File.GetAttributes(ticket).HasFlag(FileAttributes.ReparsePoint)) throw new SecurityException("Transaction cannot be a reparse point.");
        var transaction = JsonSerializer.Deserialize<Transaction>(File.ReadAllText(ticket)) ?? throw new InvalidDataException("Invalid transaction.");
        var directory = Path.GetDirectoryName(ticket)!;
        if (transaction.Schema != 1 || transaction.Target != Path.Combine(directory, AppExe) || transaction.Previous != Path.Combine(directory, "Previous", AppExe))
            throw new SecurityException("Transaction executable paths are invalid.");
        return transaction;
    }
    private static void Save(string ticket, Transaction transaction) => File.WriteAllText(ticket, JsonSerializer.Serialize(transaction, JsonOptions));
    public static async Task StartRestartAsync(PreparedUpdate prepared, bool rollback = false)
    {
        RequireWrite();
        var transaction = Load(prepared.Ticket);
        if (rollback && !Path.GetFullPath(Environment.ProcessPath!).Equals(transaction.Target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Rollback does not belong to this installation.");
        if (!rollback && !Path.GetFullPath(Environment.ProcessPath!).Equals(transaction.Origin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Update does not belong to this process.");
        VerifyFiles(rollback ? transaction.Previous : transaction.Target, rollback ? transaction.PreviousFiles : transaction.TargetFiles);
        using var parent = Process.GetCurrentProcess();
        transaction.Origin = Environment.ProcessPath!;
        transaction.ParentPid = parent.Id; transaction.ParentStart = parent.StartTime.ToUniversalTime().Ticks;
        transaction.State = rollback ? "RollbackRequested" : "InstallRequested";
        Save(prepared.Ticket, transaction);
        transaction.WorkerToken = Guid.NewGuid().ToString("N");
        Save(prepared.Ticket, transaction);
        var ready = prepared.Ticket + ".worker-" + transaction.WorkerToken;
        VerifyFiles(transaction.Previous, transaction.PreviousFiles);
        var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(transaction.Previous)!, "Project Legion UpdateHost.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(prepared.Ticket); start.ArgumentList.Add(ready);
        using var worker = Process.Start(start) ?? throw new IOException("Update worker did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(ready))
        {
            if (worker.HasExited) throw new IOException("Update worker exited before accepting the request. The current app remains open.");
            await Task.Delay(100, timeout.Token);
        }
    }
    public static PreparedUpdate? GetRollbackCandidate()
    {
        var ticket = Path.Combine(AppContext.BaseDirectory, "install.json");
        if (Folders.IsReadOnlySession || !File.Exists(ticket)) return null;
        try
        {
            var transaction = Load(ticket);
            VerifyFiles(transaction.Previous, transaction.PreviousFiles);
            return new PreparedUpdate(ticket, transaction.Previous, AssemblyName.GetAssemblyName(Path.Combine(Path.GetDirectoryName(transaction.Previous)!, "Project Legion.dll")).Version!);
        }
        catch { return null; }
    }
    public static void ConfirmStartup(string[] args)
    {
        if (Folders.IsReadOnlySession) return;
        var ticket = args.FirstOrDefault(arg => arg.StartsWith("--update-session=", StringComparison.Ordinal))?[17..];
        if (ticket is null) return;
        try
        {
            var transaction = Load(ticket);
            var current = Path.GetFullPath(Environment.ProcessPath!);
            if (!current.Equals(transaction.Target, StringComparison.OrdinalIgnoreCase) && !current.Equals(transaction.Previous, StringComparison.OrdinalIgnoreCase)) return;
            File.WriteAllText(ticket + ".healthy-" + Environment.ProcessId, current);
        }
        catch (Exception ex) { Log.Instance.Trace($"Update startup confirmation failed.", ex); }
    }
    public static async Task RunWorkerAsync(string ticket, string ready)
    {
        RequireWrite();
        using var owner = new Semaphore(1, 1, @"Local\ProjectLegion_UpdateHandoff");
        var acquired = owner.WaitOne(0);
        if (!acquired) throw new InvalidOperationException("Another update handoff is active.");
        try
        {
        var transaction = Load(ticket);
        if (!Guid.TryParseExact(transaction.WorkerToken, "N", out _) || ready != ticket + ".worker-" + transaction.WorkerToken) throw new SecurityException("Invalid worker handshake.");
        using var parent = Process.GetProcessById(transaction.ParentPid);
        if (parent.StartTime.ToUniversalTime().Ticks != transaction.ParentStart || !string.Equals(parent.MainModule?.FileName, transaction.Origin, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Update origin no longer matches the requesting process.");
        var rollback = transaction.State == "RollbackRequested";
        if (!rollback && transaction.State != "InstallRequested") throw new InvalidOperationException("No explicit install request.");
        var destination = rollback ? transaction.Previous : transaction.Target;
        VerifyFiles(destination, rollback ? transaction.PreviousFiles : transaction.TargetFiles);
        File.WriteAllText(ready, "accepted");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await parent.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { transaction.State = "Cancelled: requesting app did not exit"; Save(ticket, transaction); return; }
        var previous = rollback ? transaction.Target : transaction.Previous;
        try
        {
            transaction.State = await ExecuteRestartPlanAsync(destination, previous,
                path => LaunchAndObserveAsync(path, ticket),
                path => VerifyFiles(path, path == transaction.Target ? transaction.TargetFiles : transaction.PreviousFiles),
                path => RedirectShortcuts(transaction.Origin, path));
        }
        catch (Exception ex)
        {
            transaction.State = "Restart failed: " + ex.Message;
            // Never force-close a process or repeatedly retry execution.
        }
        Save(ticket, transaction);
        }
        finally { owner.Release(); }
    }
    private static async Task<string> ExecuteRestartPlanAsync(string destination, string previous,
        Func<string, Task<string>> launch, Action<string> verify, Action<string> commit)
    {
        string outcome;
        try { verify(destination); outcome = await launch(destination); }
        catch { outcome = "LaunchFailed"; } // No process was started by a failed verification.
        if (outcome == "Healthy")
        {
            try { commit(destination); return outcome; }
            catch { return "Healthy; shortcuts need attention"; }
        }
        if (outcome is not ("LaunchFailed" or "ExitedBeforeReady")) return outcome;
        verify(previous);
        var restored = await launch(previous); // At most one fallback; never retry a possibly running app.
        if (restored != "Healthy") return "PreviousVersionNeedsAttention";
        try { commit(previous); return "PreviousVersionRestored"; }
        catch { return "PreviousVersionRestored; shortcuts need attention"; }
    }
    private static async Task<string> LaunchAndObserveAsync(string executable, string ticket)
    {
        Process process;
        try { process = Launch(executable, ticket); }
        catch { return "LaunchFailed"; }
        using (process)
        {
            try { return await ObserveStartupAsync(() => process.HasExited, () => File.Exists(ticket + ".healthy-" + process.Id), CancellationToken.None); }
            catch { return "Pending: process observation unavailable; previous version retained"; }
        }
    }
    private static Process Launch(string executable, string ticket)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("--update-session=" + ticket);
        return Process.Start(start) ?? throw new IOException("Windows did not start the selected version.");
    }
    private static async Task<string> ObserveStartupAsync(Func<bool> exited, Func<bool> healthy, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            while (true)
            {
                if (exited()) return "ExitedBeforeReady";
                if (healthy()) return "Healthy";
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) { return "Pending: startup not confirmed; previous version retained"; }
    }
    private static void RedirectShortcuts(string oldExe, string newExe)
    {
        // Existing matching shortcuts only; do not create startup tasks or change their arguments.
        var directories = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar") };
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) throw new IOException("Shortcut service is unavailable; open the installed version directly.");
        var failed = false;
        dynamic shell = Activator.CreateInstance(type)!;
        foreach (var directory in directories.Where(Directory.Exists))
            foreach (var link in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly))
                try
                {
                    dynamic shortcut = shell.CreateShortcut(link);
                    if (!string.Equals((string)shortcut.TargetPath, oldExe, StringComparison.OrdinalIgnoreCase)) continue;
                    shortcut.TargetPath = newExe; shortcut.WorkingDirectory = Path.GetDirectoryName(newExe);
                    shortcut.IconLocation = newExe + ",0"; shortcut.Save();
                }
                catch { failed = true; }
        if (failed) throw new IOException("Some shortcuts could not be updated; both installations remain available.");
    }
}
