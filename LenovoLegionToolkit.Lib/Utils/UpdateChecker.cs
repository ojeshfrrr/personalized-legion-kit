using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Settings;
using NeoSmart.AsyncLock;
using Octokit;
using Octokit.Internal;

namespace LenovoLegionToolkit.Lib.Utils;

public class UpdateChecker
{
    private readonly HttpClientFactory _httpClientFactory;
    private readonly UpdateSettings _updateSettings = IoCContainer.Resolve<UpdateSettings>();
    private readonly AsyncLock _updateSemaphore = new();

    private DateTime _lastUpdate;
    private TimeSpan _minimumTimeSpanForRefresh;
    private Update[] _updates = [];

    // Retained for compatibility with the update UI. Project Legion currently has no server feed.
    public UpdateFromServer? UpdateFromServer;

    public bool Disable { get; set; }
    public UpdateCheckStatus Status { get; set; }

    public UpdateChecker(HttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        UpdateMinimumTimeSpanForRefresh();
        _lastUpdate = _updateSettings.Store.LastUpdateCheckDateTime ?? DateTime.MinValue;
    }

    public async Task<Version?> CheckAsync(bool forceCheck)
    {
        using (await _updateSemaphore.LockAsync().ConfigureAwait(false))
        {
            // No network request is made until the fork has an explicitly configured release feed.
            if (Disable || !ProjectBranding.HasUpdateFeed)
            {
                _lastUpdate = DateTime.UtcNow;
                _updates = [];
                Status = UpdateCheckStatus.Error;
                return null;
            }

            var shouldCheck = DateTime.UtcNow - _lastUpdate > _minimumTimeSpanForRefresh;
            if (!forceCheck && !shouldCheck)
                return _updates.Length != 0 ? _updates[0].Version : null;

            try
            {
                Log.Instance.Trace($"Checking the configured Project Legion GitHub release feed...");

                var adapter = new HttpClientAdapter(_httpClientFactory.CreateHandler);
                var connection = new Connection(new ProductHeaderValue("ProjectLegion-UpdateChecker"), adapter);
                var githubClient = new GitHubClient(connection);
                var releases = await githubClient.Repository.Release.GetAll(
                    ProjectBranding.UpdateGitHubOwner,
                    ProjectBranding.UpdateGitHubRepository,
                    new ApiOptions { PageSize = 5 }).ConfigureAwait(false);

                var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
                var buildDate = Assembly.GetEntryAssembly()?.GetBuildDateTime() ?? new DateTime(2000, 1, 1);
                _updates = releases
                    .Where(release => !release.Draft)
                    .Where(release => IsMatchingChannel(release, _updateSettings.Store.UpdateChannel))
                    .Where(release => (release.PublishedAt ?? release.CreatedAt).UtcDateTime >= buildDate)
                    .Select(release => new { Release = release, Version = TryParseReleaseVersion(release.TagName) })
                    .Where(item => item.Version is not null && item.Version > currentVersion)
                    .OrderByDescending(item => item.Version)
                    .Select(item => new Update(item.Release))
                    .ToArray();

                Status = UpdateCheckStatus.Success;
                return _updates.Length != 0 ? _updates[0].Version : null;
            }
            catch (RateLimitExceededException ex)
            {
                Log.Instance.Trace($"Reached GitHub API rate limit.", ex);
                Status = UpdateCheckStatus.RateLimitReached;
                return null;
            }
            catch (Exception ex)
            {
                Log.Instance.Trace($"Error checking the configured Project Legion GitHub release feed.", ex);
                Status = UpdateCheckStatus.Error;
                return null;
            }
            finally
            {
                _lastUpdate = DateTime.UtcNow;
                _updateSettings.Store.LastUpdateCheckDateTime = _lastUpdate;
                _updateSettings.SynchronizeStore();
            }
        }
    }

    public async Task<Update[]> GetUpdatesAsync()
    {
        using (await _updateSemaphore.LockAsync().ConfigureAwait(false))
            return _updates;
    }

    public async Task<string> DownloadLatestUpdateAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
    {
        using (await _updateSemaphore.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!ProjectBranding.HasUpdateFeed)
                throw new InvalidOperationException("Project Legion update feed is not configured.");

            var latestUpdate = _updates.OrderByDescending(update => update.Version).FirstOrDefault();
            if (latestUpdate.Url is null)
                throw new InvalidOperationException("No Project Legion installer was found in the configured release.");

            var tempPath = global::System.IO.Path.Combine(Folders.Temp, $"ProjectLegionSetup_{Guid.NewGuid()}.exe");
            using var httpClient = _httpClientFactory.Create();
            await using (var fileStream = global::System.IO.File.OpenWrite(tempPath))
            {
                await httpClient.DownloadAsync(latestUpdate.Url, fileStream, progress, cancellationToken).ConfigureAwait(false);
            }

            VerifySignature(tempPath);
            return tempPath;
        }
    }

    public void UpdateMinimumTimeSpanForRefresh() => _minimumTimeSpanForRefresh = _updateSettings.Store.UpdateCheckFrequency switch
    {
        UpdateCheckFrequency.Never => TimeSpan.FromSeconds(0),
        UpdateCheckFrequency.PerHour => TimeSpan.FromHours(1),
        UpdateCheckFrequency.PerThreeHours => TimeSpan.FromHours(3),
        UpdateCheckFrequency.PerTwelveHours => TimeSpan.FromHours(12),
        UpdateCheckFrequency.PerDay => TimeSpan.FromDays(1),
        UpdateCheckFrequency.PerWeek => TimeSpan.FromDays(7),
        UpdateCheckFrequency.PerMonth => TimeSpan.FromDays(30),
        _ => throw new ArgumentException(nameof(_updateSettings.Store.UpdateCheckFrequency))
    };

    private static bool IsMatchingChannel(Release release, UpdateChannel channel)
    {
        var branch = channel switch
        {
            UpdateChannel.Stable or UpdateChannel.Beta => "main",
            UpdateChannel.Dev => "dev",
            UpdateChannel.Test => "test",
            _ => string.Empty
        };

        if (branch.Length == 0)
            return false;

        var matchesBranch = string.IsNullOrWhiteSpace(release.TargetCommitish)
            ? branch.Equals("main", StringComparison.OrdinalIgnoreCase)
            : release.TargetCommitish.Equals(branch, StringComparison.OrdinalIgnoreCase)
              || release.TargetCommitish.EndsWith($"/{branch}", StringComparison.OrdinalIgnoreCase)
              || release.TargetCommitish.EndsWith($"\\{branch}", StringComparison.OrdinalIgnoreCase);

        return matchesBranch && (channel != UpdateChannel.Stable || !release.Prerelease)
                            && (channel != UpdateChannel.Beta || release.Prerelease)
                            && (channel is not (UpdateChannel.Dev or UpdateChannel.Test) || !release.Prerelease);
    }

    private static Version? TryParseReleaseVersion(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return null;

        var normalized = tagName.TrimStart('v', 'V');
        if (Version.TryParse(normalized, out var parsed))
            return parsed;

        var numericPart = new string(normalized.TakeWhile(character => char.IsDigit(character) || character == '.')
            .ToArray()).Trim('.');
        return Version.TryParse(numericPart, out parsed) ? parsed : null;
    }

    private static void VerifySignature(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var embeddedCertificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            using var certificate = new X509Certificate2(embeddedCertificate);
            if (!certificate.Thumbprint.Equals(ProjectBranding.UpdateTrustedSignerThumbprint, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("The installer signer does not match the configured Project Legion certificate.");
        }
        catch (Exception ex)
        {
            try
            {
                if (global::System.IO.File.Exists(filePath))
                    global::System.IO.File.Delete(filePath);
            }
            catch { /* Ignore cleanup failures. */ }

            throw new SecurityException("The downloaded installer signature could not be verified.", ex);
        }
    }
}

public class SecurityException : Exception
{
    public SecurityException() { }
    public SecurityException(string message) : base(message) { }
    public SecurityException(string message, Exception innerException) : base(message, innerException) { }
}
