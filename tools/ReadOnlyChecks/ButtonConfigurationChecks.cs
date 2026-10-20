using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.WPF.Presentation;

internal static class ButtonConfigurationChecks
{
    // Every device response in this file is synthetic. No native handle or app is opened.
    public static async Task RunAsync(Action<bool, string> check)
    {
        var getter = DeathAdderV3ButtonProtocol.CreateRead(1, 4, 1);
        check(getter[6] == 10 && getter[7] == 2 && getter[8] == 0x8C && getter[9] == 1 && getter[10] == 4 &&
              getter[11] == 0 && getter[89] == 0x81, "assignment getter matches exact product-178 ten-byte 02/8C vector");
        WindowsMouseFeatureReadTransport.ValidateGetter(getter);
        var list = DeathAdderV3ButtonProtocol.CreateProfileQuery(MouseProfileQuery.Ids, 1);
        check(list[6] == 80 && list[7] == 5 && list[8] == 0x81 && list[89] == 0xD4,
            "profile ID query uses the exact device-bank wire vector rather than invented profile counts");
        // Captured 1532:00B2 behavior: list limit 5, one actual ID, returned payload length 2.
        // Synapse slices its actual returned data; it does not pad the response to request size.
        var shortIds = Reply(list); shortIds[6] = 2; shortIds[9] = 5; shortIds[10] = 1; Rechecksum(shortIds);
        check(DeathAdderV3ButtonProtocol.ParseIds(shortIds, 1).SequenceEqual(new byte[] { 1 }),
            "captured short profile-ID response uses genuine returned payload length without fabricating missing IDs");
        var duplicateIds = Reply(list); duplicateIds[9] = 2; duplicateIds[10] = duplicateIds[11] = 1; Rechecksum(duplicateIds);
        Reject<InvalidDataException>(() => DeathAdderV3ButtonProtocol.ParseIds(duplicateIds, 1));
        check(true, "variable-length bank parsing still rejects duplicate device IDs");
        foreach (var action in DeathAdderV3ButtonProtocol.Actions)
        {
            var setter = DeathAdderV3ButtonProtocol.CreateSet(4, action, 2);
            DeathAdderV3ButtonProtocol.ValidateSetter(setter);
            var reply = Reply(setter);
            DeathAdderV3ButtonProtocol.ValidateAcknowledgement(reply, 4, action, 2);
            check(DeathAdderV3ButtonProtocol.ParseAssignment(reply, 1, 4, 2, true).KnownAction == action && setter.Skip(19).Take(70).All(b => b == 0),
                $"{action} has canonical exact-model function data and no unrelated payload");
        }
        Reject<InvalidOperationException>(() => DeathAdderV3ButtonProtocol.CreateSet(1, MouseButtonAction.Disabled, 1));
        Reject<ArgumentOutOfRangeException>(() => DeathAdderV3ButtonProtocol.CreateSet(255, MouseButtonAction.Back, 1));
        Reject<ArgumentOutOfRangeException>(() => DeathAdderV3ButtonProtocol.CreateSet(4, (MouseButtonAction)999, 1));
        check(true, "primary left click is protected and unsupported target/functions are rejected before transport");
        foreach (var index in new[] { 0, 6, 7, 8, 9, 11, 12, 13, 15, 19, 89, 90 })
        {
            var modified = DeathAdderV3ButtonProtocol.CreateSet(4, MouseButtonAction.Forward, 1); modified[index] ^= 1;
            try { DeathAdderV3ButtonProtocol.ValidateSetter(modified); throw new Exception("Accepted modified setter."); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        var unverifiedProfileWrite = (byte[])list.Clone(); unverifiedProfileWrite[8] = 4;
        Reject<ArgumentOutOfRangeException>(() => WindowsMouseFeatureReadTransport.ValidateGetter(unverifiedProfileWrite));
        check(true, "native allowlists reject stored-bank/hypershift/extra-data setters and profile activation commands");
        var valid = Reply(getter); valid[12] = 200; valid[13] = 3; Rechecksum(valid);
        var unknown = DeathAdderV3ButtonProtocol.ParseAssignment(valid, 1, 4, 1);
        check(unknown.KnownAction is null && unknown.Description.Contains("Unsupported", StringComparison.Ordinal),
            "unknown assignment functions remain genuine raw readbacks without guessed UI assignments");
        foreach (var index in new[] { 0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 89, 90 })
        {
            var bad = Reply(getter); bad[index] ^= 1;
            Reject<InvalidDataException>(() => DeathAdderV3ButtonProtocol.ParseAssignment(bad, 1, 4, 1));
        }
        valid = Reply(getter); valid[13] = 6; Rechecksum(valid);
        Reject<InvalidDataException>(() => DeathAdderV3ButtonProtocol.ParseAssignment(valid, 1, 4, 1));
        valid = Reply(getter); valid[1] = 5;
        Reject<NotSupportedException>(() => DeathAdderV3ButtonProtocol.ParseAssignment(valid, 1, 4, 1));
        check(true, "assignment parser validates transaction/framing/checksum/bank/button/layer/status and bounded data size");

        var fake = new FakeTransport(); var client = Client(fake);
        var banks = await client.ReadProfilesAsync();
        check(banks.ReportedCapacity == 7 && banks.Ids.SequenceEqual(new byte[] { 1, 3 }) && fake.Writes == 0,
            "profile discovery exposes actual synthetic capacity and IDs without assuming four onboard slots");
        var bank = await client.ReadBankAsync(1);
        check(bank.Assignments.Count == 8 && bank.Assignments.Single(a => a.Button == 4).KnownAction == MouseButtonAction.Back && fake.Writes == 0,
            "button-bank inspection retrieves all exact-model normal-layer assignments without writes");
        await RejectAsync<NotSupportedException>(() => client.ReadBankAsync(2));
        var expected = bank.Assignments.Single(a => a.Button == 4);
        fake.Commands.Clear();
        var result = await client.ApplyAsync(expected, MouseButtonAction.Forward);
        check(result.Success && result.Actual?.KnownAction == MouseButtonAction.Forward && fake.Writes == 1 &&
              fake.Commands.SequenceEqual(new[] { "05/8A", "05/80", "05/81", "02/8C", "02/0C", "02/8C" }),
            "button Apply succeeds only after bank discovery, fresh matching original, validated acknowledgement and readback");
        result = await client.ApplyAsync(result.Actual!, MouseButtonAction.Forward);
        check(result.Status == MouseButtonApplyStatus.Unchanged && !result.WriteAttempted && fake.Writes == 1,
            "already-matching button assignment sends no redundant setter");
        result = await client.ApplyAsync(expected, MouseButtonAction.DoubleClick);
        check(result.Status == MouseButtonApplyStatus.PreconditionChanged && !result.WriteAttempted && fake.Writes == 1,
            "external mapping changes invalidate the UI baseline instead of silently overwriting them");
        await RejectAsync<NotSupportedException>(() => client.ApplyAsync(expected with { Profile = 3 }, MouseButtonAction.Forward));
        await RejectAsync<NotSupportedException>(() => client.ApplyAsync(unknown, MouseButtonAction.Forward));
        check(fake.Writes == 1, "unproven stored-bank writes and unrestorable unknown assignments never reach the transport");
        fake = new FakeTransport { BadAck = true }; result = await Client(fake).ApplyAsync(expected, MouseButtonAction.Forward);
        check(!result.Success && result.Status == MouseButtonApplyStatus.Uncertain && result.Actual?.KnownAction == MouseButtonAction.Forward && fake.Writes == 1,
            "ambiguous button acknowledgement allows one safe getter, never another setter or false success");
        fake = new FakeTransport { IgnoreWrite = true }; result = await Client(fake).ApplyAsync(expected, MouseButtonAction.Forward);
        check(result.Status == MouseButtonApplyStatus.ReadbackMismatch && !result.Success,
            "acknowledged button command with mismatching fresh readback is not success");
        fake = new FakeTransport { BadAck = true, FailReadback = true }; result = await Client(fake).ApplyAsync(expected, MouseButtonAction.Forward);
        check(result.Actual is null && !result.Success && fake.Writes == 1 && fake.Commands.Count == 6,
            "failed button recovery keeps final state unavailable and is not retried");
        fake = new FakeTransport { WriteError = new Win32Exception(5) }; result = await Client(fake).ApplyAsync(expected, MouseButtonAction.Forward);
        check(result.Actual is null && fake.Writes == 1 && fake.Commands.Count == 5,
            "denied button access stops without further device queries");
        fake = new FakeTransport { BlockWrite = true }; result = await Client(fake, 40).ApplyAsync(expected, MouseButtonAction.Forward);
        check(!result.Success && result.Actual?.KnownAction == MouseButtonAction.Forward && fake.Writes == 1 && fake.Active == 0,
            "button timeout drains native-contract work and uses a fresh bounded read deadline");
        using (var cancelled = new CancellationTokenSource())
        {
            fake = new FakeTransport { OnWrite = cancelled.Cancel };
            result = await Client(fake).ApplyAsync(expected, MouseButtonAction.Forward, cancelled.Token);
            check(result.Actual is null && fake.Commands.Count == 5 && fake.Active == 0,
                "button cancellation suppresses optional recovery and leaves no worker");
        }
        fake = new FakeTransport { UnsupportedProfiles = true };
        await RejectAsync<NotSupportedException>(() => Client(fake).ReadProfilesAsync());
        check(fake.Commands.Count == 1 && fake.Writes == 0, "unsupported profile discovery stops without speculative fallback commands");
        fake = new FakeTransport { Delay = TimeSpan.FromMilliseconds(2) }; client = Client(fake);
        var apply = client.ApplyAsync(expected, MouseButtonAction.Forward); var read = client.ReadProfilesAsync();
        await Task.WhenAll(apply, read);
        check(fake.Peak == 1 && fake.Commands.Take(6).Last() == "02/8C", "assignment batches serialize with profile reads without overlapping workers");
        await RejectAsync<InvalidOperationException>(() => new MouseControlService().ApplyButtonAsync(expected, MouseButtonAction.Forward));
        check(true, "production button service rejects read-only writes before device discovery");
        await PresentationChecks(check);
        await DeathAdderControlledButtonTest.RunGuardChecksAsync(check);
    }

    private static async Task PresentationChecks(Action<bool, string> check)
    {
        var fake = new FakeService(); var page = new MouseButtonsSession(fake, () => true);
        page.Activate();
        check(fake.Calls == 0 && page.Current is null, "button page construction/navigation performs no getters or setters and invents no mappings");
        await page.ReadProfilesAsync(); var calls = fake.Calls;
        page.SelectBank(3);
        check(fake.Calls == calls && page.Current is null && page.SelectedBank == 3,
            "bank selection only changes the inspection target, with no device activation or I/O");
        await page.ReadAssignmentsAsync();
        check(page.Current?.Profile == 3 && !page.CanApply(4), "device banks other than bank 1 remain genuinely readable and write-locked");
        page.SelectBank(1); await page.ReadAssignmentsAsync();
        check(!page.CanApply(1) && page.CanApply(4), "button UI protects primary click and only enables known bank-1 assignments");
        await page.ApplyAsync(4, MouseButtonAction.Forward);
        check(fake.Writes == 1 && page.Current?.Assignments.Single(a => a.Button == 4).KnownAction == MouseButtonAction.Forward,
            "explicit button Apply connects the presentation to backend-confirmed assignment state");
        page.Deactivate(); check(page.Current is null && page.LastConfirmed is not null && !page.CanApply(4),
            "button navigation retires current mappings and retains explicitly historical confirmation");
        fake = new FakeService(); page = new(fake, () => false); page.Activate(); await page.ReadProfilesAsync(); await page.ReadAssignmentsAsync();
        await RejectAsync<InvalidOperationException>(() => page.ApplyAsync(4, MouseButtonAction.Forward));
        check(fake.Writes == 0 && page.Current is not null, "read-only button UI keeps inspection available while rejecting Apply");
        page.Deactivate();
        var source = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Pages", "MousePage.Buttons.cs"));
        var draftStart = source.IndexOf("private void ButtonDraft_Changed", StringComparison.Ordinal);
        var applyStart = source.IndexOf("private async void ApplyButtonAssignment_Click", StringComparison.Ordinal);
        var draft = source[draftStart..applyStart];
        var queryGuard = source[source.IndexOf("private bool IsButtonQuerySource", StringComparison.Ordinal)..];
        check(draft.Contains("RefreshButtonAccess()", StringComparison.Ordinal) && !draft.Contains("ApplyAsync(", StringComparison.Ordinal) &&
              !draft.Contains("ReadButton", StringComparison.Ordinal) && !queryGuard.Contains("_applyButtonAssignment", StringComparison.Ordinal) &&
              !queryGuard.Contains("_buttonActionSelector", StringComparison.Ordinal),
            "button selectors never invoke hardware operations and read-only query routing excludes Apply and assignment editing");
        fake = new FakeService { BlockRead = true }; page = new(fake, () => true); page.Activate();
        var pending = page.ReadProfilesAsync(); page.Deactivate(); page.Activate(); fake.BlockRead = false;
        await pending.WaitAsync(TimeSpan.FromSeconds(2)); await page.ReadProfilesAsync();
        check(!page.IsBusy && page.Banks is not null && fake.Writes == 0, "rapid button-page reentry drains cancellation and resumes explicit reads without duplicate work");
        page.Deactivate();
    }
    private static DeathAdderV3ButtonClient Client(FakeTransport fake, int milliseconds = 3000) => new(fake, TimeSpan.FromMilliseconds(milliseconds));
    private static byte[] Reply(byte[] request) { var reply = (byte[])request.Clone(); reply[1] = 2; return reply; }
    private static void Rechecksum(byte[] frame) => frame[89] = (byte)frame.Skip(3).Take(86).Aggregate(0, (a, b) => a ^ b);
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static MouseButtonAssignment Assignment(byte profile, byte button, MouseButtonAction action)
    {
        var frame = DeathAdderV3ButtonProtocol.CreateSet(button == 1 ? (byte)4 : button, action, 1);
        var reply = Reply(frame); reply[8] = 0x8C; reply[9] = profile; reply[10] = button; Rechecksum(reply);
        return DeathAdderV3ButtonProtocol.ParseAssignment(reply, profile, button, 1);
    }
    private sealed class FakeTransport : IMouseFeatureButtonTransport
    {
        public int Writes, Active, Peak;
        public bool BadAck, IgnoreWrite, FailReadback, BlockWrite, UnsupportedProfiles;
        public TimeSpan Delay;
        public Exception? WriteError;
        public Action? OnWrite;
        public MouseButtonAction Current = MouseButtonAction.Back;
        public List<string> Commands { get; } = [];
        public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> report, CancellationToken token) => Exchange(report.ToArray(), false, token);
        public Task<byte[]> ExchangeButtonAssignmentAsync(ReadOnlyMemory<byte> report, CancellationToken token) => Exchange(report.ToArray(), true, token);
        private async Task<byte[]> Exchange(byte[] request, bool writing, CancellationToken token)
        {
            var active = Interlocked.Increment(ref Active); Peak = Math.Max(Peak, active); Commands.Add($"{request[7]:X2}/{request[8]:X2}");
            try
            {
                await Task.Delay(Delay, token); var reply = Reply(request);
                if (writing)
                {
                    DeathAdderV3ButtonProtocol.ValidateSetter(request); Writes++; OnWrite?.Invoke(); token.ThrowIfCancellationRequested();
                    if (WriteError is not null) throw WriteError;
                    if (!IgnoreWrite) Current = DeathAdderV3ButtonProtocol.DecodeAction(request[12], request[13], Convert.ToHexString(request.AsSpan(14, 5)))!.Value;
                    if (BlockWrite) await Task.Delay(Timeout.Infinite, token);
                    if (BadAck) reply[12] = 200;
                }
                else
                {
                    WindowsMouseFeatureReadTransport.ValidateGetter(request);
                    if (request[7] == 5)
                    {
                        if (UnsupportedProfiles) reply[1] = 5;
                        else if (request[8] == 0x8A) reply[9] = 7;
                        else if (request[8] == 0x80) reply[9] = 2;
                        else { reply[9] = 2; reply[10] = 1; reply[11] = 3; }
                    }
                    else
                    {
                        if (FailReadback && Writes > 0) throw new IOException("Synthetic mapping read failure");
                        var value = Assignment(request[9], request[10], request[10] == 1 ? MouseButtonAction.LeftClick : Current);
                        reply[12] = value.Function; reply[13] = value.DataSize; Convert.FromHexString(value.DataHex).CopyTo(reply, 14);
                    }
                }
                Rechecksum(reply); return reply;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private sealed class FakeService : IMouseButtonService
    {
        public int Calls, Writes; public bool BlockRead;
        public Task<MouseProfileBanks> ReadProfileBanksAsync(CancellationToken token = default)
        {
            Calls++; return Read(token);
            async Task<MouseProfileBanks> Read(CancellationToken cancellation)
            {
                if (BlockRead) await Task.Delay(Timeout.Infinite, cancellation);
                return new(7, Array.AsReadOnly(new byte[] { 1, 3 }));
            }
        }
        public Task<MouseButtonBank> ReadButtonBankAsync(byte profile, CancellationToken token = default)
        {
            Calls++; return Task.FromResult(new MouseButtonBank(profile, Array.AsReadOnly(DeathAdderV3ButtonProtocol.Buttons.Select(b =>
                Assignment(profile, b.Id, b.Id == 1 ? MouseButtonAction.LeftClick : MouseButtonAction.Back)).ToArray())));
        }
        public Task<MouseButtonApplyResult> ApplyButtonAsync(MouseButtonAssignment expected, MouseButtonAction action, CancellationToken token = default)
        {
            Calls++; Writes++; return Task.FromResult(new MouseButtonApplyResult(MouseButtonApplyStatus.Confirmed, expected,
                Assignment(expected.Profile, expected.Button, action), true, "Synthetic validated assignment readback."));
        }
    }
}
