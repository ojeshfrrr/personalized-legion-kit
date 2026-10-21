using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.WPF.Resources;
using Wpf.Ui.Common;

namespace LenovoLegionToolkit.WPF.Controls.Dashboard;

public class BatteryModeControl : AbstractComboBoxFeatureCardControl<BatteryState>
{
    public BatteryModeControl()
    {
        Icon = SymbolRegular.BatteryCharge24;
        Title = Resource.BatteryModeControl_Title;
        Subtitle = Resource.BatteryModeControl_Message;
    }
    protected override void OnConfirmationChanged()
    {
        var confirmed = Confirmation.LastConfirmed is { } state
            ? state.GetDisplayName() : "Unavailable";
        var status = Confirmation.IsPending
            ? $"Applying {Confirmation.Requested?.GetDisplayName()}… Last confirmed: {confirmed}."
            : Confirmation.Error is not null
                ? $"Change not verified. Last confirmed: {confirmed}."
                : $"Device-reported mode: {confirmed}.";
        Subtitle = $"{status} Numeric charge thresholds are not reported by this API.";
    }
}
