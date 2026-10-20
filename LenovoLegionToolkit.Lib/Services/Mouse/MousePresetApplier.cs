using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Services.Mouse;

public enum MousePresetStepStatus { NotAttempted, Confirmed, Unchanged, Failed, Uncertain }
public sealed record MousePresetStepResult(string Setting, string Requested, MousePresetStepStatus Status,
    bool? WriteAttempted = false, string? Before = null, string? Actual = null, string? Message = null,
    MouseDpi? DpiReadback = null, MousePollingRate? PollingReadback = null,
    MouseButtonAssignment? ButtonReadback = null, DateTimeOffset? ReadAt = null);
public sealed record MousePresetApplyResult(IReadOnlyList<MousePresetStepResult> Steps,
    MouseButtonBank? BaselineBank, DateTimeOffset? BaselineReadAt)
{
    public bool Success => Steps.All(s => s.Status is MousePresetStepStatus.Confirmed or MousePresetStepStatus.Unchanged);
    public string Summary => (Success ? "Preset verified. " : "Preset stopped; it was not fully applied. ") +
        string.Join(Environment.NewLine, Steps.Select(s => $"{s.Setting}: {s.Status}" +
            (s.WriteAttempted == true ? " (write attempted)" : s.WriteAttempted == false ? " (no write)" : " (write transmission unknown)") +
            (s.Before is null ? "" : $" · before {s.Before}") +
            (s.Actual is null ? "" : $" · readback {s.Actual}") + $" · requested {s.Requested}. {s.Message}"));
}

/// <summary>Explicit sequential composition of existing verified operations, never an onboard switch.</summary>
public sealed class MousePresetApplier(IMouseControlService controls, IMouseButtonService buttons, Func<bool> canWrite)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<MousePresetApplyResult> ApplyAsync(MouseSoftwareProfile preset, CancellationToken token = default)
    {
        var target = MouseSoftwareProfileStore.ValidateAndCopy(preset); // Entire plan before any I/O.
        RequirePermission();
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("A local preset operation is already running.");
        try
        {
            var assignments = target.Assignments.OrderBy(p => p.Key).ToArray();
            var steps = new List<MousePresetStepResult>
            {
                new("Preflight", "Fresh device baseline", MousePresetStepStatus.NotAttempted),
                new("DPI", $"{target.Dpi.X} / {target.Dpi.Y}", MousePresetStepStatus.NotAttempted),
                new("Polling", $"{(int)target.PollingRate} Hz", MousePresetStepStatus.NotAttempted),
            };
            steps.AddRange(assignments.Select(p => new MousePresetStepResult($"Button {p.Key}", p.Value.Description, MousePresetStepStatus.NotAttempted)));
            MouseButtonBank? bank = null;
            DateTimeOffset? baselineTime = null;
            var index = 0;
            var attempted = false;
            MousePresetApplyResult Result() => new(steps.AsReadOnly(), bank, baselineTime);
            try
            {
                Check();
                var dpi = await controls.ReadActiveDpiAsync(token).ConfigureAwait(false);
                _ = DeathAdderV3Protocol.ValidateDpi(dpi.X, dpi.Y);
                Check();
                var polling = await controls.ReadPollingRateAsync(token).ConfigureAwait(false);
                _ = DeathAdderV3PollingProtocol.Encode(polling);
                if (assignments.Length != 0)
                {
                    Check();
                    bank = await buttons.ReadButtonBankAsync(1, token).ConfigureAwait(false);
                    if (bank.Profile != 1) throw new InvalidDataException("Preflight returned another device bank.");
                    foreach (var (id, binding) in assignments)
                    {
                        var originals = bank.Assignments.Where(a => a.Button == id).ToArray();
                        if (originals.Length != 1) throw new InvalidDataException($"Button {id} has no unique current assignment.");
                        DeathAdderV3ButtonClient.ValidateBindingApply(originals[0], binding);
                    }
                    baselineTime = DateTimeOffset.Now;
                }
                Check();
                steps[0] = steps[0] with { Status = MousePresetStepStatus.Confirmed, Message = "All requested values and original assignments validated; no write." };

                index = 1; Check(); attempted = true;
                var d = await controls.ApplyActiveDpiAsync(target.Dpi.X, target.Dpi.Y, token).ConfigureAwait(false);
                var dpiOk = d.Success && d.Actual == target.Dpi;
                steps[index] = steps[index] with
                {
                    Status = dpiOk ? (d.WriteAttempted ? MousePresetStepStatus.Confirmed : MousePresetStepStatus.Unchanged) : Failure(d.WriteAttempted),
                    WriteAttempted = d.WriteAttempted, Before = d.Previous is { } oldDpi ? $"{oldDpi.X} / {oldDpi.Y}" : null,
                    Actual = d.Actual is { } actualDpi ? $"{actualDpi.X} / {actualDpi.Y}" : null, Message = d.Message,
                    DpiReadback = dpiOk ? d.Actual : null, ReadAt = dpiOk ? DateTimeOffset.Now : null,
                };
                if (!dpiOk) return Result();

                index = 2; attempted = false; Check(); attempted = true;
                var p = await controls.ApplyPollingRateAsync(target.PollingRate, token).ConfigureAwait(false);
                var pollingOk = p.Success && p.Actual == target.PollingRate;
                steps[index] = steps[index] with
                {
                    Status = pollingOk ? (p.WriteAttempted ? MousePresetStepStatus.Confirmed : MousePresetStepStatus.Unchanged) : Failure(p.WriteAttempted),
                    WriteAttempted = p.WriteAttempted, Before = p.Previous is { } oldRate ? $"{(int)oldRate} Hz" : null,
                    Actual = p.Actual is { } actualRate ? $"{(int)actualRate} Hz" : null, Message = p.Message,
                    PollingReadback = pollingOk ? p.Actual : null, ReadAt = pollingOk ? DateTimeOffset.Now : null,
                };
                if (!pollingOk) return Result();

                foreach (var (id, binding) in assignments)
                {
                    index++; attempted = false; Check();
                    var original = bank!.Assignments.Single(a => a.Button == id);
                    attempted = true;
                    var b = binding.Action is { } action
                        ? await buttons.ApplyButtonAsync(original, action, token).ConfigureAwait(false)
                        : await buttons.ApplyKeyboardAsync(original, binding.Keyboard!, token).ConfigureAwait(false);
                    var buttonOk = b.Success && b.Actual is { Profile: 1, Layer: 0 } actual && actual.Button == id && actual.KnownBinding == binding;
                    steps[index] = steps[index] with
                    {
                        Status = buttonOk ? (b.WriteAttempted ? MousePresetStepStatus.Confirmed : MousePresetStepStatus.Unchanged) : Failure(b.WriteAttempted),
                        WriteAttempted = b.WriteAttempted, Before = b.Previous?.Description, Actual = b.Actual?.Description, Message = b.Message,
                        ButtonReadback = buttonOk ? b.Actual : null, ReadAt = buttonOk ? DateTimeOffset.Now : null,
                    };
                    if (!buttonOk) return Result();
                }
                return Result();
            }
            catch (Exception ex)
            {
                // A throwing backend may have sent a write. Never retry, restore
                // automatically, or continue to the next requested setting.
                steps[index] = steps[index] with { Status = Failure(attempted), WriteAttempted = attempted ? null : false,
                    Message = (attempted ? "Outcome unknown; no further operation or recovery attempted. " : "No setting write attempted for this step. ") + ex.Message };
                return Result();
            }
            void Check() { token.ThrowIfCancellationRequested(); RequirePermission(); }
        }
        finally { _gate.Release(); }
    }
    private void RequirePermission()
    {
        if (!canWrite()) throw new InvalidOperationException("Mouse preset hardware changes are locked.");
    }
    private static MousePresetStepStatus Failure(bool attempted) => attempted ? MousePresetStepStatus.Uncertain : MousePresetStepStatus.Failed;
}
