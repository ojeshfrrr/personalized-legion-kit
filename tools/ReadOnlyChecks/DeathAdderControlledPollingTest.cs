using System;
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

internal static class DeathAdderControlledPollingTest
{
    // Only reached through the explicitly approved diagnostic argument. No DPI writer is exposed.
    public static async Task RunAsync(string metadataEvidence, string evidenceDirectory, bool allowRunningGame = false)
    {
        Directory.CreateDirectory(evidenceDirectory);
        // Atomic one-shot sentinel: even an interrupted run cannot be retried in this directory.
        using (new FileStream(Path.Combine(evidenceDirectory, "one-shot.started"), FileMode.CreateNew, FileAccess.Write)) { }
        var resultPath = Path.Combine(evidenceDirectory, "controlled-result.json");
        var phase = "preconditions";
        var restored = false;
        Snapshot? baseline = null, changed = null, final = null;
        BoundedTransport? bounded = null;
        string? profileBefore = null;
        var realProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProjectBranding.AppDataFolderName);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "authorization-context.json"),
                JsonSerializer.Serialize(new { RunningGameExplicitlyApproved = allowRunningGame,
                    Scope = "One 2000-to-8000-to-2000 Hz test; no DPI writes or retries", Utc = DateTime.UtcNow }));
            RequireNoGame(allowRunningGame);
            if (!Directory.Exists(realProfile) || !File.Exists(Path.Combine(realProfile, ".upstream-settings-migrated")))
                throw new InvalidOperationException("Existing configuration/migration marker required; no initialization or migration is permitted.");
            profileBefore = Fingerprint(realProfile);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "configuration-before.json"), profileBefore);
            AppFlags.Initialize([]); // No application, IoC, settings objects, listeners or services.
            if (AppFlags.Instance.ReadOnly || AppFlags.Instance.UiPreview || Folders.IsReadOnlySession)
                throw new InvalidOperationException("Session flags prohibit the approved write; stopping.");
            RequireUnchangedConfiguration();
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataEvidence));
            var paths = metadata.RootElement.GetProperty("interfaces").EnumerateArray()
                .Where(d => d.TryGetProperty("openSucceeded", out var success) && success.GetBoolean() &&
                            WindowsMouseFeatureReadTransport.IsConfigurationPath(d.GetProperty("path").GetString()))
                .Select(d => d.GetProperty("path").GetString()!).ToArray();
            if (paths.Length != 1) throw new InvalidDataException("Expected exactly one verified configuration interface.");
            phase = "open";
            await using var native = WindowsMouseFeatureReadTransport.Open(paths[0]);
            bounded = new BoundedTransport(native, Path.Combine(evidenceDirectory, "controlled-reports.jsonl"), allowRunningGame);
            var polling = new DeathAdderV3PollingClient(bounded, TimeSpan.FromSeconds(5));
            byte transaction = 10;
            async Task<Snapshot> Capture()
            {
                bounded.Phase = phase;
                var rate = await polling.ReadAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var activeReply = await bounded.ExchangeAsync(DeathAdderV3Protocol.CreateReadRequest(
                    DeathAdderV3ReadCommand.CurrentDpi, ++transaction, MouseDpiScope.ActiveOverride), deadline.Token);
                var active = DeathAdderV3Protocol.ParseCurrentDpi(activeReply, transaction, MouseDpiScope.ActiveOverride);
                var profileReply = await bounded.ExchangeAsync(DeathAdderV3Protocol.CreateReadRequest(
                    DeathAdderV3ReadCommand.CurrentDpi, ++transaction), deadline.Token);
                var profile = DeathAdderV3Protocol.ParseCurrentDpi(profileReply, transaction);
                var stageReply = await bounded.ExchangeAsync(DeathAdderV3Protocol.CreateReadRequest(
                    DeathAdderV3ReadCommand.DpiStages, ++transaction), deadline.Token);
                var stages = DeathAdderV3Protocol.ParseDpiStages(stageReply, transaction);
                return new Snapshot((int)rate, active, profile, stages, Payload(profileReply), Payload(stageReply));
            }
            phase = "fresh-baseline";
            baseline = await Capture();
            await Save("baseline.json", baseline);
            if (baseline.PollingHz != 2000)
                throw new InvalidOperationException("Fresh rate is not 2000 Hz; the approved conditional test must stop before any write.");
            if (baseline.Active != new MouseDpi(1600, 1600))
                throw new InvalidOperationException("Active DPI differs from the previously verified 1600/1600 baseline; stopping before writes.");
            DeathAdderV3PollingProtocol.ValidateSetRequest(DeathAdderV3PollingProtocol.CreateSetRequest(MousePollingRate.Hz8000, 1));
            DeathAdderV3PollingProtocol.ValidateSetRequest(DeathAdderV3PollingProtocol.CreateSetRequest(MousePollingRate.Hz2000, 2));
            RequireUnchangedConfiguration();
            HardwareRuntime.EnableControls();
            phase = bounded.Phase = "approved-change-8000";
            var change = await polling.ApplyAsync(MousePollingRate.Hz8000);
            await Save("change-result.json", change);
            if (change.Status != MousePollingApplyStatus.Confirmed || change.Previous != MousePollingRate.Hz2000 ||
                change.Actual != MousePollingRate.Hz8000 || bounded.WriteCalls != 1)
                throw new InvalidOperationException("Change acknowledgement/readback ambiguous; no further commands permitted. " + change.Message);
            phase = "verify-8000-and-unrelated-state";
            changed = await Capture();
            await Save("changed-snapshot.json", changed);
            if (changed.PollingHz != 8000 || !SameUnrelatedState(baseline, changed))
                throw new InvalidOperationException("Rate/DPI/profile/stages verification failed; no further commands permitted.");
            RequireUnchangedConfiguration();
            bounded.RestorePermitted = true;
            phase = bounded.Phase = "approved-restore-2000";
            var restore = await polling.ApplyAsync(MousePollingRate.Hz2000);
            await Save("restore-result.json", restore);
            if (restore.Status != MousePollingApplyStatus.Confirmed || restore.Previous != MousePollingRate.Hz8000 ||
                restore.Actual != MousePollingRate.Hz2000 || bounded.WriteCalls != 2)
                throw new InvalidOperationException("Restoration acknowledgement/readback ambiguous; stopping. " + restore.Message);
            phase = "verify-restored-and-unrelated-state";
            final = await Capture();
            await Save("restored-snapshot.json", final);
            if (final.PollingHz != baseline.PollingHz || !SameUnrelatedState(baseline, final))
                throw new InvalidOperationException("Restoration or unchanged unrelated state could not be verified.");
            restored = true;
            var profileAfter = Fingerprint(realProfile);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "configuration-after.json"), profileAfter);
            RequireUnchangedConfiguration();
            var result = new { Success = true, RestorationVerified = true, ConfigurationUnchanged = true,
                SettingWriteCalls = bounded.WriteCalls, DpiWrites = 0, Baseline = baseline, Changed = changed, Restored = final,
                AutomaticRetries = 0, RunningGameExplicitlyApproved = allowRunningGame,
                EffectiveEventFrequencyMeasured = false, Utc = DateTime.UtcNow };
            await Save("controlled-result.json", result);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            // Intentionally no retry or automatic restore following an ambiguous result.
            var profileAfter = Directory.Exists(realProfile) ? Fingerprint(realProfile) : null;
            if (profileAfter is not null) await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "configuration-after.json"), profileAfter);
            var result = new { Success = false, RestorationVerified = restored, Phase = phase,
                SettingWriteCalls = bounded?.WriteCalls ?? 0, DpiWrites = 0, Baseline = baseline, Changed = changed, Restored = final,
                ErrorType = ex.GetType().Name, Error = ex.Message, NativeError = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode,
                ConfigurationUnchanged = profileBefore is null ? (bool?)null : profileBefore == profileAfter,
                AutomaticRetries = 0, RunningGameExplicitlyApproved = allowRunningGame, Stopped = true, Utc = DateTime.UtcNow };
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result));
            Environment.ExitCode = 1;
        }
        void RequireUnchangedConfiguration()
        {
            if (Fingerprint(realProfile) != profileBefore) throw new InvalidOperationException("Configuration fingerprint changed; stopping.");
        }
        Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(evidenceDirectory, name), JsonSerializer.Serialize(value));
    }

    private static void RequireNoGame(bool allowRunningGame)
    {
        var games = Process.GetProcessesByName("VALORANT-Win64-Shipping");
        try { if (games.Length != 0 && !allowRunningGame) throw new InvalidOperationException("Valorant is running; no polling-rate write is permitted."); }
        finally { foreach (var game in games) game.Dispose(); }
    }
    private sealed record Snapshot(int PollingHz, MouseDpi Active, MouseDpi Profile, MouseDpiStages Stages,
        string DpiProfilePayload, string StagePayload);
    private static string Payload(byte[] report) => Convert.ToHexString(report.AsSpan(9, report[6]));
    private static bool SameUnrelatedState(Snapshot before, Snapshot after) => before.Active == after.Active &&
        before.Profile == after.Profile && before.DpiProfilePayload == after.DpiProfilePayload && before.StagePayload == after.StagePayload;
    private static string Fingerprint(string directory) => JsonSerializer.Serialize(Directory.EnumerateFileSystemEntries(directory)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => Directory.Exists(p)
            ? new { Name = Path.GetFileName(p), Directory = true, Bytes = 0L, Modified = 0L, Hash = "" }
            : new { Name = Path.GetFileName(p), Directory = false, Bytes = new FileInfo(p).Length,
                Modified = File.GetLastWriteTimeUtc(p).Ticks, Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }).ToArray());

    private sealed class BoundedTransport(IMouseFeaturePollingTransport native, string journal, bool allowRunningGame) : IMouseFeaturePollingTransport
    {
        public int WriteCalls { get; private set; }
        public bool RestorePermitted;
        public string Phase = "baseline";
        private MousePollingRate? _lastRate;
        public async Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> report, CancellationToken token)
        {
            var response = await native.ExchangeAsync(report, token);
            await Record(report, response, false);
            if (report.Span[7] == 0 && report.Span[8] == 0xC0)
                _lastRate = DeathAdderV3PollingProtocol.ParseReadback(response, report.Span[2]);
            return response;
        }
        public async Task<byte[]> ExchangePollingRateAsync(ReadOnlyMemory<byte> report, CancellationToken token)
        {
            RequireNoGame(allowRunningGame);
            var expected = WriteCalls switch
            {
                0 when Phase == "approved-change-8000" && _lastRate == MousePollingRate.Hz2000 => MousePollingRate.Hz8000,
                1 when RestorePermitted && Phase == "approved-restore-2000" && _lastRate == MousePollingRate.Hz8000 => MousePollingRate.Hz2000,
                _ => throw new InvalidOperationException("Approved two-write budget or fresh-rate precondition not satisfied."),
            };
            var exact = DeathAdderV3PollingProtocol.CreateSetRequest(expected, report.Span[2]);
            if (!report.Span.SequenceEqual(exact)) throw new InvalidOperationException("Write differs from the exact approved profile-1 polling operation.");
            WriteCalls++;
            await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(new { Utc = DateTime.UtcNow, Phase,
                WriteAttempt = WriteCalls, Request = Convert.ToHexString(report.Span) }) + Environment.NewLine);
            var response = await native.ExchangePollingRateAsync(report, token);
            await Record(report, response, true);
            return response;
        }
        private Task Record(ReadOnlyMemory<byte> request, byte[] response, bool writing) => File.AppendAllTextAsync(journal,
            JsonSerializer.Serialize(new { Utc = DateTime.UtcNow, Phase, Writing = writing,
                Request = Convert.ToHexString(request.Span), Response = Convert.ToHexString(response) }) + Environment.NewLine);
    }
}
