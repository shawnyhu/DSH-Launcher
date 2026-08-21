using DshLauncher.Infrastructure;
using DshLauncher.Models;

namespace DshLauncher.Services;

internal sealed class DshHomeWatcher : IDisposable
{
    private readonly SettingsStore _store;
    private readonly LauncherSettings _settings;
    private readonly AppLogger _log;
    private FileSystemWatcher? _watcher;
    private DshHomeEntry? _home;
    private string? _version;
    private System.Threading.Timer? _debounce;

    public DshHomeWatcher(SettingsStore store, LauncherSettings settings, AppLogger log)
    {
        _store = store;
        _settings = settings;
        _log = log;
    }

    public void Start(DshHomeEntry home, string version)
    {
        Stop();
        Directory.CreateDirectory(home.Path);
        _home = home;
        _version = version;
        _watcher = new FileSystemWatcher(home.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += (_, e) =>
        {
            home.ObservationReliable = false;
            _log.Warn("DSH_HOME watcher error: " + e.GetException().Message);
        };
    }

    public void Stop()
    {
        _debounce?.Dispose();
        _debounce = null;
        _watcher?.Dispose();
        _watcher = null;
        _home = null;
        _version = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (string.Equals(Path.GetFileName(e.FullPath), ".credentials.yaml",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(
            async _ => await RecordAsync(),
            null,
            TimeSpan.FromSeconds(2),
            Timeout.InfiniteTimeSpan);
    }

    private void OnRenamed(object sender, RenamedEventArgs e) => OnChanged(sender, e);

    private async Task RecordAsync()
    {
        var home = _home;
        var version = _version;
        if (home is null || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        home.LastObservedWriterVersion = version;
        home.LastObservedWriteAt = DateTimeOffset.Now;
        home.ObservationReliable = true;
        try
        {
            await _store.SaveAsync(_settings);
        }
        catch (Exception error)
        {
            _log.Error("Failed to persist DSH_HOME writer metadata.", error);
        }
    }

    public void Dispose() => Stop();
}
