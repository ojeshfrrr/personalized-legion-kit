using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using BlackSharp.Core.Extensions;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using Markdig;
using Markdig.Wpf;
using Wpf.Ui.Controls;

namespace LenovoLegionToolkit.WPF.Windows.Utils;

public partial class UpdateWindow : IProgress<float>
{
    private readonly UpdateChecker _updateChecker = IoCContainer.Resolve<UpdateChecker>();

    private CancellationTokenSource? _downloadCancellationTokenSource;
    private ProjectRelease? _availableUpdate;
    private string? _downloadedPackagePath;
    private bool _closed;
    private bool _installing;

    public UpdateWindow()
    {
        InitializeComponent();
        _markdownViewer.Pipeline = new MarkdownPipelineBuilder().UseSupportedExtensions().Build();
    }

    private void Hyperlink_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0)
        {
            uri.Open();
            e.Handled = true;
        }
    }

    private async void UpdateWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var updates = await _updateChecker.GetUpdatesAsync();

        if (updates.Length > 0)
        {
            _availableUpdate = updates[0];
            var stringBuilder = new StringBuilder();
            foreach (var update in updates)
            {
                stringBuilder.AppendLine("**" + update.Title + "**   _(" + update.Date.ToString("D") + ")_")
                    .AppendLine()
                    .AppendLine(update.Description)
                    .AppendLine();
            }

            _markdownViewer.Markdown = stringBuilder.ToString();
        }
        _packageStatus.Text = Folders.IsReadOnlySession ? "Downloads are disabled in read-only mode." :
            _availableUpdate is null ? "No newer version is available in the selected channel." :
            _availableUpdate.Package is null ? "This release has no ZIP with verifiable SHA-256 metadata. Open the release page for details." :
            $"Version {_availableUpdate.Version} · Windows x64 ZIP · {_availableUpdate.Package.Size / 1048576d:0.0} MiB. Downloading will not install or restart the app.";
        SetDownloading(false);
    }

    private void UpdateWindow_Closing(object? sender, CancelEventArgs e)
    {
        _closed = true;
        _downloadCancellationTokenSource?.Cancel();
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellationTokenSource is not null || _availableUpdate?.Package is null || Folders.IsReadOnlySession)
            return;
        using var lifetime = _downloadCancellationTokenSource = new CancellationTokenSource();
        try
        {
            SetDownloading(true);
            _packageStatus.Text = "Downloading and checking the Windows x64 package…";
            _downloadedPackagePath = await _updateChecker.DownloadPortableUpdateAsync(_availableUpdate, this, lifetime.Token);
            if (_closed) return;
            _packageStatus.Text = "SHA-256 verified against GitHub release metadata. Install and restart keeps a separate verified backup and preserves your profile. This package is unsigned; the checksum does not verify a trusted publisher. Manual extraction remains available.";
            _openFolderButton.Visibility = Visibility.Visible;
            _installButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            if (!_closed) _packageStatus.Text = "Download cancelled. Partial downloads were removed.";
        }
        catch (SecurityException ex)
        {
            if (_closed) return;
            _packageStatus.Text = "Package verification failed. Nothing was installed.";
            var innerMsg = ex.InnerException is not null ? "\n" + ex.InnerException.Message : string.Empty;
            await SnackbarHelper.ShowAsync(Resource.UpdateWindow_SecurityError_Title, ex.Message + innerMsg, SnackbarType.Error);
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _packageStatus.Text = "The update could not be downloaded. You can retry or open the release page.";
            await SnackbarHelper.ShowAsync(Resource.UpdateWindow_DownloadError_Title, ex.Message, SnackbarType.Error);
        }
        finally
        {
            _downloadCancellationTokenSource = null;
            if (!_closed) SetDownloading(false);
        }
    }

    private void ReleasePageButton_Click(object sender, RoutedEventArgs e) =>
        new Uri(_availableUpdate?.ReleaseUrl ?? ProjectBranding.ReleasesUrl).Open();

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedPackagePath is not null && File.Exists(_downloadedPackagePath))
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_downloadedPackagePath)!) { UseShellExecute = true });
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e) => _downloadCancellationTokenSource?.Cancel();

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installing || _downloadCancellationTokenSource is not null || Folders.IsReadOnlySession || _updateChecker.Disable ||
            _downloadedPackagePath is null || _availableUpdate?.Package is null) return;
        if (System.Windows.MessageBox.Show(this,
            $"Install version {_availableUpdate.Version} and restart Project Legion?\n\nThis unsigned package will run with the application's normal privileges. It is installed side by side in Program Files with a verified previous-version backup. Your user profile is not replaced. Normal startup/shutdown behavior and configured features still apply.\n\nWindows security prompts will not be bypassed.",
            "Install verified update", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _installing = true;
        using var cancellation = _downloadCancellationTokenSource = new CancellationTokenSource();
        _installButton.IsEnabled = false; _openFolderButton.IsEnabled = false;
        try
        {
            _packageStatus.Text = "Verifying, installing side by side and backing up the previous version…";
            var prepared = await PortableUpdateInstaller.PrepareAsync(_downloadedPackagePath, _availableUpdate.Package, _availableUpdate.Version, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _packageStatus.Text = "Preparing the restart handoff…";
            await PortableUpdateInstaller.StartRestartAsync(prepared);
            cancellation.Token.ThrowIfCancellationRequested();
            await App.Current.ShutdownAsync();
        }
        catch (OperationCanceledException) { if (!_closed) _packageStatus.Text = "Installation cancelled. The current version remains available."; }
        catch (Exception ex) { if (!_closed) _packageStatus.Text = "Installation did not complete: " + ex.Message + " The current version has not been replaced."; }
        finally
        {
            _installing = false; _downloadCancellationTokenSource = null;
            if (!_closed) { _installButton.IsEnabled = !Folders.IsReadOnlySession && !_updateChecker.Disable; _openFolderButton.IsEnabled = true; }
        }
    }

    private void SetDownloading(bool isDownloading)
    {
        _installButton.IsEnabled = !isDownloading && !_installing && !Folders.IsReadOnlySession && !_updateChecker.Disable;
        if (isDownloading)
        {
            _downloadProgressBar.Visibility = Visibility.Visible;

            _downloadButton.Visibility = Visibility.Collapsed;
            _downloadButton.IsEnabled = false;

            _cancelDownloadButton.Visibility = Visibility.Visible;
            _cancelDownloadButton.IsEnabled = true;
        }
        else
        {
            _downloadProgressBar.Value = 0;
            _downloadProgressBar.IsIndeterminate = true;
            _downloadProgressBar.Visibility = Visibility.Hidden;

            _downloadButton.Visibility = _downloadedPackagePath is null ? Visibility.Visible : Visibility.Collapsed;
            _downloadButton.IsEnabled = _availableUpdate?.Package is not null && !Folders.IsReadOnlySession && _downloadedPackagePath is null;

            _cancelDownloadButton.Visibility = Visibility.Collapsed;
            _cancelDownloadButton.IsEnabled = false;
        }
    }

    public void Report(float value) => Dispatcher.Invoke(() =>
    {
        if (_closed) return;
        _downloadProgressBar.IsIndeterminate = !(value > 0);
        _downloadProgressBar.Value = value;
    });
}
