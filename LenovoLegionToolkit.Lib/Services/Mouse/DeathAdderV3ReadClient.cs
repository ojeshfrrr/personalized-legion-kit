using System;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

/// <summary>
/// One transport/session per physical device. Implementations must finish cancellation
/// and release their I/O buffers before completing ExchangeAsync. Native access is
/// explicit and on demand; no mouse transport is registered at application startup.
/// </summary>
public interface IMouseFeatureReadTransport
{
    Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> getterReport, CancellationToken cancellationToken);
}

/// <summary>
/// On-demand, serialized getter pipeline; no polling, initialization writes, settings
/// persistence, automatic retries, or production startup registration.
/// </summary>
public sealed class DeathAdderV3ReadClient
{
    private readonly IMouseFeatureReadTransport _transport;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly TimeSpan _timeout;
    private byte _transaction;

    public DeathAdderV3ReadClient(ushort vendorId, ushort productId, IMouseFeatureReadTransport transport, TimeSpan timeout)
    {
        if (vendorId != DeathAdderV3Protocol.VendorId || productId != DeathAdderV3Protocol.ProductId)
            throw new NotSupportedException("This protocol is restricted to the wired DeathAdder V3 (1532:00B2).");
        ArgumentNullException.ThrowIfNull(transport);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _transport = transport;
        _timeout = timeout;
    }

    public Task<MouseDpi> ReadCurrentDpiAsync(CancellationToken token = default) =>
        ReadAsync(DeathAdderV3ReadCommand.CurrentDpi, (r, t) => DeathAdderV3Protocol.ParseCurrentDpi(r, t), token);

    public Task<MouseDpiStages> ReadDpiStagesAsync(CancellationToken token = default) =>
        ReadAsync(DeathAdderV3ReadCommand.DpiStages, DeathAdderV3Protocol.ParseDpiStages, token);

    private delegate T ReplyParser<T>(ReadOnlySpan<byte> reply, byte transaction);

    private async Task<T> ReadAsync<T>(DeathAdderV3ReadCommand command, ReplyParser<T> parser, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        await _operation.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            _transaction = (byte)(_transaction % 31 + 1);
            var request = DeathAdderV3Protocol.CreateReadRequest(command, _transaction);
            var reply = await _transport.ExchangeAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return parser(reply, _transaction);
        }
        finally
        {
            _operation.Release();
        }
    }
}
