using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autofac;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Pages;
using LenovoLegionToolkit.WPF.Controls.Settings;

// Actual production page and service constructors, with only update-related
// dependencies registered. No production App, hardware services, network or window.
internal static class UpdaterPageIntegrationChecks
{
    public static void Run(string directory)
    {
        var profile = Path.Combine(Path.GetFullPath(directory), "updater-profile-" + Guid.NewGuid().ToString("N"));
        AppFlags.Initialize(["--read-only", "--qa-profile=" + profile]);
        _ = Log.Instance; // Diagnostic directory is allowed in the isolated QA profile.
        var baseline = Snapshot(profile);
        IoCContainer.Initialize(new UpdateOnlyModule());
        try
        {
            var checker = IoCContainer.Resolve<UpdateChecker>();
            checker.Disable = true; // Match production read-only startup policy.
            foreach (var width in new[] { 500, 990 })
            {
                var page = new UpdaterPage();
                page.Measure(new Size(width, 560));
                page.Arrange(new Rect(0, 0, width, 560));
                page.UpdateLayout();
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                page.UpdateLayout();
                var status = (TextBlock)page.FindName("_updateStatus");
                if (status.Text != "Updater is locked in this read-only session." || status.ActualHeight < 15)
                    throw new InvalidOperationException("Production Updater page failed to load visible locked status.");
                var image = new RenderTargetBitmap(width, 560, 96, 96, PixelFormats.Pbgra32);
                image.Render(page);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(Path.Combine(directory, $"updater-production-{width}.png"))) encoder.Save(stream);
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

                // Exercise the legacy Settings host too, without initializing its
                // unrelated hardware/settings controls or showing a native window.
                var settings = new SettingsPage();
                SettingsPage.PendingTabKey = "Updates";
                ((Task)typeof(SettingsPage).GetMethod("InitializeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, null)!).GetAwaiter().GetResult();
                var navigation = (ListBox)settings.FindName("_navigationListBox");
                navigation.SelectedItem = navigation.Items.Cast<object>().Single(item =>
                    (string)item.GetType().GetProperty("Key")!.GetValue(item)! == "Updates");
                var host = (ContentControl)settings.FindName("_contentControl");
                if (host.Content is not SettingsUpdateControl preferences) throw new InvalidOperationException("Settings Updates navigation failed to mount its control.");
                // An unrelated unavailable dependency must not break Updates or
                // leave the selected section empty. No hardware is registered.
                navigation.SelectedItem = navigation.Items.Cast<object>().Single(item =>
                    (string)item.GetType().GetProperty("Key")!.GetValue(item)! == "Appearance");
                if (host.Content is not TextBlock { Text.Length: > 0 })
                    throw new InvalidOperationException("Failed settings pane did not show a recoverable explanation.");
                navigation.SelectedItem = navigation.Items.Cast<object>().Single(item =>
                    (string)item.GetType().GetProperty("Key")!.GetValue(item)! == "Updates");
                if (!ReferenceEquals(host.Content, preferences)) throw new InvalidOperationException("Settings Updates did not recover with its cached control.");
                foreach (var name in new[] { "_checkUpdatesCard", "_updateChannelCard", "_updateCheckFrequencyCard" })
                    if (((FrameworkElement)preferences.FindName(name)).Visibility != Visibility.Visible)
                        throw new InvalidOperationException("Disabled updater hid a settings row: " + name);
                var checkButton = (Button)preferences.FindName("_checkUpdatesButton");
                if (checkButton.IsEnabled || ((ComboBox)preferences.FindName("_updateChannelComboBox")).IsEnabled)
                    throw new InvalidOperationException("Read-only update inputs were enabled.");

                settings.Measure(new Size(width, 560));
                settings.Arrange(new Rect(0, 0, width, 560));
                settings.UpdateLayout();

                var guard = typeof(LenovoLegionToolkit.WPF.Windows.MainWindow).GetMethod("IsReadOnlyNavigationInput", BindingFlags.Static | BindingFlags.NonPublic)!;
                if ((bool)guard.Invoke(null, [checkButton])!) throw new InvalidOperationException("Read-only shell accepted Settings update input.");
                // Simulate only the routing policy, not any user operation. The
                // isolated profile remains selected; no setters/events are invoked.
                var readOnly = typeof(Folders).GetField("_isReadOnlySession", BindingFlags.Static | BindingFlags.NonPublic)!;
                try
                {
                    readOnly.SetValue(null, false);
                    checker.Disable = false;
                    if (!(bool)guard.Invoke(null, [checkButton])! ||
                        (bool)guard.Invoke(null, [new Button { Name = "_checkUpdatesButton" }])!)
                        throw new InvalidOperationException("Settings updater routing was blocked or escaped its owner.");
                    checker.Disable = true;
                    if ((bool)guard.Invoke(null, [checkButton])!)
                        throw new InvalidOperationException("Disabled updater accepted Settings input.");
                }
                finally { readOnly.SetValue(null, true); checker.Disable = true; }
                settings.Measure(new Size(width, 560));
                settings.Arrange(new Rect(0, 0, width, 560));
                settings.UpdateLayout();
                var notice = (TextBlock)preferences.FindName("_updatesNotice");
                if (notice.ActualWidth < 100 || notice.ActualHeight < 15 || host.Opacity < 0.9)
                    throw new InvalidOperationException($"Settings Updates content collapsed: host {host.ActualWidth}x{host.ActualHeight}, opacity {host.Opacity}, notice {notice.ActualWidth}x{notice.ActualHeight}.");
                var settingsImage = new RenderTargetBitmap(width, 560, 96, 96, PixelFormats.Pbgra32);
                settingsImage.Render(settings);
                foreach (var input in new FrameworkElement[] { checkButton, (ComboBox)preferences.FindName("_updateChannelComboBox") })
                {
                    var origin = input.TranslatePoint(new Point(), settings);
                    var bounds = new Int32Rect((int)origin.X, (int)origin.Y, (int)input.ActualWidth, (int)input.ActualHeight);
                    var stride = bounds.Width * 4;
                    var pixels = new byte[stride * bounds.Height];
                    settingsImage.CopyPixels(bounds, pixels, stride, 0);
                    var bright = 0;
                    for (var index = 0; index < pixels.Length; index += 4)
                        if (pixels[index] > 230 && pixels[index + 1] > 230 && pixels[index + 2] > 230) bright++;
                    if (bright > bounds.Width * bounds.Height / 4)
                        throw new InvalidOperationException("Disabled updater input reverted to a bright system fill: " + input.Name);
                }
                var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsImage));
                using (var stream = File.Create(Path.Combine(directory, $"settings-updates-production-{width}.png"))) settingsEncoder.Save(stream);
            }
            if (!baseline.SequenceEqual(Snapshot(profile)))
                throw new InvalidOperationException("Production Updater page mutated its isolated read-only profile.");
            Console.WriteLine("PASS: actual Updater and Settings Updates entry/navigation at both widths, independent lazy section creation, cached recovery after unrelated failure, visible disabled rows and scoped shell input guards; isolated profile unchanged; no network or hardware services.");
        }
        finally { IoCContainer.Dispose(); }
    }

    private static string[] Snapshot(string profile) => Directory.EnumerateFileSystemEntries(profile, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => File.Exists(path)
            ? path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            : path + ":directory").ToArray();

    private sealed class UpdateOnlyModule : Autofac.Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<HttpClientFactory>().SingleInstance();
            builder.RegisterType<ApplicationSettings>().SingleInstance();
            builder.RegisterType<UpdateSettings>().SingleInstance();
            builder.RegisterType<UpdateChecker>().SingleInstance();
        }
    }
}
