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
    public bool IsActive { get; private set; }
    public OptionalUiActivity(FrameworkElement element, Func<bool, Task> transition, GamingActivityMonitor? monitor = null)
    {
        _element = element;
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
        _monitor.Changed += OnPolicyChanged;
        Update();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _monitor.Changed -= OnPolicyChanged;
        Update();
    }
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
            var active = _loaded && _element.IsVisible && !_monitor.OptionalWorkPaused;
            if (active == IsActive) return;
            IsActive = active;
            await _transition(active);
        }
        catch (Exception ex) { Log.Instance.Trace($"Optional UI monitoring transition failed.", ex); }
        finally { _transitionGate.Release(); }
    }
}
