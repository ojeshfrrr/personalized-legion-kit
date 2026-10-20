using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.Lib.Utils;

internal static class DeathAdderControlledDpiTest
{
    // Explicitly approved one-shot diagnostic; never called by normal assertions/startup.
    public static async Task RunAsync(string metadataEvidence, string evidenceDirectory)
    {
        var resultPath = Path.Combine(evidenceDirectory, "controlled-result.json");
        if (File.Exists(resultPath)) throw new InvalidOperationException("This one-shot result already exists; refusing to repeat the test.");
        Directory.CreateDirectory(evidenceDirectory);
        var phase = "preconditions";
        var restored = false;
        Snapshot? baseline = null, changed = null, final = null;
        BoundedTransport? bounded = null;
        string? profileBefore = null;
        var realProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProjectBranding.AppDataFolderName);
        var journal = Path.Combine(evidenceDirectory, "controlled-reports.jsonl");
        try
        {
            var games = Process.GetProcessesByName("VALORANT-Win64-Shipping");
            try { if (games.Length != 0) throw new InvalidOperationException("Valorant is running; no test is permitted."); }
            finally { foreach (var game in games) game.Dispose(); }
            // Normal flags would consult Folders.AppData. Require the existing profile
            // and migration marker so initialization cannot create/migrate user files.
            if (!Directory.Exists(realProfile) || !File.Exists(Path.Combine(realProfile, ".upstream-settings-migrated")))
                throw new InvalidOperationException("Normal diagnostic initialization cannot be proven free of profile initialization writes.");
            profileBefore = Fingerprint(realProfile);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "configuration-before.json"), profileBefore);
            AppFlags.Initialize([]); // No application/IoC/services are constructed.
            if (AppFlags.Instance.ReadOnly || AppFlags.Instance.UiPreview || Folders.IsReadOnlySession)
                throw new InvalidOperationException("An existing read-only flag prevents this explicitly approved write session.");
            if (Fingerprint(realProfile) != profileBefore) throw new InvalidOperationException("Configuration changed during initialization; stopping.");
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataEvidence));
            var paths = metadata.RootElement.GetProperty("interfaces").EnumerateArray()
                .Where(d => d.TryGetProperty("openSucceeded", out var success) && success.GetBoolean() &&
                            WindowsMouseFeatureReadTransport.IsConfigurationPath(d.GetProperty("path").GetString()))
                .Select(d => d.GetProperty("path").GetString()!).ToArray();
            if (paths.Length != 1) throw new InvalidDataException("Expected one verified configuration interface.");
            phase = "open";
            await using var native = WindowsMouseFeatureReadTransport.Open(paths[0]);
            bounded = new BoundedTransport(native, journal);
            var active = new DeathAdderV3DpiClient(bounded, TimeSpan.FromSeconds(3));
            byte transaction = 10;
            async Task<Snapshot> Capture()
            {
                bounded.Phase = phase;
                var dpi = await active.ReadActiveDpiAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var profileReply = await bounded.ExchangeAsync(DeathAdderV3Protocol.CreateReadRequest(
                    DeathAdderV3ReadCommand.CurrentDpi, ++transaction), deadline.Token);
                var profile = DeathAdderV3Protocol.ParseCurrentDpi(profileReply, transaction);
                var stageReply = await bounded.ExchangeAsync(DeathAdderV3Protocol.CreateReadRequest(
                    DeathAdderV3ReadCommand.DpiStages, ++transaction), deadline.Token);
                var stages = DeathAdderV3Protocol.ParseDpiStages(stageReply, transaction);
                return new Snapshot(dpi, profile, stages, Payload(profileReply), Payload(stageReply));
            }
            phase = "fresh-baseline";
            baseline = await Capture();
            await Save("controlled-baseline.json", baseline);
            if (baseline.Active != new MouseDpi(1600, 1600)) throw new InvalidOperationException("Active DPI is not exactly 1600/1600; no write permitted.");
            // Both frames are constructed/validated before enabling writes. Only these
            // two active override targets can pass the bounded transport below.
            WindowsMouseFeatureReadTransport.ValidateActiveDpiRequest(DeathAdderV3Protocol.CreateActiveDpiRequest(1650, 1650, 1));
            WindowsMouseFeatureReadTransport.ValidateActiveDpiRequest(DeathAdderV3Protocol.CreateActiveDpiRequest(1600, 1600, 2));
            if (Fingerprint(realProfile) != profileBefore) throw new InvalidOperationException("Configuration changed before the write; stopping.");
            HardwareRuntime.EnableControls();
            phase = bounded.Phase = "approved-change-1650";
            var change = await active.ApplyActiveDpiAsync(1650, 1650);
            await Save("change-result.json", change);
            if (change.Status != MouseDpiApplyStatus.Confirmed || change.Previous != baseline.Active ||
                change.Actual != new MouseDpi(1650, 1650) || bounded.WriteCalls != 1)
                throw new InvalidOperationException("Change acknowledgement/readback is ambiguous; no further mouse commands permitted. " + change.Message);
            phase = "verify-1650-profile-and-stages";
            changed = await Capture();
            await Save("changed-snapshot.json", changed);
            if (changed.Active != new MouseDpi(1650, 1650) || !SameStoredState(baseline, changed))
                throw new InvalidOperationException("Active/profile/stage verification failed after change; no further mouse commands permitted.");
            phase = bounded.Phase = "approved-restore-1600";
            bounded.RestorePermitted = true;
            var restore = await active.ApplyActiveDpiAsync(1600, 1600);
            await Save("restore-result.json", restore);
            if (restore.Status != MouseDpiApplyStatus.Confirmed || restore.Previous != new MouseDpi(1650, 1650) ||
                restore.Actual != baseline.Active || bounded.WriteCalls != 2)
                throw new InvalidOperationException("Restoration acknowledgement/readback is ambiguous; stopping. " + restore.Message);
            phase = "verify-restored-profile-and-stages";
            final = await Capture();
            await Save("restored-snapshot.json", final);
            if (final.Active != baseline.Active || !SameStoredState(baseline, final))
                throw new InvalidOperationException("Restoration or unchanged profile/stages could not be verified.");
            restored = true;
            var profileAfter = Fingerprint(realProfile);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "configuration-after.json"), profileAfter);
            if (profileAfter != profileBefore) throw new InvalidOperationException("User configuration fingerprint changed; no complete success claim.");
            var result = new { Success = true, RestorationVerified = restored, ConfigurationUnchanged = true,
                SettingWriteCalls = bounded.WriteCalls, Baseline = baseline, Changed = changed, Restored = final,
                AutomaticRetries = 0, Utc = DateTime.UtcNow };
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            var profileAfter = Directory.Exists(realProfile) ? Fingerprint(realProfile) : null;
            if (profileAfter is not null) await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "configuration-after.json"), profileAfter);
            var result = new { Success = false, RestorationVerified = restored, Phase = phase,
                SettingWriteCalls = bounded?.WriteCalls ?? 0, Baseline = baseline, Changed = changed, Restored = final,
                ErrorType = ex.GetType().Name, Error = ex.Message, NativeError = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode,
                ConfigurationUnchanged = profileBefore is not null && profileAfter == profileBefore,
                AutomaticRetries = 0, Stopped = true, Utc = DateTime.UtcNow };
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result));
            Environment.ExitCode = 1;
        }
        Task Save(string name, object data) => File.WriteAllTextAsync(Path.Combine(evidenceDirectory, name), JsonSerializer.Serialize(data));
    }

    private sealed record Snapshot(MouseDpi Active, MouseDpi Profile, MouseDpiStages Stages, string ProfilePayload, string StagePayload);
    private static string Payload(byte[] report) => Convert.ToHexString(report.AsSpan(9, report[6]));
    private static bool SameStoredState(Snapshot before, Snapshot after) => before.Profile == after.Profile &&
        before.ProfilePayload == after.ProfilePayload && before.StagePayload == after.StagePayload;
    private static string Fingerprint(string directory) => JsonSerializer.Serialize(Directory.EnumerateFileSystemEntries(directory)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => Directory.Exists(p)
            ? new { Name = Path.GetFileName(p), Directory = true, Bytes = 0L, Modified = 0L, Hash = "" }
            : new { Name = Path.GetFileName(p), Directory = false, Bytes = new FileInfo(p).Length,
                Modified = File.GetLastWriteTimeUtc(p).Ticks, Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }).ToArray());

    private sealed class BoundedTransport(WindowsMouseFeatureReadTransport native, string journal) : IMouseFeatureDpiTransport
    {
        public int WriteCalls { get; private set; }
        public bool RestorePermitted;
        public string Phase = "baseline";
        private MouseDpi? _lastActive;
        public async Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> report, CancellationToken token)
        {
            var response = await native.ExchangeAsync(report, token);
            await Record(report, response, false);
            if (report.Span[8] == 0x85 && report.Span[9] == 0)
                _lastActive = DeathAdderV3Protocol.ParseCurrentDpi(response, report.Span[2], MouseDpiScope.ActiveOverride);
            return response;
        }
        public async Task<byte[]> ExchangeActiveDpiAsync(ReadOnlyMemory<byte> report, CancellationToken token)
        {
            var expected = WriteCalls switch
            {
                0 when Phase == "approved-change-1650" && _lastActive == new MouseDpi(1600, 1600) => 1650,
                1 when RestorePermitted && Phase == "approved-restore-1600" && _lastActive == new MouseDpi(1650, 1650) => 1600,
                _ => throw new InvalidOperationException("The approved two-write budget or fresh-DPI precondition was not satisfied."),
            };
            var exact = DeathAdderV3Protocol.CreateActiveDpiRequest(expected, expected, report.Span[2]);
            if (!report.Span.SequenceEqual(exact)) throw new InvalidOperationException("Write differs from the exact approved operation.");
            WriteCalls++;
            await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(new { Utc = DateTime.UtcNow, Phase,
                WriteAttempt = WriteCalls, Request = Convert.ToHexString(report.Span) }) + Environment.NewLine);
            var response = await native.ExchangeActiveDpiAsync(report, token);
            await Record(report, response, true);
            return response;
        }
        private Task Record(ReadOnlyMemory<byte> request, byte[] response, bool writing) => File.AppendAllTextAsync(journal,
            JsonSerializer.Serialize(new { Utc = DateTime.UtcNow, Phase, Writing = writing,
                Request = Convert.ToHexString(request.Span), Response = Convert.ToHexString(response) }) + Environment.NewLine);
    }
}
