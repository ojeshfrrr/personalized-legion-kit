using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private readonly ApplicationSettings _settings = IoCContainer.Resolve<ApplicationSettings>();
    private readonly UpdateChecker _updateChecker = IoCContainer.Resolve<UpdateChecker>();
    private readonly UpdateSettings _updateSettings = IoCContainer.Resolve<UpdateSettings>();

    private bool _isRefreshing;
    private bool _checking;

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

        if (_updateChecker.Disable)
        {
            _checkUpdatesCard.Visibility = Visibility.Collapsed;
            _updateCheckFrequencyCard.Visibility = Visibility.Collapsed;
            _updateChannelCard.Visibility = Visibility.Collapsed;
            _updateMethodCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            _checkUpdatesButton.Visibility = Visibility.Visible;
            _updateCheckFrequencyComboBox.Visibility = Visibility.Visible;
            _updateCheckFrequencyComboBox.SetItems(Enum.GetValues<UpdateCheckFrequency>(), _updateSettings.Store.UpdateCheckFrequency, t => t.GetDisplayName());
            _updateMethodCard.Visibility = Visibility.Collapsed;
            _updateChannelComboBox.Visibility = Visibility.Visible;
            var channels = new[] { UpdateChannel.Stable, UpdateChannel.Beta };
            _updateChannelComboBox.SetItems(channels, _updateSettings.Store.UpdateChannel,
                t => t == UpdateChannel.Stable ? "Stable" : "Preview + stable");
        }

        _isRefreshing = false;

        return Task.CompletedTask;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _checking || Folders.IsReadOnlySession)
            return;

        if (App.Current.MainWindow is not MainWindow mainWindow)
            return;

        _checking = true;
        _checkUpdatesButton.IsEnabled = false;
        try
        {
            await SnackbarHelper.ShowAsync(Resource.SettingsPage_CheckUpdates_Started_Title, type: SnackbarType.Info);
            await mainWindow.CheckForUpdatesAsync(true);
        }
        finally
        {
            _checking = false;
            _checkUpdatesButton.IsEnabled = !_updateChecker.Disable && !Folders.IsReadOnlySession;
        }
    }

    private void UpdateChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing || Folders.IsReadOnlySession)
            return;

        if (!_updateChannelComboBox.TryGetSelectedItem(out UpdateChannel updateChannel))
            return;

        _updateSettings.Store.UpdateChannel = updateChannel;
        _updateSettings.SynchronizeStore();
    }

    private void UpdateMethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing || Folders.IsReadOnlySession)
            return;

        if (!_updateMethodComboBox.TryGetSelectedItem(out UpdateMethod updateMethod))
            return;

        _updateSettings.Store.UpdateMethod = updateMethod;
        _updateSettings.SynchronizeStore();
    }

    private void UpdateCheckFrequencyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing || Folders.IsReadOnlySession)
            return;

        if (!_updateCheckFrequencyComboBox.TryGetSelectedItem(out UpdateCheckFrequency frequency))
            return;

        _updateSettings.Store.UpdateCheckFrequency = frequency;
        _updateSettings.SynchronizeStore();
        _updateChecker.UpdateMinimumTimeSpanForRefresh();
    }
}
