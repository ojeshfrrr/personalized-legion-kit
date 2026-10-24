using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.IO;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Settings;
using NeoSmart.AsyncLock;

namespace LenovoLegionToolkit.Lib.Utils;

public class UpdateChecker
{
    private readonly HttpClientFactory _httpClientFactory;
    private readonly UpdateSettings _updateSettings;
    private readonly AsyncLock _updateSemaphore = new();

    private DateTime _lastUpdate;
    private TimeSpan _minimumTimeSpanForRefresh;
    private ProjectRelease[] _updates = [];

    // Retained for compatibility with the update UI. Project Legion currently has no server feed.
    public UpdateFromServer? UpdateFromServer;

    public bool Disable { get; set; }
    public UpdateCheckStatus Status { get; set; }

    public UpdateChecker(HttpClientFactory httpClientFactory) : this(httpClientFactory, IoCContainer.Resolve<UpdateSettings>()) { }

    private UpdateChecker(HttpClientFactory httpClientFactory, UpdateSettings updateSettings)
    {
        _httpClientFactory = httpClientFactory;
        _updateSettings = updateSettings;
        UpdateMinimumTimeSpanForRefresh();
        _lastUpdate = _updateSettings.Store.LastUpdateCheckDateTime ?? DateTime.MinValue;
    }

    public async Task<Version?> CheckAsync(bool forceCheck)
    {
        using (await _updateSemaphore.LockAsync().ConfigureAwait(false))
        {
            // No network request is made until the fork has an explicitly configured release feed.
            if (Disable || !ProjectBranding.HasUpdateFeed || Folders.IsReadOnlySession)
            {
                _lastUpdate = DateTime.UtcNow;
                _updates = [];
                Status = UpdateCheckStatus.Error;
                return null;
            }

            if (!forceCheck && _updateSettings.Store.UpdateCheckFrequency == UpdateCheckFrequency.Never)
                return null;
            var shouldCheck = DateTime.UtcNow - _lastUpdate > _minimumTimeSpanForRefresh;
            if (!forceCheck && !shouldCheck)
                return _updates.Length != 0 ? _updates[0].Version : null;

            try
            {
                Log.Instance.Trace($"Checking the configured Project Legion GitHub release feed...");

                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var client = _httpClientFactory.CreateForUpdates();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectLegion-UpdateChecker");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using var response = await client.GetAsync(ProjectReleaseFeed.ApiUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")))
                {
                    _updates = [];
                    Status = UpdateCheckStatus.RateLimitReached;
                    return null;
                }
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var metadata = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    if (metadata.Length + count > 4 * 1024 * 1024) throw new InvalidOperationException("The release response was too large.");
                    metadata.Write(buffer, 0, count);
                }
                var json = Encoding.UTF8.GetString(metadata.GetBuffer(), 0, (int)metadata.Length);
                var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
                _updates = ProjectReleaseFeed.Parse(json, currentVersion, _updateSettings.Store.UpdateChannel);

                Status = UpdateCheckStatus.Success;
                return _updates.Length != 0 ? _updates[0].Version : null;
            }
            catch (Exception ex)
            {
                _updates = [];
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

    public async Task<ProjectRelease[]> GetUpdatesAsync()
    {
        using (await _updateSemaphore.LockAsync().ConfigureAwait(false))
            return (ProjectRelease[])_updates.Clone();
    }

    public async Task<string> DownloadPortableUpdateAsync(ProjectRelease update, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
    {
        // Guard before even resolving a writable Temp path. Downloading is an explicit
        // user action; checks, navigation and selectors never create update files.
        if (Folders.IsReadOnlySession || Disable || !ProjectBranding.HasUpdateFeed)
            throw new InvalidOperationException("Update downloads are disabled in this session.");
        if (update.Package is not { } package)
            throw new InvalidOperationException("This release has no verified Windows x64 ZIP. Open its release page instead.");
        using (await _updateSemaphore.LockAsync(cancellationToken).ConfigureAwait(false))
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var client = _httpClientFactory.CreateForUpdates())
        {
            if (Folders.IsReadOnlySession || Disable)
                throw new InvalidOperationException("Update downloads are disabled in this session.");
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            return await PortableUpdateDownloader.DownloadAsync(client, package,
                global::System.IO.Path.Combine(Folders.Temp, "Updates"), progress, deadline.Token).ConfigureAwait(false);
        }
    }

    public async Task<string> DownloadLatestUpdateAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
    {
        using (await _updateSemaphore.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Folders.IsReadOnlySession || Disable || !ProjectBranding.HasSignedInstallerFeed)
                throw new InvalidOperationException("A trusted Project Legion installer signer is not configured, or the session is read-only.");

            var latestUpdate = _updates.OrderByDescending(update => update.Version).FirstOrDefault();
            if (latestUpdate?.InstallerUrl is null)
                throw new InvalidOperationException("No Project Legion installer was found in the configured release.");

            var tempPath = global::System.IO.Path.Combine(Folders.Temp, $"ProjectLegionSetup_{Guid.NewGuid()}.exe");
            using var httpClient = _httpClientFactory.CreateForUpdates();
            await using (var fileStream = global::System.IO.File.OpenWrite(tempPath))
            {
                await httpClient.DownloadAsync(latestUpdate.InstallerUrl, fileStream, progress, cancellationToken).ConfigureAwait(false);
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
