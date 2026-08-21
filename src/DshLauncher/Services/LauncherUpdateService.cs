using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
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
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public async Task<LauncherRelease> GetLatestAsync(
        string repository,
        CancellationToken cancellationToken = default)
    {
        var slug = NormalizeRepository(repository);
        using var response = await _http.GetAsync(
            $"https://api.github.com/repos/{slug}/releases/latest",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var tag = RequiredString(root, "tag_name");
        var name = GetString(root, "name") ?? tag;
        var pageUrl = RequiredString(root, "html_url");
        var version = ParseVersion(tag);
        if (!root.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The GitHub release does not contain assets.");
        }

        JsonElement? match = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var assetName = GetString(asset, "name");
            if (assetName is not null &&
                assetName.StartsWith("DSHLauncher-Update-", StringComparison.OrdinalIgnoreCase) &&
                assetName.EndsWith("-x64.exe", StringComparison.OrdinalIgnoreCase))
            {
                match = asset;
                break;
            }
        }

        if (match is null)
        {
            throw new InvalidOperationException(
                "The latest release does not contain a DSHLauncher-Update-*-x64.exe asset.");
        }

        var selected = match.Value;
        return new LauncherRelease(
            tag,
            name,
            version,
            pageUrl,
            RequiredString(selected, "name"),
            RequiredString(selected, "browser_download_url"),
            selected.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes)
                ? bytes
                : 0);
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

    private static string RequiredString(JsonElement element, string name) =>
        GetString(element, name) ??
        throw new InvalidDataException($"GitHub release response is missing {name}.");

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose() => _http.Dispose();
}
