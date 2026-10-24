using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LenovoLegionToolkit.Lib.Utils;

public sealed record PortableUpdatePackage(string Url, long Size, string Sha256);

public sealed record ProjectRelease(Version Version, string Title, string Description,
    DateTimeOffset Date, string ReleaseUrl, PortableUpdatePackage? Package, string? InstallerUrl);

/// <summary>Only this fork's published releases and exact Windows x64 ZIP are update candidates.</summary>
public static class ProjectReleaseFeed
{
    public const string PackageName = "ProjectLegion-win-x64-framework-dependent.zip";
    public const long MaximumPackageSize = 512L * 1024 * 1024;
    public static string ApiUrl => $"https://api.github.com/repos/{ProjectBranding.UpdateGitHubOwner}/{ProjectBranding.UpdateGitHubRepository}/releases?per_page=30";

    public static ProjectRelease[] Parse(string json, Version currentVersion, UpdateChannel channel)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("The release feed was not a release list.");
        var updates = new List<ProjectRelease>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var preview = release.GetProperty("prerelease").GetBoolean();
            if (channel != UpdateChannel.Beta && (channel != UpdateChannel.Stable || preview)) continue;
            var tag = Text(release, "tag_name");
            if (!TryParseVersion(tag, out var version) || version <= Normalize(currentVersion)) continue;
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            var matching = assets.Where(a => Text(a, "name") == PackageName && Text(a, "state") == "uploaded").ToArray();
            PortableUpdatePackage? package = null;
            if (matching.Length == 1)
            {
                var asset = matching[0];
                var url = Text(asset, "browser_download_url");
                var digest = Text(asset, "digest");
                if (asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var size) &&
                    size is > 0 and <= MaximumPackageSize && IsAssetUrl(url, tag, PackageName) &&
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsSha256(digest[7..]))
                    package = new(url, size, digest[7..].ToUpperInvariant());
            }
            var installer = assets.FirstOrDefault(a => Text(a, "name").StartsWith("ProjectLegionSetup", StringComparison.Ordinal) &&
                Text(a, "name").EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                IsAssetUrl(Text(a, "browser_download_url"), tag, Text(a, "name")));
            var date = DateTimeOffset.TryParse(Text(release, "published_at"), out var published) ? published : DateTimeOffset.MinValue;
            updates.Add(new(version!, Text(release, "name"), Text(release, "body"), date,
                ProjectBranding.ReleasesUrl + "/tag/" + Uri.EscapeDataString(tag), package,
                installer.ValueKind == JsonValueKind.Undefined ? null : Text(installer, "browser_download_url")));
        }
        // TargetCommitish can be a commit SHA or a custom branch; channel is release metadata, not branch spelling.
        return updates.OrderByDescending(u => u.Version).DistinctBy(u => u.Version).ToArray();
    }

    public static bool TryParseVersion(string tag, out Version? version)
    {
        version = null;
        if (tag.StartsWith('v') || tag.StartsWith('V')) tag = tag[1..];
        if (tag.Length == 0 || tag.Any(c => !char.IsAsciiDigit(c) && c != '.') || !Version.TryParse(tag, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    public static bool IsAssetUrl(string url, string tag, string name) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath == $"/{ProjectBranding.UpdateGitHubOwner}/{ProjectBranding.UpdateGitHubRepository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";

    public static bool IsPackageUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Segments.Length == 7 && TryParseVersion(uri.Segments[^2].TrimEnd('/'), out _) &&
        IsAssetUrl(url, uri.Segments[^2].TrimEnd('/'), PackageName);

    public static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private static string Text(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
