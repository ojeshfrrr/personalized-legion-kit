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
internal static class UpdateMarkupChecks
{
    public static void Run(string directory)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var source = XDocument.Load("LenovoLegionToolkit.WPF/Windows/Utils/UpdateWindow.xaml").Root!;
        var markup = new XElement(p + "Grid", source.Attributes().Where(a => a.IsNamespaceDeclaration),
            source.Elements().Where(e => e.Name.LocalName is "BaseWindow.Resources" or "Grid").Select(e => new XElement(e)));
        foreach (var ns in markup.Attributes().Where(a => a.IsNamespaceDeclaration &&
            a.Value.StartsWith("clr-namespace:LenovoLegionToolkit.WPF", StringComparison.Ordinal) && !a.Value.Contains(";assembly=")))
            ns.Value += ";assembly=Project Legion";
        markup.Elements().Single(e => e.Name.LocalName == "BaseWindow.Resources").Name = p + "Grid.Resources";
        XNamespace w = "clr-namespace:Wpf.Ui.Controls;assembly=Wpf.Ui";
        markup.SetAttributeValue(XNamespace.Xmlns + "wpfui", w.NamespaceName);
        foreach (var node in markup.Descendants().Where(e => e.Name.NamespaceName == "http://schemas.lepo.co/wpfui/2022/xaml"))
            node.Name = w + node.Name.LocalName;
        foreach (var a in markup.Descendants().Attributes().Where(a => a.Name.LocalName == "Click").ToArray()) a.Remove();
        foreach (var width in new[] { 620, 760 })
        foreach (var verified in new[] { false, true })
        {
            var root = (Grid)System.Windows.Markup.XamlReader.Parse(markup.ToString());
            var status = (TextBlock)root.FindName("_packageStatus");
            status.Text = verified
                ? "SHA-256 verified against GitHub release metadata. Close Project Legion, extract the ZIP into a new folder, and launch Project Legion.exe there. Your existing settings stay in your local profile. This package is unsigned; the checksum does not verify a trusted publisher."
                : "Version 0.2.0.0 · Windows x64 ZIP · Offline layout fixture. Downloading will not install or restart the app.";
            ((Markdig.Wpf.MarkdownViewer)root.FindName("_markdownViewer")).Markdown = "**Offline update fixture**\n\nNo package is downloaded, installed or executed.";
            var open = (FrameworkElement)root.FindName("_openFolderButton");
            open.Visibility = verified ? Visibility.Visible : Visibility.Collapsed;
            var install = (FrameworkElement)root.FindName("_installButton");
            install.Visibility = verified ? Visibility.Visible : Visibility.Collapsed;
            install.IsEnabled = verified;
            var cancel = (FrameworkElement)root.FindName("_cancelDownloadButton");
            cancel.Visibility = Visibility.Collapsed;
            var download = (FrameworkElement)root.FindName("_downloadButton");
            download.Visibility = verified ? Visibility.Collapsed : Visibility.Visible;
            download.IsEnabled = true;
            root.Measure(new Size(width, 600)); root.Arrange(new Rect(0, 0, width, 600)); root.UpdateLayout();
            foreach (var element in verified ? new[] { (FrameworkElement)status, open, install } : new[] { (FrameworkElement)status, download })
            {
                var origin = element.TranslatePoint(new Point(), root);
                if (element.ActualWidth < 100 || element.ActualHeight < 15 || origin.X < 0 || origin.Y < 0 ||
                    origin.X + element.ActualWidth > width + 1 || origin.Y + element.ActualHeight > 601)
                    throw new InvalidOperationException("Update status or explicit action is clipped at width " + width);
            }
            var image = new RenderTargetBitmap(width, 600, 96, 96, PixelFormats.Pbgra32); image.Render(root);
            var pixels = new byte[width * 600 * 4]; image.CopyPixels(pixels, width * 4, 0);
            int ReadablePixels(int top, int bottom)
            {
                var count = 0;
                for (var y = top; y < bottom; y++)
                for (var x = 32; x < width - 32; x++)
                {
                    var i = (y * width + x) * 4;
                    if (pixels[i] > 110 && pixels[i + 1] > 110 && pixels[i + 2] > 110) count++;
                }
                return count;
            }
            if (ReadablePixels(45, 115) < 100 || ReadablePixels(115, 300) < 100)
                throw new InvalidOperationException("Update heading or release notes are not readable against the AMOLED background.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(directory, $"updates-{width}-{verified}.png")); encoder.Save(stream);
        }
        Console.WriteLine("PASS: four detached update-dialog layouts; download/verified-folder actions and instructions fit at 620 and 760 pixels. No updater or network calls.");
    }
}
