using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using LenovoLegionToolkit.Lib.Messaging;
using LenovoLegionToolkit.Lib.Messaging.Messages;
using LenovoLegionToolkit.WPF.Controls;

// Exercises the production lifecycle source with no App, window, IoC, settings,
// hardware services, driver initialization, or administrator manifest.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var control = new ProbeControl();
        var activeCallbacks = 0;
        var inactiveCallbacks = 0;
        for (var cycle = 0; cycle < 25; cycle++)
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var previous = control.Callbacks;
            MessagingCenter.Publish(new ProbeMessage());
            activeCallbacks += control.Callbacks - previous;
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            previous = control.Callbacks;
            MessagingCenter.Publish(new ProbeMessage());
            inactiveCallbacks += control.Callbacks - previous;
        }
        MessagingCenter.Unsubscribe(control);

        var duplicateLoad = new ProbeControl();
        duplicateLoad.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        duplicateLoad.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        MessagingCenter.Publish(new ProbeMessage());
        var duplicateLoadCallbacks = duplicateLoad.Callbacks;
        duplicateLoad.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        MessagingCenter.Unsubscribe(duplicateLoad);

        // A burst of requests must share the same in-flight read.
        var refreshing = new ProbeControl();
        var tasks = new Task[50];
        for (var i = 0; i < tasks.Length; i++) tasks[i] = refreshing.ReadAsync();
        refreshing.PendingRead.SetResult();
        Task.WhenAll(tasks).GetAwaiter().GetResult();
        var result = new { Cycles = 25, ActiveCallbacks = activeCallbacks,
            InactiveCallbacks = inactiveCallbacks, ConcurrentRequests = tasks.Length,
            ActualReads = refreshing.Reads, DuplicateLoadCallbacks = duplicateLoadCallbacks };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (args.Length > 0) File.WriteAllText(Path.GetFullPath(args[0]), json);
        if (args.Length > 1 && args[1] == "--assert" &&
            (activeCallbacks != 25 || inactiveCallbacks != 0 || refreshing.Reads != 1 || duplicateLoadCallbacks != 1))
            throw new InvalidOperationException("Lifecycle or single-flight read regression.");
    }
}

internal sealed class ProbeMessage : IMessage;
internal sealed class ProbeControl : AbstractRefreshingControl
{
    public int Callbacks;
    public int Reads;
    public readonly TaskCompletionSource PendingRead = new();
    public Task ReadAsync() => RefreshAsync();
    protected override void OnFinishedLoading() =>
        MessagingCenter.Subscribe<ProbeMessage>(this, _ => Callbacks++);
    protected override void OnFinishedUnloading() => MessagingCenter.Unsubscribe<ProbeMessage>(this);
    protected override Task OnRefreshAsync() { Reads++; return PendingRead.Task; }
}
