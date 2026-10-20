using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Features.WhiteKeyboardBacklight;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Controls.KeyboardBacklight.RGB;
using LenovoLegionToolkit.WPF.Controls.Dashboard;
using LenovoLegionToolkit.WPF.Controls.KeyboardBacklight.Spectrum;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows.Utils;
using Microsoft.Win32;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class KeyboardBacklightPage
{
    private readonly ApplicationSettings _settings = IoCContainer.Resolve<ApplicationSettings>();
    private const string REGISTRY_PATH = @"SOFTWARE\Microsoft\Lighting";
    private const string REGISTRY_VALUE = "AmbientLightingEnabled";

    public KeyboardBacklightPage() => InitializeComponent();

    private async void KeyboardBacklightPage_Initialized(object? sender, EventArgs e)
    {
        _titleTextBlock.Visibility = Visibility.Visible;

        try
        {
            if (HardwareRuntime.ControlsEnabled)
                await CheckDynamicLightingWarningAsync();
            await LoadKeyboardBacklightControlAsync();
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to load keyboard backlight control", ex);
            ShowErrorMessage();
        }
        finally
        {
            _loader.IsLoading = false;
        }
    }

    private async Task CheckDynamicLightingWarningAsync()
    {
        if (_settings.Store.DynamicLightingWarningDontShowAgain)
            return;

        try
        {
            bool isDynamicLightingEnabled = IsDynamicLightingEnabled();
            if (!isDynamicLightingEnabled)
            {
                return;
            }

            var result = await ShowDynamicLightingWarningDialogAsync();
            if (result.ShouldDisable)
            {
                DisableDynamicLightingViaRegistry();
            }

            if (result.DontShowAgain)
            {
                _settings.Store.DynamicLightingWarningDontShowAgain = true;
                _settings.SynchronizeStore();
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to load keyboard backlight control", ex);
        }
    }

    private bool IsDynamicLightingEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(REGISTRY_PATH);
            if (key?.GetValue(REGISTRY_VALUE) is int intValue)
            {
                return intValue == 1;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to load keyboard backlight control", ex);
            return false;
        }
    }

    private async Task<(bool ShouldDisable, bool DontShowAgain)> ShowDynamicLightingWarningDialogAsync()
    {
        return await await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var result = await MessageBoxHelper.ShowAsync(
                this,
                Resource.Warning,
                Resource.KeyboardBacklightPage_DynamicLightingEnabled,
                true,
                Resource.Yes,
                Resource.No);

            return (result.Result, result.DontShowAgain);
        });
    }

    private void DisableDynamicLightingViaRegistry()
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = @$"add ""HKEY_CURRENT_USER\{REGISTRY_PATH}"" /v {REGISTRY_VALUE} /t REG_DWORD /d 0 /f",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            Process.Start(processStartInfo)?.WaitForExit();
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to load keyboard backlight control", ex);
        }
    }

    private async Task LoadKeyboardBacklightControlAsync()
    {
        try
        {
            bool anyAdded = false;

            var spectrumController = IoCContainer.Resolve<SpectrumKeyboardBacklightController>();
            var rgbController = IoCContainer.Resolve<RGBKeyboardBacklightController>();

            if (await spectrumController.Is1ZoneKeyboardAsync() && await spectrumController.IsSupportedAsync())
            {
                var control = new SpectrumRGBKeyboard1ZoneControl();
                _content.Children.Add(control);
                anyAdded = true;
                Log.Instance.Trace($"Added 1-zone RGB keyboard control.");

                return;
            }

            if (await spectrumController.IsSupportedAsync())
            {
                var control = new SpectrumKeyboardBacklightControl();
                _content.Children.Add(control);
                anyAdded = true;
                Log.Instance.Trace($"Added Spectrum keyboard control.");

                return;
            }

            if (await rgbController.IsSupportedAsync())
            {
                _content.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "The RGB preset below is saved configuration; firmware readback is unavailable through this device API.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                });
                var control = new RGBKeyboardBacklightControl();
                _content.Children.Add(control);
                anyAdded = true;
                Log.Instance.Trace($"Added RGB keyboard control.");

                return;
            }

            if (await IoCContainer.Resolve<WhiteKeyboardBacklightFeature>().IsSupportedAsync())
            {
                _content.Children.Add(new WhiteKeyboardBacklightControl());
                anyAdded = true;
            }
            if (await IoCContainer.Resolve<OneLevelWhiteKeyboardBacklightFeature>().IsSupportedAsync())
            {
                _content.Children.Add(new OneLevelWhiteKeyboardBacklightControl());
                anyAdded = true;
            }
            if (!anyAdded)
            {
                ShowNoKeyboardsMessage();
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to load keyboard backlight control", ex);
            ShowNoKeyboardsMessage();
        }
    }

    private void ShowNoKeyboardsMessage()
    {
        _noKeyboardsText.Visibility = Visibility.Visible;
    }

    private void ShowErrorMessage()
    {
        _noKeyboardsText.Text = "Keyboard lighting information could not be read. Check device support and driver availability.";
        _noKeyboardsText.Visibility = Visibility.Visible;
    }

    public static async Task<bool> IsSupportedAsync()
    {
        try
        {
            var spectrumController = IoCContainer.Resolve<SpectrumKeyboardBacklightController>();
            if (await spectrumController.IsSupportedAsync())
            {
                return true;
            }

            var rgbController = IoCContainer.Resolve<RGBKeyboardBacklightController>();
            if (await rgbController.IsSupportedAsync())
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Error checking keyboard support: {ex.Message}");
            return false;
        }
    }
}
