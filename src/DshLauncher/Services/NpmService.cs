using System.Text.Json;
using DshLauncher.Infrastructure;
using DshLauncher.Models;

namespace DshLauncher.Services;

internal sealed record NpmVersionInfo(string Version, DateTimeOffset? PublishedAt, bool IsLatest)
{
    public override string ToString()
    {
        var suffix = IsLatest ? "  · latest" : string.Empty;
        return PublishedAt.HasValue
            ? $"{Version}{suffix}  · {PublishedAt:yyyy-MM-dd}"
            : $"{Version}{suffix}";
    }
}

internal sealed record NpmUpdateInfo(
    string LatestVersion,
    DateTimeOffset? LatestPublishedAt,
    bool IsUpdateAvailable);

internal sealed class NpmService
{
    private const string PackageName = "@deepseek-ai/dsh";
    private const string OwnershipFile = ".dsh-launcher-instance.json";
    private readonly CommandRunner _commands;
    private readonly AppLogger _log;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public NpmService(CommandRunner commands, AppLogger log)
    {
        _commands = commands;
        _log = log;
    }

    public string? FindNode() =>
        CommandRunner.FindOnPath("node.exe") ??
        CommandRunner.FindOnPath("node") ??
        Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));

    public string? FindNpm() =>
        CommandRunner.FindOnPath("npm.cmd") ??
        CommandRunner.FindOnPath("npm.exe") ??
        CommandRunner.FindOnPath("npm") ??
        Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd"));

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    public async Task<DshInstallation?> DiscoverGlobalAsync(CancellationToken cancellationToken = default)
    {
        var npm = FindNpm();
        var node = FindNode();
        if (npm is null || node is null)
        {
            return null;
        }

        var rootResult = await _commands.RunAsync(npm, ["root", "--global"], timeout: TimeSpan.FromSeconds(20), cancellationToken: cancellationToken);
        if (!rootResult.Success)
        {
            _log.Warn("无法读取 npm 全局目录：" + rootResult.CombinedOutput);
            return null;
        }

        var packageRoot = Path.Combine(rootResult.StandardOutput.Trim(), "@deepseek-ai", "dsh");
        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        var prefixResult = await _commands.RunAsync(npm, ["prefix", "--global"], timeout: TimeSpan.FromSeconds(20), cancellationToken: cancellationToken);
        var version = await ReadInstalledVersionAsync(packageRoot, cancellationToken);
        return new DshInstallation
        {
            Name = "全局 DSH",
            Scope = DshInstallScope.Global,
            InstallRoot = prefixResult.Success ? prefixResult.StandardOutput.Trim() : rootResult.StandardOutput.Trim(),
            PackageRoot = packageRoot,
            NodeExecutable = node,
            NpmExecutable = npm,
            InstalledVersion = version ?? string.Empty,
            LastVerifiedAt = DateTimeOffset.Now
        };
    }

    public async Task<IReadOnlyList<NpmVersionInfo>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        var npm = FindNpm() ?? throw new InvalidOperationException("未检测到 npm。请先安装 Node.js 24 LTS。");
        var versionsTask = _commands.RunAsync(npm, ["view", PackageName, "versions", "--json"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
        var tagsTask = _commands.RunAsync(npm, ["view", PackageName, "dist-tags", "--json"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
        var timeTask = _commands.RunAsync(npm, ["view", PackageName, "time", "--json"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
        await Task.WhenAll(versionsTask, tagsTask, timeTask);

        var versionsResult = await versionsTask;
        if (!versionsResult.Success)
        {
            throw new InvalidOperationException("读取 DSH 版本列表失败：" + versionsResult.CombinedOutput);
        }

        using var versionsDocument = JsonDocument.Parse(versionsResult.StandardOutput);
        var versions = versionsDocument.RootElement.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList();
        string? latest = null;
        var tagsResult = await tagsTask;
        if (tagsResult.Success)
        {
            using var tagsDocument = JsonDocument.Parse(tagsResult.StandardOutput);
            if (tagsDocument.RootElement.TryGetProperty("latest", out var latestNode))
            {
                latest = latestNode.GetString();
            }
        }

        var published = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var timeResult = await timeTask;
        if (timeResult.Success)
        {
            using var timeDocument = JsonDocument.Parse(timeResult.StandardOutput);
            foreach (var property in timeDocument.RootElement.EnumerateObject())
            {
                if (DateTimeOffset.TryParse(property.Value.GetString(), out var value))
                {
                    published[property.Name] = value;
                }
            }
        }

        var available = versions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var releases = new DshReleaseService(_log);
        var official = await releases.GetPublishedAsync(
            available,
            cancellationToken);
        return BuildVersionList(
            versions,
            published,
            official,
            latest);
    }

    internal static IReadOnlyList<NpmVersionInfo> BuildVersionList(
        IReadOnlyList<string> versions,
        IReadOnlyDictionary<string, DateTimeOffset> npmPublished,
        IReadOnlyList<DshReleaseVersion> official,
        string? distTagLatest)
    {
        var latest = official.FirstOrDefault()?.Version ?? versions
            .Where(npmPublished.ContainsKey)
            .OrderByDescending(version => npmPublished[version])
            .FirstOrDefault() ?? distTagLatest;
        var officialPublished = official
            .GroupBy(
                release => release.Version,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(release => release.PublishedAt),
                StringComparer.OrdinalIgnoreCase);

        return versions
            .Select(version =>
            {
                var publishedAt = officialPublished.TryGetValue(
                    version,
                    out var releaseDate)
                    ? releaseDate
                    : npmPublished.TryGetValue(version, out var npmDate)
                        ? npmDate
                        : (DateTimeOffset?)null;
                return new NpmVersionInfo(
                    version,
                    publishedAt,
                    string.Equals(
                        version,
                        latest,
                        StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(item =>
                item.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(
                item => item.Version,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<NpmUpdateInfo> CheckForUpdateAsync(
        string installedVersion,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetVersionsAsync(cancellationToken);
        var latest = versions.FirstOrDefault(item => item.IsLatest)
            ?? throw new InvalidOperationException(
                "没有找到可安装的 DSH Release。");
        var installed = versions.FirstOrDefault(item =>
            string.Equals(
                item.Version,
                installedVersion,
                StringComparison.OrdinalIgnoreCase));
        var sameVersion = string.Equals(
            latest.Version,
            installedVersion,
            StringComparison.OrdinalIgnoreCase);
        var isUpdateAvailable = !sameVersion &&
            (installed?.PublishedAt is null ||
             latest.PublishedAt is null ||
             latest.PublishedAt > installed.PublishedAt);

        return new NpmUpdateInfo(
            latest.Version,
            latest.PublishedAt,
            isUpdateAvailable);
    }

    public async Task<DshInstallation> InstallAsync(
        DshInstallScope scope,
        string installRoot,
        string version,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var npm = FindNpm() ?? throw new InvalidOperationException("未检测到 npm。请先安装 Node.js 24 LTS。");
            var node = FindNode() ?? throw new InvalidOperationException("未检测到 node.exe。请先安装 Node.js 24 LTS。");
            var spec = PackageName + "@" + version;
            CommandResult result;
            string packageRoot;

            if (scope == DshInstallScope.Global)
            {
                progress?.Report("正在安装全局 DSH " + version + "…");
                result = await _commands.RunAsync(npm,
                    ["install", "--global", spec, "--no-audit", "--no-fund"],
                    timeout: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken);
                if (!result.Success)
                {
                    throw new InvalidOperationException("全局安装失败：" + result.CombinedOutput);
                }

                var discovered = await DiscoverGlobalAsync(cancellationToken);
                return discovered ?? throw new InvalidOperationException("安装完成，但未能定位全局 DSH。");
            }

            installRoot = Path.GetFullPath(installRoot);
            ValidateManagedRoot(installRoot);
            Directory.CreateDirectory(installRoot);
            await WriteOwnershipMarkerAsync(installRoot, cancellationToken);
            await EnsurePackageJsonAsync(installRoot, cancellationToken);
            progress?.Report("正在安装 DSH " + version + " 到 " + installRoot + "…");
            result = await _commands.RunAsync(npm,
                ["install", "--prefix", installRoot, "--save-exact", spec, "--no-audit", "--no-fund"],
                timeout: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException("独立安装失败：" + result.CombinedOutput);
            }

            packageRoot = Path.Combine(installRoot, "node_modules", "@deepseek-ai", "dsh");
            var installedVersion = await ReadInstalledVersionAsync(packageRoot, cancellationToken)
                ?? throw new InvalidOperationException("安装完成，但无法读取 DSH 版本。");
            return new DshInstallation
            {
                Name = "DSH " + installedVersion,
                Scope = DshInstallScope.Managed,
                InstallRoot = installRoot,
                PackageRoot = packageRoot,
                NodeExecutable = node,
                NpmExecutable = npm,
                InstalledVersion = installedVersion,
                LastVerifiedAt = DateTimeOffset.Now
            };
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<DshInstallation> UpdateAsync(
        DshInstallation installation,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var update = await CheckForUpdateAsync(
            installation.InstalledVersion,
            cancellationToken);
        if (!update.IsUpdateAvailable)
        {
            return installation;
        }

        return await UpdateToAsync(
            installation,
            update.LatestVersion,
            progress,
            cancellationToken);
    }

    public async Task<DshInstallation> UpdateToAsync(
        DshInstallation installation,
        string targetVersion,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(
                targetVersion,
                installation.InstalledVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            return installation;
        }

        return await InstallAsync(
            installation.Scope,
            installation.InstallRoot,
            targetVersion,
            progress,
            cancellationToken);
    }

    public async Task UninstallAsync(DshInstallation installation, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var npm = File.Exists(installation.NpmExecutable) ? installation.NpmExecutable : FindNpm();
            if (npm is null)
            {
                throw new InvalidOperationException("未找到安装该实例所用的 npm。");
            }

            var arguments = installation.Scope == DshInstallScope.Global
                ? new[] { "uninstall", "--global", PackageName, "--no-audit", "--no-fund" }
                : new[] { "uninstall", "--prefix", installation.InstallRoot, PackageName, "--no-audit", "--no-fund" };
            var result = await _commands.RunAsync(npm, arguments, timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException("卸载失败：" + result.CombinedOutput);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public static string GetEntryScript(DshInstallation installation) =>
        Path.Combine(installation.PackageRoot, "lib", "bin.js");

    private static async Task<string?> ReadInstalledVersionAsync(string packageRoot, CancellationToken cancellationToken)
    {
        var manifest = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        await using var stream = File.OpenRead(manifest);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("version", out var value) ? value.GetString() : null;
    }

    private static void ValidateManagedRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var entries = Directory.EnumerateFileSystemEntries(root).ToList();
        if (entries.Count == 0 || File.Exists(Path.Combine(root, OwnershipFile)))
        {
            return;
        }

        throw new InvalidOperationException("所选目录非空，且不属于 DSH Launcher。请选择空目录，避免覆盖已有文件。");
    }

    private static async Task WriteOwnershipMarkerAsync(string root, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.Serialize(new
        {
            owner = "DSH Launcher",
            createdAt = DateTimeOffset.Now
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(root, OwnershipFile), content, cancellationToken);
    }

    private static async Task EnsurePackageJsonAsync(string root, CancellationToken cancellationToken)
    {
        var manifest = Path.Combine(root, "package.json");
        if (File.Exists(manifest))
        {
            return;
        }

        await File.WriteAllTextAsync(manifest,
            "{\n  \"name\": \"dsh-launcher-managed-runtime\",\n  \"private\": true\n}\n",
            cancellationToken);
    }
}
