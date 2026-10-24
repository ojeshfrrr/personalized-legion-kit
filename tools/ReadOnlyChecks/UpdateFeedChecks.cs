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

internal static class UpdateFeedChecks
{
    private static string AssetUrl(string tag) => $"https://github.com/ojeshfrrr/personalized-legion-kit/releases/download/{tag}/{ProjectReleaseFeed.PackageName}";
    private static JsonObject Release(string tag, bool preview = false, bool draft = false) => new()
    {
        ["tag_name"] = tag, ["name"] = tag, ["body"] = "Offline release fixture", ["draft"] = draft,
        ["prerelease"] = preview, ["target_commitish"] = "6dd9ce2784421d17e50a0aca7a2db44ddee51e2e",
        ["published_at"] = "2000-01-01T00:00:00Z",
        ["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = ProjectReleaseFeed.PackageName, ["state"] = "uploaded", ["size"] = 100,
            ["digest"] = "sha256:" + new string('A', 64), ["browser_download_url"] = AssetUrl(tag)
        })
    };
    public static void Run(Action<bool, string> check)
    {
        var current = new Version(0, 1, 0, 0);
        var feed = new JsonArray(Release("v0.2.0.0"), Release("v0.3.0.0", true), Release("v0.1.0.0"), Release("v9.0.0.0", draft: true));
        var preview = ProjectReleaseFeed.Parse(feed.ToJsonString(), current, UpdateChannel.Beta);
        var stable = ProjectReleaseFeed.Parse(feed.ToJsonString(), current, UpdateChannel.Stable);
        check(preview.Select(r => r.Version).SequenceEqual(new[] { new Version(0, 3, 0, 0), new Version(0, 2, 0, 0) }) &&
            stable.Length == 1 && stable[0].Version == new Version(0, 2, 0, 0),
            "updater compares versions independently of release age/commit SHA; Preview includes stable and draft/current releases are excluded");
        check(ProjectReleaseFeed.TryParseVersion("v1.2.3", out var version) && version == new Version(1, 2, 3, 0) &&
            !ProjectReleaseFeed.TryParseVersion("v1.2.3-unverified", out _) &&
            ProjectReleaseFeed.Parse(feed.ToJsonString(), current, UpdateChannel.Dev).Length == 0,
            "update versions normalize missing revisions and reject malformed tags/unsupported legacy channels");
        foreach (var field in new[] { "digest", "browser_download_url", "size", "name" })
        {
            var release = Release("v0.2.0.0");
            var asset = release["assets"]![0]!;
            asset[field] = field switch { "digest" => JsonValue.Create("sha256:invalid"), "browser_download_url" => JsonValue.Create("https://github.com.evil.test/file.zip"), "size" => JsonValue.Create(-1), _ => JsonValue.Create("ProjectLegion-corresponding-source.zip") };
            var parsed = ProjectReleaseFeed.Parse(new JsonArray(release).ToJsonString(), current, UpdateChannel.Beta);
            check(parsed.Length == 1 && parsed[0].Package is null, "release remains visible but download rejects invalid " + field);
        }
        var duplicate = Release("v0.2.0.0");
        ((JsonArray)duplicate["assets"]!).Add(duplicate["assets"]![0]!.DeepClone());
        check(ProjectReleaseFeed.Parse(new JsonArray(duplicate).ToJsonString(), current, UpdateChannel.Beta).Single().Package is null &&
            !ProjectReleaseFeed.IsPackageUrl(AssetUrl("v0.2.0.0") + "?redirect=elsewhere") &&
            !ProjectReleaseFeed.IsPackageUrl(AssetUrl("v0.2.0.0").Replace("ojeshfrrr", "another-owner")) &&
            !ProjectReleaseFeed.IsPackageUrl(AssetUrl("v0.2.0.0").Replace("https:", "http:")),
            "updater rejects ambiguous packages, foreign repositories, query redirects and non-HTTPS URLs");
    }
}
