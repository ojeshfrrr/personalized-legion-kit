using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.SoftwareDisabler;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Controls.Settings;
using Wpf.Ui.Common;
using CustomControls = LenovoLegionToolkit.WPF.Controls.Custom;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class SettingsPage
{
    public static SettingsPage? Instance { get; private set; }
    public static string? PendingTabKey { get; set; }

    private SettingsAppearanceControl? _appearanceControl;
    private SettingsAppBehaviorControl? _appBehaviorControl;
    private SettingsSoftwareControlControl? _softwareControlControl;
    private SettingsSpecialKeyControl? _specialKeyControl;
    private SettingsDisplayControl? _displayControl;
    private SettingsUpdateControl? _updateControl;
    private SettingsPowerControl? _powerControl;
    private SettingsIntegrationsControl? _integrationsControl;

    private const double CompactNavThreshold = 560;
    private const double CompactNavWidth = 48;
    private const double NormalNavWidth = 184;

    private bool _isInitialized;
    private bool _isInitializing;
    private DataTemplate? _defaultNavTemplate;

    public SettingsPage()
    {
        InitializeComponent();
        Instance = this;
        IsVisibleChanged += SettingsPage_IsVisibleChanged;
        SizeChanged += (_, e) => { if (e.WidthChanged && _isInitialized) UpdateNavLayout(); };
    }

    public void ApplyPendingTab()
    {
        if (!_isInitialized || string.IsNullOrEmpty(PendingTabKey)) return;

        var items = _navigationListBox.ItemsSource as IEnumerable<NavigationItem>;
        var target = items?.FirstOrDefault(i => i.Key == PendingTabKey);

        if (target != null)
            _navigationListBox.SelectedItem = target;

        PendingTabKey = null;
    }

    private async void SettingsPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        try
        {
            if (IsVisible)
                await InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to initialize settings page.", ex);
        }
    }

    private async Task InitializeAsync()
    {
        if (!_isInitialized)
        {
            _isInitializing = true;
            try
            {
                // Navigation must remain available even when an unrelated pane
                // cannot initialize. Construct only the selected section below.
                InitializeNavigationItems();
                _isInitialized = true;
            }
            finally { _isInitializing = false; }
        }

        await RefreshCurrentControlAsync();
    }

    private void InitializeNavigationItems()
    {
        var navigationItems = new List<NavigationItem>
        {
            new() { Key = "AppBehavior", Title = WPF.Resources.Resource.SettingsPage_Category_AppBehavior, Icon = SymbolRegular.Apps24 },
            new() { Key = "Appearance", Title = WPF.Resources.Resource.SettingsPage_Category_Appearance, Icon = SymbolRegular.PaintBrush24 },
            new() { Key = "Power", Title = WPF.Resources.Resource.SettingsPage_Category_Power, Icon = SymbolRegular.Battery024 },
            new() { Key = "Display", Title = WPF.Resources.Resource.SettingsPage_Category_Display, Icon = SymbolRegular.Desktop24 },
            new() { Key = "SmartKeys", Title = WPF.Resources.Resource.SettingsPage_Category_SmartKeys, Icon = SymbolRegular.Keyboard24 },
            new() { Key = "SoftwareControl", Title = WPF.Resources.Resource.SettingsPage_Category_SoftwareControl, Icon = SymbolRegular.ShieldTask24 },
            new() { Key = "Updates", Title = WPF.Resources.Resource.SettingsPage_Category_Updates, Icon = SymbolRegular.ArrowSync24 },
            new() { Key = "Integrations", Title = WPF.Resources.Resource.SettingsPage_Category_Integrations, Icon = SymbolRegular.PlugConnected24 }
        };

        _navigationListBox.ItemsSource = navigationItems;

        _defaultNavTemplate ??= _navigationListBox.ItemTemplate;

        UpdateNavLayout();

        if (!string.IsNullOrEmpty(PendingTabKey))
        {
            var target = navigationItems.FirstOrDefault(i => i.Key == PendingTabKey);
            _navigationListBox.SelectedItem = target ?? navigationItems[0];
            PendingTabKey = null;
        }
        else
        {
            _navigationListBox.SelectedIndex = 0;
        }
    }

    private void UpdateNavLayout()
    {

        var isNarrow = ActualWidth < CompactNavThreshold;
        _navColumn.Width = new GridLength(isNarrow ? CompactNavWidth : NormalNavWidth);
        _navigationListBox.ItemTemplate = isNarrow
            ? (DataTemplate)Resources["CompactNavTemplate"]
            : _defaultNavTemplate;
    }

    private async void NavigationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _navigationListBox.SelectedItem is not NavigationItem selectedItem)
            return;

        await ShowControlAsync(selectedItem.Key);
    }

    private async Task ShowControlAsync(string key)
    {
        try
        {
            UserControl? control = key switch
            {
                "Appearance" => _appearanceControl ??= new SettingsAppearanceControl(),
                "AppBehavior" => _appBehaviorControl ??= new SettingsAppBehaviorControl(),
                "SoftwareControl" => GetSoftwareControl(),
                "SmartKeys" => _specialKeyControl ??= new SettingsSpecialKeyControl(),
                "Display" => _displayControl ??= new SettingsDisplayControl(),
                "Updates" => _updateControl ??= new SettingsUpdateControl(),
                "Power" => _powerControl ??= new SettingsPowerControl(),
                "Integrations" => _integrationsControl ??= new SettingsIntegrationsControl(),
                _ => null
            };
            if (control is null) return;
            _contentControl.Content = control;
            PlayTransitionAnimation();
            await RefreshControlAsync(key);
        }
        catch (Exception ex) { ShowSectionFailure(key, ex); }
    }

    private SettingsSoftwareControlControl GetSoftwareControl()
    {
        if (_softwareControlControl is null)
        {
            _softwareControlControl = new SettingsSoftwareControlControl();
            _softwareControlControl.FnKeysStatusChanged += SoftwareControl_FnKeysStatusChanged;
        }
        return _softwareControlControl;
    }

    private void ShowSectionFailure(string key, Exception exception)
    {
        Log.Instance.Trace($"Failed to load settings section '{key}'.", exception);
        if (_navigationListBox.SelectedItem is not NavigationItem selected || selected.Key != key) return;
        _contentControl.Content = new TextBlock
        {
            Text = "This settings section could not be loaded. Select another section and return to retry.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("ProjectLegionNoticeStyle")
        };
    }

    private async Task RefreshCurrentControlAsync()
    {
        if (_navigationListBox.SelectedItem is NavigationItem selectedItem)
            await ShowControlAsync(selectedItem.Key);
    }

    private async Task RefreshControlAsync(string key)
    {
        try
        {
            switch (key)
            {
                case "Appearance":
                    if (_appearanceControl is not null) await _appearanceControl.RefreshAsync();
                    break;
                case "AppBehavior":
                    if (_appBehaviorControl is not null) await _appBehaviorControl.RefreshAsync();
                    break;
                case "SoftwareControl":
                    if (_softwareControlControl is not null) await _softwareControlControl.RefreshAsync();
                    break;
                case "SmartKeys":
                    if (_specialKeyControl is not null) await _specialKeyControl.RefreshAsync();
                    break;
                case "Display":
                    if (_displayControl is not null) await _displayControl.RefreshAsync();
                    break;
                case "Updates":
                    if (_updateControl is not null) await _updateControl.RefreshAsync();
                    break;
                case "Power":
                    if (_powerControl is not null) await _powerControl.RefreshAsync();
                    break;
                case "Integrations":
                    if (_integrationsControl is not null) await _integrationsControl.RefreshAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowSectionFailure(key, ex);
        }
    }

    private void SoftwareControl_FnKeysStatusChanged(object? sender, SoftwareStatus fnKeysStatus)
    {
        try
        {
            _specialKeyControl?.UpdateFnKeysVisibility(fnKeysStatus);
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to update FnKeys visibility.", ex);
        }
    }

    private void PlayTransitionAnimation()
    {
        if (TryFindResource("ContentTransitionAnimation") is Storyboard storyboard)
            storyboard.Begin();
    }

    private class NavigationItem
    {
        public string Key { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public SymbolRegular Icon { get; init; }
    }
}
