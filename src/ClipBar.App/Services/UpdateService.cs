using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipBar.Core.Security;

namespace ClipBar.App.Services;

/// <summary>Result of one update check. Failures are values, never exceptions, so the tray
/// command can report them without a crash dialog.</summary>
public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? DownloadUrl = null,
    string? ReleaseUrl = null,
    string? Error = null);

/// <summary>
/// Reads the release metadata the official site publishes at /release.json. The site is updated
/// as part of every release and is not subject to GitHub's anonymous API rate limit; the payload
/// also carries the exact installer and portable URLs. ClipBar only reads metadata — the
/// installer is never downloaded or executed here. The caller opens the chosen URL through
/// <see cref="SafeUrl"/> in the shell.
/// </summary>
public sealed class UpdateService
{
    public const string ReleaseFeedUrl = "https://clipbar.us.ci/release.json";

    private const string ReleasePageBase = "https://github.com/xiaopeng66/ClipBar/releases/";

    private static readonly HttpClient SharedClient = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _client;
    private readonly Func<string> _currentVersion;

    public UpdateService(HttpClient? client = null, Func<string>? currentVersion = null)
    {
        _client = client ?? SharedClient;
        _currentVersion = currentVersion ?? DefaultCurrentVersion;
    }

    /// <summary>Checks the official release feed. Network, HTTP and JSON errors become
    /// <see cref="UpdateCheckStatus.Failed"/> results instead of throwing.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = Normalize(_currentVersion());
        if (!TryParse(current, out var currentVersion))
        {
            return Failed(current, "当前版本号无法识别");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseFeedUrl);
            request.Headers.UserAgent.ParseAdd("ClipBar/" + current);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed(current, response.StatusCode == HttpStatusCode.Forbidden
                    ? "更新服务器暂时不可用，请稍后再试"
                    : $"更新服务器返回 {(int)response.StatusCode}");
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var feed = await JsonSerializer
                .DeserializeAsync<ReleaseFeed>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (feed is null || string.IsNullOrWhiteSpace(feed.Version))
            {
                return Failed(current, "发布信息不完整");
            }

            var latest = Normalize(feed.Version);
            if (!TryParse(latest, out var latestVersion))
            {
                return Failed(current, "最新版本号无法识别");
            }

            if (latestVersion <= currentVersion)
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, current, latest);
            }

            var releaseUrl = SafeUrl.Normalize(
                string.IsNullOrWhiteSpace(feed.Tag)
                    ? ReleasePageBase + "latest"
                    : ReleasePageBase + "tag/" + feed.Tag);
            var download = FindSafeAsset(feed, "installer")
                ?? FindSafeAsset(feed, "standard")
                ?? releaseUrl;
            if (download is null)
            {
                return Failed(current, "最新版本没有可用的下载地址");
            }

            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                current,
                latest,
                download,
                releaseUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(current, "检查更新超时");
        }
        catch (HttpRequestException ex)
        {
            return Failed(current, "网络不可用：" + ex.Message);
        }
        catch (JsonException)
        {
            return Failed(current, "更新信息格式不正确");
        }
    }

    private static string? FindSafeAsset(ReleaseFeed feed, string key)
    {
        if (feed.Assets is null)
        {
            return null;
        }

        foreach (var pair in feed.Assets)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return SafeUrl.Normalize(pair.Value?.Url);
            }
        }

        return null;
    }

    private static UpdateCheckResult Failed(string current, string error)
        => new(UpdateCheckStatus.Failed, current, Error: error);

    private static string DefaultCurrentVersion()
        => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static string Normalize(string value)
        => (value ?? string.Empty).Trim().TrimStart('v', 'V');

    /// <summary>Accepts 1.2 / 1.2.3 and ignores a pre-release suffix such as 1.2.0-beta.</summary>
    private static bool TryParse(string value, out Version version)
    {
        var numeric = value.Split('-', 2)[0];
        if (Version.TryParse(numeric, out var parsed))
        {
            version = new Version(
                parsed.Major,
                parsed.Minor,
                parsed.Build < 0 ? 0 : parsed.Build);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static HttpClient CreateClient() => new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private sealed class ReleaseFeed
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("tag")]
        public string? Tag { get; set; }

        [JsonPropertyName("assets")]
        public Dictionary<string, ReleaseAsset>? Assets { get; set; }
    }

    private sealed class ReleaseAsset
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}