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

internal static class UpdateProbeChecks
{
    public static void Inspect(string file)
    {
        var releases = ProjectReleaseFeed.Parse(File.ReadAllText(file), new Version(0, 0, 0, 0), UpdateChannel.Beta);
        if (!releases.Any(r => r.Package is not null)) throw new InvalidDataException("No downloadable release found in the actual feed.");
        foreach (var release in releases) Console.WriteLine($"Version {release.Version}; verified package metadata: {release.Package is not null}");
        Console.WriteLine("PASS: actual release feed parsed; no packages downloaded or executed.");
    }
    public static async Task VerifyDownloadAsync(string file)
    {
        var release = ProjectReleaseFeed.Parse(File.ReadAllText(file), new Version(0, 0, 0, 0), UpdateChannel.Beta)
            .First(r => r.Package is not null);
        using var client = new HttpClientFactory().CreateForUpdates();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var output = Path.Combine(Environment.CurrentDirectory, "BuildProjectLegion", "UpdaterChecks", "download-" + Guid.NewGuid().ToString("N"));
        var path = await PortableUpdateDownloader.DownloadAsync(client, release.Package!, output, cancellationToken: deadline.Token);
        Console.WriteLine($"PASS: real version {release.Version} downloaded and SHA-256/ZIP verified: {path}. No extraction or execution.");
    }
}
