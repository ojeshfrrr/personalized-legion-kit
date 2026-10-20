using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Services.Mouse;

namespace LenovoLegionToolkit.WPF.Presentation;

public sealed record MouseButtonRestorePoint(MouseButtonAssignment Original, MouseButtonAssignment Changed);
/// <summary>Explicit reads/Apply only. Selecting a bank never activates it or sends a command.</summary>
public sealed class MouseButtonsSession(IMouseButtonService service, Func<bool> canWrite)
{
    private CancellationTokenSource? _lifetime;
    private Task _pending = Task.CompletedTask;
    private readonly Dictionary<byte, DateTimeOffset> _confirmedAt = [];
    public bool IsActive => _lifetime is not null;
    public bool IsBusy { get; private set; }
    public MouseProfileBanks? Banks { get; private set; }
    public byte? SelectedBank { get; private set; }
    public MouseButtonBank? Current { get; private set; }
    public MouseButtonBank? LastConfirmed { get; private set; }
    public DateTimeOffset? LastReadAt { get; private set; }
    public MouseButtonRestorePoint? RestorePoint { get; private set; }
    public DateTimeOffset? ConfirmedAt(byte button) => _confirmedAt.TryGetValue(button, out var time) ? time : null;
    public string Status { get; private set; } = "Read device banks, then read assignments. No mappings are assumed.";
    public event Action? Changed;
    public void InvalidateForPreset()
    {
        Current = null; RestorePoint = null;
        Status = "Preset in progress. Previous bank readings are historical; no automatic restoration.";
        Changed?.Invoke();
    }
    public void AcceptPresetResult(MousePresetApplyResult result)
    {
        if (!IsActive || IsBusy) return;
        RestorePoint = null;
        if (result.Success && result.BaselineBank is { } bank && result.BaselineReadAt is { } readAt)
        {
            var readbacks = result.Steps.Where(s => s.ButtonReadback is not null).ToDictionary(s => s.ButtonReadback!.Button);
            Current = LastConfirmed = new(1, Array.AsReadOnly(bank.Assignments.Select(a =>
                readbacks.TryGetValue(a.Button, out var step) ? step.ButtonReadback! : a).ToArray()));
            _confirmedAt.Clear();
            foreach (var a in Current.Assignments)
                _confirmedAt[a.Button] = readbacks.TryGetValue(a.Button, out var step) ? step.ReadAt!.Value : readAt;
            LastReadAt = readAt; SelectedBank = 1;
            Status = "Preset assignment readbacks verified. No onboard profile was saved or switched.";
        }
        else
        {
            Current = null;
            Status = "Preset bank state requires a new explicit assignment read. See the per-setting result; no automatic rollback.";
        }
        Changed?.Invoke();
    }
    public void Activate() { _lifetime ??= new CancellationTokenSource(); Changed?.Invoke(); }
    public void Deactivate()
    {
        var lifetime = _lifetime; _lifetime = null; lifetime?.Cancel(); Current = null; Banks = null; SelectedBank = null;
        Status = "Current assignments unavailable; read the device again."; Changed?.Invoke();
        if (!IsBusy) lifetime?.Dispose();
    }
    public void SelectBank(byte profile)
    {
        if (IsBusy || Banks is null || !Banks.Ids.Contains(profile)) return;
        if (SelectedBank == profile) return;
        SelectedBank = profile; Current = null;
        Status = $"Device bank {profile} selected for inspection only. Read assignments; the active profile has not been changed.";
        Changed?.Invoke();
    }
    public Task ReadProfilesAsync() => Run(async lifetime =>
    {
        Banks = null; Current = null; SelectedBank = null; Status = "Reading device bank metadata…"; Changed?.Invoke();
        var banks = await service.ReadProfileBanksAsync(lifetime.Token);
        if (!IsCurrent(lifetime)) return;
        Banks = banks;
        SelectedBank = Banks.Ids.Contains((byte)1) ? (byte)1 : Banks.Ids.FirstOrDefault();
        if (Banks.Ids.Count == 0) SelectedBank = null;
        Status = $"Mouse reports capacity {Banks.ReportedCapacity}, {Banks.Ids.Count} bank IDs. Select a bank and read assignments. " +
            "Capacity is not proof of saved profiles or reconnect persistence.";
    });
    public Task ReadAssignmentsAsync()
    {
        if (SelectedBank is not { } bank) return Task.CompletedTask;
        return Run(async lifetime =>
        {
            Current = null; Status = $"Reading bank {bank} · normal layer…"; Changed?.Invoke();
            var result = await service.ReadButtonBankAsync(bank, lifetime.Token);
            if (!IsCurrent(lifetime)) return;
            Current = LastConfirmed = result; LastReadAt = DateTimeOffset.Now;
            _confirmedAt.Clear(); foreach (var assignment in result.Assignments) _confirmedAt[assignment.Button] = LastReadAt.Value;
            Status = "Assignments read from the mouse. Bank 1's normal layer can be edited; primary left click is protected. " +
                "Other banks and unknown functions are read-only. Onboard persistence and profile activation remain unverified.";
        });
    }
    public bool CanApply(byte button) => IsActive && !IsBusy && canWrite() && Current?.Profile == 1 && button != 1 &&
        Current.Assignments.SingleOrDefault(a => a.Button == button)?.KnownBinding is not null;
    public bool CanRestore => RestorePoint is { } point && CanApply(point.Changed.Button) &&
        Current!.Assignments.Single(a => a.Button == point.Changed.Button) == point.Changed;
    public Task ApplyAsync(byte button, MouseButtonAction action) => ApplyBindingAsync(button, MouseButtonBinding.Basic(action));
    public Task ApplyKeyboardAsync(byte button, MouseKeyboardAssignment assignment) => ApplyBindingAsync(button, MouseButtonBinding.Key(assignment));
    public Task RestoreAsync()
    {
        if (!CanRestore || RestorePoint is not { } point || point.Original.KnownBinding is not { } original)
            throw new InvalidOperationException("Read and confirm the changed assignment before restoring it.");
        return ApplyBindingAsync(point.Changed.Button, original, restoring: true);
    }
    private Task ApplyBindingAsync(byte button, MouseButtonBinding binding, bool restoring = false)
    {
        var expected = Current?.Assignments.SingleOrDefault(a => a.Button == button)
            ?? throw new InvalidOperationException("Read the selected bank before applying an assignment.");
        DeathAdderV3ButtonClient.ValidateBindingApply(expected, binding);
        if (!CanApply(button)) throw new InvalidOperationException("Button changes are locked or unavailable.");
        return Run(async lifetime =>
        {
            Status = restoring ? "Restoring previous assignment and verifying device readback…" : "Applying requested assignment and verifying device readback…";
            Changed?.Invoke();
            var result = binding.Action is { } action
                ? await service.ApplyButtonAsync(expected, action, lifetime.Token)
                : await service.ApplyKeyboardAsync(expected, binding.Keyboard!, lifetime.Token);
            if (!IsCurrent(lifetime)) return;
            if (result.Actual is not null && Current is not null)
            {
                Current = LastConfirmed = new(Current.Profile, Array.AsReadOnly(Current.Assignments.Select(a =>
                    a.Button == button ? result.Actual : a).ToArray()));
                _confirmedAt[button] = DateTimeOffset.Now;
            }
            else Current = null;
            if (restoring && result.Success && result.Actual == RestorePoint?.Original)
            {
                RestorePoint = null; Status = "Restoration verified · " + result.Message;
            }
            else
            {
                if (!restoring && result.Success && result.Previous is not null && result.Actual is not null && result.Previous != result.Actual)
                    RestorePoint = new(result.Previous, result.Actual);
                Status = (result.Success ? "Confirmed · " : "Not confirmed · ") + result.Message;
            }
        });
    }
    private bool IsCurrent(CancellationTokenSource lifetime) => ReferenceEquals(_lifetime, lifetime) && !lifetime.IsCancellationRequested;
    private Task Run(Func<CancellationTokenSource, Task> action)
    {
        if (_lifetime is null || IsBusy) return _pending;
        var lifetime = _lifetime; var preceding = _pending;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _pending = finished.Task;
        _ = Execute(); return finished.Task;
        async Task Execute()
        {
            try
            {
                await preceding; if (!IsCurrent(lifetime)) return;
                IsBusy = true; Changed?.Invoke(); await action(lifetime);
            }
            catch (Exception ex)
            {
                if (IsCurrent(lifetime))
                {
                    Current = null;
                    Status = (ex is NotSupportedException ? "Unsupported · " : "Unavailable · ") + ex.Message;
                }
            }
            finally
            {
                IsBusy = false;
                if (IsActive) Changed?.Invoke();
                if (!IsCurrent(lifetime)) lifetime.Dispose();
                finished.TrySetResult();
            }
        }
    }
}
