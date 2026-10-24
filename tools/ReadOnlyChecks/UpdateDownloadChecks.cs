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

internal static class UpdateDownloadChecks
{
    private static string AssetUrl(string tag) => $"https://github.com/ojeshfrrr/personalized-legion-kit/releases/download/{tag}/{ProjectReleaseFeed.PackageName}";
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "BuildProjectLegion", "UpdaterChecks", "offline-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Unexpected test-directory collision.");
        var bytes = Archive();
        var package = Package(bytes);
        var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var client = new HttpClient(handler);
        check(await RejectAsync<InvalidOperationException>(() => PortableUpdateDownloader.DownloadAsync(client, package, root)) &&
            handler.Calls == 0 && !Directory.Exists(root),
            "public update downloader blocks actual read-only mode before network access or directory creation");
        try
        {
            var path = await Download(client, package, root);
            check(File.ReadAllBytes(path).SequenceEqual(bytes) && Path.GetExtension(path) == ".zip" &&
                Directory.GetFiles(root).Length == 1 && !Directory.Exists(Path.Combine(root, "extracted")),
                "verified update is a separate exact ZIP; no application extraction, replacement, execution or restart occurs");
            var before = Directory.GetFiles(root).Length;
            check(await RejectAsync<LenovoLegionToolkit.Lib.Utils.SecurityException>(() => Download(client, package with { Sha256 = new string('0', 64) }, root)) &&
                Directory.GetFiles(root).Length == before,
                "checksum failure removes its partial file and preserves earlier verified downloads");
            using var errorClient = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent(bytes) }));
            check(await RejectAsync<HttpRequestException>(() => Download(errorClient, package, root)) && Directory.GetFiles(root).Length == before,
                "HTTP failure cannot become an update package");
            check(await RejectAsync<InvalidDataException>(() => Download(client, package with { Size = bytes.Length + 1 }, root)) && Directory.GetFiles(root).Length == before,
                "declared-size mismatch leaves no final or partial update");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            check(await RejectAsync<OperationCanceledException>(() => Download(client, package, root, cancellation.Token)) && Directory.GetFiles(root).Length == before,
                "cancelled update download leaves no new files");
            using var midStreamCancellation = new CancellationTokenSource();
            using var cancelClient = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new CancellingStream(bytes, midStreamCancellation)) }));
            check(await RejectAsync<OperationCanceledException>(() => Download(cancelClient, package, root, midStreamCancellation.Token)) && Directory.GetFiles(root).Length == before,
                "mid-stream cancellation removes the partially written update and preserves prior packages");
            var writable = true;
            using var lockClient = new HttpClient(new ResponseHandler(() =>
            { writable = false; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }));
            check(await RejectAsync<InvalidOperationException>(() => Download(lockClient, package, root, canWrite: () => writable)) && Directory.GetFiles(root).Length == before,
                "losing write permission during a download prevents final publication and cleans its temporary file");
            var unsafeBytes = Archive("../outside.exe");
            using var unsafeClient = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(unsafeBytes) }));
            check(await RejectAsync<LenovoLegionToolkit.Lib.Utils.SecurityException>(() => Download(unsafeClient, Package(unsafeBytes), root)) && Directory.GetFiles(root).Length == before,
                "matching checksum cannot authorize an archive with a traversal path");
            var missingBytes = Archive(omitExe: true);
            using var missingClient = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(missingBytes) }));
            check(await RejectAsync<InvalidDataException>(() => Download(missingClient, Package(missingBytes), root)) && Directory.GetFiles(root).Length == before,
                "matching checksum cannot authorize an incomplete application archive");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static PortableUpdatePackage Package(byte[] data) => new(AssetUrl("v0.2.0.0"), data.Length, Convert.ToHexString(SHA256.HashData(data)));
    private static byte[] Archive(string? extra = null, bool omitExe = false)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var name in new[] { "Project Legion.exe", "Project Legion.dll", "LenovoLegionToolkit.Lib.dll", "Project Legion.deps.json", "Project Legion.runtimeconfig.json", "LICENSE", "NOTICE" }.Concat(extra is null ? Array.Empty<string>() : new[] { extra }))
            {
                if (omitExe && name == "Project Legion.exe") continue;
                using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("Synthetic archive fixture; not executable.");
            }
        return stream.ToArray();
    }
    private static Task<string> Download(HttpClient client, PortableUpdatePackage package, string root, CancellationToken token = default, Func<bool>? canWrite = null)
    {
        // Isolated fake-HTTP tests inject a writable policy into the private core;
        // public entry points always enforce the real read-only flag.
        var core = typeof(PortableUpdateDownloader).GetMethod("DownloadCoreAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (Task<string>)core.Invoke(null, [client, package, root, null, token, canWrite ?? (Func<bool>)(() => true)])!;
    }
    private static async Task<bool> RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return true; } return false; }
    private sealed class ResponseHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(respond()); }
    }
    private sealed class CancellingStream(byte[] data, CancellationTokenSource cancellation) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await base.ReadAsync(buffer[..Math.Min(64, buffer.Length)], token);
            cancellation.Cancel();
            return read;
        }
    }
}
