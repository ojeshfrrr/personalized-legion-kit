using System;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

public sealed partial class MouseControlService
{
    public Task<MouseProfileBanks> ReadProfileBanksAsync(CancellationToken token = default) =>
        WithButtonsAsync(client => client.ReadProfilesAsync(token), token);
    public Task<MouseButtonBank> ReadButtonBankAsync(byte profile, CancellationToken token = default) =>
        WithButtonsAsync(client => client.ReadBankAsync(profile, token), token);
    private async Task<T> WithButtonsAsync<T>(Func<DeathAdderV3ButtonClient, Task<T>> operation, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                await using var transport = OpenUniqueDevice();
                return await operation(new DeathAdderV3ButtonClient(transport, TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public Task<MouseButtonApplyResult> ApplyButtonAsync(MouseButtonAssignment expected, MouseButtonAction action, CancellationToken token = default) =>
        ApplyBindingAsync(expected, MouseButtonBinding.Basic(action), token);
    public Task<MouseButtonApplyResult> ApplyKeyboardAsync(MouseButtonAssignment expected, MouseKeyboardAssignment assignment, CancellationToken token = default) =>
        ApplyBindingAsync(expected, MouseButtonBinding.Key(assignment), token);
    private async Task<MouseButtonApplyResult> ApplyBindingAsync(MouseButtonAssignment expected, MouseButtonBinding binding, CancellationToken token)
    {
        RequireMouseWritePermission();
        DeathAdderV3ButtonClient.ValidateBindingApply(expected, binding);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var started = false;
        try
        {
            RequireMouseWritePermission();
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested(); RequireMouseWritePermission();
                await using var transport = OpenUniqueDevice(); started = true;
                return await new DeathAdderV3ButtonClient(transport, TimeSpan.FromSeconds(5)).ApplyBindingAsync(expected, binding, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new(started ? MouseButtonApplyStatus.Uncertain : MouseButtonApplyStatus.Failed, null, null, started, ex.Message);
        }
        finally { _gate.Release(); }
    }
}
