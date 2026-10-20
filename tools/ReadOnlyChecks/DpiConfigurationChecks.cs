using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;

internal static class DpiConfigurationChecks
{
    // All transport state below is synthetic. No device discovery or native I/O occurs.
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(DeathAdderV3Protocol.ValidateDpi(100, 30000) == new MouseDpi(100, 30000),
            "exact-model DPI boundaries support independent X/Y axes");
        foreach (var value in new[] { -1, 0, 99, 101, 1599, 30001, 65536 })
        {
            Reject<ArgumentOutOfRangeException>(() => DeathAdderV3Protocol.ValidateDpi(value, 1600));
            Reject<ArgumentOutOfRangeException>(() => DeathAdderV3Protocol.ValidateDpi(1600, value));
        }
        check(true, "invalid DPI ranges and increments are rejected before transport use");
        var request = DeathAdderV3Protocol.CreateActiveDpiRequest(1650, 1650, 1);
        check(request.Length == 91 && request[2] == 1 && request[6] == 7 && request[7] == 4 &&
              request[8] == 5 && request[9] == 0 && request[10] == 6 && request[11] == 0x72 &&
              request[12] == 6 && request[13] == 0x72 && request[14] == 0 && request[15] == 0 && request[89] == 6,
            "active-DPI setter matches the explicit class-0 04/05 1650-DPI wire vector");
        WindowsMouseFeatureReadTransport.ValidateActiveDpiRequest(request);
        foreach (var index in new[] { 0, 6, 8, 9, 14, 16, 89, 90 })
        {
            var modified = (byte[])request.Clone();
            modified[index] ^= 1;
            Reject<ArgumentException>(() => WindowsMouseFeatureReadTransport.ValidateActiveDpiRequest(modified));
        }
        check(true, "native write allowlist rejects stage commands, profile scope, modified framing and nonzero Z");
        var activeRead = DeathAdderV3Protocol.CreateReadRequest(DeathAdderV3ReadCommand.CurrentDpi, 1, MouseDpiScope.ActiveOverride);
        WindowsMouseFeatureReadTransport.ValidateGetter(activeRead);
        var ack = Reply(request, 1650, 1650);
        DeathAdderV3Protocol.ValidateActiveDpiAcknowledgement(ack, 1, new MouseDpi(1650, 1650));
        Reject<InvalidDataException>(() => DeathAdderV3Protocol.ValidateActiveDpiAcknowledgement(ack, 2, new MouseDpi(1650, 1650)));
        Reject<InvalidDataException>(() => DeathAdderV3Protocol.ValidateActiveDpiAcknowledgement(ack, 1, new MouseDpi(1600, 1650)));
        ack[1] = 5;
        Reject<NotSupportedException>(() => DeathAdderV3Protocol.ValidateActiveDpiAcknowledgement(ack, 1, new MouseDpi(1650, 1650)));
        check(true, "setter acknowledgement validates success, transaction and requested X/Y rather than send completion");

        var fake = new FakeMouse();
        var client = Client(fake);
        var result = await client.ApplyActiveDpiAsync(1650, 1700);
        check(result.Success && result.Status == MouseDpiApplyStatus.Confirmed && result.Actual == new MouseDpi(1650, 1700) &&
              result.Previous == new MouseDpi(1600, 1600) && fake.Commands.SequenceEqual(new byte[] { 0x85, 5, 0x85 }),
            "synthetic DPI change succeeds only after pre-read, acknowledged write, and fresh provider readback");
        var unchanged = await client.ApplyActiveDpiAsync(1650, 1700);
        check(unchanged.Success && !unchanged.WriteAttempted && fake.Writes == 1,
            "already-matching active DPI performs no redundant write");
        var before = fake.Commands.Count;
        await RejectAsync<ArgumentOutOfRangeException>(() => client.ApplyActiveDpiAsync(1601, 1600));
        check(fake.Commands.Count == before, "invalid apply never enters the fake transport");

        fake = new FakeMouse { IgnoreWrite = true };
        result = await Client(fake).ApplyActiveDpiAsync(1650, 1650);
        check(!result.Success && result.Status == MouseDpiApplyStatus.ReadbackMismatch && result.Actual == new MouseDpi(1600, 1600),
            "successful acknowledgement with mismatching readback is an explicit failure");
        fake = new FakeMouse { BadAcknowledgement = true };
        result = await Client(fake).ApplyActiveDpiAsync(1650, 1650);
        check(!result.Success && result.Status == MouseDpiApplyStatus.Uncertain && result.Actual == new MouseDpi(1650, 1650) &&
              result.WriteAttempted && fake.Writes == 1 && fake.Commands.SequenceEqual(new byte[] { 0x85, 5, 0x85 }),
            "malformed DPI acknowledgement gets one safe readback while keeping write outcome unconfirmed");
        fake = new FakeMouse { FailFirstRead = true };
        result = await Client(fake).ApplyActiveDpiAsync(1650, 1650);
        check(result.Status == MouseDpiApplyStatus.Failed && !result.WriteAttempted && fake.Writes == 0,
            "pre-read disconnection prevents the write");
        fake = new FakeMouse { DisconnectOnWrite = true };
        result = await Client(fake).ApplyActiveDpiAsync(1650, 1650);
        check(result.Status == MouseDpiApplyStatus.Uncertain && result.WriteAttempted,
            "disconnection after entering a write does not claim unchanged state");
        fake = new FakeMouse { FailReadback = true };
        result = await Client(fake).ApplyActiveDpiAsync(1650, 1650);
        check(result.Status == MouseDpiApplyStatus.Uncertain && result.Actual is null && !result.Success,
            "failed readback never returns a stale DPI as a current reading");

        fake = new FakeMouse { BlockFirstRead = true };
        result = await Client(fake, TimeSpan.FromMilliseconds(40)).ApplyActiveDpiAsync(1650, 1650);
        check(result.Status == MouseDpiApplyStatus.Failed && !result.WriteAttempted && fake.Active == 0,
            "pre-write deadline cancels the fake operation and leaves no write or residual worker");
        fake = new FakeMouse { BlockAfterWrite = true };
        result = await Client(fake, TimeSpan.FromMilliseconds(40)).ApplyActiveDpiAsync(1650, 1650);
        check(result.Status == MouseDpiApplyStatus.Uncertain && result.Actual == new MouseDpi(1650, 1650) &&
              fake.Writes == 1 && fake.Active == 0 && fake.Commands.Count == 3,
            "post-write DPI deadline drains work and permits one bounded getter without retrying the write");
        fake = new FakeMouse { BadAcknowledgement = true, FailReadback = true };
        result = await Client(fake).ApplyActiveDpiAsync(1650, 1650);
        check(!result.Success && result.Actual is null && fake.Writes == 1 && fake.Commands.Count == 3,
            "failed DPI recovery read remains unknown and is not retried");
        fake = new FakeMouse { WriteError = new System.ComponentModel.Win32Exception(5) };
        result = await Client(fake).ApplyActiveDpiAsync(1650, 1650);
        check(result.Actual is null && fake.Commands.Count == 2 && fake.Writes == 1,
            "denied DPI writes stop without retrying device access");
        using (var cancelDuringWrite = new CancellationTokenSource())
        {
            fake = new FakeMouse { OnWrite = cancelDuringWrite.Cancel };
            result = await Client(fake).ApplyActiveDpiAsync(1650, 1650, cancelDuringWrite.Token);
            check(!result.Success && result.Actual is null && fake.Commands.Count == 2 && fake.Active == 0,
                "navigation cancellation after a DPI write suppresses optional recovery reads and drains the operation");
        }
        fake = new FakeMouse();
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            result = await Client(fake).ApplyActiveDpiAsync(1650, 1650, cancellation.Token);
            check(result.Status == MouseDpiApplyStatus.Failed && fake.Commands.Count == 0,
                "pre-cancelled apply performs no transport work");
        }
        fake = new FakeMouse { Delay = TimeSpan.FromMilliseconds(3) };
        client = Client(fake);
        var first = client.ApplyActiveDpiAsync(1650, 1650);
        var second = client.ApplyActiveDpiAsync(1700, 1700);
        var read = client.ReadActiveDpiAsync();
        await Task.WhenAll(first, second);
        await read;
        check(first.Result.Success && second.Result.Success && second.Result.Previous == new MouseDpi(1650, 1650) &&
              fake.Peak == 1 && fake.Commands.Take(6).SequenceEqual(new byte[] { 0x85, 5, 0x85, 0x85, 5, 0x85 }),
            "competing operations cannot interleave a write/readback batch or duplicate transport work");

        fake = new FakeMouse { BlockFirstRead = true };
        client = Client(fake);
        using (var activeCancellation = new CancellationTokenSource())
        using (var queuedCancellation = new CancellationTokenSource())
        {
            var active = client.ApplyActiveDpiAsync(1650, 1650, activeCancellation.Token);
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var queued = client.ApplyActiveDpiAsync(1700, 1700, queuedCancellation.Token);
            queuedCancellation.Cancel();
            var cancelled = await queued;
            check(!cancelled.WriteAttempted && fake.Commands.Count == 1 && fake.Active == 1,
                "queued apply cancellation does not release the active transaction's gate");
            activeCancellation.Cancel();
            await active;
        }
        fake.BlockFirstRead = false;
        result = await client.ApplyActiveDpiAsync(1650, 1650);
        check(result.Success && fake.Active == 0, "cancellation releases the client for a later valid operation");

        // The current assertion profile remains read-only. The real service must
        // reject before discovery; tests never enable global hardware controls.
        await RejectAsync<InvalidOperationException>(() => new MouseControlService().ApplyActiveDpiAsync(1650, 1650));
        check(true, "production mouse service rejects a read-only write before any discovery or native access");
    }

    private static DeathAdderV3DpiClient Client(FakeMouse fake, TimeSpan? timeout = null) => new(fake, timeout ?? TimeSpan.FromSeconds(3));

    private static byte[] Reply(byte[] request, int x, int y)
    {
        var response = new byte[91];
        response[1] = 2; response[2] = request[2]; response[6] = 7; response[7] = 4; response[8] = request[8];
        response[9] = request[9]; response[10] = (byte)(x >> 8); response[11] = (byte)x;
        response[12] = (byte)(y >> 8); response[13] = (byte)y;
        response[89] = (byte)response.Skip(3).Take(86).Aggregate(0, (a, b) => a ^ b);
        return response;
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeMouse : IMouseFeatureDpiTransport
    {
        public int X = 1600, Y = 1600, Writes, Active, Peak;
        public bool IgnoreWrite, BadAcknowledgement, FailFirstRead, FailReadback, DisconnectOnWrite, BlockFirstRead, BlockAfterWrite;
        public TimeSpan Delay;
        public Exception? WriteError;
        public Action? OnWrite;
        public readonly List<byte> Commands = [];
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> report, CancellationToken token) => Exchange(report.ToArray(), false, token);
        public Task<byte[]> ExchangeActiveDpiAsync(ReadOnlyMemory<byte> report, CancellationToken token) => Exchange(report.ToArray(), true, token);

        private async Task<byte[]> Exchange(byte[] report, bool writing, CancellationToken token)
        {
            var active = Interlocked.Increment(ref Active);
            Peak = Math.Max(Peak, active);
            Commands.Add(report[8]);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Delay, token);
                if (!writing)
                {
                    WindowsMouseFeatureReadTransport.ValidateGetter(report);
                    if (BlockFirstRead && Writes == 0) await Task.Delay(Timeout.Infinite, token);
                    if (FailFirstRead || (FailReadback && Writes > 0)) throw new IOException("Synthetic disconnect");
                    return Reply(report, X, Y);
                }
                WindowsMouseFeatureReadTransport.ValidateActiveDpiRequest(report);
                Writes++;
                OnWrite?.Invoke();
                token.ThrowIfCancellationRequested();
                if (WriteError is not null) throw WriteError;
                if (DisconnectOnWrite) throw new IOException("Synthetic write disconnect");
                var x = report[10] * 256 + report[11];
                var y = report[12] * 256 + report[13];
                if (!IgnoreWrite) { X = x; Y = y; }
                if (BlockAfterWrite) await Task.Delay(Timeout.Infinite, token);
                var reply = Reply(report, x, y);
                if (BadAcknowledgement) reply[89] ^= 1;
                return reply;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
