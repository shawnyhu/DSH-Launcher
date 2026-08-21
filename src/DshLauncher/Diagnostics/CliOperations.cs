using DshLauncher.Infrastructure;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher.Diagnostics;

internal static class CliOperations
{
    public static bool IsInstallCommand(string[] args) =>
        args.Length >= 3 &&
        args[0].Equals("--install-dsh", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> InstallDshAsync(string[] args)
    {
        AppPaths.EnsureCreated();
        using var log = new AppLogger();
        try
        {
            var standardNode = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs");
            if (Directory.Exists(standardNode))
                Environment.SetEnvironmentVariable(
                    "PATH", standardNode + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            var scope = args[1].Equals("global", StringComparison.OrdinalIgnoreCase)
                ? DshInstallScope.Global
                : DshInstallScope.Managed;
            string root;
            string version;
            if (scope == DshInstallScope.Global)
            {
                root = string.Empty;
                version = args[2];
            }
            else
            {
                if (args.Length < 4)
                {
                    throw new ArgumentException("Managed install requires a path and a version.");
                }

                root = args[2];
                version = args[3];
            }

            var homeOptionIndex = Array.FindIndex(
                args,
                item => item.Equals("--dsh-home", StringComparison.OrdinalIgnoreCase));
            if (homeOptionIndex >= 0 && homeOptionIndex + 1 >= args.Length)
            {
                throw new ArgumentException("--dsh-home requires a path.");
            }
            var requestedHome = homeOptionIndex >= 0 ? Path.GetFullPath(args[homeOptionIndex + 1]) : null;

            var store = new SettingsStore();
            var settings = await store.LoadAsync();
            var npm = new NpmService(new CommandRunner(), log);
            var installed = await npm.InstallAsync(scope, root, version);

            var existing = scope == DshInstallScope.Global
                ? settings.Installations.FirstOrDefault(item => item.Scope == DshInstallScope.Global)
                : settings.Installations.FirstOrDefault(item =>
                    string.Equals(
                        Path.GetFullPath(item.InstallRoot),
                        Path.GetFullPath(installed.InstallRoot),
                        StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                installed.Id = existing.Id;
                var index = settings.Installations.IndexOf(existing);
                settings.Installations[index] = installed;
            }
            else
            {
                settings.Installations.Add(installed);
            }

            settings.SelectedInstallationId = installed.Id;
            if (requestedHome is not null)
            {
                Directory.CreateDirectory(requestedHome);
                var home = settings.Homes.FirstOrDefault(item =>
                    string.Equals(
                        Path.GetFullPath(item.Path),
                        requestedHome,
                        StringComparison.OrdinalIgnoreCase));
                if (home is null)
                {
                    var directoryName = new DirectoryInfo(requestedHome).Name;
                    home = new DshHomeEntry
                    {
                        Name = string.IsNullOrWhiteSpace(directoryName) ? "DSH_HOME" : directoryName,
                        Path = requestedHome
                    };
                    settings.Homes.Add(home);
                }

                settings.SelectedHomeId = home.Id;
            }
            await store.SaveAsync(settings);
            log.Info($"CLI installation completed: {installed}");
            return 0;
        }
        catch (Exception error)
        {
            log.Error("CLI installation failed.", error);
            return 1;
        }
    }
}
