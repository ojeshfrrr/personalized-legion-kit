using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Xaml.Behaviors;
using LenovoLegionToolkit.WPF.Behaviors;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Automation;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows;
using Newtonsoft.Json;
using Wpf.Ui.Controls;
using Wpf.Ui.Controls.Interfaces;
using Frame = System.Windows.Controls.Frame;
using TextBlock = System.Windows.Controls.TextBlock;

// WPF integration tests, not an input injector. The real production assemblies,
// compatibility checks, resources, pages and query services are used unchanged.
// The transparent, non-activating test window never requests foreground focus.
internal static class Program
{
    private static readonly List<object> Results = [];
    private static string _output = null!;
    private static bool _reopenOnly;
    private static bool _layoutOnly;
    private static bool _performanceOnly;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--mouse-markup-only")
        {
            MouseMarkupChecks.Run(args[1]);
            return;
        }
        try { RunDispatcher(args); }
        catch (Exception ex)
        {
            if (_output is not null)
                File.WriteAllText(Path.Combine(_output, "startup-error.txt"), ex.ToString());
            Environment.ExitCode = 1;
        }
    }

    private static void RunDispatcher(string[] args)
    {
        _output = Path.GetFullPath(args[0]);
        _reopenOnly = args.Contains("--reopen-only", StringComparer.Ordinal);
        _layoutOnly = args.Contains("--layout-only", StringComparer.Ordinal);
        _performanceOnly = args.Contains("--performance-only", StringComparer.Ordinal);
        Directory.CreateDirectory(_output);
        File.WriteAllText(Path.Combine(_output, "resource-assembly.txt"), System.Windows.Application.ResourceAssembly.FullName);
        var profile = Path.Combine(_output, "profile");
        AppFlags.Initialize(args.Contains("--no-trace", StringComparer.Ordinal)
            ? ["--read-only", "--qa-profile=" + profile]
            : ["--read-only", "--trace", "--qa-profile=" + profile]);
        Log.Instance.IsTraceEnabled = AppFlags.Instance.IsTraceEnabled;
        var app = new App();
        // Qualify production pack URIs for this separate host assembly. Preserve
        // every production dictionary and inline style, with no test UI templates.
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ProductionApp.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(source.Descendants(presentation + "ResourceDictionary").First());
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        XNamespace markup = "clr-namespace:Wpf.Ui.Markup;assembly=Wpf.Ui";
        XNamespace controls = "clr-namespace:Wpf.Ui.Controls;assembly=Wpf.Ui";
        foreach (var node in dictionary.Descendants().Where(n => n.Name.NamespaceName == "http://schemas.lepo.co/wpfui/2022/xaml"))
            node.Name = (node.Name.LocalName is "ControlsDictionary" or "ThemesDictionary" ? markup : controls) + node.Name.LocalName;
        dictionary.SetAttributeValue(XNamespace.Xmlns + "wpfui", controls.NamespaceName);
        dictionary.SetAttributeValue(XNamespace.Xmlns + "navigation", "clr-namespace:Wpf.Ui.Controls.Navigation;assembly=Wpf.Ui");
        foreach (var attribute in dictionary.Descendants().Attributes().Where(a => a.Value.Contains("wpfui:NavigationHeader", StringComparison.Ordinal)))
            attribute.Value = attribute.Value.Replace("wpfui:NavigationHeader", "navigation:NavigationHeader");
        foreach (var node in dictionary.Descendants().Where(n => n.Attribute("Source") is not null))
        {
            var path = node.Attribute("Source")!.Value;
            if (!path.Contains(";component/", StringComparison.Ordinal))
                node.SetAttributeValue("Source", "pack://application:,,,/Project%20Legion;component/" + path.Replace("pack://application:,,,/", ""));
        }
        app.Resources = new ResourceDictionary();
        var merged = dictionary.Element(presentation + "ResourceDictionary.MergedDictionaries")!;
        foreach (var node in merged.Elements())
            app.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(node.ToString()));
        merged.Remove();
        app.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString()));
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Exit += (_, _) => IoCContainer.Dispose();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Results.Add(new { DispatcherError = e.Exception.ToString() });
            Environment.ExitCode = 1;
            e.Handled = true;
        };
        // Do not call Application.Run: the production startup handler would start
        // another interactive session. Only the test dispatcher is pumped here.
        app.Dispatcher.BeginInvoke(async () =>
        {
            try { await RunAsync(app); }
            catch (Exception ex) { Results.Add(new { Error = ex.ToString() }); Environment.ExitCode = 1; }
            finally
            {
                File.WriteAllText(Path.Combine(_output, "results.json"), JsonConvert.SerializeObject(Results, Formatting.Indented));
                try { await app.ShutdownAsync(); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(_output, "shutdown-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                app.Dispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
    }

    private static async Task RunAsync(App app)
    {
        IoCContainer.Initialize(new LenovoLegionToolkit.Lib.IoCModule(), new LenovoLegionToolkit.Lib.Automation.IoCModule(),
            new LenovoLegionToolkit.Lib.Macro.IoCModule(), new LenovoLegionToolkit.WPF.IoCModule());
        var (compatible, _) = await Compatibility.IsCompatibleAsync();
        if (!compatible) throw new InvalidOperationException("Compatibility check failed; refusing UI device queries.");
        if (HardwareRuntime.ControlsEnabled) throw new InvalidOperationException("Commands unexpectedly enabled.");
        var settings = IoCContainer.Resolve<ApplicationSettings>();
        settings.Store.MinimizeToTray = false;
        settings.Store.MinimizeOnClose = false;
        settings.Store.EnableHardwareSensors = true;
        settings.Store.UseNewSensorDashboard = true;
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        await LocalizationHelper.SetLanguageAsync(true);
        IoCContainer.Resolve<ThemeManager>().Apply();
        await IoCContainer.Resolve<AutomationProcessor>().InitializeAsync(startListeners: false);
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 1250, Height = 780 };
        app.MainWindow = App.MainWindowInstance = window;
        window.Show();
        await window.InitializeAfterFeaturesAsync();
        await SettleAsync();
        Results.Add(new { Check = "WindowOnOpen", Width = window.Width, Height = window.Height,
            SavedWidth = settings.Store.WindowSize?.Width, SavedHeight = settings.Store.WindowSize?.Height });
        if (_reopenOnly && (window.Width != 980 || window.Height != 680))
            throw new InvalidOperationException("Saved window dimensions were not restored on reopen.");
        window.Width = 1250; window.Height = 780;
        await SettleAsync();
        var nav = Field<NavigationStore>(window, "_navigationStore");
        var frame = Field<Frame>(window, "_rootFrame");
        var items = nav.Items.Cast<object>().Concat(nav.Footer.Cast<object>()).OfType<INavigationItem>().ToArray();
        if (_reopenOnly) items = items.Where(i => i.PageTag is "dashboard" or "about").ToArray();
        if (_layoutOnly) items = items.Where(i => i.PageTag == "thermals").ToArray();
        Results.Add(new { Check = "Environment", Compatible = compatible, ControlsLocked = !HardwareRuntime.ControlsEnabled,
            Assembly = typeof(App).Assembly.Location, Profile = Folders.AppData, Pages = items.Select(i => i.PageTag).ToArray(),
            Note = "Offscreen WPF integration checks. No foreground input, actual monitor DPI, or native minimize/maximize is tested." });
        if (_performanceOnly) await PerformanceStagesAsync(window, nav, items);
        for (var pass = 0; pass < (_performanceOnly ? 0 : _reopenOnly || _layoutOnly ? 1 : 3); pass++)
        {
            foreach (var item in items)
            {
                var timer = Stopwatch.StartNew();
                nav.Navigate(item.PageTag);
                await SettleAsync();
                var texts = ReadTexts(window).ToArray();
                var active = (bool?)item.GetType().GetProperty("IsActive")?.GetValue(item);
                if (nav.Current?.PageTag != item.PageTag || active == false)
                    throw new InvalidOperationException("Navigation selection mismatch: " + item.PageTag);
                if (HardwareRuntime.ControlsEnabled) throw new InvalidOperationException("Commands enabled during navigation.");
                var process = Process.GetCurrentProcess();
                Results.Add(new { Check = "Navigation", Pass = pass, Tag = item.PageTag, Current = nav.Current?.PageTag,
                    IsActive = active, ContentType = frame.Content?.GetType().FullName, SettledMs = timer.ElapsedMilliseconds,
                    WorkingSetMiB = process.WorkingSet64 / 1048576d, PrivateMiB = process.PrivateMemorySize64 / 1048576d,
                    Handles = process.HandleCount, Threads = process.Threads.Count,
                    Loading = texts.Where(t => t.Contains("Loading", StringComparison.OrdinalIgnoreCase)).ToArray(),
                    Unavailable = texts.Where(t => t.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || t.Contains("unsupported", StringComparison.OrdinalIgnoreCase)).ToArray() });
                if (pass != 0) continue;
                Capture(window, item.PageTag + "-1250x780");
                File.WriteAllLines(Path.Combine(_output, item.PageTag + "-text.txt"), texts);
                window.Width = 760; window.Height = 640;
                await SettleAsync();
                Capture(window, item.PageTag + "-760x640");
                window.Width = 1250; window.Height = 780;
                await SettleAsync();
                if (item.PageTag == "settings" && frame.Content is FrameworkElement page)
                {
                    var list = Field<ListBox>(page, "_navigationListBox");
                    for (var index = 0; index < list.Items.Count; index++)
                    {
                        list.SelectedIndex = index;
                        await SettleAsync();
                        Capture(window, "settings-tab-" + index);
                        Results.Add(new { Check = "SettingsTab", Index = index, Selected = list.SelectedIndex, Text = ReadTexts(window).ToArray() });
                    }
                }
            }
        }
        // Programmatic resize persistence exercises the real close handler only in this disposable profile.
        window.Width = 980; window.Height = 680;
        await SettleAsync();
        Results.Add(new { Check = "BeforeClose", ControlsLocked = !HardwareRuntime.ControlsEnabled,
            IdleSampleNote = "CPU for the separately launched production session is recorded by MeasureReadOnlySession.ps1." });
        window.SuppressClosingEventHandler = true;
        window.Close();
        await Task.Delay(1500);
        var reloaded = new ApplicationSettings();
        Results.Add(new { Check = "Persistence", Width = reloaded.Store.WindowSize?.Width, Height = reloaded.Store.WindowSize?.Height,
            Passed = reloaded.Store.WindowSize is { Width: 980, Height: 680 } });
    }

    private static async Task PerformanceStagesAsync(MainWindow window, NavigationStore nav, INavigationItem[] items)
    {
        nav.Navigate("dashboard");
        await SettleAsync();
        await SampleStageAsync("HomeAnimations", 20);
        var detached = Descendants(window).OfType<System.Windows.Controls.ProgressBar>()
            .SelectMany(bar => Interaction.GetBehaviors(bar).OfType<ProgressBarAnimateBehavior>()
                .Select(behavior => (Bar: bar, Behavior: behavior)).ToArray()).ToArray();
        try
        {
            foreach (var pair in detached) Interaction.GetBehaviors(pair.Bar).Remove(pair.Behavior);
            await SampleStageAsync("HomeAnimationsDetached", 20);
        }
        finally { foreach (var pair in detached) Interaction.GetBehaviors(pair.Bar).Add(pair.Behavior); }
        Results.Add(new { Check = "AnimationAblation", Count = detached.Length, ProductionChanged = false });
        await SampleStageAsync("HomeAnimationsRestored", 20);
        nav.Navigate("about");
        await SettleAsync();
        await SampleStageAsync("AboutSensorsNotVisible", 20);
        foreach (var item in items)
        {
            nav.Navigate(item.PageTag);
            await SettleAsync();
            await SampleStageAsync("Navigation-" + item.PageTag, 2);
        }
        nav.Navigate("dashboard");
        await SettleAsync();
        await SampleStageAsync("HomeAfterNavigation", 60);
    }

    private static async Task SampleStageAsync(string stage, int seconds)
    {
        var samples = new List<object>();
        for (var elapsed = 0; elapsed < seconds; elapsed += Math.Min(5, seconds))
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime.TotalSeconds;
            var clock = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, seconds)));
            process.Refresh();
            samples.Add(new { Utc = DateTime.UtcNow, Seconds = clock.Elapsed.TotalSeconds,
                CpuOneCore = 100 * (process.TotalProcessorTime.TotalSeconds - cpu) / clock.Elapsed.TotalSeconds,
                WorkingSetMiB = process.WorkingSet64 / 1048576d, PrivateMiB = process.PrivateMemorySize64 / 1048576d,
                process.HandleCount, Threads = process.Threads.Count });
        }
        Results.Add(new { Check = "PerformanceStage", Stage = stage, Samples = samples });
        File.WriteAllText(Path.Combine(_output, "performance-progress.json"), JsonConvert.SerializeObject(Results, Formatting.Indented));
    }

    private static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new InvalidOperationException("Missing field: " + name));

    private static async Task SettleAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(1500);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<string> ReadTexts(DependencyObject root)
    {
        if (root is TextBlock text && text.IsVisible && !string.IsNullOrWhiteSpace(text.Text)) yield return text.Text;
        if (root is ContentControl { Content: string content } control && control.IsVisible && !string.IsNullOrWhiteSpace(content)) yield return content;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var value in ReadTexts(VisualTreeHelper.GetChild(root, i))) yield return value;
    }

    private static void Capture(MainWindow window, string name)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_output, name + ".png"));
        encoder.Save(output);
        var layout = new List<object>();
        DescribeLayout(root, 0, layout);
        File.WriteAllText(Path.Combine(_output, name + "-layout.json"), JsonConvert.SerializeObject(layout, Formatting.Indented));
        var collapsedHeaders = Descendants(root).OfType<LenovoLegionToolkit.WPF.Controls.CardHeaderControl>()
            .Where(header => header.IsVisible && !string.IsNullOrWhiteSpace(header.Title) && header.ActualWidth < 1)
            .Select(header => new { header.Title, header.ActualWidth, header.ActualHeight }).ToArray();
        Results.Add(new { Check = "Layout", Render = name, ZeroWidthHeaders = collapsedHeaders,
            Passed = collapsedHeaders.Length == 0 });
        if (collapsedHeaders.Length != 0) Environment.ExitCode = 1;
    }

    private static void DescribeLayout(DependencyObject node, int depth, List<object> result)
    {
        if (node is FrameworkElement element)
        {
            result.Add(new { Depth = depth, Type = node.GetType().FullName, element.Name,
                Text = node is TextBlock text ? text.Text : null,
                element.ActualWidth, element.ActualHeight, DesiredWidth = element.DesiredSize.Width,
                DesiredHeight = element.DesiredSize.Height, element.Width, element.Height,
                element.MinWidth, element.MinHeight, element.MaxWidth, element.MaxHeight,
                Visibility = element.Visibility.ToString(), element.IsVisible, element.Opacity,
                Slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(element).ToString(),
                Clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(element)?.Bounds.ToString(),
                Columns = node is Grid grid ? string.Join(",", grid.ColumnDefinitions.Select(c => $"{c.Width}:{c.ActualWidth}")) : null,
                Rows = node is Grid rows ? string.Join(",", rows.RowDefinitions.Select(r => $"{r.Height}:{r.ActualHeight}")) : null });
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            DescribeLayout(VisualTreeHelper.GetChild(node, index), depth + 1, result);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(node, index))) yield return child;
    }
}
