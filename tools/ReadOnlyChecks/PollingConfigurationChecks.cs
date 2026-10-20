using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;

internal static class PollingConfigurationChecks
{
    // Synthetic firmware/configuration model only. No native calls or device settings.
    public static async Task RunAsync(Action<bool, string> check)
    {
        var getter = DeathAdderV3PollingProtocol.CreateReadRequest(1);
        check(getter.Length == 91 && getter[2] == 1 && getter[6] == 2 && getter[7] == 0 && getter[8] == 0xC0 &&
              getter[9] == 1 && getter[10] == 0 && getter[89] == 0xC3,
            "polling getter matches the exact product-178 profile-1 00/C0 wire vector");
        WindowsMouseFeatureReadTransport.ValidateGetter(getter);
        var setter = DeathAdderV3PollingProtocol.CreateSetRequest(MousePollingRate.Hz8000, 2);
        check(setter[8] == 0x40 && setter[9] == 1 && setter[10] == 1 && setter[89] == 0x42 &&
              setter.Skip(11).Take(78).All(b => b == 0),
            "8000-Hz setter contains only the verified profile/rate arguments and no DPI data");
        DeathAdderV3PollingProtocol.ValidateSetRequest(setter);
        foreach (var rate in DeathAdderV3PollingProtocol.SupportedRates)
        {
            var code = DeathAdderV3PollingProtocol.Encode(rate);
            check(DeathAdderV3PollingProtocol.Decode(code) == rate &&
                  DeathAdderV3PollingProtocol.ParseReadback(Reply(getter, rate), 1) == rate,
                $"synthetic {(int)rate}-Hz configuration encodes and decodes without guessed values");
        }
        foreach (var invalid in new[] { 0, 100, 333, 750, 7999, 16000 })
            Reject<ArgumentOutOfRangeException>(() => DeathAdderV3PollingProtocol.CreateSetRequest((MousePollingRate)invalid, 1));
        foreach (var invalid in new byte[] { 0, 3, 7, 128, 255 })
            Reject<InvalidDataException>(() => DeathAdderV3PollingProtocol.Decode(invalid));
        check(true, "unknown rate codes never default to 8000 Hz and invalid requests are rejected before transport");
        foreach (var index in new[] { 0, 6, 7, 8, 9, 10, 12, 89, 90 })
        {
            var altered = (byte[])setter.Clone(); altered[index] ^= 1;
            try { DeathAdderV3PollingProtocol.ValidateSetRequest(altered); throw new Exception("Accepted altered setter."); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { }
        }
        Reject<ArgumentException>(() => DeathAdderV3PollingProtocol.ValidateReadRequest(setter));
        var oldGetter = (byte[])getter.Clone(); oldGetter[8] = 0x8E;
        Reject<ArgumentOutOfRangeException>(() => WindowsMouseFeatureReadTransport.ValidateGetter(oldGetter));
        check(true, "transport rejects changed profile/payload/framing and the unverified legacy polling getter");
        var reply = Reply(getter, MousePollingRate.Hz1000);
        Reject<InvalidDataException>(() => DeathAdderV3PollingProtocol.ParseReadback(reply[..90], 1));
        Reject<InvalidDataException>(() => DeathAdderV3PollingProtocol.ParseReadback(reply, 2));
        foreach (var index in new[] { 0, 6, 7, 8, 9, 89, 90 })
        {
            var bad = (byte[])reply.Clone(); bad[index] ^= 1;
            Reject<InvalidDataException>(() => DeathAdderV3PollingProtocol.ParseReadback(bad, 1));
        }
        foreach (var status in new byte[] { 0, 1, 3, 4, 99 })
        {
            var bad = (byte[])reply.Clone(); bad[1] = status;
            Reject<IOException>(() => DeathAdderV3PollingProtocol.ParseReadback(bad, 1));
        }
        reply[1] = 5;
        Reject<NotSupportedException>(() => DeathAdderV3PollingProtocol.ParseReadback(reply, 1));
        Reject<InvalidDataException>(() => DeathAdderV3PollingProtocol.ValidateAcknowledgement(Reply(setter, MousePollingRate.Hz4000), 2, MousePollingRate.Hz8000));
        check(true, "polling replies require matching transaction/profile/command, checksum, length, status and acknowledgement rate");

        var fake = new FakePolling();
        var client = Client(fake);
        var initial = await client.ReadAsync();
        await Task.Delay(20);
        check(initial == MousePollingRate.Hz1000 && fake.Commands.Count == 1, "polling configuration reads are on demand with no polling loop");
        var result = await client.ApplyAsync(MousePollingRate.Hz8000);
        check(result.Success && result.Status == MousePollingApplyStatus.Confirmed && result.Previous == MousePollingRate.Hz1000 &&
              result.Actual == MousePollingRate.Hz8000 && fake.Commands.Skip(1).SequenceEqual(new byte[] { 0xC0, 0x40, 0xC0 }),
            "polling apply succeeds only after pre-read, matching acknowledgement, and fresh configuration readback");
        result = await client.ApplyAsync(MousePollingRate.Hz8000);
        check(result.Status == MousePollingApplyStatus.Unchanged && !result.WriteAttempted && fake.Writes == 1,
            "unchanged polling configuration does not generate another write");
        var count = fake.Commands.Count;
        await RejectAsync<ArgumentOutOfRangeException>(() => client.ApplyAsync((MousePollingRate)7000));
        check(fake.Commands.Count == count, "unsupported polling values never reach transport");
        fake = new FakePolling { IgnoreWrite = true };
        result = await Client(fake).ApplyAsync(MousePollingRate.Hz8000);
        check(!result.Success && result.Status == MousePollingApplyStatus.ReadbackMismatch && result.Actual == MousePollingRate.Hz1000,
            "acknowledged polling write with mismatching readback is not success");
        fake = new FakePolling { BadAck = true };
        result = await Client(fake).ApplyAsync(MousePollingRate.Hz8000);
        check(!result.Success && result.Status == MousePollingApplyStatus.Uncertain && result.Actual == MousePollingRate.Hz8000 &&
              fake.Writes == 1 && fake.Commands.SequenceEqual(new byte[] { 0xC0, 0x40, 0xC0 }),
            "bad polling acknowledgement gets one safe readback without retrying or claiming write success");
        fake = new FakePolling { DisconnectBeforeWrite = true };
        result = await Client(fake).ApplyAsync(MousePollingRate.Hz8000);
        check(result.Status == MousePollingApplyStatus.Failed && !result.WriteAttempted && fake.Writes == 0,
            "polling pre-read disconnection prevents a setting write");
        fake = new FakePolling { DisconnectOnWrite = true };
        result = await Client(fake).ApplyAsync(MousePollingRate.Hz8000);
        check(result.Status == MousePollingApplyStatus.Uncertain && result.WriteAttempted,
            "polling write disconnection never assumes the mouse stayed unchanged");
        fake = new FakePolling { FailReadback = true };
        result = await Client(fake).ApplyAsync(MousePollingRate.Hz8000);
        check(!result.Success && result.Actual is null && result.Status == MousePollingApplyStatus.Uncertain,
            "failed polling readback is unavailable rather than a stale rate");
        fake = new FakePolling { BlockRead = true };
        result = await Client(fake, TimeSpan.FromMilliseconds(40)).ApplyAsync(MousePollingRate.Hz8000);
        check(!result.WriteAttempted && fake.Active == 0, "polling pre-write timeout cancels without a setting write or residual worker");
        fake = new FakePolling { BlockAfterWrite = true };
        result = await Client(fake, TimeSpan.FromMilliseconds(40)).ApplyAsync(MousePollingRate.Hz8000);
        check(result.Status == MousePollingApplyStatus.Uncertain && result.Actual == MousePollingRate.Hz8000 &&
              fake.Writes == 1 && fake.Active == 0 && fake.Commands.Count == 3,
            "polling post-write timeout drains work and uses one fresh-deadline getter without another write");
        fake = new FakePolling { BadAck = true, FailReadback = true };
        result = await Client(fake).ApplyAsync(MousePollingRate.Hz8000);
        check(!result.Success && result.Actual is null && fake.Writes == 1 && fake.Commands.Count == 3,
            "failed polling recovery read remains unknown and is not retried");
        fake = new FakePolling { WriteError = new System.ComponentModel.Win32Exception(5) };
        result = await Client(fake).ApplyAsync(MousePollingRate.Hz8000);
        check(result.Actual is null && fake.Commands.Count == 2 && fake.Writes == 1,
            "denied polling writes stop without retrying device access");
        using (var cancelDuringWrite = new CancellationTokenSource())
        {
            fake = new FakePolling { OnWrite = cancelDuringWrite.Cancel };
            result = await Client(fake).ApplyAsync(MousePollingRate.Hz8000, cancelDuringWrite.Token);
            check(!result.Success && result.Actual is null && fake.Commands.Count == 2 && fake.Active == 0,
                "navigation cancellation after a polling write suppresses optional recovery reads and drains the operation");
        }
        fake = new FakePolling();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            result = await Client(fake).ApplyAsync(MousePollingRate.Hz8000, cancelled.Token);
            check(!result.WriteAttempted && fake.Commands.Count == 0, "pre-cancelled polling apply performs no transport work");
        }
        fake = new FakePolling { Delay = TimeSpan.FromMilliseconds(2) };
        client = Client(fake);
        var first = client.ApplyAsync(MousePollingRate.Hz8000);
        var second = client.ApplyAsync(MousePollingRate.Hz4000);
        var read = client.ReadAsync();
        await Task.WhenAll(first, second); await read;
        check(first.Result.Success && second.Result.Previous == MousePollingRate.Hz8000 && fake.Peak == 1 &&
              fake.Commands.Take(6).SequenceEqual(new byte[] { 0xC0, 0x40, 0xC0, 0xC0, 0x40, 0xC0 }),
            "competing polling requests serialize entire write/readback batches");
        fake = new FakePolling { BlockRead = true }; client = Client(fake);
        using (var active = new CancellationTokenSource())
        using (var queued = new CancellationTokenSource())
        {
            var pending = client.ApplyAsync(MousePollingRate.Hz8000, active.Token);
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var waiting = client.ApplyAsync(MousePollingRate.Hz4000, queued.Token);
            queued.Cancel(); var cancelled = await waiting;
            check(!cancelled.WriteAttempted && fake.Active == 1 && fake.Commands.Count == 1,
                "cancelling queued polling work does not release another transaction's gate");
            active.Cancel(); await pending;
        }
        fake.BlockRead = false;
        check((await client.ApplyAsync(MousePollingRate.Hz8000)).Success && fake.Active == 0,
            "polling client resumes safely after cancellation");
        await RejectAsync<InvalidOperationException>(() => new MouseControlService().ApplyPollingRateAsync(MousePollingRate.Hz8000));
        check(true, "production polling setter checks read-only permission before device discovery");
    }

    private static DeathAdderV3PollingClient Client(FakePolling fake, TimeSpan? timeout = null) => new(fake, timeout ?? TimeSpan.FromSeconds(3));
    private static byte[] Reply(byte[] request, MousePollingRate rate)
    {
        var reply = new byte[91]; reply[1] = 2; reply[2] = request[2]; reply[6] = 2;
        reply[7] = 0; reply[8] = request[8]; reply[9] = 1; reply[10] = DeathAdderV3PollingProtocol.Encode(rate);
        reply[89] = (byte)reply.Skip(3).Take(86).Aggregate(0, (a, b) => a ^ b);
        return reply;
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private sealed class FakePolling : IMouseFeaturePollingTransport
    {
        public MousePollingRate Rate = MousePollingRate.Hz1000;
        public int Writes, Active, Peak;
        public bool IgnoreWrite, BadAck, DisconnectBeforeWrite, DisconnectOnWrite, FailReadback, BlockRead, BlockAfterWrite;
        public TimeSpan Delay;
        public Exception? WriteError;
        public Action? OnWrite;
        public readonly List<byte> Commands = [];
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> report, CancellationToken token) => Exchange(report.ToArray(), false, token);
        public Task<byte[]> ExchangePollingRateAsync(ReadOnlyMemory<byte> report, CancellationToken token) => Exchange(report.ToArray(), true, token);
        private async Task<byte[]> Exchange(byte[] request, bool writing, CancellationToken token)
        {
            var active = Interlocked.Increment(ref Active); Peak = Math.Max(Peak, active);
            Commands.Add(request[8]); Started.TrySetResult();
            try
            {
                await Task.Delay(Delay, token);
                if (!writing)
                {
                    WindowsMouseFeatureReadTransport.ValidateGetter(request);
                    if (BlockRead) await Task.Delay(Timeout.Infinite, token);
                    if (DisconnectBeforeWrite || (FailReadback && Writes > 0)) throw new IOException("Synthetic polling disconnect");
                    return Reply(request, Rate);
                }
                DeathAdderV3PollingProtocol.ValidateSetRequest(request); Writes++;
                OnWrite?.Invoke();
                token.ThrowIfCancellationRequested();
                if (WriteError is not null) throw WriteError;
                if (DisconnectOnWrite) throw new IOException("Synthetic polling write disconnect");
                var desired = DeathAdderV3PollingProtocol.Decode(request[10]);
                if (!IgnoreWrite) Rate = desired;
                if (BlockAfterWrite) await Task.Delay(Timeout.Infinite, token);
                return Reply(request, BadAck ? MousePollingRate.Hz500 : desired);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
