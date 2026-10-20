using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

public interface IMouseFeaturePollingTransport : IMouseFeatureReadTransport
{
    Task<byte[]> ExchangePollingRateAsync(ReadOnlyMemory<byte> report, CancellationToken token);
}

public enum MousePollingApplyStatus { Confirmed, Unchanged, Failed, Uncertain, ReadbackMismatch }
public sealed record MousePollingApplyResult(MousePollingApplyStatus Status, MousePollingRate Requested,
    MousePollingRate? Previous, MousePollingRate? Actual, bool WriteAttempted, string Message)
{
    public bool Success => Status is MousePollingApplyStatus.Confirmed or MousePollingApplyStatus.Unchanged;
}

/// <summary>On-demand serialized read/write/ack/readback; no polling loop or retries.</summary>
public sealed class DeathAdderV3PollingClient
{
    private readonly IMouseFeaturePollingTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _timeout;
    private byte _transaction;

    public DeathAdderV3PollingClient(IMouseFeaturePollingTransport transport, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _transport = transport;
        _timeout = timeout;
    }
    private byte Next() => _transaction = (byte)(_transaction % 31 + 1);
    private async Task<MousePollingRate> ReadCoreAsync(CancellationToken token)
    {
        var id = Next();
        var reply = await _transport.ExchangeAsync(DeathAdderV3PollingProtocol.CreateReadRequest(id), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return DeathAdderV3PollingProtocol.ParseReadback(reply, id);
    }
    public async Task<MousePollingRate> ReadAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { return await ReadCoreAsync(deadline.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public async Task<MousePollingApplyResult> ApplyAsync(MousePollingRate rate, CancellationToken token = default)
    {
        _ = DeathAdderV3PollingProtocol.Encode(rate);
        MousePollingRate? previous = null;
        var attempted = false;
        var entered = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            previous = await ReadCoreAsync(deadline.Token).ConfigureAwait(false);
            if (previous == rate)
                return new(MousePollingApplyStatus.Unchanged, rate, previous, previous, false, "The device already reports this polling configuration.");
            deadline.Token.ThrowIfCancellationRequested();
            var id = Next();
            var request = DeathAdderV3PollingProtocol.CreateSetRequest(rate, id);
            attempted = true;
            var ack = await _transport.ExchangePollingRateAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            DeathAdderV3PollingProtocol.ValidateAcknowledgement(ack, id, rate);
            var actual = await ReadCoreAsync(deadline.Token).ConfigureAwait(false);
            return actual == rate
                ? new(MousePollingApplyStatus.Confirmed, rate, previous, actual, true, "Polling configuration confirmed by a fresh device read.")
                : new(MousePollingApplyStatus.ReadbackMismatch, rate, previous, actual, true, "Polling readback differs from the request. No retry was attempted.");
        }
        catch (Exception ex)
        {
            MousePollingRate? recovered = null;
            if (entered && attempted && !token.IsCancellationRequested &&
                ex is not Win32Exception { NativeErrorCode: 5 or 32 })
            {
                using var recovery = CancellationTokenSource.CreateLinkedTokenSource(token);
                recovery.CancelAfter(_timeout);
                try { recovered = await ReadCoreAsync(recovery.Token).ConfigureAwait(false); }
                catch (Exception) { /* Preserve uncertainty and the original error. */ }
            }
            return new(attempted ? MousePollingApplyStatus.Uncertain : MousePollingApplyStatus.Failed, rate, previous, recovered,
                attempted, (recovered.HasValue
                    ? $"Write not confirmed. A separate device read reports {(int)recovered.Value:N0} Hz. No write was retried. "
                    : attempted ? "Final polling state is unknown; read the device before any further action. " : "No polling write was attempted. ") + ex.Message);
        }
        finally { if (entered) _gate.Release(); }
    }
}
