using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.WPF.Controls;

public abstract class AbstractRefreshingControl : UserControl
{
    private Task? _refreshTask;
    private bool _isRefreshing;
    private object? _normalContent;
    private bool _showingStatus;
    private bool _loadHandlersAttached;

    protected virtual string StatusTitle => "Device information";

    protected bool IsRefreshing => _isRefreshing;

    protected virtual bool DisablesWhileRefreshing => true;

    protected AbstractRefreshingControl()
    {
        IsEnabled = false;

        Loaded += RefreshingControl_Loaded;
        Unloaded += RefreshingControl_Unloaded;
        IsVisibleChanged += RefreshingControl_IsVisibleChanged;
    }

    private void RefreshingControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadHandlersAttached) return;
        _loadHandlersAttached = true;
        OnFinishedLoading();
    }

    private void RefreshingControl_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_loadHandlersAttached) return;
        _loadHandlersAttached = false;
        OnFinishedUnloading();
    }

    protected virtual void OnFinishedUnloading() { }

    protected abstract void OnFinishedLoading();

    private async void RefreshingControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            await RefreshAsync();
    }

    protected async Task RefreshAsync()
    {
        if (_refreshTask is not null)
        {
            await _refreshTask;
            return;
        }
        _isRefreshing = true;
        if (_showingStatus)
        {
            Content = _normalContent;
            _showingStatus = false;
        }
        _normalContent = Content;
        Log.Instance.Trace($"Refreshing control... [feature={GetType().Name}]");

        var exceptions = false;

        try
        {
            if (DisablesWhileRefreshing)
                IsEnabled = false;

            _refreshTask ??= OnRefreshAsync();
            await _refreshTask;
        }
        catch (NotSupportedException)
        {
            exceptions = true;
            ShowReadStatus($"{StatusTitle}: unsupported on this device.");

            Log.Instance.Trace($"Unsupported. [feature={GetType().Name}]");
        }
        catch (Exception ex)
        {
            exceptions = true;
            ShowReadStatus($"{StatusTitle}: unavailable. The current state could not be read.", ex.Message);

            Log.Instance.Trace($"Exception when refreshing control. [feature={GetType().Name}]", ex);
        }
        finally
        {
            _refreshTask = null;
            _isRefreshing = false;

            if (exceptions)
            {
                Visibility = Visibility.Visible;
                IsEnabled = true;
            }
            else
            {
                IsEnabled = true;
            }
        }
    }

    protected abstract Task OnRefreshAsync();

    private void ShowReadStatus(string message, string? detail = null)
    {
        Content = new TextBlock
        {
            Text = message,
            ToolTip = detail,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 10, 12, 10)
        };
        _showingStatus = true;
    }
}
