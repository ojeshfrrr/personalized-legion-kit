using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.WPF.Utils;

// One transition queue per UI owner. Cancellation/retirement finishes before a
// replacement starts, including rapid game/page/minimize transitions.
internal sealed class OptionalUiActivity
{
    private readonly FrameworkElement _element;
    private readonly Func<bool, Task> _transition;
    private readonly GamingActivityMonitor _monitor;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private bool _loaded;
    private Window? _window;
    private readonly bool _requireForeground;
    private readonly bool _suspendForGaming;
    public bool IsActive { get; private set; }
    public OptionalUiActivity(FrameworkElement element, Func<bool, Task> transition, GamingActivityMonitor? monitor = null, bool requireForeground = true, bool suspendForGaming = true)
    {
        _element = element;
        _requireForeground = requireForeground;
        _suspendForGaming = suspendForGaming;
        _transition = transition;
        _monitor = monitor ?? GamingActivityMonitor.Instance;
        element.Loaded += OnLoaded;
        element.Unloaded += OnUnloaded;
        element.IsVisibleChanged += (_, _) => Update();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _window = Window.GetWindow(_element);
        if (_window is not null)
        {
            _window.Activated += OnWindowActivityChanged;
            _window.Deactivated += OnWindowActivityChanged;
            _window.StateChanged += OnWindowActivityChanged;
        }
        if (_suspendForGaming) _monitor.Changed += OnPolicyChanged;
        Update();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivityChanged;
            _window.Deactivated -= OnWindowActivityChanged;
            _window.StateChanged -= OnWindowActivityChanged;
            _window = null;
        }
        if (_suspendForGaming) _monitor.Changed -= OnPolicyChanged;
        Update();
    }
    private void OnWindowActivityChanged(object? sender, EventArgs e) => Update();

    private void OnPolicyChanged(object? sender, EventArgs e)
    {
        if (!_element.Dispatcher.HasShutdownStarted)
            _element.Dispatcher.BeginInvoke(new Action(Update), DispatcherPriority.Background);
    }
    private async void Update()
    {
        await _transitionGate.WaitAsync();
        try
        {
            var active = _loaded && _element.IsVisible && _window is not null && (!_requireForeground || _window.IsActive) &&
                _window.WindowState != WindowState.Minimized && (!_suspendForGaming || !_monitor.OptionalWorkPaused);
            if (active == IsActive) return;
            IsActive = active;
            await _transition(active);
        }
        catch (Exception ex) { Log.Instance.Trace($"Optional UI monitoring transition failed.", ex); }
        finally { _transitionGate.Release(); }
    }
}
