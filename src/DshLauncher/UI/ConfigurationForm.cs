using System.Diagnostics;
using DshLauncher.Infrastructure;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher.UI;

internal sealed class ConfigurationForm : Form
{
    private readonly LauncherSettings _settings;
    private readonly SettingsStore _store;
    private readonly NpmService _npm;
    private readonly DshRuntimeService _runtime;
    private readonly ListBox _installations = new() { Dock = DockStyle.Fill };
    private readonly ListBox _homes = new() { Dock = DockStyle.Fill };
    private readonly Label _installationDetails = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _homeDetails = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _compatibility = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DarkGoldenrod };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 3080, Width = 120 };
    private readonly TextBox _workdir = new() { Dock = DockStyle.Fill };
    private readonly TextBox _updateRepository = new() { Dock = DockStyle.Fill, PlaceholderText = "owner/repository" };
    private readonly CheckBox _startDsh = new() { Text = "\u542F\u52A8 Launcher \u540E\u81EA\u52A8\u8FD0\u884C DSH", AutoSize = true };
    private readonly CheckBox _openBrowser = new() { Text = "\u542F\u52A8 DSH \u540E\u81EA\u52A8\u6253\u5F00\u7F51\u9875", AutoSize = true };
    private readonly CheckBox _startWindows = new() { Text = "\u5F00\u673A\u81EA\u542F DSH Launcher", AutoSize = true };
    private readonly Button _update = new() { Text = "\u68C0\u67E5\u5E76\u66F4\u65B0", AutoSize = true };
    private readonly Button _reinstall = new() { Text = "\u91CD\u65B0\u5B89\u88C5", AutoSize = true };
    private readonly Button _removeInstallation = new() { Text = "\u5378\u8F7D", AutoSize = true };
    private bool _busy;

    public ConfigurationForm(
        LauncherSettings settings,
        SettingsStore store,
        NpmService npm,
        DshRuntimeService runtime)
    {
        _settings = settings;
        _store = store;
        _npm = npm;
        _runtime = runtime;

        Text = "DSH Launcher \u914D\u7F6E";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(680, 650);
        Size = new Size(900, 720);
        Font = new Font("Microsoft YaHei UI", 9F);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildInstallationsTab());
        tabs.TabPages.Add(BuildHomesTab());
        tabs.TabPages.Add(BuildGeneralTab());

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12)
        };
        var cancel = new Button { Text = "\u5173\u95ED", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "\u4FDD\u5B58", AutoSize = true };
        var saveRestart = new Button { Text = "\u4FDD\u5B58\u5E76\u91CD\u542F DSH", AutoSize = true };
        save.Click += async (_, _) => await SaveAndCloseAsync(false);
        saveRestart.Click += async (_, _) => await SaveAndCloseAsync(true);
        footer.Controls.Add(cancel);
        footer.Controls.Add(save);
        footer.Controls.Add(saveRestart);

        Controls.Add(tabs);
        Controls.Add(footer);
        CancelButton = cancel;

        _port.Value = Math.Clamp(settings.Port, 1, 65535);
        _workdir.Text = settings.WorkingDirectory;
        _updateRepository.Text = settings.LauncherUpdateRepository;
        _startDsh.Checked = settings.StartDshWithLauncher;
        _openBrowser.Checked = settings.OpenBrowserAfterStart;
        _startWindows.Checked = AutoStartService.IsEnabled();

        RefreshInstallations();
        RefreshHomes();
    }

    public bool RestartRequested { get; private set; }

    private static SplitContainer CreateVerticalSplitContainer()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6
        };
        split.Resize += (_, _) =>
        {
            var available = split.ClientSize.Height - split.SplitterWidth;
            const int minimumTop = 120;
            const int minimumBottom = 150;
            if (available < minimumTop + minimumBottom)
            {
                return;
            }

            var desired = (int)(available * 0.62);
            split.SplitterDistance = Math.Clamp(
                desired,
                minimumTop,
                available - minimumBottom);
        };
        return split;
    }

    private TabPage BuildInstallationsTab()
    {
        var page = new TabPage("DSH \u7248\u672C") { Padding = new Padding(12) };
        var split = CreateVerticalSplitContainer();
        split.Panel1.Controls.Add(_installations);

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        var install = new Button { Text = "\u5B89\u88C5\u7248\u672C\u2026", AutoSize = true };
        install.Click += async (_, _) => await InstallVersionAsync();
        _update.Click += async (_, _) => await UpdateSelectedAsync();
        _reinstall.Click += async (_, _) => await ReinstallSelectedAsync();
        _removeInstallation.Click += async (_, _) => await RemoveSelectedAsync();
        buttons.Controls.Add(install);
        buttons.Controls.Add(_update);
        buttons.Controls.Add(_reinstall);
        buttons.Controls.Add(_removeInstallation);
        details.Controls.Add(new Label { Text = "\u5F53\u524D\u9009\u4E2D\u7684 DSH \u5B89\u88C5\u5B9E\u4F8B", AutoSize = true }, 0, 0);
        details.Controls.Add(_installationDetails, 0, 1);
        details.Controls.Add(_compatibility, 0, 2);
        details.Controls.Add(buttons, 0, 3);
        split.Panel2.Controls.Add(details);
        page.Controls.Add(split);
        _installations.SelectedIndexChanged += (_, _) => InstallationSelectionChanged();
        return page;
    }

    private TabPage BuildHomesTab()
    {
        var page = new TabPage("DSH_HOME") { Padding = new Padding(12) };
        var split = CreateVerticalSplitContainer();
        split.Panel1.Controls.Add(_homes);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight
        };
        var addExisting = new Button { Text = "\u6DFB\u52A0\u5DF2\u6709\u76EE\u5F55", AutoSize = true };
        var create = new Button { Text = "\u521B\u5EFA\u65B0\u76EE\u5F55", AutoSize = true };
        var open = new Button { Text = "\u6253\u5F00\u76EE\u5F55", AutoSize = true };
        var remove = new Button { Text = "\u4ECE\u5217\u8868\u79FB\u9664", AutoSize = true };
        addExisting.Click += (_, _) => AddHome(false);
        create.Click += (_, _) => AddHome(true);
        open.Click += (_, _) => OpenSelectedHome();
        remove.Click += (_, _) => RemoveSelectedHome();
        buttons.Controls.Add(addExisting);
        buttons.Controls.Add(create);
        buttons.Controls.Add(open);
        buttons.Controls.Add(remove);
        right.Controls.Add(new Label { Text = "\u6570\u636E\u76EE\u5F55\u7531 DSH \u76F4\u63A5\u4F7F\u7528\uFF0CLauncher \u4E0D\u8BFB\u53D6 API \u51ED\u636E\u3002", AutoSize = true }, 0, 0);
        right.Controls.Add(_homeDetails, 0, 1);
        right.Controls.Add(buttons, 0, 2);
        split.Panel2.Controls.Add(right);
        page.Controls.Add(split);
        _homes.SelectedIndexChanged += (_, _) => HomeSelectionChanged();
        return page;
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("\u542F\u52A8\u8BBE\u7F6E") { Padding = new Padding(18) };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = "\u7AEF\u53E3", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        table.Controls.Add(_port, 1, 0);
        var available = new Button { Text = "\u67E5\u627E\u53EF\u7528\u7AEF\u53E3", AutoSize = true };
        available.Click += (_, _) => _port.Value = PortService.FindAvailablePort((int)_port.Value);
        table.Controls.Add(available, 2, 0);
        table.Controls.Add(new Label { Text = "\u9ED8\u8BA4\u5DE5\u4F5C\u76EE\u5F55", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        table.Controls.Add(_workdir, 1, 1);
        var browse = new Button { Text = "\u6D4F\u89C8\u2026", AutoSize = true };
        browse.Click += (_, _) => BrowseWorkdir();
        table.Controls.Add(browse, 2, 1);
        table.Controls.Add(new Label { Text = "Launcher GitHub \u4ED3\u5E93", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        table.Controls.Add(_updateRepository, 1, 2);
        table.SetColumnSpan(_updateRepository, 2);
        table.Controls.Add(_startDsh, 1, 3);
        table.Controls.Add(_openBrowser, 1, 4);
        table.Controls.Add(_startWindows, 1, 5);
        page.Controls.Add(table);
        return page;
    }

    private void RefreshInstallations()
    {
        var selectedId = _settings.SelectedInstallationId;
        _installations.BeginUpdate();
        _installations.Items.Clear();
        _installations.Items.AddRange(_settings.Installations.Cast<object>().ToArray());
        _installations.EndUpdate();
        var index = _settings.Installations.FindIndex(item => item.Id == selectedId);
        _installations.SelectedIndex = index >= 0 ? index : (_installations.Items.Count > 0 ? 0 : -1);
        InstallationSelectionChanged();
    }

    private void RefreshHomes()
    {
        var selectedId = _settings.SelectedHomeId;
        _homes.BeginUpdate();
        _homes.Items.Clear();
        _homes.Items.AddRange(_settings.Homes.Cast<object>().ToArray());
        _homes.EndUpdate();
        var index = _settings.Homes.FindIndex(item => item.Id == selectedId);
        _homes.SelectedIndex = index >= 0 ? index : 0;
        HomeSelectionChanged();
    }

    private void InstallationSelectionChanged()
    {
        if (_installations.SelectedItem is not DshInstallation item)
        {
            _installationDetails.Text = "\u5C1A\u672A\u5B89\u88C5 DSH\u3002";
            _update.Enabled = _reinstall.Enabled = _removeInstallation.Enabled = false;
            return;
        }

        _settings.SelectedInstallationId = item.Id;
        _installationDetails.Text =
            $"\u7248\u672C\uFF1A{item.InstalledVersion}\r\n" +
            $"\u7C7B\u578B\uFF1A{(item.Scope == DshInstallScope.Global ? "\u5168\u5C40 npm" : "Launcher \u7BA1\u7406")}\r\n" +
            $"\u8DEF\u5F84\uFF1A{item.InstallRoot}\r\nNode\uFF1A{item.NodeExecutable}";
        _update.Enabled = _reinstall.Enabled = _removeInstallation.Enabled = !_busy;
        UpdateCompatibility();
    }

    private void HomeSelectionChanged()
    {
        if (_homes.SelectedItem is not DshHomeEntry home) return;
        _settings.SelectedHomeId = home.Id;
        var version = string.IsNullOrWhiteSpace(home.LastObservedWriterVersion)
            ? "\u672A\u77E5"
            : home.LastObservedWriterVersion;
        var time = home.LastObservedWriteAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "\u672A\u77E5";
        var reliability = home.ObservationReliable ? "Launcher \u5DF2\u89C2\u5BDF" : "\u8BB0\u5F55\u53EF\u80FD\u4E0D\u5B8C\u6574";
        _homeDetails.Text =
            $"\u8DEF\u5F84\uFF1A{home.Path}\r\n" +
            $"Launcher \u89C2\u5BDF\u5230\u7684\u6700\u540E\u5199\u5165\u7248\u672C\uFF1A{version}\r\n" +
            $"\u65F6\u95F4\uFF1A{time}\r\n\u53EF\u4FE1\u5EA6\uFF1A{reliability}";
        UpdateCompatibility();
    }

    private void UpdateCompatibility()
    {
        var installation = _installations.SelectedItem as DshInstallation;
        var home = _homes.SelectedItem as DshHomeEntry;
        if (installation is null || home is null || string.IsNullOrWhiteSpace(home.LastObservedWriterVersion))
        {
            _compatibility.Text = "\u6570\u636E\u517C\u5BB9\u6027\uFF1A\u65E0\u6CD5\u5224\u65AD";
            return;
        }

        var comparison = CompareVersions(installation.InstalledVersion, home.LastObservedWriterVersion);
        _compatibility.Text = comparison switch
        {
            < 0 => $"\u8B66\u544A\uFF1A\u8BE5\u6570\u636E\u6700\u540E\u7531\u8F83\u65B0\u7684 DSH {home.LastObservedWriterVersion} \u5199\u5165\u3002",
            > 0 => $"\u63D0\u793A\uFF1A\u9996\u6B21\u4F7F\u7528 {installation.InstalledVersion} \u53EF\u80FD\u5347\u7EA7\u6570\u636E\u683C\u5F0F\u3002",
            _ => "\u6570\u636E\u517C\u5BB9\u6027\uFF1A\u7248\u672C\u4E00\u81F4"
        };
    }

    private async Task InstallVersionAsync()
    {
        if (!await EnsurePackageMutationAllowedAsync()) return;
        using var dialog = new InstallVersionForm(_npm);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Installed is null) return;
        UpsertInstallation(dialog.Installed);
        RefreshInstallations();
        await _store.SaveAsync(_settings);
    }

    private async Task UpdateSelectedAsync()
    {
        if (_installations.SelectedItem is not DshInstallation selected)
        {
            return;
        }

        if (!await EnsurePackageMutationAllowedAsync())
        {
            return;
        }

        try
        {
            SetBusy(true);
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
                    this,
                    message,
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var releaseTime = update.LatestPublishedAt?
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm") ?? "未知";
            if (MessageBox.Show(
                    this,
                    $"将所选 DSH 从 {selected.InstalledVersion} 更新到 {latest}？\r\n" +
                    $"官方发布时间：{releaseTime}\r\n\r\n" +
                    selected.InstallRoot,
                    "检查并更新所选 DSH",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question) != DialogResult.OK)
            {
                return;
            }

            using var progressWindow =
                new UpdateProgressForm("正在更新 DSH");
            progressWindow.ShowFor(this);
            progressWindow.Report(new OperationProgress(
                $"正在准备从 {selected.InstalledVersion} 更新到 {latest}…",
                5,
                selected.InstallRoot));

            if (_runtime.OwnsRunningProcess &&
                selected.Id == _settings.SelectedInstallationId)
            {
                progressWindow.Report(new OperationProgress(
                    "正在停止当前 DSH…",
                    15,
                    selected.InstallRoot));
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
                85,
                updated.InstallRoot));
            ReplaceInstallation(originalId, updated);
            await _store.SaveAsync(_settings);
            RefreshInstallations();
            progressWindow.Report(new OperationProgress(
                $"DSH {updated.InstalledVersion} 更新完成。",
                100,
                updated.InstallRoot));
            progressWindow.CloseWhenFinished();
            MessageBox.Show(
                this,
                $"已更新到 DSH {updated.InstalledVersion}。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                error.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReinstallSelectedAsync()
    {
        if (_installations.SelectedItem is not DshInstallation selected) return;
        if (!await EnsurePackageMutationAllowedAsync()) return;
        if (MessageBox.Show(this,
            $"\u91CD\u65B0\u5B89\u88C5 DSH {selected.InstalledVersion}\uFF1F",
            Text,
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            SetBusy(true);
            if (_runtime.OwnsRunningProcess && selected.Id == _settings.SelectedInstallationId) await _runtime.StopAsync();
            var originalId = selected.Id;
            var repaired = await _npm.InstallAsync(selected.Scope, selected.InstallRoot, selected.InstalledVersion);
            repaired.Id = originalId;
            ReplaceInstallation(originalId, repaired);
            await _store.SaveAsync(_settings);
            RefreshInstallations();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RemoveSelectedAsync()
    {
        if (_installations.SelectedItem is not DshInstallation selected) return;
        if (!await EnsurePackageMutationAllowedAsync()) return;
        if (MessageBox.Show(this,
            $"\u5378\u8F7D {selected}\uFF1F\r\n\r\nDSH_HOME \u548C\u5BF9\u8BDD\u6570\u636E\u4F1A\u4FDD\u7559\u3002",
            "\u5378\u8F7D DSH",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning) != DialogResult.OK) return;
        try
        {
            SetBusy(true);
            if (_runtime.OwnsRunningProcess && selected.Id == _settings.SelectedInstallationId) await _runtime.StopAsync();
            await _npm.UninstallAsync(selected);
            _settings.Installations.RemoveAll(item => item.Id == selected.Id);
            _settings.SelectedInstallationId = _settings.Installations.FirstOrDefault()?.Id;
            await _store.SaveAsync(_settings);
            RefreshInstallations();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AddHome(bool create)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = create ? "\u9009\u62E9\u65B0 DSH_HOME \u76EE\u5F55" : "\u9009\u62E9\u5DF2\u6709 DSH_HOME",
            UseDescriptionForTitle = true,
            SelectedPath = AppPaths.DefaultDshHome
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var path = Path.GetFullPath(dialog.SelectedPath);
        if (create) Directory.CreateDirectory(path);
        var existing = _settings.Homes.FirstOrDefault(item =>
            string.Equals(Path.GetFullPath(item.Path), path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _settings.SelectedHomeId = existing.Id;
            RefreshHomes();
            return;
        }

        var home = new DshHomeEntry
        {
            Name = new DirectoryInfo(path).Name,
            Path = path
        };
        _settings.Homes.Add(home);
        _settings.SelectedHomeId = home.Id;
        RefreshHomes();
    }

    private void RemoveSelectedHome()
    {
        if (_homes.SelectedItem is not DshHomeEntry home || _settings.Homes.Count <= 1) return;
        if (MessageBox.Show(this,
            "\u53EA\u4ECE Launcher \u5217\u8868\u79FB\u9664\u8BE5\u8DEF\u5F84\uFF0C\u4E0D\u5220\u9664\u78C1\u76D8\u4E0A\u7684\u4EFB\u4F55\u6570\u636E\u3002",
            Text,
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information) != DialogResult.OK) return;
        _settings.Homes.Remove(home);
        _settings.SelectedHomeId = _settings.Homes[0].Id;
        RefreshHomes();
    }

    private void OpenSelectedHome()
    {
        if (_homes.SelectedItem is not DshHomeEntry home) return;
        Directory.CreateDirectory(home.Path);
        Process.Start(new ProcessStartInfo(home.Path) { UseShellExecute = true });
    }

    private void BrowseWorkdir()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "\u9009\u62E9 DSH \u9ED8\u8BA4\u5DE5\u4F5C\u76EE\u5F55",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_workdir.Text) ? _workdir.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _workdir.Text = dialog.SelectedPath;
    }

    private async Task SaveAndCloseAsync(bool restart)
    {
        _settings.Port = (int)_port.Value;
        _settings.WorkingDirectory = _workdir.Text.Trim();
        _settings.StartDshWithLauncher = _startDsh.Checked;
        _settings.OpenBrowserAfterStart = _openBrowser.Checked;
        _settings.StartWithWindows = _startWindows.Checked;
        _settings.LauncherUpdateRepository = _updateRepository.Text.Trim();
        AutoStartService.SetEnabled(_startWindows.Checked);
        await _store.SaveAsync(_settings);
        RestartRequested = restart;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void UpsertInstallation(DshInstallation installation)
    {
        var existing = installation.Scope == DshInstallScope.Global
            ? _settings.Installations.FirstOrDefault(item => item.Scope == DshInstallScope.Global)
            : _settings.Installations.FirstOrDefault(item =>
                string.Equals(item.InstallRoot, installation.InstallRoot, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            installation.Id = existing.Id;
            ReplaceInstallation(existing.Id, installation);
        }
        else
        {
            _settings.Installations.Add(installation);
        }

        _settings.SelectedInstallationId = installation.Id;
    }

    private void ReplaceInstallation(Guid id, DshInstallation replacement)
    {
        var index = _settings.Installations.FindIndex(item => item.Id == id);
        if (index >= 0) _settings.Installations[index] = replacement;
        _settings.SelectedInstallationId = replacement.Id;
    }

    private async Task<bool> EnsurePackageMutationAllowedAsync()
    {
        if (_runtime.OwnsRunningProcess ||
            !await _runtime.IsDshReadyAsync(_settings.Port))
        {
            return true;
        }

        MessageBox.Show(
            this,
            "检测到当前端口上有 Launcher 未管理的 DSH。请先自行关闭该 DSH，再安装、更新、重装或卸载程序包。",
            Text,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return false;
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        UseWaitCursor = value;
        _installations.Enabled = !value;
        _update.Enabled = _reinstall.Enabled = _removeInstallation.Enabled = !value && _installations.SelectedItem is not null;
    }

    private static int CompareVersions(string left, string right)
    {
        static Version Parse(string value)
        {
            var numeric = value.TrimStart('v', 'V').Split('-', 2)[0];
            return Version.TryParse(numeric, out var parsed) ? parsed : new Version(0, 0);
        }

        return Parse(left).CompareTo(Parse(right));
    }
}
