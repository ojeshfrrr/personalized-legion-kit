using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

public interface IMouseFeatureDpiTransport : IMouseFeatureReadTransport
{
    Task<byte[]> ExchangeActiveDpiAsync(ReadOnlyMemory<byte> request, CancellationToken token);
}

public enum MouseDpiApplyStatus { Confirmed, Unchanged, Failed, Uncertain, ReadbackMismatch }

public sealed record MouseDpiApplyResult(MouseDpiApplyStatus Status, MouseDpi Requested,
    MouseDpi? Previous, MouseDpi? Actual, bool WriteAttempted, string Message)
{
    public bool Success => Status is MouseDpiApplyStatus.Confirmed or MouseDpiApplyStatus.Unchanged;
}

/// <summary>
/// Device-independent transaction engine. Native transport enforces hardware permission.
/// A caller owns one client/transport session; reads and write/readback batches share a gate.
/// </summary>
public sealed class DeathAdderV3DpiClient
{
    private readonly IMouseFeatureDpiTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _timeout;
    private byte _transaction;

    public DeathAdderV3DpiClient(IMouseFeatureDpiTransport transport, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _transport = transport;
        _timeout = timeout;
    }

    private byte NextTransaction() => _transaction = (byte)(_transaction % 31 + 1);

    private async Task<MouseDpi> ReadCoreAsync(CancellationToken token)
    {
        var id = NextTransaction();
        var request = DeathAdderV3Protocol.CreateReadRequest(DeathAdderV3ReadCommand.CurrentDpi, id, MouseDpiScope.ActiveOverride);
        var response = await _transport.ExchangeAsync(request, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return DeathAdderV3Protocol.ParseCurrentDpi(response, id, MouseDpiScope.ActiveOverride);
    }

    public async Task<MouseDpi> ReadActiveDpiAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { return await ReadCoreAsync(deadline.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<MouseDpiApplyResult> ApplyActiveDpiAsync(int x, int y, CancellationToken token = default)
    {
        var requested = DeathAdderV3Protocol.ValidateDpi(x, y);
        MouseDpi? previous = null;
        var attempted = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        var entered = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            previous = await ReadCoreAsync(deadline.Token).ConfigureAwait(false);
            if (previous == requested)
                return new(MouseDpiApplyStatus.Unchanged, requested, previous, previous, false, "Device already reports the requested active DPI.");
            deadline.Token.ThrowIfCancellationRequested();
            var id = NextTransaction();
            var request = DeathAdderV3Protocol.CreateActiveDpiRequest(x, y, id);
            attempted = true; // Conservative: cancellation/disconnection can follow a successful send.
            var acknowledgement = await _transport.ExchangeActiveDpiAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            DeathAdderV3Protocol.ValidateActiveDpiAcknowledgement(acknowledgement, id, requested);
            var actual = await ReadCoreAsync(deadline.Token).ConfigureAwait(false);
            return actual == requested
                ? new(MouseDpiApplyStatus.Confirmed, requested, previous, actual, true, "Active DPI confirmed by a fresh device read.")
                : new(MouseDpiApplyStatus.ReadbackMismatch, requested, previous, actual, true, "Readback differs from the request. No retry or automatic restoration was attempted.");
        }
        catch (Exception ex)
        {
            MouseDpi? recovered = null;
            // Native cancellation has drained before Exchange completes. Keep this gate
            // while attempting one getter with a fresh deadline; never retry the write.
            if (entered && attempted && !token.IsCancellationRequested &&
                ex is not Win32Exception { NativeErrorCode: 5 or 32 })
            {
                using var recovery = CancellationTokenSource.CreateLinkedTokenSource(token);
                recovery.CancelAfter(_timeout);
                try { recovered = await ReadCoreAsync(recovery.Token).ConfigureAwait(false); }
                catch (Exception) { /* Final state remains unknown; original error is retained. */ }
            }
            return new(attempted ? MouseDpiApplyStatus.Uncertain : MouseDpiApplyStatus.Failed,
                requested, previous, recovered, attempted,
                (recovered is not null
                    ? $"Write not confirmed. A separate device read reports {recovered.X} / {recovered.Y} DPI. No write was retried. "
                    : attempted ? "Final DPI is unknown; read the device before any further action. " : "No DPI write was attempted. ") + ex.Message);
        }
        finally { if (entered) _gate.Release(); }
    }
}
