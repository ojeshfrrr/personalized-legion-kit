using System;
using System.IO;
using System.Linq;
using LenovoLegionToolkit.Lib.Services.Mouse;
using LenovoLegionToolkit.WPF.Presentation;

internal static class MouseDpiSliderChecks
{
    public static void Run(Action<bool, string> check)
    {
        var values = Enumerable.Range(0, 599).Select(i => 100 + i * 50).ToArray();
        check(values.All(dpi => MouseDpiScale.FromPosition(MouseDpiScale.ToPosition(dpi)) == dpi),
            "all 599 verified DPI values round-trip through the logarithmic slider exactly");
        var samples = Enumerable.Range(0, 10001).Select(i => MouseDpiScale.FromPosition(i / 10000d)).ToArray();
        check(samples.All(MouseDpiScale.IsValid) && samples.Zip(samples.Skip(1), (a, b) => a <= b).All(b => b) &&
            samples[0] == 100 && samples[^1] == 30000,
            "slider dragging stays monotonic and quantized to 50 DPI across the full range");
        check(MouseDpiScale.ToPosition(6400) - MouseDpiScale.ToPosition(400) > 0.48 &&
            MouseDpiScale.ToPosition(1600) > 0.48 && MouseDpiScale.ToPosition(1600) < 0.49,
            "logarithmic mapping gives useful travel to low and medium DPI rather than compressing it near zero");
        check(MouseDpiScale.FromPosition(-1) == 100 && MouseDpiScale.FromPosition(2) == 30000 &&
            Reject(() => MouseDpiScale.FromPosition(double.NaN)) && Reject(() => MouseDpiScale.FromPosition(double.PositiveInfinity)) &&
            Reject(() => MouseDpiScale.ToPosition(125)), "DPI scale clamps pointer bounds and rejects nonfinite or unsupported values");
        check(values.All(d => MouseDpiScale.Move(d, 1) == Math.Min(30000, d + 50) &&
                             MouseDpiScale.Move(d, -1) == Math.Max(100, d - 50)) &&
            MouseDpiScale.Move(1600, int.MaxValue) == 30000 && MouseDpiScale.Move(1600, int.MinValue) == 100,
            "exact keyboard steps remain 50 DPI at every scale position and saturate without overflow");
        var draft = new MouseDpiDraft();
        check(!draft.TryGet(out _) && draft.X.Length == 0 && draft.Y.Length == 0,
            "DPI editor starts unavailable instead of inventing a live or requested value");
        draft.LoadConfirmed(new(1600, 1800));
        check(!draft.Linked && draft.TryGet(out var current) && current == new MouseDpi(1600, 1800),
            "asymmetric device readback preserves both axes and safely unlinks the draft");
        draft.SetLinked(true);
        check(draft.X == "1600" && draft.Y == "1600", "linking uses requested X locally without a device operation");
        draft.SetText(false, "1250");
        check(draft.X == "1250" && draft.Y == "1250", "linked exact Y input updates both requested axes");
        draft.SetPosition(true, MouseDpiScale.ToPosition(800));
        check(draft.X == "800" && draft.Y == "800", "linked slider adjusts both requested axes");
        draft.SetLinked(false); draft.SetPosition(false, MouseDpiScale.ToPosition(2450));
        check(draft.X == "800" && draft.Y == "2450", "unlinked sliders preserve the other requested axis");
        draft.SetText(true, "1300");
        check(draft.X == "1300" && draft.Y == "2450", "unlinked numeric input preserves independent Y");
        foreach (var invalid in new[] { "", "16", "1234", "30001", "1,600", "1600.0", " 1600", "-100" })
        {
            draft.LoadConfirmed(new(1600, 1600)); draft.SetLinked(true); draft.SetText(true, invalid);
            check(!draft.TryGet(out _) && draft.X == invalid && draft.Y == "1600",
                "incomplete or invalid numeric DPI stays unverified and is never silently rounded: " + invalid);
        }
        draft.SetLinked(false); draft.SetLinked(true);
        check(draft.X == "1600" && draft.Y == "1600", "linking can use valid Y when requested X is incomplete");
        draft.LoadConfirmed(new(900, 1200));
        check(!draft.Linked && draft.X == "900" && draft.Y == "1200",
            "reset/readback restores exact asymmetric values without linked-axis overwrite");
        draft.LoadConfirmed(new(125, 1600));
        check(draft.X == "125" && draft.Y == "1600" && !draft.TryGet(out _),
            "unusual readback remains exact and cannot silently become an applicable rounded draft");
        var editor = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Presentation", "MouseDpiDraftEditor.cs"));
        check(new[] { "ApplyDpiAsync", "ApplyActiveDpiAsync", "HardwareCommandRunner", "IMouseControlService", "ReadActiveDpiAsync", ".Save(" }
            .All(term => !editor.Contains(term, StringComparison.Ordinal)),
            "slider typing linking and keyboard presenter has no hardware or persistence API");
        var source = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "LenovoLegionToolkit.WPF", "Pages", "MousePage.xaml.cs"));
        check(source.Contains("_session.CanEditDpi && !PresetBusy", StringComparison.Ordinal) &&
            source.Contains("await HardwareCommandRunner.RunAsync(() => _session.ApplyDpiAsync(requested.X, requested.Y))", StringComparison.Ordinal) &&
            source.Contains("_currentDpiText.Text = dpi is not null", StringComparison.Ordinal),
            "DPI editor respects existing permission/batch guards and keeps the original verified explicit Apply and genuine readback");
        var reset = source[source.IndexOf("private void Reset_Click", StringComparison.Ordinal)..source.IndexOf("private async void Refresh_Click", StringComparison.Ordinal)];
        check(reset.Contains("_dpiEditor.SetConfirmed(current.Value)", StringComparison.Ordinal) && !reset.Contains("Apply", StringComparison.Ordinal),
            "Reset inputs restores the confirmed local draft without applying hardware settings");
    }
    private static bool Reject(Action action)
    { try { action(); return false; } catch (ArgumentOutOfRangeException) { return true; } }
}
