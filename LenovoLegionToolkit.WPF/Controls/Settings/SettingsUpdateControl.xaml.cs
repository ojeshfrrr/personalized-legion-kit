using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows;

namespace LenovoLegionToolkit.WPF.Controls.Settings;

public partial class SettingsUpdateControl
{
    private readonly UpdateChecker _updateChecker = IoCContainer.Resolve<UpdateChecker>();
    private readonly UpdateSettings _updateSettings = IoCContainer.Resolve<UpdateSettings>();

    private bool _isRefreshing;
    private bool _checking;
    public event EventHandler? CheckCompleted;

    public SettingsUpdateControl()
    {
        InitializeComponent();
    }

    public Task RefreshAsync()
    {
        _isRefreshing = true;
        _updatesNotice.Text = $"Installed version: {typeof(App).Assembly.GetName().Version}. " +
            (Folders.IsReadOnlySession ? "Update downloads and preferences are locked in read-only mode." :
            "Preview includes current Project Legion prereleases and stable versions. Downloads are verified ZIPs; installation is a manual folder replacement.");

        try
        {
            var enabled = !_updateChecker.Disable && !Folders.IsReadOnlySession;
            if (_updateChecker.Disable && !Folders.IsReadOnlySession)
                _updatesNotice.Text = $"Installed version: {typeof(App).Assembly.GetName().Version}. Update checks are disabled for this session. Restart without --disable-update-checker to enable them.";
            _checkUpdatesCard.Visibility = Visibility.Visible;
            _updateCheckFrequencyCard.Visibility = Visibility.Visible;
            _updateChannelCard.Visibility = Visibility.Visible;
            _checkUpdatesButton.IsEnabled = enabled && !_checking;
            _updateCheckFrequencyComboBox.IsEnabled = enabled;
            _updateChannelComboBox.IsEnabled = enabled;
            _checkUpdatesButton.Visibility = Visibility.Visible;
            _updateCheckFrequencyComboBox.Visibility = Visibility.Visible;
            _updateCheckFrequencyComboBox.SetItems(Enum.GetValues<UpdateCheckFrequency>(), _updateSettings.Store.UpdateCheckFrequency, t => t.GetDisplayName());
            _updateMethodCard.Visibility = Visibility.Collapsed;
            _updateChannelComboBox.Visibility = Visibility.Visible;
            var channels = new[] { UpdateChannel.Stable, UpdateChannel.Beta };
            _updateChannelComboBox.SetItems(channels, _updateSettings.Store.UpdateChannel,
                t => t == UpdateChannel.Stable ? "Stable" : "Preview + stable");
        }
        finally { _isRefreshing = false; }

        return Task.CompletedTask;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _checking || _updateChecker.Disable || Folders.IsReadOnlySession)
            return;

        if (App.Current.MainWindow is not MainWindow mainWindow)
            return;

        _checking = true;
        _checkUpdatesButton.IsEnabled = false;
        try
        {
            await SnackbarHelper.ShowAsync(Resource.SettingsPage_CheckUpdates_Started_Title, type: SnackbarType.Info);
            await mainWindow.CheckForUpdatesAsync(true);
            CheckCompleted?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _checking = false;
            _checkUpdatesButton.IsEnabled = !_updateChecker.Disable && !Folders.IsReadOnlySession;
        }
    }

    private void UpdateChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing || _updateChecker.Disable || Folders.IsReadOnlySession)
            return;

        if (!_updateChannelComboBox.TryGetSelectedItem(out UpdateChannel updateChannel))
            return;

        _updateSettings.Store.UpdateChannel = updateChannel;
        _updateSettings.SynchronizeStore();
    }

    private void UpdateMethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing || _updateChecker.Disable || Folders.IsReadOnlySession)
            return;

        if (!_updateMethodComboBox.TryGetSelectedItem(out UpdateMethod updateMethod))
            return;

        _updateSettings.Store.UpdateMethod = updateMethod;
        _updateSettings.SynchronizeStore();
    }

    private void UpdateCheckFrequencyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing || _updateChecker.Disable || Folders.IsReadOnlySession)
            return;

        if (!_updateCheckFrequencyComboBox.TryGetSelectedItem(out UpdateCheckFrequency frequency))
            return;

        _updateSettings.Store.UpdateCheckFrequency = frequency;
        _updateSettings.SynchronizeStore();
        _updateChecker.UpdateMinimumTimeSpanForRefresh();
    }

    // This same control is hosted in both Settings and Updater. Exempt only its
    // software-update inputs from the hardware lock, never from read-only mode.
    public bool IsUpdateInputSource(DependencyObject? source) =>
        IsUpdateInputSourceCore(source, this, Folders.IsReadOnlySession || _updateChecker.Disable);

    private static bool IsUpdateInputSourceCore(DependencyObject? source, DependencyObject owner, bool locked)
    {
        if (locked) return false;
        var allowed = false;
        while (source is not null && !ReferenceEquals(source, owner))
        {
            if (source is FrameworkElement { Name: "_checkUpdatesButton" or "_updateChannelComboBox" or "_updateCheckFrequencyComboBox" })
                allowed = true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return ReferenceEquals(source, owner) && allowed;
    }
}
