using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

public interface IMouseFeatureButtonTransport : IMouseFeatureReadTransport
{
    Task<byte[]> ExchangeButtonAssignmentAsync(ReadOnlyMemory<byte> report, CancellationToken token);
}
public interface IMouseButtonService
{
    Task<MouseProfileBanks> ReadProfileBanksAsync(CancellationToken token = default);
    Task<MouseButtonBank> ReadButtonBankAsync(byte profile, CancellationToken token = default);
    Task<MouseButtonApplyResult> ApplyButtonAsync(MouseButtonAssignment expected, MouseButtonAction action, CancellationToken token = default);
    Task<MouseButtonApplyResult> ApplyKeyboardAsync(MouseButtonAssignment expected, MouseKeyboardAssignment assignment, CancellationToken token = default) =>
        throw new NotSupportedException("Keyboard assignments are unavailable in this service.");
}
public enum MouseButtonApplyStatus { Confirmed, Unchanged, PreconditionChanged, Failed, Uncertain, ReadbackMismatch }
public sealed record MouseButtonApplyResult(MouseButtonApplyStatus Status, MouseButtonAssignment? Previous,
    MouseButtonAssignment? Actual, bool WriteAttempted, string Message)
{
    public bool Success => Status is MouseButtonApplyStatus.Confirmed or MouseButtonApplyStatus.Unchanged;
}

/// <summary>Exact-model, on-demand assignment operations. No profile-storage or activation writes.</summary>
public sealed class DeathAdderV3ButtonClient(IMouseFeatureButtonTransport transport, TimeSpan timeout)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _timeout = timeout > TimeSpan.Zero && timeout <= TimeSpan.FromSeconds(15)
        ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));
    private byte _transaction;
    private byte Next() => _transaction = (byte)(_transaction % 31 + 1);
    private async Task<MouseProfileBanks> ProfilesCore(CancellationToken token)
    {
        async Task<byte> Scalar(MouseProfileQuery query)
        {
            var id = Next(); var reply = await transport.ExchangeAsync(DeathAdderV3ButtonProtocol.CreateProfileQuery(query, id), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return DeathAdderV3ButtonProtocol.ParseScalar(reply, query, id);
        }
        var capacity = await Scalar(MouseProfileQuery.Capacity).ConfigureAwait(false);
        var count = await Scalar(MouseProfileQuery.Count).ConfigureAwait(false);
        var transaction = Next();
        var response = await transport.ExchangeAsync(DeathAdderV3ButtonProtocol.CreateProfileQuery(MouseProfileQuery.Ids, transaction), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var ids = DeathAdderV3ButtonProtocol.ParseIds(response, transaction);
        if (ids.Count != count || capacity == 0) throw new InvalidDataException("Inconsistent profile count/IDs or unavailable capacity.");
        return new(capacity, ids);
    }
    private async Task<MouseButtonAssignment> ReadCore(byte profile, byte button, CancellationToken token)
    {
        var id = Next(); var response = await transport.ExchangeAsync(DeathAdderV3ButtonProtocol.CreateRead(profile, button, id), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); return DeathAdderV3ButtonProtocol.ParseAssignment(response, profile, button, id);
    }
    public async Task<MouseProfileBanks> ReadProfilesAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(_timeout);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { return await ProfilesCore(deadline.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public async Task<MouseButtonBank> ReadBankAsync(byte profile, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(_timeout);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            var banks = await ProfilesCore(deadline.Token).ConfigureAwait(false);
            if (!banks.Ids.Contains(profile)) throw new NotSupportedException("Requested bank was not reported by the mouse.");
            var assignments = new List<MouseButtonAssignment>();
            foreach (var button in DeathAdderV3ButtonProtocol.Buttons)
                assignments.Add(await ReadCore(profile, button.Id, deadline.Token).ConfigureAwait(false));
            return new(profile, assignments.AsReadOnly());
        }
        finally { _gate.Release(); }
    }
    public static void ValidateApply(MouseButtonAssignment expected, MouseButtonAction action)
        => ValidateBindingApply(expected, MouseButtonBinding.Basic(action));
    public static void ValidateBindingApply(MouseButtonAssignment expected, MouseButtonBinding binding)
    {
        ArgumentNullException.ThrowIfNull(expected);
        _ = DeathAdderV3ButtonProtocol.CreateBindingSet(expected.Button, binding, 1);
        if (expected.Profile != 1 || expected.Layer != 0)
            throw new NotSupportedException("Only bank 1's normal layer is writable; other banks are for inspection.");
        if (expected.KnownBinding is null) throw new NotSupportedException("Original assignment is not supported for exact restoration; no write permitted.");
    }
    public Task<MouseButtonApplyResult> ApplyAsync(MouseButtonAssignment expected, MouseButtonAction action, CancellationToken token = default) =>
        ApplyBindingAsync(expected, MouseButtonBinding.Basic(action), token);
    public Task<MouseButtonApplyResult> ApplyKeyboardAsync(MouseButtonAssignment expected, MouseKeyboardAssignment assignment, CancellationToken token = default) =>
        ApplyBindingAsync(expected, MouseButtonBinding.Key(assignment), token);
    public async Task<MouseButtonApplyResult> ApplyBindingAsync(MouseButtonAssignment expected, MouseButtonBinding binding, CancellationToken token = default)
    {
        ValidateBindingApply(expected, binding);
        var entered = false; var attempted = false;
        MouseButtonAssignment? previous = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(_timeout);
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            var banks = await ProfilesCore(deadline.Token).ConfigureAwait(false);
            if (!banks.Ids.Contains((byte)1)) throw new NotSupportedException("The mouse did not report bank 1.");
            previous = await ReadCore(1, expected.Button, deadline.Token).ConfigureAwait(false);
            if (previous != expected) return new(MouseButtonApplyStatus.PreconditionChanged, previous, previous, false,
                "Assignment changed since the displayed read. No write attempted; review the fresh reading before applying again.");
            if (previous.KnownBinding == binding) return new(MouseButtonApplyStatus.Unchanged, previous, previous, false, "The mouse already reports this assignment.");
            deadline.Token.ThrowIfCancellationRequested();
            var id = Next(); var request = DeathAdderV3ButtonProtocol.CreateBindingSet(expected.Button, binding, id);
            attempted = true;
            var ack = await transport.ExchangeButtonAssignmentAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            DeathAdderV3ButtonProtocol.ValidateBindingAcknowledgement(ack, expected.Button, binding, id);
            var actual = await ReadCore(1, expected.Button, deadline.Token).ConfigureAwait(false);
            return new(actual.KnownBinding == binding ? MouseButtonApplyStatus.Confirmed : MouseButtonApplyStatus.ReadbackMismatch,
                previous, actual, true, actual.KnownBinding == binding ? "Assignment confirmed by a fresh device read."
                    : "Assignment readback differs from the request. No write retry or automatic restoration attempted.");
        }
        catch (Exception ex)
        {
            MouseButtonAssignment? recovered = null;
            if (entered && attempted && !token.IsCancellationRequested && ex is not Win32Exception { NativeErrorCode: 5 or 32 })
            {
                using var recovery = CancellationTokenSource.CreateLinkedTokenSource(token); recovery.CancelAfter(_timeout);
                try { recovered = await ReadCore(1, expected.Button, recovery.Token).ConfigureAwait(false); }
                catch (Exception) { /* Keep unknown state; never retry the setter. */ }
            }
            return new(attempted ? MouseButtonApplyStatus.Uncertain : MouseButtonApplyStatus.Failed, previous, recovered, attempted,
                (recovered is not null ? "Write unconfirmed; one separate read recovered the current assignment. "
                    : attempted ? "Final assignment is unknown. Read the device before any further action. " : "No assignment write attempted. ") + ex.Message);
        }
        finally { if (entered) _gate.Release(); }
    }
}
