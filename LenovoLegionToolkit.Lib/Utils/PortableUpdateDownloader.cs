using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LenovoLegionToolkit.Lib.Utils;

/// <summary>Downloads and verifies a portable archive. Never extracts, executes, or replaces application files.</summary>
public static class PortableUpdateDownloader
{
    public static Task<string> DownloadAsync(HttpClient client, PortableUpdatePackage package, string directory,
        IProgress<float>? progress = null, CancellationToken cancellationToken = default) =>
        DownloadCoreAsync(client, package, directory, progress, cancellationToken, static () => !Folders.IsReadOnlySession);

    private static async Task<string> DownloadCoreAsync(HttpClient client, PortableUpdatePackage package, string directory,
        IProgress<float>? progress, CancellationToken cancellationToken, Func<bool> canWrite)
    {
        void RequireWrite() { if (!canWrite()) throw new InvalidOperationException("Update downloads are disabled in read-only mode."); }
        RequireWrite();
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProjectReleaseFeed.IsPackageUrl(package.Url) || !ProjectReleaseFeed.IsSha256(package.Sha256) ||
            package.Size is <= 0 or > ProjectReleaseFeed.MaximumPackageSize)
            throw new ArgumentException("The release has no valid Windows x64 package and SHA-256 digest.");
        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, "ProjectLegion-update-" + Guid.NewGuid().ToString("N") + ".zip");
        var partial = final + ".partial";
        try
        {
            using var response = await client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } actual && actual.Scheme != Uri.UriSchemeHttps)
                throw new SecurityException("The package download was redirected away from HTTPS.");
            if (response.Content.Headers.ContentLength is { } length && length != package.Size)
                throw new InvalidDataException("The package length does not match the release metadata.");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                progress?.Report(0);
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    RequireWrite();
                    total += count;
                    if (total > package.Size) throw new InvalidDataException("The download exceeded the declared package size.");
                    hash.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    progress?.Report((float)total / package.Size);
                }
                if (total != package.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("The downloaded package failed SHA-256 or size verification.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            RequireWrite();
            VerifyArchive(partial);
            cancellationToken.ThrowIfCancellationRequested();
            RequireWrite();
            File.Move(partial, final);
            return final;
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }

    internal static void VerifyArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("The archive contains too many entries.");
        var names = new global::System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is ".." or ".") ||
                !names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new SecurityException("The archive contains an unsafe or duplicate file path.");
            expanded = checked(expanded + entry.Length);
            if (expanded > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("The archive exceeds the expanded-size limit.");
        }
        foreach (var file in new[] { "Project Legion.exe", "Project Legion.dll", "LenovoLegionToolkit.Lib.dll", "Project Legion.deps.json", "Project Legion.runtimeconfig.json", "LICENSE", "NOTICE" })
            if (!names.Contains(file)) throw new InvalidDataException("The archive is missing " + file + ".");
    }
}
