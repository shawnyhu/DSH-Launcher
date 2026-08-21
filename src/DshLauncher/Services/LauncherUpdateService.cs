using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DshLauncher.Infrastructure;
using DshLauncher.Models;

namespace DshLauncher.Services;

internal sealed record LauncherRelease(
    string Tag,
    string Name,
    Version Version,
    string PageUrl,
    string AssetName,
    string DownloadUrl,
    long AssetSize);

internal sealed class LauncherUpdateService : IDisposable
{
    private static readonly Version LegacyTagMaximum = new(0, 1, 8);
    private static readonly Regex WindowsTagPattern = new(
        "^win-v(?<version>[0-9]+\\.[0-9]+\\.[0-9]+(?:\\.[0-9]+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LegacyTagPattern = new(
        "^v(?<version>[0-9]+\\.[0-9]+\\.[0-9]+(?:\\.[0-9]+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly AppLogger _log;

    public LauncherUpdateService(AppLogger log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("DSHLauncher", CurrentVersion.ToString()));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public static string CurrentVersionText
    {
        get
        {
            var version = CurrentVersion;
            return version.Build >= 0 ? version.ToString(3) : version.ToString();
        }
    }

    public async Task<LauncherRelease> GetLatestAsync(
        string repository,
        CancellationToken cancellationToken = default)
    {
        var slug = NormalizeRepository(repository);
        try
        {
            return await GetLatestFromApiAsync(slug, cancellationToken);
        }
        catch (TaskCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            _log.Info(
                "Launcher GitHub Releases API timed out; " +
                "using the official Atom feed.");
        }
        catch (Exception error)
            when (error is HttpRequestException or JsonException)
        {
            _log.Info(
                "Launcher GitHub Releases API is unavailable; " +
                "using the official Atom feed: " + error.Message);
        }

        try
        {
            return await GetLatestFromFeedAsync(slug, cancellationToken);
        }
        catch (TaskCanceledException error)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw UpdateChannelUnavailable(error);
        }
        catch (Exception error)
            when (error is HttpRequestException or XmlException)
        {
            throw UpdateChannelUnavailable(error);
        }
    }

    private async Task<LauncherRelease> GetLatestFromApiAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{slug}/releases?per_page=50";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var candidates = new List<LauncherRelease>();

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) &&
                draft.GetBoolean())
            {
                continue;
            }

            if (!release.TryGetProperty("tag_name", out var tagNode) ||
                !TryParseWindowsReleaseTag(
                    tagNode.GetString(),
                    out var version,
                    out var legacy))
            {
                continue;
            }

            var tag = tagNode.GetString()!;
            var assetName = WindowsAssetName(version, legacy);
            if (!release.TryGetProperty("assets", out var assets))
            {
                continue;
            }

            JsonElement? matchingAsset = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var assetNameNode) &&
                    string.Equals(
                        assetNameNode.GetString(),
                        assetName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matchingAsset = asset;
                    break;
                }
            }

            if (!matchingAsset.HasValue ||
                !matchingAsset.Value.TryGetProperty(
                    "browser_download_url",
                    out var downloadNode) ||
                string.IsNullOrWhiteSpace(downloadNode.GetString()))
            {
                continue;
            }

            var name = release.TryGetProperty("name", out var nameNode) &&
                       !string.IsNullOrWhiteSpace(nameNode.GetString())
                ? nameNode.GetString()!
                : tag;
            var pageUrl = release.TryGetProperty(
                    "html_url",
                    out var pageNode)
                ? pageNode.GetString() ??
                  $"https://github.com/{slug}/releases/tag/{tag}"
                : $"https://github.com/{slug}/releases/tag/{tag}";
            var size = matchingAsset.Value.TryGetProperty(
                    "size",
                    out var sizeNode) &&
                       sizeNode.TryGetInt64(out var assetSize)
                ? assetSize
                : 0;
            candidates.Add(new LauncherRelease(
                tag,
                name,
                version,
                pageUrl,
                assetName,
                downloadNode.GetString()!,
                size));
        }

        return SelectLatest(candidates);
    }

    private async Task<LauncherRelease> GetLatestFromFeedAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        var url = $"https://github.com/{slug}/releases.atom";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(
            cancellationToken);
        var document = XDocument.Parse(content);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var candidates = new List<LauncherRelease>();

        foreach (var entry in document.Descendants(atom + "entry"))
        {
            var pageUrl = entry.Elements(atom + "link")
                .Select(link => link.Attribute("href")?.Value)
                .FirstOrDefault(value => value?.Contains(
                    "/releases/tag/",
                    StringComparison.OrdinalIgnoreCase) == true);
            if (string.IsNullOrWhiteSpace(pageUrl))
            {
                continue;
            }

            var tag = TagFromReleaseUrl(pageUrl);
            if (!TryParseWindowsReleaseTag(
                    tag,
                    out var version,
                    out var legacy))
            {
                continue;
            }

            var assetName = WindowsAssetName(version, legacy);
            var downloadUrl =
                $"https://github.com/{slug}/releases/download/" +
                $"{Uri.EscapeDataString(tag)}/" +
                Uri.EscapeDataString(assetName);
            candidates.Add(new LauncherRelease(
                tag,
                entry.Element(atom + "title")?.Value ?? tag,
                version,
                pageUrl,
                assetName,
                downloadUrl,
                0));
        }

        return SelectLatest(candidates);
    }

    private static LauncherRelease SelectLatest(
        IEnumerable<LauncherRelease> candidates) =>
        candidates.OrderByDescending(item => item.Version).FirstOrDefault()
        ?? throw new InvalidDataException(
            "没有找到带 win-v 标签和 Windows x64 更新包的 Launcher Release。");

    internal static bool TryParseWindowsReleaseTag(
        string? tag,
        out Version version,
        out bool legacy)
    {
        version = new Version(0, 0);
        legacy = false;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var match = WindowsTagPattern.Match(tag.Trim());
        if (!match.Success)
        {
            match = LegacyTagPattern.Match(tag.Trim());
            legacy = match.Success;
        }

        if (!match.Success ||
            !Version.TryParse(
                match.Groups["version"].Value,
                out var parsed))
        {
            legacy = false;
            return false;
        }

        if (legacy && parsed > LegacyTagMaximum)
        {
            legacy = false;
            return false;
        }

        version = parsed;
        return true;
    }

    internal static string WindowsAssetName(
        Version version,
        bool legacy)
    {
        var versionText = VersionText(version);
        return legacy
            ? $"DSHLauncher-Update-{versionText}-x64.exe"
            : $"DSHLauncher-Windows-Update-{versionText}-x64.exe";
    }

    private static string TagFromReleaseUrl(string pageUrl)
    {
        const string marker = "/releases/tag/";
        var index = pageUrl.LastIndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? string.Empty
            : Uri.UnescapeDataString(pageUrl[(index + marker.Length)..])
                .Trim('/');
    }

    private static string VersionText(Version version) =>
        version.Build >= 0 ? version.ToString(3) : version.ToString();

    private static InvalidOperationException UpdateChannelUnavailable(
        Exception error) =>
        new(
            "无法读取 Windows Launcher 更新通道。" +
            "请检查网络连接后重试。",
            error);

    public async Task<string> DownloadAsync(
        LauncherRelease release,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DSHLauncher",
            release.Tag.Replace(Path.DirectorySeparatorChar, '_'));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, release.AssetName);
        var temporary = destination + ".download";
        progress?.Report(new OperationProgress(
            "正在连接 GitHub 下载服务器…",
            Detail: release.AssetName));

        try
        {
            using var response = await _http.GetAsync(
                release.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if ((!total.HasValue || total.Value <= 0) &&
                release.AssetSize > 0)
            {
                total = release.AssetSize;
            }

            await using var source =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            var length = await WriteDownloadFileAsync(
                source,
                temporary,
                total,
                release.AssetName,
                progress,
                cancellationToken);
            if (release.AssetSize > 0 && length != release.AssetSize)
            {
                throw new InvalidDataException(
                    $"Downloaded updater size mismatch. " +
                    $"Expected {release.AssetSize}; got {length}.");
            }

            File.Move(temporary, destination, true);
            progress?.Report(new OperationProgress(
                "Launcher 更新包下载完成。",
                100,
                DownloadDetail(length, total, release.AssetName)));
            _log.Info(
                $"Downloaded Launcher update {release.Tag}: {destination}");
            return destination;
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    internal static async Task<long> WriteDownloadFileAsync(
        Stream source,
        string temporaryPath,
        long? total,
        string assetName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using (var target = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            await CopyDownloadAsync(
                source,
                target,
                total,
                assetName,
                progress,
                cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new FileInfo(temporaryPath).Length;
    }

    internal static async Task CopyDownloadAsync(
        Stream source,
        Stream target,
        long? total,
        string assetName,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[81920];
        long downloaded = 0;
        var lastReport = DateTimeOffset.MinValue;

        while (true)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);
            downloaded += read;
            var now = DateTimeOffset.UtcNow;
            if (now - lastReport < TimeSpan.FromMilliseconds(100) &&
                (!total.HasValue || downloaded < total.Value))
            {
                continue;
            }

            progress?.Report(new OperationProgress(
                "正在下载 Launcher 更新包…",
                CalculateDownloadPercentage(downloaded, total),
                DownloadDetail(
                    downloaded,
                    total,
                    assetName)));
            lastReport = now;
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double megabyte = 1024 * 1024;
        return bytes >= megabyte
            ? $"{bytes / megabyte:0.0} MB"
            : $"{bytes / 1024d:0.0} KB";
    }

    private static string DownloadDetail(
        long downloaded,
        long? total,
        string assetName) =>
        total.HasValue && total.Value > 0
            ? $"{FormatBytes(downloaded)} / " +
              $"{FormatBytes(total.Value)}  ·  {assetName}"
            : $"{FormatBytes(downloaded)}  ·  {assetName}";

    internal static int? CalculateDownloadPercentage(
        long downloaded,
        long? total)
    {
        if (!total.HasValue || total.Value <= 0)
        {
            return null;
        }

        return (int)Math.Clamp(
            downloaded * 100L / total.Value,
            0,
            100);
    }

    public static void CleanupDownloadedUpdates(AppLogger log)
    {
        var root = Path.Combine(Path.GetTempPath(), "DSHLauncher");
        TryCleanupDownloadedUpdates(root, log);
    }

    internal static bool TryCleanupDownloadedUpdates(
        string root,
        AppLogger log)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullTemp = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!fullRoot.StartsWith(
                    fullTemp,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullRoot).Equals(
                    "DSHLauncher",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to clean an updater directory outside " +
                    "the expected temporary path.");
            }

            if (!Directory.Exists(fullRoot))
            {
                return true;
            }

            Directory.Delete(fullRoot, true);
            log.Info($"Deleted downloaded Launcher updates: {fullRoot}");
            return true;
        }
        catch (Exception error)
            when (error is IOException or
                  UnauthorizedAccessException or
                  InvalidOperationException)
        {
            log.Warn(
                "Could not delete downloaded Launcher updates: " +
                error.Message);
            return false;
        }
    }

    private static string NormalizeRepository(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = uri.AbsolutePath.Trim('/');
        }

        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            throw new ArgumentException(
                "GitHub repository must use owner/repository or https://github.com/owner/repository.");
        }

        return Uri.EscapeDataString(segments[0]) + "/" + Uri.EscapeDataString(segments[1]);
    }

    public void Dispose() => _http.Dispose();
}
