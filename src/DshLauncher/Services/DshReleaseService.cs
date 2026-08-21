using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DshLauncher.Infrastructure;

namespace DshLauncher.Services;

internal sealed record DshReleaseVersion(
    string Tag,
    string Version,
    DateTimeOffset PublishedAt);

internal sealed class DshReleaseService : IDisposable
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/deepseek-ai/deepseek-harness/releases?per_page=30";
    private const string ReleasesFeedUrl =
        "https://github.com/deepseek-ai/deepseek-harness/releases.atom";

    private readonly HttpClient _http;
    private readonly AppLogger _log;

    public DshReleaseService(AppLogger log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "DSHLauncher",
                LauncherUpdateService.CurrentVersionText));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<IReadOnlyList<DshReleaseVersion>> GetPublishedAsync(
        IReadOnlySet<string> availableVersions,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetFromApiAsync(
                availableVersions,
                cancellationToken);
        }
        catch (TaskCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            _log.Info(
                "DSH GitHub Releases API timed out; using the official Atom feed.");
        }
        catch (Exception error)
            when (error is HttpRequestException or JsonException)
        {
            _log.Info(
                "DSH GitHub Releases API is unavailable; " +
                "using the official Atom feed: " +
                error.Message);
        }

        try
        {
            return await GetFromFeedAsync(
                availableVersions,
                cancellationToken);
        }
        catch (TaskCanceledException error)
            when (!cancellationToken.IsCancellationRequested)
        {
            LogNpmFallback(error);
            return [];
        }
        catch (Exception error)
            when (error is HttpRequestException or XmlException)
        {
            LogNpmFallback(error);
            return [];
        }
    }

    private async Task<IReadOnlyList<DshReleaseVersion>> GetFromApiAsync(
        IReadOnlySet<string> availableVersions,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            ReleasesApiUrl,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var releases = new List<DshReleaseVersion>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) &&
                draft.GetBoolean())
            {
                continue;
            }

            if (!release.TryGetProperty("tag_name", out var tagNode) ||
                !release.TryGetProperty("published_at", out var publishedNode) ||
                !DateTimeOffset.TryParse(
                    publishedNode.GetString(),
                    out var publishedAt))
            {
                continue;
            }

            AddRelease(
                releases,
                availableVersions,
                tagNode.GetString(),
                publishedAt);
        }

        return Order(releases);
    }

    private async Task<IReadOnlyList<DshReleaseVersion>> GetFromFeedAsync(
        IReadOnlySet<string> availableVersions,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            ReleasesFeedUrl,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var document = XDocument.Parse(content);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var releases = new List<DshReleaseVersion>();

        foreach (var entry in document.Descendants(atom + "entry"))
        {
            var updatedText = entry.Element(atom + "updated")?.Value;
            var href = entry.Elements(atom + "link")
                .Select(link => link.Attribute("href")?.Value)
                .FirstOrDefault(value =>
                    value?.Contains(
                        "/releases/tag/",
                        StringComparison.OrdinalIgnoreCase) == true);
            if (!DateTimeOffset.TryParse(updatedText, out var publishedAt) ||
                string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var tag = Uri.UnescapeDataString(
                href[(href.LastIndexOf(
                    "/releases/tag/",
                    StringComparison.OrdinalIgnoreCase) +
                    "/releases/tag/".Length)..]);
            AddRelease(
                releases,
                availableVersions,
                tag,
                publishedAt);
        }

        return Order(releases);
    }

    private static void AddRelease(
        ICollection<DshReleaseVersion> releases,
        IReadOnlySet<string> availableVersions,
        string? tag,
        DateTimeOffset publishedAt)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var version = PackageVersionFromTag(tag);
        if (availableVersions.Contains(version))
        {
            releases.Add(new DshReleaseVersion(tag, version, publishedAt));
        }
    }

    private static IReadOnlyList<DshReleaseVersion> Order(
        IEnumerable<DshReleaseVersion> releases) =>
        releases
            .OrderByDescending(release => release.PublishedAt)
            .ToList();

    internal static string PackageVersionFromTag(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith("dsh-v", StringComparison.OrdinalIgnoreCase))
        {
            return value[5..];
        }

        if (value.StartsWith("dsh-", StringComparison.OrdinalIgnoreCase))
        {
            return value[4..].TrimStart('v', 'V');
        }

        return value.TrimStart('v', 'V');
    }

    private void LogNpmFallback(Exception error)
    {
        _log.Warn(
            "读取 DSH 官方 GitHub Releases 失败，将按 npm 发布时间选择版本：" +
            error.Message);
    }

    public void Dispose() => _http.Dispose();
}
