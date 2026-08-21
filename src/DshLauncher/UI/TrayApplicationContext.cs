using System.Diagnostics;
using DshLauncher.Infrastructure;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly LauncherSettings _settings;
    private readonly SettingsStore _store;
    private readonly AppLogger _log;
    private readonly NpmService _npm;
    private readonly DshRuntimeService _runtime;
    private readonly DshEventMonitor _events;
    private readonly DshHomeWatcher _homeWatcher;
    private readonly WhaleIconFactory _icons = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _restart;
    private readonly ToolStripMenuItem _currentInstallation;
    private readonly ToolStripMenuItem _currentHome;
    private readonly ToolStripMenuItem _launcherVersion;
    private readonly ToolStripMenuItem _update;
    private readonly ToolStripMenuItem _updateLauncher;
    private readonly ToolStripMenuItem _autoStart;
    private readonly System.Windows.Forms.Timer _flash = new() { Interval = 500 };
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly SynchronizationContext _ui;
    private DshStatusSnapshot _status = DshStatusSnapshot.Stopped;
    private bool _alternateAttention;
    private bool _exiting;
    private readonly bool _openConfigurationOnStart;

    public TrayApplicationContext(
        LauncherSettings settings,
        SettingsStore store,
        AppLogger log,
        NpmService npm,
        bool openConfigurationOnStart = false)
    {
        _settings = settings;
        _store = store;
        _log = log;
        _npm = npm;
        _openConfigurationOnStart = openConfigurationOnStart;
        _runtime = new DshRuntimeService(log);
        _events = new DshEventMonitor(log);
        _homeWatcher = new DshHomeWatcher(store, settings, log);
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _open = Item("\u6253\u5F00 DSH \u7F51\u9875", async (_, _) => await OpenAsync());
        _start = Item("\u542F\u52A8 DSH", async (_, _) => await StartAsync(true));
        _stop = Item("\u505C\u6B62 DSH", async (_, _) => await StopAsync());
        _restart = Item("\u91CD\u542F DSH", async (_, _) => await RestartAsync());
        _currentInstallation = new ToolStripMenuItem { Enabled = false };
        _currentHome = new ToolStripMenuItem { Enabled = false };
        _launcherVersion = new ToolStripMenuItem
        {
            Text = $"Launcher 版本：{LauncherUpdateService.CurrentVersionText}",
            Enabled = false
        };
        _update = Item("\u68C0\u67E5\u5E76\u66F4\u65B0\u5F53\u524D DSH", async (_, _) => await UpdateCurrentAsync());
        _updateLauncher = Item("检查 Launcher 更新", async (_, _) => await UpdateLauncherAsync());
        var configure = Item("\u914D\u7F6E\u2026", async (_, _) => await ConfigureAsync());
        _autoStart = Item("\u5F00\u673A\u81EA\u542F", async (_, _) => await ToggleAutoStartAsync());
        _autoStart.CheckOnClick = true;
        _autoStart.Checked = AutoStartService.IsEnabled();
        var exit = Item("\u9000\u51FA\u5E76\u505C\u6B62 DSH", async (_, _) => await ExitAsync());

        _menu.Items.AddRange([
            _open,
            _start,
            _stop,
            _restart,
            new ToolStripSeparator(),
            _launcherVersion,
            _currentInstallation,
            _currentHome,
            _update,
            _updateLauncher,
            configure,
            new ToolStripSeparator(),
            _autoStart,
            exit
        ]);

        _tray.ContextMenuStrip = _menu;
        _tray.Icon = _icons.Get(DshActivityState.Stopped);
        _tray.Text = "DSH Launcher";
        _tray.Visible = true;
        _tray.DoubleClick += async (_, _) => await OpenAsync();
        _tray.BalloonTipClicked += (_, _) => DshRuntimeService.OpenWeb(_settings.Port);

        _events.StatusChanged += (_, snapshot) => _ui.Post(_ => ApplyStatus(snapshot), null);
        _events.NotificationRequested += (_, notification) =>
            _ui.Post(_ => ShowNotification(notification), null);
        _flash.Tick += (_, _) =>
        {
            _alternateAttention = !_alternateAttention;
            _tray.Icon = _icons.Get(DshActivityState.Attention, _alternateAttention);
        };

        RefreshMenu();
        _events.Start(_settings.Port);
        Application.Idle += OnInitialIdle;
    }

    private async void OnInitialIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnInitialIdle;
        if (_openConfigurationOnStart)
        {
            await ConfigureAsync();
            return;
        }

        if (_settings.StartDshWithLauncher && _settings.SelectedInstallation is not null)
        {
            await StartAsync(_settings.OpenBrowserAfterStart);
        }
        else if (_settings.SelectedInstallation is null)
        {
            await ConfigureAsync();
        }
    }

    private static ToolStripMenuItem Item(string text, EventHandler handler)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += handler;
        return item;
    }

    private async Task OpenAsync()
    {
        if (!await _runtime.IsDshReadyAsync(_settings.Port))
        {
            await StartAsync(false);
        }

        if (await _runtime.IsDshReadyAsync(_settings.Port))
        {
            DshRuntimeService.OpenWeb(_settings.Port);
        }
    }

    private async Task StartAsync(bool openBrowser)
    {
        await RunOperationAsync(async () =>
        {
            var installation = _settings.SelectedInstallation;
            var home = _settings.SelectedHome;
            if (installation is null || home is null)
            {
                MessageBox.Show(
                    "\u8BF7\u5148\u5728\u201C\u914D\u7F6E\u201D\u4E2D\u9009\u62E9 DSH \u7248\u672C\u548C DSH_HOME\u3002",
                    "DSH Launcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                await ConfigureAsync();
                return;
            }

            if (!ConfirmCompatibility(installation, home))
            {
                return;
            }

            _events.Start(_settings.Port);
            await _runtime.StartAsync(
                installation,
                home.Path,
                _settings.WorkingDirectory,
                _settings.Port);
            if (_runtime.OwnsRunningProcess)
            {
                _homeWatcher.Start(home, installation.InstalledVersion);
            }
            else
            {
                _homeWatcher.Stop();
            }
            if (openBrowser) DshRuntimeService.OpenWeb(_settings.Port);
        });
    }

    private async Task StopAsync()
    {
        if (!_runtime.OwnsRunningProcess && await _runtime.IsDshReadyAsync(_settings.Port))
        {
            MessageBox.Show(
                "\u5F53\u524D DSH \u8FDB\u7A0B\u4E0D\u7531 Launcher \u542F\u52A8\u3002\u4E3A\u907F\u514D\u8BEF\u6740\u5176\u4ED6\u8FDB\u7A0B\uFF0CLauncher \u4E0D\u4F1A\u5F3A\u5236\u7ED3\u675F\u5B83\u3002",
                "DSH Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        await RunOperationAsync(async () =>
        {
            _homeWatcher.Stop();
            await _runtime.StopAsync();
        });
    }

    private async Task RestartAsync()
    {
        if (!_runtime.OwnsRunningProcess && await _runtime.IsDshReadyAsync(_settings.Port))
        {
            await StopAsync();
            return;
        }

        await RunOperationAsync(async () =>
        {
            var installation = _settings.SelectedInstallation
                ?? throw new InvalidOperationException("No DSH installation is selected.");
            var home = _settings.SelectedHome
                ?? throw new InvalidOperationException("No DSH_HOME is selected.");
            if (!ConfirmCompatibility(installation, home)) return;
            _homeWatcher.Stop();
            _events.Start(_settings.Port);
            await _runtime.RestartAsync(
                installation,
                home.Path,
                _settings.WorkingDirectory,
                _settings.Port);
            if (_runtime.OwnsRunningProcess)
            {
                _homeWatcher.Start(home, installation.InstalledVersion);
            }
            if (_settings.OpenBrowserAfterStart)
            {
                DshRuntimeService.OpenWeb(_settings.Port);
            }
        });
    }

    private async Task UpdateCurrentAsync()
    {
        var selected = _settings.SelectedInstallation;
        if (selected is null)
        {
            await ConfigureAsync();
            return;
        }

        if (!_runtime.OwnsRunningProcess &&
            await _runtime.IsDshReadyAsync(_settings.Port))
        {
            MessageBox.Show(
                "检测到当前端口上有 Launcher 未管理的 DSH。请先自行关闭该 DSH，再更新程序包。",
                "DSH Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        await RunOperationAsync(async () =>
        {
            var update = await _npm.CheckForUpdateAsync(
                selected.InstalledVersion);
            var latest = update.LatestVersion;
            if (!update.IsUpdateAvailable)
            {
                var message = string.Equals(
                    latest,
                    selected.InstalledVersion,
                    StringComparison.OrdinalIgnoreCase)
                    ? $"DSH {selected.InstalledVersion} 已是官方最新 Release。"
                    : $"没有发现比当前 DSH {selected.InstalledVersion} " +
                      $"发布时间更晚的官方 Release。\r\n\r\n" +
                      $"官方最新可安装版本：{latest}";
                MessageBox.Show(
                    message,
                    "检查并更新当前 DSH",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var releaseTime = update.LatestPublishedAt?
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm") ?? "未知";
            if (MessageBox.Show(
                    $"将当前 DSH 从 {selected.InstalledVersion} 更新到 {latest}？\r\n" +
                    $"官方发布时间：{releaseTime}\r\n\r\n" +
                    selected.InstallRoot,
                    "检查并更新当前 DSH",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question) != DialogResult.OK)
            {
                return;
            }

            using var progressWindow =
                new UpdateProgressForm("正在更新 DSH");
            progressWindow.ShowFor();
            progressWindow.Report(new OperationProgress(
                $"正在准备从 {selected.InstalledVersion} 更新到 {latest}…",
                5,
                selected.InstallRoot));

            var wasRunning = _runtime.OwnsRunningProcess;
            if (wasRunning)
            {
                progressWindow.Report(new OperationProgress(
                    "正在停止当前 DSH…",
                    15,
                    selected.InstallRoot));
                _homeWatcher.Stop();
                await _runtime.StopAsync();
            }

            progressWindow.Report(new OperationProgress(
                $"正在安装 DSH {latest}…",
                Detail: selected.InstallRoot));
            var npmProgress = new Progress<string>(message =>
                progressWindow.Report(new OperationProgress(
                    message,
                    Detail: selected.InstallRoot)));
            var originalId = selected.Id;
            var updated = await _npm.UpdateToAsync(
                selected,
                latest,
                npmProgress);
            updated.Id = originalId;

            progressWindow.Report(new OperationProgress(
                "正在保存 Launcher 配置…",
                80,
                updated.InstallRoot));
            var index = _settings.Installations.FindIndex(
                item => item.Id == originalId);
            if (index >= 0)
            {
                _settings.Installations[index] = updated;
            }

            await _store.SaveAsync(_settings);
            RefreshMenu();
            if (wasRunning)
            {
                var home = _settings.SelectedHome;
                if (home is not null)
                {
                    progressWindow.Report(new OperationProgress(
                        "正在重新启动 DSH…",
                        Detail: $"端口 {_settings.Port}"));
                    _events.Start(_settings.Port);
                    await _runtime.StartAsync(
                        updated,
                        home.Path,
                        _settings.WorkingDirectory,
                        _settings.Port);
                    if (_runtime.OwnsRunningProcess)
                    {
                        _homeWatcher.Start(
                            home,
                            updated.InstalledVersion);
                    }
                }
            }

            progressWindow.Report(new OperationProgress(
                $"DSH {updated.InstalledVersion} 更新完成。",
                100,
                updated.InstallRoot));
            progressWindow.CloseWhenFinished();
            MessageBox.Show(
                $"已更新到 DSH {updated.InstalledVersion}。",
                "DSH Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        });
    }

    private async Task UpdateLauncherAsync()
    {
        if (string.IsNullOrWhiteSpace(
                _settings.LauncherUpdateRepository))
        {
            MessageBox.Show(
                "请先在“配置 → 启动设置”中填写 Launcher GitHub 仓库。",
                "检查 Launcher 更新",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            await ConfigureAsync();
            return;
        }

        string? updaterPath = null;
        await RunOperationAsync(async () =>
        {
            using var service = new LauncherUpdateService(_log);
            var release = await service.GetLatestAsync(
                _settings.LauncherUpdateRepository);
            if (release.Version <= LauncherUpdateService.CurrentVersion)
            {
                MessageBox.Show(
                    $"当前 Launcher {LauncherUpdateService.CurrentVersionText} 已是最新版本。",
                    "检查 Launcher 更新",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(
                    $"发现 Launcher {release.Tag}。\r\n\r\n" +
                    $"{release.Name}\r\n\r\n" +
                    "下载并安装轻量更新包？",
                    "检查 Launcher 更新",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            using var progressWindow =
                new UpdateProgressForm("正在更新 DSH Launcher");
            progressWindow.ShowFor();
            updaterPath = await service.DownloadAsync(
                release,
                progressWindow);
            progressWindow.Report(new OperationProgress(
                "正在启动 Launcher 更新程序…",
                100,
                release.AssetName));
            progressWindow.CloseWhenFinished();
        });

        if (updaterPath is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(updaterPath)
        {
            UseShellExecute = true
        });
        await ExitAsync();
    }

    private async Task ConfigureAsync()
    {
        var originalInstallationId = _settings.SelectedInstallationId;
        var originalHomeId = _settings.SelectedHomeId;
        var originalPort = _settings.Port;
        var originalWorkingDirectory = _settings.WorkingDirectory;
        var originalInstallation = _settings.SelectedInstallation;
        var originalHome = _settings.SelectedHome;
        _homeWatcher.Stop();

        using var form = new ConfigurationForm(_settings, _store, _npm, _runtime);
        if (form.ShowDialog() != DialogResult.OK)
        {
            if (_runtime.OwnsRunningProcess &&
                originalInstallation is not null &&
                originalHome is not null)
            {
                _homeWatcher.Start(originalHome, originalInstallation.InstalledVersion);
            }
            return;
        }

        var runtimeConfigurationChanged =
            _settings.SelectedInstallationId != originalInstallationId ||
            _settings.SelectedHomeId != originalHomeId ||
            _settings.Port != originalPort ||
            !string.Equals(
                _settings.WorkingDirectory,
                originalWorkingDirectory,
                StringComparison.OrdinalIgnoreCase);

        RefreshMenu();
        if (form.RestartRequested ||
            (_runtime.OwnsRunningProcess && runtimeConfigurationChanged))
        {
            await RestartAsync();
        }
        else
        {
            _events.Start(_settings.Port);
            if (_runtime.OwnsRunningProcess &&
                _settings.SelectedInstallation is { } installation &&
                _settings.SelectedHome is { } home)
            {
                _homeWatcher.Start(home, installation.InstalledVersion);
            }
        }
    }

    private async Task ToggleAutoStartAsync()
    {
        try
        {
            AutoStartService.SetEnabled(_autoStart.Checked);
            _settings.StartWithWindows = _autoStart.Checked;
            await _store.SaveAsync(_settings);
        }
        catch (Exception error)
        {
            _autoStart.Checked = !_autoStart.Checked;
            MessageBox.Show(error.Message, "DSH Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (!await _operations.WaitAsync(0))
        {
            return;
        }

        try
        {
            SetMenuBusy(true);
            await operation();
        }
        catch (Exception error)
        {
            _log.Error("Launcher operation failed.", error);
            MessageBox.Show(
                error.Message,
                "DSH Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetMenuBusy(false);
            _operations.Release();
            RefreshMenu();
        }
    }

    private void ApplyStatus(DshStatusSnapshot snapshot)
    {
        _status = snapshot;
        if (snapshot.State == DshActivityState.Attention)
        {
            if (!_flash.Enabled) _flash.Start();
        }
        else
        {
            _flash.Stop();
            _alternateAttention = false;
            _tray.Icon = _icons.Get(snapshot.State);
        }

        _tray.Text = LimitTooltip(snapshot.Summary);
        RefreshMenu();
    }

    private void ShowNotification(DshNotification notification)
    {
        if (!_settings.NotifyOnCompletion && notification.IsCompletion)
        {
            return;
        }

        _tray.BalloonTipTitle = notification.Title;
        _tray.BalloonTipText = notification.Message;
        _tray.BalloonTipIcon = notification.Icon;
        _tray.ShowBalloonTip(8000);
    }

    private void RefreshMenu()
    {
        var installation = _settings.SelectedInstallation;
        var home = _settings.SelectedHome;
        _currentInstallation.Text = installation is null
            ? "\u5F53\u524D DSH\uFF1A\u672A\u9009\u62E9"
            : $"\u5F53\u524D DSH\uFF1A{installation.InstalledVersion}\uFF08{(installation.Scope == DshInstallScope.Global ? "\u5168\u5C40" : "\u72EC\u7ACB")}\uFF09";
        _currentHome.Text = home is null
            ? "DSH_HOME\uFF1A\u672A\u9009\u62E9"
            : "DSH_HOME\uFF1A" + home.Path;
        _start.Enabled = _status.State == DshActivityState.Stopped && installation is not null;
        _stop.Enabled = _status.State != DshActivityState.Stopped;
        _restart.Enabled = installation is not null;
        _update.Enabled = installation is not null;
        _autoStart.Checked = AutoStartService.IsEnabled();
    }

    private void SetMenuBusy(bool busy)
    {
        foreach (ToolStripItem item in _menu.Items)
        {
            if (item is not ToolStripSeparator) item.Enabled = !busy;
        }

        _currentInstallation.Enabled = false;
        _currentHome.Enabled = false;
    }

    private static bool ConfirmCompatibility(DshInstallation installation, DshHomeEntry home)
    {
        if (string.IsNullOrWhiteSpace(home.LastObservedWriterVersion) ||
            string.Equals(installation.InstalledVersion, home.LastObservedWriterVersion, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return MessageBox.Show(
            $"\u8BE5 DSH_HOME \u6700\u540E\u7531 DSH {home.LastObservedWriterVersion} \u5199\u5165\u3002\r\n" +
            $"\u5F53\u524D\u9009\u62E9\u7248\u672C\u662F {installation.InstalledVersion}\u3002\r\n\r\n" +
            "\u4E0D\u540C\u7248\u672C\u53EF\u80FD\u4F7F\u7528\u4E0D\u540C\u7684\u6570\u636E\u683C\u5F0F\u3002\u662F\u5426\u7EE7\u7EED\uFF1F",
            "DSH_HOME \u517C\u5BB9\u6027\u63D0\u793A",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning) == DialogResult.OK;
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            _homeWatcher.Stop();
            await _runtime.StopAsync();
            await _events.DisposeAsync();
            await _runtime.DisposeAsync();
        }
        finally
        {
            _tray.Visible = false;
            _tray.Dispose();
            _flash.Dispose();
            _homeWatcher.Dispose();
            _icons.Dispose();
            ExitThread();
        }
    }

    private static string LimitTooltip(string text) =>
        text.Length <= 63 ? text : text[..60] + "...";

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_exiting)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _flash.Dispose();
            _homeWatcher.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }
}
