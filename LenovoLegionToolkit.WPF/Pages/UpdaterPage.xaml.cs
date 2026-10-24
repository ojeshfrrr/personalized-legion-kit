using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Windows;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class UpdaterPage
{
    private readonly UpdateChecker _checker = IoCContainer.Resolve<UpdateChecker>();
    private readonly UpdateSettings _settings = IoCContainer.Resolve<UpdateSettings>();
    private bool _loaded;
    private bool _checkedHere;

    public UpdaterPage() => InitializeComponent();

    private async void UpdaterPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        await _preferences.RefreshAsync();
        await RefreshStatusAsync(); // Cache only: navigation never downloads or checks the network.
    }
    private void UpdaterPage_Unloaded(object sender, RoutedEventArgs e) => _loaded = false;

    private async void Preferences_CheckCompleted(object? sender, EventArgs e)
    {
        _checkedHere = true;
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var updates = await _checker.GetUpdatesAsync();
        if (!_loaded) return;
        _availableUpdateButton.IsEnabled = updates.Length > 0 && !_checker.Disable && !Folders.IsReadOnlySession;
        _updateStatus.Text = Folders.IsReadOnlySession ? "Updater is locked in this read-only session." :
            _checker.Disable ? "Update checks are disabled for this session." :
            updates.Length > 0 ? $"Version {updates[0].Version} is available." :
            !_checkedHere ? "Check for updates to find a newer version." :
            _checker.Status == UpdateCheckStatus.RateLimitReached ? "GitHub's request limit was reached. Try again later." :
            _checker.Status == UpdateCheckStatus.Error ? "The update check failed. Check your connection and try again." :
            "You are up to date for the selected channel.";
        _lastCheck.Text = _settings.Store.LastUpdateCheckDateTime is { } date
            ? $"Last check: {date.ToLocalTime():g}" : "No update check has been recorded.";
    }

    private void AvailableUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!Folders.IsReadOnlySession && !_checker.Disable && App.Current.MainWindow is MainWindow main)
            main.ShowUpdateWindow();
    }
    private void ReleasePage_Click(object sender, RoutedEventArgs e) => new Uri(ProjectBranding.ReleasesUrl).Open();

    // Updating software does not require unlocking laptop controls. The exception
    // is scoped to this page and these exact controls, never to hardware pages.
    public bool IsUpdaterInputSource(DependencyObject? source) =>
        IsUpdaterInputSourceCore(source, this, Folders.IsReadOnlySession);

    private static bool IsUpdaterInputSourceCore(DependencyObject? source, DependencyObject owner, bool readOnly)
    {
        var allowed = false;
        while (source is not null && !ReferenceEquals(source, owner))
        {
            if (source is FrameworkElement element)
            {
                if (element.Name == "_releasePageButton") allowed = true;
                if (!readOnly && (element.Name is "_checkUpdatesButton" or
                    "_updateChannelComboBox" or "_updateCheckFrequencyComboBox" or "_availableUpdateButton")) allowed = true;
            }
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return ReferenceEquals(source, owner) && allowed;
    }
}
