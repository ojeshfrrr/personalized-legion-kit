using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

// Production dialog markup, with handlers removed. No IoC, profile, network or native window.
internal static class UpdaterPageMarkupChecks
{
    public static void Run(string directory)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var page = XDocument.Load("LenovoLegionToolkit.WPF/Pages/UpdaterPage.xaml").Root!;
        var preferences = XDocument.Load("LenovoLegionToolkit.WPF/Controls/Settings/SettingsUpdateControl.xaml").Root!;
        var rootMarkup = new XElement(p + "Grid", page.Attributes().Where(a => a.IsNamespaceDeclaration),
            page.Elements().Select(e => new XElement(e)));
        rootMarkup.Elements().Single(e => e.Name.LocalName == "UiPage.Resources").Name = p + "Grid.Resources";
        foreach (var ns in preferences.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.LocalName != "xmlns"))
            if (rootMarkup.Attribute(ns.Name) is null) rootMarkup.SetAttributeValue(ns.Name, ns.Value);
        rootMarkup.Descendants().Single(e => e.Name.LocalName == "SettingsUpdateControl")
            .ReplaceWith(new XElement(p + "Grid",
                new XElement(p + "Grid.Resources", preferences.Elements().Single(e => e.Name.LocalName == "UserControl.Resources").Elements()),
                new XElement(preferences.Elements().Single(e => e.Name.LocalName == "ScrollViewer")))); // No service constructor.
        foreach (var ns in rootMarkup.Attributes().Where(a => a.IsNamespaceDeclaration &&
            a.Value.StartsWith("clr-namespace:LenovoLegionToolkit.WPF", StringComparison.Ordinal) && !a.Value.Contains(";assembly=")))
            ns.Value += ";assembly=Project Legion";
        foreach (var node in rootMarkup.Descendants().Where(n => n.Name.NamespaceName.StartsWith("clr-namespace:LenovoLegionToolkit.WPF", StringComparison.Ordinal) &&
            !n.Name.NamespaceName.Contains(";assembly=")))
            node.Name = (XNamespace)(node.Name.NamespaceName + ";assembly=Project Legion") + node.Name.LocalName;
        XNamespace w = "clr-namespace:Wpf.Ui.Controls;assembly=Wpf.Ui";
        rootMarkup.SetAttributeValue(XNamespace.Xmlns + "wpfui", w.NamespaceName);
        foreach (var node in rootMarkup.Descendants().Where(n => n.Name.NamespaceName == "http://schemas.lepo.co/wpfui/2022/xaml"))
            node.Name = w + node.Name.LocalName;
        foreach (var a in rootMarkup.Descendants().Attributes().Where(a => a.Name.LocalName is "Click" or "SelectionChanged" or "CheckCompleted").ToArray()) a.Remove();
        foreach (var width in new[] { 500, 990 })
        {
            var root = (Grid)System.Windows.Markup.XamlReader.Parse(rootMarkup.ToString());
            ((TextBlock)root.FindName("_updatesNotice")).Text = "Installed version: offline layout fixture. Preview includes current prereleases and stable versions.";
            ((TextBlock)root.FindName("_updateStatus")).Text = "Version 0.2.0.0 is available. Offline layout fixture.";
            ((TextBlock)root.FindName("_lastCheck")).Text = "Last check: offline fixture; no requests were made.";
            var check = (Button)root.FindName("_checkUpdatesButton"); check.Visibility = Visibility.Visible;
            foreach (var name in new[] { "_updateChannelComboBox", "_updateCheckFrequencyComboBox" })
            {
                var combo = (ComboBox)root.FindName(name); combo.Visibility = Visibility.Visible;
                combo.ItemsSource = new[] { "Offline selection fixture" }; combo.SelectedIndex = 0;
            }
            ((FrameworkElement)root.FindName("_updateMethodCard")).Visibility = Visibility.Collapsed;
            var available = (Button)root.FindName("_availableUpdateButton"); available.IsEnabled = true;
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height)); root.UpdateLayout();
            foreach (var element in new FrameworkElement[] { check, available, (TextBlock)root.FindName("_updateStatus") })
            {
                var point = element.TranslatePoint(new Point(), root);
                if (element.ActualWidth < 100 || element.ActualHeight < 15 || point.X < 0 || point.X + element.ActualWidth > width + 1)
                    throw new InvalidOperationException("Updater page action/status clipped at content width " + width);
            }
            var route = typeof(LenovoLegionToolkit.WPF.Pages.UpdaterPage).GetMethod("IsUpdaterInputSourceCore", BindingFlags.Static | BindingFlags.NonPublic)!;
            bool Allowed(DependencyObject source, bool readOnly) => (bool)route.Invoke(null, [source, root, readOnly])!;
            var release = (Button)root.FindName("_releasePageButton");
            if (!Allowed(check, false) || !Allowed(available, false) || Allowed(check, true) || Allowed(available, true) ||
                !Allowed(release, true) || Allowed(new Button { Name = "_checkUpdatesButton" }, false))
                throw new InvalidOperationException("Updater input exception escaped its page or read-only restriction.");
            var image = new RenderTargetBitmap(width, (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(directory, $"updater-page-{width}.png")); encoder.Save(stream);
        }
        var shell = XDocument.Load("LenovoLegionToolkit.WPF/Windows/MainWindow.xaml");
        if (shell.Descendants().Count(e => (string?)e.Attribute("PageTag") == "updater" &&
            (string?)e.Attribute("PageType") == "{x:Type pages:UpdaterPage}") != 1)
            throw new InvalidOperationException("Updater navigation entry missing or duplicated.");
        Console.WriteLine("PASS: Updater sidebar destination, two detached page layouts, normal-mode update inputs with hardware controls locked, and read-only/foreign-page input restrictions. No page constructors, settings access or network calls.");
    }
}
