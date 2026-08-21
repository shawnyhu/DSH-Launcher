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
                failures.Add("Global DSH was not found.");
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
            failures.Add("Global DSH discovery: " + error.Message);
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

        if (failures.Count == 0)
        {
            Console.WriteLine("SELF-CHECK PASSED");
            return 0;
        }

        foreach (var failure in failures) Console.WriteLine("[FAIL] " + failure);
        Console.WriteLine("SELF-CHECK FAILED");
        return 1;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);
}
