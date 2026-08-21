using System.Runtime.InteropServices;
using DshLauncher.Infrastructure;
using DshLauncher.Models;
using DshLauncher.Services;
using DshLauncher.UI;

namespace DshLauncher.Diagnostics;

internal static class SelfCheck
{
    public static async Task<int> RunAsync()
    {
        AttachConsole(unchecked((uint)-1));
        Console.WriteLine();
        Console.WriteLine("DSH Launcher self-check");
        var failures = new List<string>();
        try
        {
            AppPaths.EnsureCreated();
            Console.WriteLine("[OK] Data directory: " + AppPaths.DataRoot);
        }
        catch (Exception error)
        {
            failures.Add("Data directory: " + error.Message);
        }

        try
        {
            using var icons = new WhaleIconFactory();
            foreach (var state in Enum.GetValues<DshActivityState>())
            {
                _ = icons.Get(state);
            }
            _ = icons.Get(DshActivityState.Attention, true);
            Console.WriteLine("[OK] Official whale SVG parsed and all tray icons rendered.");
        }
        catch (Exception error)
        {
            failures.Add("Whale icon: " + error.Message);
        }

        using var log = new AppLogger();
        if (DshReleaseService.PackageVersionFromTag(
                "dsh-v0.1.1-rc.1") == "0.1.1-rc.1")
        {
            Console.WriteLine(
                "[OK] DSH GitHub release tags map to npm package versions.");
        }
        else
        {
            failures.Add("DSH release tag parsing failed.");
        }

        var canonicalWindowsTag =
            LauncherUpdateService.TryParseWindowsReleaseTag(
                "win-v0.1.9",
                out var canonicalWindowsVersion,
                out var canonicalIsLegacy);
        var bridgeWindowsTag =
            LauncherUpdateService.TryParseWindowsReleaseTag(
                "v0.1.8",
                out var bridgeWindowsVersion,
                out var bridgeIsLegacy);
        if (canonicalWindowsTag &&
            canonicalWindowsVersion == new Version(0, 1, 9) &&
            !canonicalIsLegacy &&
            bridgeWindowsTag &&
            bridgeWindowsVersion == new Version(0, 1, 8) &&
            bridgeIsLegacy &&
            !LauncherUpdateService.TryParseWindowsReleaseTag(
                "mac-v9.0.0",
                out _,
                out _) &&
            !LauncherUpdateService.TryParseWindowsReleaseTag(
                "v0.1.9",
                out _,
                out _) &&
            LauncherUpdateService.WindowsAssetName(
                canonicalWindowsVersion,
                false) ==
            "DSHLauncher-Windows-Update-0.1.9-x64.exe" &&
            LauncherUpdateService.WindowsAssetName(
                bridgeWindowsVersion,
                true) ==
            "DSHLauncher-Update-0.1.8-x64.exe")
        {
            Console.WriteLine(
                "[OK] Launcher updates enforce Windows tags, versions, " +
                "architecture, and the v0.1.8 bridge.");
        }
        else
        {
            failures.Add("Launcher platform release filtering failed.");
        }

        var sampleVersions = new[]
        {
            "0.1.0-rc.7",
            "0.1.0-rc.8",
            "0.1.1-rc.1"
        };
        var sampleDates = new Dictionary<string, DateTimeOffset>
        {
            ["0.1.0-rc.7"] = DateTimeOffset.Parse("2026-08-17T11:50:59Z"),
            ["0.1.0-rc.8"] = DateTimeOffset.Parse("2026-08-19T15:41:29Z"),
            ["0.1.1-rc.1"] = DateTimeOffset.Parse("2026-08-21T06:49:18Z")
        };
        var sampleReleases = new[]
        {
            new DshReleaseVersion(
                "dsh-v0.1.1-rc.1",
                "0.1.1-rc.1",
                DateTimeOffset.Parse("2026-08-21T07:12:39Z")),
            new DshReleaseVersion(
                "dsh-v0.1.0-rc.8",
                "0.1.0-rc.8",
                DateTimeOffset.Parse("2026-08-19T15:37:57Z")),
            new DshReleaseVersion(
                "dsh-v0.1.0-rc.7",
                "0.1.0-rc.7",
                DateTimeOffset.Parse("2026-08-17T12:01:58Z"))
        };
        var sampleCatalog = NpmService.BuildVersionList(
            sampleVersions,
            sampleDates,
            sampleReleases,
            "0.1.0-rc.7");
        if (sampleCatalog.Select(item => item.Version).SequenceEqual(
                new[]
                {
                    "0.1.1-rc.1",
                    "0.1.0-rc.8",
                    "0.1.0-rc.7"
                }) &&
            sampleCatalog[0].IsLatest)
        {
            Console.WriteLine(
                "[OK] DSH releases are ordered by official publish time.");
        }
        else
        {
            failures.Add("DSH release publish-time ordering failed.");
        }

        var cleanupParent = Path.Combine(
            Path.GetTempPath(),
            "DSHLauncher-SelfCheck-" + Guid.NewGuid().ToString("N"));
        var cleanupRoot = Path.Combine(cleanupParent, "DSHLauncher");
        try
        {
            var fakeVersion = Path.Combine(cleanupRoot, "v0.1.5");
            Directory.CreateDirectory(fakeVersion);
            File.WriteAllText(
                Path.Combine(fakeVersion, "update.exe"),
                "test");
            if (!LauncherUpdateService.TryCleanupDownloadedUpdates(
                    cleanupRoot,
                    log) ||
                Directory.Exists(cleanupRoot))
            {
                failures.Add("Downloaded updater cleanup failed.");
            }
            else
            {
                Console.WriteLine(
                    "[OK] Downloaded updater cleanup is confined " +
                    "to the temp directory.");
            }
        }
        finally
        {
            if (Directory.Exists(cleanupParent))
            {
                Directory.Delete(cleanupParent, true);
            }
        }

        var npm = new NpmService(new CommandRunner(), log);
        Console.WriteLine(npm.FindNode() is { } node
            ? "[OK] Node: " + node
            : "[FAIL] Node was not found.");
        Console.WriteLine(npm.FindNpm() is { } npmPath
            ? "[OK] npm: " + npmPath
            : "[FAIL] npm was not found.");
        try
        {
            var global = await npm.DiscoverGlobalAsync();
            if (global is null)
            {
                Console.WriteLine("[INFO] Global DSH is not installed; package discovery was skipped.");
            }
            else
            {
                Console.WriteLine($"[OK] Global DSH {global.InstalledVersion}: {global.PackageRoot}");
                var entry = NpmService.GetEntryScript(global);
                if (!File.Exists(entry)) failures.Add("DSH CLI entry is missing: " + entry);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine("[INFO] Global DSH discovery is unavailable: " + error.Message);
        }

        try
        {
            var settingsStore = new SettingsStore();
            var settings = await settingsStore.LoadAsync();
            await using var runtime = new DshRuntimeService(log);
            using var form = new ConfigurationForm(
                settings,
                settingsStore,
                npm,
                runtime)
            {
                Size = new Size(680, 650)
            };
            form.CreateControl();
            Console.WriteLine("[OK] Configuration window constructed at minimum width.");
        }
        catch (Exception error)
        {
            failures.Add("Configuration window: " + error.Message);
        }

        try
        {
            using var progressForm =
                new UpdateProgressForm("更新进度自检");
            progressForm.CreateControl();
            progressForm.Report(new OperationProgress(
                "正在安装 DSH…",
                Detail: "动态进度"));
            progressForm.Report(new OperationProgress(
                "正在下载 Launcher 更新包…",
                50,
                "5.0 MB / 10.0 MB"));
            var recorder = new OperationProgressRecorder();
            var payload = new byte[256 * 1024];
            await using var source = new MemoryStream(payload);
            await using var target = new MemoryStream();
            await LauncherUpdateService.CopyDownloadAsync(
                source,
                target,
                payload.Length,
                "test-update.exe",
                recorder);
            var finalProgress = recorder.Values.LastOrDefault();
            if (LauncherUpdateService.CalculateDownloadPercentage(
                    5,
                    10) != 50 ||
                LauncherUpdateService.CalculateDownloadPercentage(
                    5,
                    null) is not null ||
                target.Length != payload.Length ||
                finalProgress?.Percentage != 100)
            {
                failures.Add("Update progress calculation failed.");
            }
            else
            {
                Console.WriteLine(
                    "[OK] Update progress window supports marquee, " +
                    "percentage, and streamed byte progress.");
            }
        }
        catch (Exception error)
        {
            failures.Add("Update progress window: " + error.Message);
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("SELF-CHECK PASSED");
            return 0;
        }

        foreach (var failure in failures) Console.WriteLine("[FAIL] " + failure);
        Console.WriteLine("SELF-CHECK FAILED");
        return 1;
    }

    private sealed class OperationProgressRecorder :
        IProgress<OperationProgress>
    {
        public List<OperationProgress> Values { get; } = [];

        public void Report(OperationProgress value) =>
            Values.Add(value);
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);
}
