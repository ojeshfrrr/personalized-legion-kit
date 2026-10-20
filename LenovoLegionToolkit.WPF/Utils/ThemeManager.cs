using System;
using System.Windows;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Extensions;
using BackgroundType = Wpf.Ui.Appearance.BackgroundType;
using Accent = Wpf.Ui.Appearance.Accent;

namespace LenovoLegionToolkit.WPF.Utils;

public class ThemeManager
{
    private static readonly RGBColor DefaultAccentColor = new(255, 51, 79);

    private readonly ApplicationSettings _settings;

    public event EventHandler? ThemeApplied;

    public ThemeManager(ApplicationSettings settings)
    {
        _settings = settings;
    }

    public void Apply()
    {
        Wpf.Ui.Appearance.Theme.Apply(Wpf.Ui.Appearance.ThemeType.Dark, GetBackgroundType(_settings.Store.BackdropType), false);

        var accentColor = GetAccentColor().ToColor();
        Accent.Apply(systemAccent: accentColor,
            primaryAccent: accentColor,
            secondaryAccent: accentColor,
            tertiaryAccent: accentColor);

        ApplyProjectLegionPalette(accentColor);
        ThemeApplied?.Invoke(this, EventArgs.Empty);
    }

    public RGBColor GetAccentColor()
    {
        switch (_settings.Store.AccentColorSource)
        {
            case AccentColorSource.Custom:
                return _settings.Store.AccentColor ?? DefaultAccentColor;
            case AccentColorSource.System:
                try
                {
                    return SystemTheme.GetAccentColor();
                }
                catch (Exception ex)
                {
                    Log.Instance.Trace($"Couldn't check system accent color; using default.", ex);
                    return DefaultAccentColor;
                }
            default:
                return DefaultAccentColor;
        }
    }

    public static BackgroundType GetBackgroundType(WindowBackdropType backdropType)
    {
        return backdropType switch
        {
            WindowBackdropType.Mica => BackgroundType.Mica,
            WindowBackdropType.Acrylic => BackgroundType.Acrylic,
            WindowBackdropType.Tabbed => BackgroundType.Tabbed,
            _ => BackgroundType.None
        };
    }

    private static void ApplyProjectLegionPalette(System.Windows.Media.Color accentColor)
    {
        var resources = Application.Current.Resources;
        resources["ProjectLegionAccentBrush"] = new System.Windows.Media.SolidColorBrush(accentColor);
        resources["ApplicationBackgroundBrush"] = Application.Current.FindResource("ProjectLegionBackgroundBrush");
        resources["ApplicationPageBackgroundBrush"] = Application.Current.FindResource("ProjectLegionContentBrush");
        resources["SolidBackgroundFillColorBase"] = (System.Windows.Media.Color)Application.Current.FindResource("ProjectLegionSurfaceColor");
        resources["SolidBackgroundFillColorBaseBrush"] = Application.Current.FindResource("ProjectLegionSurfaceBrush");
        resources["SolidBackgroundFillColorSecondary"] = Application.Current.FindResource("ProjectLegionBackgroundColor");
        resources["SolidBackgroundFillColorSecondaryBrush"] = Application.Current.FindResource("ProjectLegionBackgroundBrush");
        resources["CardBackgroundFillColorDefaultBrush"] = Application.Current.FindResource("ProjectLegionSurfaceBrush");
        resources["CardStrokeColorDefaultBrush"] = Application.Current.FindResource("ProjectLegionBorderBrush");
        resources["CardStrokeColorDefaultSolidBrush"] = Application.Current.FindResource("ProjectLegionBorderBrush");
        resources["ControlFillColorDefaultBrush"] = Application.Current.FindResource("ProjectLegionSurfaceBrush");
        resources["ControlFillColorSecondaryBrush"] = Application.Current.FindResource("ProjectLegionHoverBrush");
        resources["ControlFillColorTertiaryBrush"] = Application.Current.FindResource("ProjectLegionSelectedBrush");
        resources["ControlStrokeColorDefaultBrush"] = Application.Current.FindResource("ProjectLegionBorderBrush");
        resources["ControlStrokeColorSecondaryBrush"] = Application.Current.FindResource("ProjectLegionBorderBrush");
        resources["ControlElevationBorderBrush"] = Application.Current.FindResource("ProjectLegionBorderBrush");
        resources["DividerStrokeColorDefaultBrush"] = Application.Current.FindResource("ProjectLegionBorderBrush");
        resources["KeyboardFocusBorderColorBrush"] = new System.Windows.Media.SolidColorBrush(accentColor);
        resources["TextFillColorPrimaryBrush"] = Application.Current.FindResource("ProjectLegionTextBrush");
        resources["TextFillColorSecondaryBrush"] = Application.Current.FindResource("ProjectLegionSecondaryTextBrush");
        resources["TextFillColorTertiaryBrush"] = Application.Current.FindResource("ProjectLegionMutedTextBrush");
        resources["TextFillColorDisabledBrush"] = Application.Current.FindResource("ProjectLegionMutedTextBrush");
        resources["LayerFillColorDefaultBrush"] = Application.Current.FindResource("ProjectLegionSurfaceBrush");
        resources["LayerFillColorAltBrush"] = Application.Current.FindResource("ProjectLegionBackgroundBrush");

        resources["TextFillColorPrimary"] = (System.Windows.Media.Color)Application.Current.FindResource("ProjectLegionTextColor");
        resources["TextFillColorSecondary"] = (System.Windows.Media.Color)Application.Current.FindResource("ProjectLegionSecondaryTextColor");
        resources["ControlFillColorSecondary"] = (System.Windows.Media.Color)Application.Current.FindResource("ProjectLegionHoverColor");
        resources["ControlFillColorDefault"] = (System.Windows.Media.Color)Application.Current.FindResource("ProjectLegionSurfaceColor");
        resources["SystemAccentColor"] = accentColor;
        resources["SystemAccentColorSecondary"] = accentColor;
        resources["TextOnAccentFillColorPrimary"] = System.Windows.Media.Colors.White;
    }
}
