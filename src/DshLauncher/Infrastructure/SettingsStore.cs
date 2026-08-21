using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Infrastructure;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.SettingsFile))
        {
            return CreateDefaults();
        }

        await using var stream = File.OpenRead(AppPaths.SettingsFile);
        var value = await JsonSerializer.DeserializeAsync<LauncherSettings>(stream, JsonOptions, cancellationToken);
        return Normalize(value ?? CreateDefaults());
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            AppPaths.EnsureCreated();
            var temporary = AppPaths.SettingsFile + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, AppPaths.SettingsFile, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static LauncherSettings CreateDefaults()
    {
        var home = new DshHomeEntry
        {
            Name = "默认数据",
            Path = AppPaths.DefaultDshHome
        };

        return new LauncherSettings
        {
            Homes = [home],
            SelectedHomeId = home.Id
        };
    }

    private static LauncherSettings Normalize(LauncherSettings settings)
    {
        if (settings.Homes.Count == 0)
        {
            var home = new DshHomeEntry { Name = "默认数据", Path = AppPaths.DefaultDshHome };
            settings.Homes.Add(home);
            settings.SelectedHomeId = home.Id;
        }

        if (settings.SelectedHome is null)
        {
            settings.SelectedHomeId = settings.Homes[0].Id;
        }

        if (settings.SelectedInstallation is null && settings.Installations.Count > 0)
        {
            settings.SelectedInstallationId = settings.Installations[0].Id;
        }

        settings.Port = Math.Clamp(settings.Port, 1, 65535);
        if (string.IsNullOrWhiteSpace(settings.WorkingDirectory))
        {
            settings.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return settings;
    }
}
