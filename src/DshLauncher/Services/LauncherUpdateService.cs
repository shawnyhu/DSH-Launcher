using System.Net.Http.Headers;
using System.Reflection;
using DshLauncher.Infrastructure;

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
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DSHLauncher",
            release.Tag.Replace(Path.DirectorySeparatorChar, '_'));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, release.AssetName);
        var temporary = destination + ".download";
        using var response = await _http.GetAsync(
            release.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        var length = new FileInfo(temporary).Length;
        if (release.AssetSize > 0 && length != release.AssetSize)
        {
            File.Delete(temporary);
            throw new InvalidDataException(
                $"Downloaded updater size mismatch. Expected {release.AssetSize}; got {length}.");
        }

        File.Move(temporary, destination, true);
        _log.Info($"Downloaded Launcher update {release.Tag}: {destination}");
        return destination;
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
