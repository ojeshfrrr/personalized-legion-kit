using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.Lib.Settings;

internal static class UpdateServiceChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var updateClient = new HttpClientFactory().CreateForUpdates();
        check(ProjectBranding.HasUpdateFeed && !ProjectBranding.HasSignedInstallerFeed,
            "owned release feed is configured while unsigned releases cannot enable the signed-installer path");

        var current = new Version(0, 1, 0, 0);
        var package = new PortableUpdatePackage(ProjectBranding.ReleasesUrl + "/download/v0.2.0.0/" + ProjectReleaseFeed.PackageName, 100, new string('A', 64));
        var settings = new UpdateSettings();
        var originalTimestamp = settings.Store.LastUpdateCheckDateTime;
        var checker = (UpdateChecker)typeof(UpdateChecker).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(HttpClientFactory), typeof(UpdateSettings)], null)!.Invoke([new HttpClientFactory(), settings]);
        check(await checker.CheckAsync(true) is null && settings.Store.LastUpdateCheckDateTime == originalTimestamp &&
            await RejectAsync<InvalidOperationException>(() => checker.DownloadPortableUpdateAsync(new(current, "fixture", "", DateTimeOffset.MinValue, ProjectBranding.ReleasesUrl, package, null))) &&
            await RejectAsync<InvalidOperationException>(() => checker.DownloadLatestUpdateAsync()),
            "read-only update service blocks checks, ZIP and installer paths without changing check preferences");
    }
    private static async Task<bool> RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return true; } return false; }
}
