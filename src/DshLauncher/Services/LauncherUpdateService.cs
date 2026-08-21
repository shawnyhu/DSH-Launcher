using System.Net.Http.Headers;
using System.Reflection;
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
    private readonly HttpClient _http;
    private readonly AppLogger _log;

    public LauncherUpdateService(AppLogger log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("DSHLauncher", CurrentVersion.ToString()));
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
        var latestUrl = $"https://github.com/{slug}/releases/latest";
        using var response = await _http.GetAsync(
            latestUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var pageUri = response.RequestMessage?.RequestUri ?? new Uri(latestUrl);
        const string marker = "/releases/tag/";
        var markerIndex = pageUri.AbsolutePath.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            throw new InvalidDataException(
                "GitHub did not redirect the latest release link to a version tag.");
        }

        var encodedTag = pageUri.AbsolutePath[(markerIndex + marker.Length)..].Trim('/');
        var tag = Uri.UnescapeDataString(encodedTag);
        var version = ParseVersion(tag);
        var versionText = version.Build >= 0 ? version.ToString(3) : version.ToString();
        var assetName = $"DSHLauncher-Update-{versionText}-x64.exe";
        var downloadUrl =
            $"https://github.com/{slug}/releases/download/" +
            $"{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";

        return new LauncherRelease(
            tag,
            tag,
            version,
            pageUri.ToString(),
            assetName,
            downloadUrl,
            0);
    }

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
            await using var target = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await CopyDownloadAsync(
                source,
                target,
                total,
                release.AssetName,
                progress,
                cancellationToken);
            await target.FlushAsync(cancellationToken);

            var length = new FileInfo(temporary).Length;
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
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
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

    private static Version ParseVersion(string value)
    {
        var numeric = value.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        return Version.TryParse(numeric, out var version)
            ? version
            : throw new FormatException($"GitHub release tag is not a supported version: {value}");
    }

    public void Dispose() => _http.Dispose();
}
