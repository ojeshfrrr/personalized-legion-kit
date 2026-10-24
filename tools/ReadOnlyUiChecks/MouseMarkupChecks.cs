using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using LenovoLegionToolkit.WPF.Pages;
using Newtonsoft.Json;

// Detached production markup only. No production App/page constructor, IoC,
// profile, window, dispatcher loop, device transport or hardware calls.
internal static class MouseMarkupChecks
{
    public static void Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var app = new System.Windows.Application();
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace w = "clr-namespace:Wpf.Ui.Controls;assembly=Wpf.Ui";
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ProductionApp.xaml"));
        var dictionary = new XElement(source.Descendants(p + "ResourceDictionary").First());
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
        foreach (var node in dictionary.Descendants().Where(n => n.Name.NamespaceName == "http://schemas.lepo.co/wpfui/2022/xaml"))
            node.Name = (node.Name.LocalName is "ControlsDictionary" or "ThemesDictionary"
                ? (XNamespace)"clr-namespace:Wpf.Ui.Markup;assembly=Wpf.Ui" : w) + node.Name.LocalName;
        dictionary.SetAttributeValue(XNamespace.Xmlns + "wpfui", w.NamespaceName);
        dictionary.SetAttributeValue(XNamespace.Xmlns + "navigation", "clr-namespace:Wpf.Ui.Controls.Navigation;assembly=Wpf.Ui");
        foreach (var a in dictionary.Descendants().Attributes().Where(a => a.Value.Contains("wpfui:NavigationHeader", StringComparison.Ordinal)))
            a.Value = a.Value.Replace("wpfui:NavigationHeader", "navigation:NavigationHeader");
        foreach (var a in dictionary.Descendants().Attributes("Source"))
            if (!a.Value.Contains(";component/", StringComparison.Ordinal))
                a.Value = "pack://application:,,,/Project%20Legion;component/" + a.Value.Replace("pack://application:,,,/", "");
        var merged = dictionary.Element(p + "ResourceDictionary.MergedDictionaries")!;
        foreach (var node in merged.Elements())
            app.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(node.ToString()));
        merged.Remove();
        app.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString()));

        var markup = XDocument.Load("LenovoLegionToolkit.WPF/Pages/MousePage.xaml").Root!;
        var resources = markup.Elements().Single(e => e.Name.LocalName == "UiPage.Resources");
        var content = markup.Elements().Single(e => e.Name.LocalName == "StackPanel");
        // Explicitly detach all handlers; loading markup cannot read or write a device.
        var events = new[] { "Loaded", "Unloaded", "Click", "SizeChanged", "TextChanged", "SelectionChanged", "Checked", "Unchecked" };
        foreach (var a in markup.DescendantsAndSelf().Attributes().Where(a => events.Contains(a.Name.LocalName)).ToArray()) a.Remove();
        foreach (var a in markup.DescendantsAndSelf().Attributes().Where(a => a.Value.Contains("utils:", StringComparison.Ordinal)).ToArray()) a.Remove();
        resources.Name = p + "Grid.Resources";
        var rootMarkup = new XElement(p + "Grid", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wpfui", w.NamespaceName), resources, content);
        foreach (var node in rootMarkup.Descendants().Where(n => n.Name.NamespaceName == "http://schemas.lepo.co/wpfui/2022/xaml")) node.Name = w + node.Name.LocalName;
        var results = new List<object>();
        foreach (var size in new[] { (1250, 780, 990d), (760, 640, 500d) })
        {
            foreach (var keyboard in new[] { false, true })
            {
                var root = (Grid)System.Windows.Markup.XamlReader.Parse(rootMarkup.ToString());
                root.Background = Brushes.Black;
                root.TextBlockForeground(Brushes.White);
                ((ItemsControl)root.FindName("_buttonLayout")).ItemsSource =
                    LenovoLegionToolkit.Lib.Services.Mouse.DeathAdderV3ButtonProtocol.Buttons.Select(b =>
                        new { b.Id, b.Name, Assignment = "Unavailable · read assignments" }).ToArray();
                // Conservative viewport after shell/sidebar. No fabricated readings.
                var width = size.Item3;
                typeof(MousePage).GetMethod("ApplyMouseLayout", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [root, width - 16]);
                ((StackPanel)root.FindName("_keyboardAssignmentOptions")).Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
                ((ComboBox)root.FindName("_buttonActionSelector")).Visibility = keyboard ? Visibility.Collapsed : Visibility.Visible;
                // Local preset inspection state only; never populate a device reading.
                ((ComboBox)root.FindName("_presetSelector")).ItemsSource = new[] { new { Name = "Offline local preset fixture" } };
                ((ComboBox)root.FindName("_presetSelector")).SelectedIndex = 0;
                ((TextBlock)root.FindName("_presetStored")).Text = "Offline stored-intent fixture. Not current device state or an onboard profile.";
                ((TextBlock)root.FindName("_presetResult")).Text = "Offline result layout fixture: DPI confirmed; polling uncertain; later buttons not attempted. No operation executed.";
                ((Expander)root.FindName("_presetInspector")).IsExpanded = keyboard;
                MouseDpiSliderMarkupChecks.Exercise(root, keyboard);
                root.Measure(new Size(width, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
                root.UpdateLayout();
                MouseDpiSliderMarkupChecks.VerifyGeometry(root, keyboard);
                var stored = (TextBlock)root.FindName("_presetStored");
                var ledger = (TextBlock)root.FindName("_presetResult");
                var inspector = (Expander)root.FindName("_presetInspector");
                var inspectorContent = (ContentPresenter)inspector.Template.FindName("InspectorContent", inspector);
                // IsVisible includes the detached root's lack of a presentation
                // source. Check actual template visibility and measured content.
                if (inspectorContent.Visibility != (keyboard ? Visibility.Visible : Visibility.Collapsed) ||
                    (keyboard && (stored.ActualHeight < 10 || ledger.ActualHeight < 10 || inspectorContent.ActualHeight < 30)))
                    throw new InvalidOperationException("Preset inspector failed to reveal stored settings and result.");
                var canvas = (Canvas)root.FindName("_mouseSchematic");
                DependencyObject? Hit(Point point)
                {
                    DependencyObject? hit = null;
                    // Detached markup has no presentation source for InputHitTest.
                    // Apply the input visibility filter to real geometry hit tests.
                    VisualTreeHelper.HitTest(root,
                        node => node is UIElement { IsHitTestVisible: false }
                            ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
                        result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; },
                        new PointHitTestParameters(point));
                    return hit;
                }
                var hitCases = new[] { (1, 110d, 130d), (2, 206d, 130d), (3, 159d, 129d),
                    (5, 66d, 201d), (4, 63d, 247d), (9, 72d, 487d), (10, 127d, 487d), (96, 227d, 487d) };
                foreach (var (id, px, py) in hitCases)
                {
                    var point = canvas.TransformToAncestor(root).Transform(new Point(px, py));
                    var rawHit = Hit(point);
                    var node = rawHit;
                    while (node is not null && node is not RadioButton) node = VisualTreeHelper.GetParent(node);
                    if (node is not RadioButton region || Convert.ToInt32(region.Tag) != id)
                        throw new InvalidOperationException($"Scaled vector hit mismatch for button {id}, window {size.Item1}: hit={rawHit?.GetType().Name}, region={(node as RadioButton)?.Name}, point={point}.");
                }
                var emptyPoint = canvas.TransformToAncestor(root).Transform(new Point(20, 300));
                var emptyHit = Hit(emptyPoint);
                while (emptyHit is not null && emptyHit is not RadioButton) emptyHit = VisualTreeHelper.GetParent(emptyHit);
                if (emptyHit is RadioButton) throw new InvalidOperationException("A transparent bounding box intercepted the empty canvas.");
                // Local selection stress only: no assignment or telemetry is invented.
                if (keyboard)
                {
                    var selected = (RadioButton)root.FindName("_region4");
                    selected.IsChecked = true;
                    root.UpdateLayout();
                    var shape = (System.Windows.Shapes.Path)selected.Template.FindName("Region", selected);
                    if (shape.Stroke is not SolidColorBrush stroke || stroke.Color != Color.FromRgb(255, 51, 79))
                        throw new InvalidOperationException("Selected vector region lost its crimson outline.");
                }
                var problems = Descendants(root).OfType<FrameworkElement>().Where(e =>
                    e.Visibility == Visibility.Visible && !string.IsNullOrEmpty(e.Name) &&
                    e.ActualWidth > 0 && e.ActualHeight > 0 &&
                    e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize)).Right > width + 1)
                    .Select(e => e.Name).ToArray();
                if (problems.Length != 0) throw new InvalidOperationException("Horizontal overflow: " + string.Join(",", problems));
                foreach (var name in new[] { "_dpiSurface", "_pollingSurface", "_assignmentEditor", "_schematicView", "_presetSelector", "_presetName", "_presetApply", "_presetStatus" })
                {
                    var element = (FrameworkElement)root.FindName(name);
                    if (element.ActualWidth < 100 || element.ActualHeight < 10) throw new InvalidOperationException("Collapsed region: " + name);
                }
                var image = new RenderTargetBitmap((int)width, (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                var nameSuffix = $"mouse-{size.Item1}x{size.Item2}-{(keyboard ? "keyboard" : "basic")}";
                using (var stream = File.Create(Path.Combine(directory, nameSuffix + ".png"))) encoder.Save(stream);
                results.Add(new { WindowWidth = size.Item1, WindowHeight = size.Item2, ContentWidth = width,
                    ContentHeight = root.ActualHeight, KeyboardExpanded = keyboard, IndependentDpiSliders = keyboard, PresetInspectorExpanded = keyboard, HorizontalOverflow = problems,
                    AlignedVectorHitRegions = hitCases.Length, EmptyCanvasDoesNotSelect = true,
                    Note = "Detached production markup with synthetic requested-input fixtures; confirmed readbacks remain unavailable. Full scroll content, not runtime navigation or device validation." });
            }
        }
        File.WriteAllText(Path.Combine(directory, "layout-results.json"), JsonConvert.SerializeObject(results, Formatting.Indented));
        Console.WriteLine("PASS: four detached Mouse layouts, native DPI slider/text/link/keyboard/read-only interactions, aligned logarithmic guide marks, 32 scaled vector hit checks and empty-canvas checks; no collapsed regions or horizontal overflow. No hardware or profile access.");
        DemandSnapshotMarkupChecks.Run(directory);
        UpdateMarkupChecks.Run(directory);
        UpdaterPageMarkupChecks.Run(directory);
        app.Shutdown();
    }
    private static void TextBlockForeground(this Grid root, Brush brush) => TextElement.SetForeground(root, brush);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
