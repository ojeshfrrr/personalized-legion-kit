using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.Lib.Utils;

internal static class DeathAdderControlledButtonTest
{
    // Explicitly approved Back -> Forward -> Back test. No DPI/poll/profile setter interfaces.
    public static async Task RunAsync(string metadataEvidence, string evidenceDirectory)
    {
        Directory.CreateDirectory(evidenceDirectory);
        using (new FileStream(Path.Combine(evidenceDirectory, "one-shot.started"), FileMode.CreateNew, FileAccess.Write)) { }
        var phase = "preconditions";
        var restored = false;
        Snapshot? baseline = null, changed = null, final = null;
        BoundedTransport? bounded = null;
        string? configurationBefore = null;
        var realProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProjectBranding.AppDataFolderName);
        try
        {
            if (!Directory.Exists(realProfile) || !File.Exists(Path.Combine(realProfile, ".upstream-settings-migrated")))
                throw new InvalidOperationException("Existing configuration and migration marker required; no initialization/migration permitted.");
            configurationBefore = Fingerprint(realProfile);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "configuration-before.json"), configurationBefore);
            AppFlags.Initialize([]); // No App, IoC, settings objects, listeners or startup automation.
            if (AppFlags.Instance.ReadOnly || AppFlags.Instance.UiPreview || Folders.IsReadOnlySession)
                throw new InvalidOperationException("Existing session flags prohibit the approved write.");
            RequireConfigurationUnchanged();
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataEvidence));
            var paths = metadata.RootElement.GetProperty("interfaces").EnumerateArray()
                .Where(d => d.TryGetProperty("openSucceeded", out var ok) && ok.GetBoolean() &&
                    WindowsMouseFeatureReadTransport.IsConfigurationPath(d.GetProperty("path").GetString()))
                .Select(d => d.GetProperty("path").GetString()!).ToArray();
            if (paths.Length != 1) throw new InvalidDataException("Expected exactly one verified configuration interface.");
            phase = "open";
            await using var native = WindowsMouseFeatureReadTransport.Open(paths[0]);
            bounded = new BoundedTransport(native, Path.Combine(evidenceDirectory, "controlled-reports.jsonl"));
            var buttons = new DeathAdderV3ButtonClient(bounded, TimeSpan.FromSeconds(5));
            byte transaction = 18;
            async Task<Snapshot> Capture()
            {
                bounded.Phase = phase;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var banks = await buttons.ReadProfilesAsync(deadline.Token);
                var bank = await buttons.ReadBankAsync(1, deadline.Token);
                var activeReply = await bounded.ExchangeAsync(DeathAdderV3Protocol.CreateReadRequest(
                    DeathAdderV3ReadCommand.CurrentDpi, ++transaction, MouseDpiScope.ActiveOverride), deadline.Token);
                var active = DeathAdderV3Protocol.ParseCurrentDpi(activeReply, transaction, MouseDpiScope.ActiveOverride);
                var pollingReply = await bounded.ExchangeAsync(DeathAdderV3PollingProtocol.CreateReadRequest(++transaction), deadline.Token);
                var polling = DeathAdderV3PollingProtocol.ParseReadback(pollingReply, transaction);
                var profileReply = await bounded.ExchangeAsync(DeathAdderV3Protocol.CreateReadRequest(
                    DeathAdderV3ReadCommand.CurrentDpi, ++transaction), deadline.Token);
                var profile = DeathAdderV3Protocol.ParseCurrentDpi(profileReply, transaction);
                var stageReply = await bounded.ExchangeAsync(DeathAdderV3Protocol.CreateReadRequest(
                    DeathAdderV3ReadCommand.DpiStages, ++transaction), deadline.Token);
                var stages = DeathAdderV3Protocol.ParseDpiStages(stageReply, transaction);
                return new(banks, bank, active, (int)polling, profile, stages, Payload(profileReply), Payload(stageReply));
            }
            phase = "fresh-baseline";
            baseline = await Capture(); await Save("baseline.json", baseline);
            var original = baseline.Bank.Assignments.Single(a => a.Button == 4);
            if (original != new MouseButtonAssignment(1, 4, 0, 1, 1, "0400000000"))
                throw new InvalidOperationException("Side button 4 no longer has canonical Back. No write permitted.");
            DeathAdderV3ButtonClient.ValidateApply(original, MouseButtonAction.Forward);
            bounded.Original = original;
            RequireConfigurationUnchanged();
            HardwareRuntime.EnableControls();
            phase = bounded.Phase = "approved-change-forward";
            var change = await buttons.ApplyAsync(original, MouseButtonAction.Forward);
            await Save("change-result.json", change);
            var expectedForward = original with { DataHex = "0500000000" };
            if (change.Status != MouseButtonApplyStatus.Confirmed || change.Previous != original ||
                change.Actual != expectedForward || bounded.WriteCalls != 1)
                throw new InvalidOperationException("Change acknowledgement/readback ambiguous; no further commands permitted. " + change.Message);
            phase = "verify-forward-and-unrelated-state";
            changed = await Capture(); await Save("changed-snapshot.json", changed);
            if (!Matches(baseline, changed, expectedForward))
                throw new InvalidOperationException("Unexpected mapping/DPI/polling/profile/stage change; no further commands permitted.");
            RequireConfigurationUnchanged();
            bounded.RestorePermitted = true;
            phase = bounded.Phase = "approved-restore-back";
            var restore = await buttons.ApplyAsync(expectedForward, MouseButtonAction.Back);
            await Save("restore-result.json", restore);
            if (restore.Status != MouseButtonApplyStatus.Confirmed || restore.Previous != expectedForward ||
                restore.Actual != original || bounded.WriteCalls != 2)
                throw new InvalidOperationException("Restoration acknowledgement/readback ambiguous; stopping. " + restore.Message);
            phase = "verify-restored-and-unrelated-state";
            final = await Capture(); await Save("restored-snapshot.json", final);
            if (!Matches(baseline, final, original))
                throw new InvalidOperationException("Full restoration or unchanged unrelated state could not be verified.");
            restored = true;
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "configuration-after.json"), Fingerprint(realProfile));
            RequireConfigurationUnchanged();
            var result = new { Success = true, RestorationVerified = true, ConfigurationUnchanged = true,
                AssignmentWriteCalls = bounded.WriteCalls, DpiWrites = 0, PollingWrites = 0, ProfileWrites = 0,
                Baseline = baseline, Changed = changed, Restored = final, AutomaticRetries = 0,
                InputInjected = false, OnboardPersistenceVerified = false, Utc = DateTime.UtcNow };
            await Save("controlled-result.json", result); Console.WriteLine(JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            // No retry or automatic restoration after an ambiguous result.
            string? after = null, fingerprintError = null;
            try
            {
                if (Directory.Exists(realProfile))
                {
                    after = Fingerprint(realProfile);
                    await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "configuration-after.json"), after);
                }
            }
            catch (Exception secondary) { fingerprintError = secondary.Message; }
            var result = new { Success = false, RestorationVerified = restored, Phase = phase,
                AssignmentWriteCalls = bounded?.WriteCalls ?? 0, DpiWrites = 0, PollingWrites = 0, ProfileWrites = 0,
                Baseline = baseline, Changed = changed, Restored = final,
                ErrorType = ex.GetType().Name, Error = ex.Message, NativeError = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode,
                ConfigurationUnchanged = configurationBefore is null || after is null ? (bool?)null : configurationBefore == after,
                FingerprintError = fingerprintError, AutomaticRetries = 0, Stopped = true, Utc = DateTime.UtcNow };
            await Save("controlled-result.json", result); Console.WriteLine(JsonSerializer.Serialize(result)); Environment.ExitCode = 1;
        }
        void RequireConfigurationUnchanged()
        {
            if (Fingerprint(realProfile) != configurationBefore) throw new InvalidOperationException("Project Legion configuration fingerprint changed; stopping.");
        }
        Task Save(string name, object data) => File.WriteAllTextAsync(Path.Combine(evidenceDirectory, name), JsonSerializer.Serialize(data));
    }

    private sealed record Snapshot(MouseProfileBanks Banks, MouseButtonBank Bank, MouseDpi ActiveDpi, int PollingHz,
        MouseDpi DpiProfile, MouseDpiStages Stages, string DpiProfilePayload, string StagePayload);
    private static string Payload(byte[] reply) => Convert.ToHexString(reply.AsSpan(9, reply[6]));
    private static bool Matches(Snapshot baseline, Snapshot actual, MouseButtonAssignment target) =>
        actual.Banks.ReportedCapacity == baseline.Banks.ReportedCapacity && actual.Banks.Ids.SequenceEqual(baseline.Banks.Ids) &&
        actual.Bank.Profile == baseline.Bank.Profile && actual.Bank.Assignments.Count == baseline.Bank.Assignments.Count &&
        actual.Bank.Assignments.All(a => a == (a.Button == 4 ? target : baseline.Bank.Assignments.Single(b => b.Button == a.Button))) &&
        actual.ActiveDpi == baseline.ActiveDpi && actual.PollingHz == baseline.PollingHz && actual.DpiProfile == baseline.DpiProfile &&
        actual.DpiProfilePayload == baseline.DpiProfilePayload && actual.StagePayload == baseline.StagePayload;
    private static string Fingerprint(string directory) => JsonSerializer.Serialize(Directory.EnumerateFileSystemEntries(directory)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => Directory.Exists(p)
            ? new { Name = Path.GetFileName(p), Directory = true, Bytes = 0L, Modified = 0L, Hash = "" }
            : new { Name = Path.GetFileName(p), Directory = false, Bytes = new FileInfo(p).Length,
                Modified = File.GetLastWriteTimeUtc(p).Ticks, Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }).ToArray());

    internal static async Task RunGuardChecksAsync(Action<bool, string> check)
    {
        var native = new SyntheticTransport();
        var journal = Path.Combine(Environment.CurrentDirectory, "BuildProjectLegion", "Phase11", "side4-guard-" + Guid.NewGuid().ToString("N") + ".synthetic.jsonl");
        var guarded = new BoundedTransport(native, journal)
        {
            Original = new(1, 4, 0, 1, 1, "0400000000"), Phase = "approved-change-forward",
        };
        await guarded.ExchangeAsync(DeathAdderV3ButtonProtocol.CreateRead(1, 4, 1), default);
        await Reject(() => guarded.ExchangeButtonAssignmentAsync(DeathAdderV3ButtonProtocol.CreateSet(5, MouseButtonAction.Forward, 2), default));
        check(native.Writes == 0 && guarded.WriteCalls == 0, "physical-test guard rejects any target outside the approved side button 4");
        await guarded.ExchangeButtonAssignmentAsync(DeathAdderV3ButtonProtocol.CreateSet(4, MouseButtonAction.Forward, 2), default);
        await guarded.ExchangeAsync(DeathAdderV3ButtonProtocol.CreateRead(1, 4, 3), default);
        guarded.Phase = "approved-restore-back";
        await Reject(() => guarded.ExchangeButtonAssignmentAsync(DeathAdderV3ButtonProtocol.CreateSet(4, MouseButtonAction.Back, 4), default));
        check(native.Writes == 1, "physical-test guard requires explicit successful snapshot verification before restoration");
        guarded.RestorePermitted = true;
        await guarded.ExchangeButtonAssignmentAsync(DeathAdderV3ButtonProtocol.CreateSet(4, MouseButtonAction.Back, 4), default);
        await Reject(() => guarded.ExchangeButtonAssignmentAsync(DeathAdderV3ButtonProtocol.CreateSet(4, MouseButtonAction.Back, 5), default));
        check(native.Writes == 2 && guarded.WriteCalls == 2, "physical-test guard permits exactly the approved change/restoration and rejects every third write");
        static async Task Reject(Func<Task<byte[]>> action)
        {
            try { await action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Synthetic guarded transport accepted an unapproved operation.");
        }
    }
    private sealed class SyntheticTransport : IMouseFeatureButtonTransport
    {
        public int Writes;
        private MouseButtonAction _current = MouseButtonAction.Back;
        public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> report, CancellationToken token) => Task.FromResult(Reply(report, _current));
        public Task<byte[]> ExchangeButtonAssignmentAsync(ReadOnlyMemory<byte> report, CancellationToken token)
        {
            Writes++; _current = DeathAdderV3ButtonProtocol.DecodeAction(report.Span[12], report.Span[13], Convert.ToHexString(report.Span.Slice(14, 5)))!.Value;
            return Task.FromResult(Reply(report, _current));
        }
        private static byte[] Reply(ReadOnlyMemory<byte> request, MouseButtonAction current)
        {
            var response = DeathAdderV3ButtonProtocol.CreateSet(4, current, request.Span[2]);
            response[1] = 2; response[8] = request.Span[8];
            response[89] = (byte)response.Skip(3).Take(86).Aggregate(0, (a, b) => a ^ b); return response;
        }
    }

    private sealed class BoundedTransport(IMouseFeatureButtonTransport native, string journal) : IMouseFeatureButtonTransport
    {
        public int WriteCalls { get; private set; }
        public string Phase = "baseline";
        public bool RestorePermitted;
        public MouseButtonAssignment? Original;
        private MouseButtonAssignment? _last;
        public async Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken token)
        {
            var response = await native.ExchangeAsync(request, token);
            await Record(request, response, false);
            if (request.Span[7] == 2 && request.Span[8] == 0x8C && request.Span[9] == 1 && request.Span[10] == 4)
                _last = DeathAdderV3ButtonProtocol.ParseAssignment(response, 1, 4, request.Span[2]);
            return response;
        }
        public async Task<byte[]> ExchangeButtonAssignmentAsync(ReadOnlyMemory<byte> request, CancellationToken token)
        {
            var forward = Original is null ? null : Original with { DataHex = "0500000000" };
            var action = WriteCalls switch
            {
                0 when Original is not null && Original == new MouseButtonAssignment(1, 4, 0, 1, 1, "0400000000") &&
                    _last == Original && Phase == "approved-change-forward" => MouseButtonAction.Forward,
                1 when RestorePermitted && _last == forward && Phase == "approved-restore-back" => MouseButtonAction.Back,
                _ => throw new InvalidOperationException("Approved two-write budget, phase or fresh original precondition failed."),
            };
            if (request.Length != 91 || !request.Span.SequenceEqual(DeathAdderV3ButtonProtocol.CreateSet(4, action, request.Span[2])))
                throw new InvalidOperationException("Only the exact approved side-4 normal-layer operation is permitted.");
            WriteCalls++;
            await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(new { Utc = DateTime.UtcNow, Phase,
                WriteAttempt = WriteCalls, Request = Convert.ToHexString(request.Span) }) + Environment.NewLine);
            var response = await native.ExchangeButtonAssignmentAsync(request, token);
            await Record(request, response, true); return response;
        }
        private Task Record(ReadOnlyMemory<byte> request, byte[] response, bool writing) => File.AppendAllTextAsync(journal,
            JsonSerializer.Serialize(new { Utc = DateTime.UtcNow, Phase, Writing = writing,
                Request = Convert.ToHexString(request.Span), Response = Convert.ToHexString(response) }) + Environment.NewLine);
    }
}
