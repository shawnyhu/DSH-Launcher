using DshLauncher.Diagnostics;
using System.Threading;
using DshLauncher.Infrastructure;
using DshLauncher.Models;
using DshLauncher.Services;
using DshLauncher.UI;

namespace DshLauncher;

internal static class Program
{
    private const string MutexName = @"Local\DSHLauncher.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        AppPaths.EnsureCreated();
        if (args.Contains("--set-autostart", StringComparer.OrdinalIgnoreCase))
        {
            var enabled = !args.Contains("off", StringComparer.OrdinalIgnoreCase);
            AutoStartService.SetEnabled(enabled);
            return 0;
        }

        if (CliOperations.IsInstallCommand(args))
        {
            return CliOperations.InstallDshAsync(args).GetAwaiter().GetResult();
        }

        var probeIndex = Array.FindIndex(args, value =>
            value.Equals("--probe-events", StringComparison.OrdinalIgnoreCase));
        if (probeIndex >= 0)
        {
            var port = probeIndex + 1 < args.Length && int.TryParse(args[probeIndex + 1], out var parsed)
                ? parsed
                : 3080;
            return EventProbe.RunAsync(port).GetAwaiter().GetResult();
        }
        if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
        {
            return SelfCheck.RunAsync().GetAwaiter().GetResult();
        }

        var exportIndex = Array.FindIndex(args, value =>
            value.Equals("--export-icon", StringComparison.OrdinalIgnoreCase));
        if (exportIndex >= 0 && exportIndex + 1 < args.Length)
        {
            using var icons = new WhaleIconFactory();
            icons.SaveIco(args[exportIndex + 1]);
            return 0;
        }


        using var mutex = new Mutex(true, MutexName, out var firstInstance);
        var store = new SettingsStore();
        if (!firstInstance)
        {
            try
            {
                var existing = store.LoadAsync().GetAwaiter().GetResult();
                DshRuntimeService.OpenWeb(existing.Port);
            }
            catch
            {
                DshRuntimeService.OpenWeb(3080);
            }

            return 0;
        }

        using var log = new AppLogger();
        try
        {
            var settings = store.LoadAsync().GetAwaiter().GetResult();
            var commands = new CommandRunner();
            var npm = new NpmService(commands, log);
            ReconcileGlobalInstallationAsync(settings, store, npm).GetAwaiter().GetResult();
            Application.Run(new TrayApplicationContext(
                settings,
                store,
                log,
                npm,
                args.Contains("--configure", StringComparer.OrdinalIgnoreCase)));
        }
        catch (Exception error)
        {
            log.Error("DSH Launcher terminated unexpectedly.", error);
            MessageBox.Show(
                error.ToString(),
                "DSH Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        return 0;
    }


    private static async Task ReconcileGlobalInstallationAsync(
        LauncherSettings settings,
        SettingsStore store,
        NpmService npm)
    {
        var discovered = await npm.DiscoverGlobalAsync();
        if (discovered is null)
        {
            return;
        }

        var existing = settings.Installations.FirstOrDefault(item =>
            item.Scope == DshInstallScope.Global);
        if (existing is not null)
        {
            discovered.Id = existing.Id;
            var index = settings.Installations.IndexOf(existing);
            settings.Installations[index] = discovered;
        }
        else
        {
            settings.Installations.Add(discovered);
        }

        settings.SelectedInstallationId ??= discovered.Id;
        await store.SaveAsync(settings);
    }
}
