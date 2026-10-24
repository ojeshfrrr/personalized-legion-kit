using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Newtonsoft.Json;

// Reuses the detached-markup host/resources. Never constructs an application
// page, hardware control, IoC container, service or native window.
internal static class DemandSnapshotMarkupChecks
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    public static void Run(string directory)
    {
        var results = new List<object>();
        foreach (var page in new[] { "SensorsControl", "SensorsControlV2", "BatteryPage", "ThermalsPage" })
        foreach (var size in new[] { (1250, 780, 990d), (760, 640, 500d) })
        {
            var root = (Grid)System.Windows.Markup.XamlReader.Parse(Detach(page).ToString());
            root.Width = size.Item3;
            root.Measure(new Size(size.Item3, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, size.Item3, root.DesiredSize.Height)); root.UpdateLayout();
            if (page == "SensorsControl") VerifyDials(root, directory, size.Item1);
            var buttons = Descendants(root).OfType<System.Windows.Controls.Primitives.ButtonBase>()
                .Where(b => Equals(b.Tag, "ReadOnlyRefresh")).ToArray();
            if (buttons.Length != 0) throw new InvalidOperationException(page + ": redundant Refresh button remains");
            var statusName = page == "BatteryPage" ? "_batteryReadStatus" : page == "SensorsControlV2" ? "_snapshotReadStatus" : "_readStatusText";
            var readStatus = (FrameworkElement)root.FindName(statusName);
            if (readStatus.ActualWidth < 50 || readStatus.ActualHeight < 10) throw new InvalidOperationException(page + ": read status is clipped");
            if (page == "ThermalsPage")
            {
                var status = (FrameworkElement)root.FindName("_fanSettingsStatus");
                var fan = (FrameworkElement)root.FindName("_fanSettingsButton");
                var card = Descendants(root).OfType<LenovoLegionToolkit.WPF.Controls.Custom.CardControl>().Single();
                if (status.ActualWidth < 120 || fan.ActualWidth < 60 || card.ActualHeight < 55)
                    throw new InvalidOperationException("Thermals cooling card compact regression");
            }
            var image = new RenderTargetBitmap((int)size.Item3, (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(directory, $"snapshot-{page}-{size.Item1}x{size.Item2}.png"))) png.Save(stream);
            results.Add(new { Page = page, WindowWidth = size.Item1, WindowHeight = size.Item2, ContentWidth = size.Item3,
                ContentHeight = root.ActualHeight, ExtraRefreshButtons = 0, ReadStatusVisible = true, CoolingCardChecked = page == "ThermalsPage",
                Note = "Detached full scroll content. Battery hardware controls omitted; no page/service constructors or runtime/foreground checks." });
        }
        File.WriteAllText(Path.Combine(directory, "snapshot-layout-results.json"), JsonConvert.SerializeObject(results, Formatting.Indented));
        Console.WriteLine("PASS: 8 detached telemetry layouts; no redundant Refresh buttons, readable status at both target widths; Thermals cooling card retains header width. No hardware access.");
    }
    private static void VerifyDials(Grid root, string directory, int windowWidth)
    {
        var update = typeof(LenovoLegionToolkit.WPF.Controls.Dashboard.SensorsControl)
            .GetMethod("UpdateValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var prefix in new[] { "_cpuTemperature", "_gpuTemperature" })
        {
            var bar = (ProgressBar)root.FindName(prefix + "Bar");
            var label = (ContentControl)root.FindName(prefix + "Label");
            var previous = -1;
            foreach (var value in new[] { 0, 50, 100 })
            {
                // Synthetic presentation values only; invoke the production publisher
                // on detached markup without constructing a sensor service or page.
                update.Invoke(null, [bar, label, 100d, (double)value, value.ToString(), null]);
                root.UpdateLayout();
                var dial = Descendants(bar).OfType<LenovoLegionToolkit.WPF.Controls.InstrumentDial>().Single();
                if (dial.ActualWidth < 50 || dial.ActualHeight < 50 || dial.Value != value || dial.Maximum != 100)
                    throw new InvalidOperationException("Sensor value did not reach a sized dial template.");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bar.ActualWidth), (int)Math.Ceiling(bar.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(bar);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var crimson = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i + 2] > 150 && pixels[i + 2] > pixels[i + 1] * 1.8) crimson++;
                if ((value == 0 && crimson != 0) || (value > 0 && crimson <= previous + 50))
                    throw new InvalidOperationException($"Dial arc failed to follow published value {value}: {crimson} pixels, previous {previous}.");
                previous = crimson;
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"dial{prefix}-{windowWidth}-{value}.png"));
                png.Save(output);
            }
        }
    }
    private static XElement Detach(string page)
    {
        var path = page.EndsWith("Page", StringComparison.Ordinal) ? $"Pages/{page}.xaml" : $"Controls/Dashboard/{page}.xaml";
        var source = XDocument.Load("LenovoLegionToolkit.WPF/" + path).Root!;
        var root = new XElement(P + "Grid", new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName));
        foreach (var ns in source.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.LocalName != "xmlns" && a.Name.LocalName != "x"))
        {
            var value = ns.Value;
            if (value.StartsWith("clr-namespace:LenovoLegionToolkit.WPF", StringComparison.Ordinal) && !value.Contains(";assembly=")) value += ";assembly=Project Legion";
            if (value == "http://schemas.lepo.co/wpfui/2022/xaml") value = "clr-namespace:Wpf.Ui.Controls;assembly=Wpf.Ui";
            root.SetAttributeValue(ns.Name, value);
        }
        foreach (var child in source.Elements())
        {
            var copy = new XElement(child);
            if (copy.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)) copy.Name = P + "Grid.Resources";
            root.Add(copy);
        }
        foreach (var node in root.Descendants().Where(n => n.Name.NamespaceName.EndsWith("Controls.Dashboard", StringComparison.Ordinal)).ToArray())
        {
            if (node.Name.LocalName == "SensorsControl") node.ReplaceWith(Detach("SensorsControl"));
            else node.Remove(); // Never instantiate BatteryModeControl or another device control.
        }
        foreach (var node in root.Descendants().Where(n => n.Name.NamespaceName.StartsWith("clr-namespace:LenovoLegionToolkit.WPF", StringComparison.Ordinal) && !n.Name.NamespaceName.Contains(";assembly=")))
            node.Name = (XNamespace)(node.Name.NamespaceName + ";assembly=Project Legion") + node.Name.LocalName;
        foreach (var node in root.Descendants().Where(n => n.Name.NamespaceName == "http://schemas.lepo.co/wpfui/2022/xaml"))
            node.Name = (XNamespace)"clr-namespace:Wpf.Ui.Controls;assembly=Wpf.Ui" + node.Name.LocalName;
        foreach (var attribute in root.DescendantsAndSelf().Attributes().Where(a => !a.IsNamespaceDeclaration &&
            (new[] { "Click", "Loaded", "Unloaded", "Initialized", "SizeChanged", "SelectionChanged" }.Contains(a.Name.LocalName) ||
             a.Name.LocalName == "Class" || a.Value.Contains("utils:") ||
             (!string.IsNullOrEmpty(a.Name.NamespaceName) && a.Name.Namespace != X))).ToArray()) attribute.Remove();
        if (page == "ThermalsPage") root.Descendants().Single(n => (string?)n.Attribute(X + "Name") == "_fanSettingsStatus").SetAttributeValue("Text", "Cooling capabilities have not been queried. Open settings explicitly to configure supported fan controls.");
        return root;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
