using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;

internal static class DeathAdderProtocolChecks
{
    // Synthetic wire fixtures, not live device readings. Framing/layout come from
    // local product-178 middleware; no proprietary implementation is included.
    public static async Task RunAsync(Action<bool, string> check)
    {
        var request = DeathAdderV3Protocol.CreateReadRequest(DeathAdderV3ReadCommand.CurrentDpi, 1);
        check(request.Length == 91 && request[0] == 0 && request[2] == 1 && request[6] == 7 &&
              request[7] == 4 && request[8] == 0x85 && request[9] == 1 && request[89] == 0x87,
              "DPI getter matches the independently specified 91-byte request vector");
        var stagesRequest = DeathAdderV3Protocol.CreateReadRequest(DeathAdderV3ReadCommand.DpiStages, 31);
        check(stagesRequest[6] == 80 && stagesRequest[8] == 0x86 && stagesRequest[89] == 0xD3,
              "stage getter requests the evidenced class-data format");
        Reject<ArgumentOutOfRangeException>(() => DeathAdderV3Protocol.CreateReadRequest((DeathAdderV3ReadCommand)5, 1));
        Reject<ArgumentOutOfRangeException>(() => DeathAdderV3Protocol.CreateReadRequest(DeathAdderV3ReadCommand.CurrentDpi, 0));
        check(true, "read codec rejects settings commands and invalid transaction identifiers");
        WindowsMouseFeatureReadTransport.ValidateGetter(request);
        WindowsMouseFeatureReadTransport.ValidateGetter(stagesRequest);
        var modified = (byte[])request.Clone();
        modified[9] = 2;
        Reject<ArgumentException>(() => WindowsMouseFeatureReadTransport.ValidateGetter(modified));
        Reject<ArgumentException>(() => WindowsMouseFeatureReadTransport.ValidateGetter(request[..90]));
        modified = (byte[])request.Clone();
        modified[8] = 5; // Setting command, rejected before native I/O.
        Reject<ArgumentOutOfRangeException>(() => WindowsMouseFeatureReadTransport.ValidateGetter(modified));
        check(true, "native transport accepts exact getter frames only and rejects settings or altered payloads before I/O");
        WindowsMouseFeatureReadTransport.ValidateFeatureCompletion(new byte[91], 90);
        Reject<InvalidDataException>(() => WindowsMouseFeatureReadTransport.ValidateFeatureCompletion(new byte[91], 89));
        Reject<InvalidDataException>(() => WindowsMouseFeatureReadTransport.ValidateFeatureCompletion(new byte[90], 90));
        var numbered = new byte[91];
        numbered[0] = 1;
        Reject<InvalidDataException>(() => WindowsMouseFeatureReadTransport.ValidateFeatureCompletion(numbered, 90));
        check(true, "unnumbered HID completion requires all 90 device bytes plus the zero report ID; truncated data stays rejected");
        const string hidSuffix = "#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        check(WindowsMouseFeatureReadTransport.IsConfigurationPath(@"\\?\hid#vid_1532&pid_00b2&mi_00#test" + hidSuffix) &&
              !WindowsMouseFeatureReadTransport.IsConfigurationPath(@"\\?\hid#vid_1532&pid_00b2&mi_00&col03#test" + hidSuffix) &&
              !WindowsMouseFeatureReadTransport.IsConfigurationPath(@"\\?\hid#vid_1532&pid_00b2&mi_01#test" + hidSuffix) &&
              !WindowsMouseFeatureReadTransport.IsConfigurationPath(@"\\?\hid#vid_1532&pid_00b3&mi_00#test" + hidSuffix) &&
              !WindowsMouseFeatureReadTransport.IsConfigurationPath(null),
              "native transport selects the plain exact-model MI_00 collection and rejects sibling or other-model paths");

        var current = Reply(request, [1, 6, 64, 6, 64, 0, 0]);
        var dpi = DeathAdderV3Protocol.ParseCurrentDpi(current, 1);
        check(dpi.X == 1600 && dpi.Y == 1600, "synthetic DPI reply decodes big-endian X/Y values");
        var stagesReply = Reply(stagesRequest, [1, 2, 2, 1, 3, 32, 3, 32, 0, 0, 2, 6, 64, 6, 64, 0, 0]);
        var stages = DeathAdderV3Protocol.ParseDpiStages(stagesReply, 31);
        check(stages.ActiveStage == 2 && stages.Stages.Count == 2 && stages.Stages[0].Dpi.X == 800,
              "synthetic stages retain identifiers, active stage, and X/Y values");

        Reject<InvalidDataException>(() => DeathAdderV3Protocol.ParseCurrentDpi(current[..90], 1));
        Reject<InvalidDataException>(() => DeathAdderV3Protocol.ParseCurrentDpi(current, 2));
        foreach (var offset in new[] { 0, 7, 8, 10, 89, 90 })
        {
            var bad = (byte[])current.Clone();
            bad[offset] ^= 1;
            Reject<InvalidDataException>(() => DeathAdderV3Protocol.ParseCurrentDpi(bad, 1));
        }
        Reject<InvalidDataException>(() => DeathAdderV3Protocol.ParseCurrentDpi(Reply(request, [1, 0, 0, 0, 0, 0, 0]), 1));
        Reject<InvalidDataException>(() => DeathAdderV3Protocol.ParseDpiStages(Reply(stagesRequest, [1, 1, 5]), 31));
        Reject<InvalidDataException>(() => DeathAdderV3Protocol.ParseDpiStages(Reply(stagesRequest, [1, 3, 1, 1, 3, 32, 3, 32, 0, 0]), 31));
        Reject<InvalidDataException>(() => DeathAdderV3Protocol.ParseDpiStages(Reply(stagesRequest, [1, 1, 2, 1, 3, 32, 3, 32, 0, 0, 1, 6, 64, 6, 64, 0, 0]), 31));
        check(true, "DPI parser rejects malformed, stale, corrupt, zero-DPI, and invalid-stage replies");
        var unsupported = (byte[])current.Clone();
        unsupported[1] = 5;
        Reject<NotSupportedException>(() => DeathAdderV3Protocol.ParseCurrentDpi(unsupported, 1));
        foreach (var status in new byte[] { 0, 1, 3, 4, 99 })
        {
            var failed = (byte[])current.Clone();
            failed[1] = status;
            Reject<IOException>(() => DeathAdderV3Protocol.ParseCurrentDpi(failed, 1));
        }
        check(true, "unsupported and unsuccessful device statuses never become readings");

        var transport = new SyntheticTransport();
        Reject<NotSupportedException>(() => new DeathAdderV3ReadClient(0x1532, 0x00B3, transport, TimeSpan.FromSeconds(1)));
        var client = new DeathAdderV3ReadClient(0x1532, 0x00B2, transport, TimeSpan.FromSeconds(3));
        await Task.WhenAll(Enumerable.Range(0, 65).Select(_ => client.ReadCurrentDpiAsync()));
        check(transport.Calls == 65 && transport.Peak == 1 && transport.Active == 0,
              "getter pipeline serializes competing reads and safely wraps transaction identifiers");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await RejectAsync<OperationCanceledException>(() => client.ReadCurrentDpiAsync(cancelled.Token));
            check(transport.Calls == 65, "pre-cancelled getter performs no transport operation");
        }
        transport.Delay = Timeout.InfiniteTimeSpan;
        using (var activeCancellation = new CancellationTokenSource())
        using (var queuedCancellation = new CancellationTokenSource())
        {
            var active = client.ReadCurrentDpiAsync(activeCancellation.Token);
            var calls = transport.Calls;
            var queued = client.ReadCurrentDpiAsync(queuedCancellation.Token);
            queuedCancellation.Cancel();
            await RejectAsync<OperationCanceledException>(() => queued);
            check(transport.Calls == calls && transport.Active == 1,
                  "cancelling a queued read neither enters transport nor releases another read's gate");
            activeCancellation.Cancel();
            await RejectAsync<OperationCanceledException>(() => active);
            check(transport.Active == 0, "cancelling an active read drains its cooperative transport");
        }
        transport.Delay = TimeSpan.Zero;
        transport.Failure = new UnauthorizedAccessException("Synthetic access denial");
        await RejectAsync<UnauthorizedAccessException>(() => client.ReadCurrentDpiAsync());
        transport.Failure = new IOException("Synthetic disconnected device");
        await RejectAsync<IOException>(() => client.ReadCurrentDpiAsync());
        transport.Failure = null;
        transport.Delay = Timeout.InfiniteTimeSpan;
        var timedClient = new DeathAdderV3ReadClient(0x1532, 0x00B2, transport, TimeSpan.FromMilliseconds(50));
        await RejectAsync<OperationCanceledException>(() => timedClient.ReadCurrentDpiAsync());
        check(transport.Active == 0, "getter deadline cancels the cooperative transport without leaving work running");
        transport.Delay = TimeSpan.Zero;
        await timedClient.ReadCurrentDpiAsync();
        check(transport.Active == 0, "access denial, disconnect, and timeout release the operation gate for a later read");
    }

    private static byte[] Reply(byte[] request, byte[] data)
    {
        var reply = new byte[91];
        reply[1] = 2;
        reply[2] = request[2];
        reply[6] = (byte)data.Length;
        reply[7] = request[7];
        reply[8] = request[8];
        data.CopyTo(reply, 9);
        reply[89] = (byte)reply.Skip(3).Take(86).Aggregate(0, (a, b) => a ^ b);
        return reply;
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class SyntheticTransport : IMouseFeatureReadTransport
    {
        public int Calls;
        public int Active;
        public int Peak;
        public Exception? Failure;
        public TimeSpan Delay = TimeSpan.FromMilliseconds(1);

        public async Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> getterReport, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref Active);
            Peak = Math.Max(Peak, active);
            try
            {
                await Task.Delay(Delay, token);
                if (Failure is not null) throw Failure;
                return Reply(getterReport.ToArray(), [1, 6, 64, 6, 64, 0, 0]);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
