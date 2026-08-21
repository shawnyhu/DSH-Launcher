using DshLauncher.Infrastructure;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher.UI;

internal sealed class InstallVersionForm : Form
{
    private readonly NpmService _npm;
    private readonly RadioButton _managed = new() { Text = "\u72EC\u7ACB\u5B89\u88C5\uFF08\u5141\u8BB8\u591A\u7248\u672C\uFF09", Checked = true, AutoSize = true };
    private readonly RadioButton _global = new() { Text = "npm \u5168\u5C40\u5B89\u88C5", AutoSize = true };
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = "\u6D4F\u89C8\u2026", AutoSize = true };
    private readonly TextBox _filter = new() { PlaceholderText = "\u641C\u7D22\u7248\u672C\u53F7", Dock = DockStyle.Top };
    private readonly ListBox _versions = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, Text = "\u6B63\u5728\u8BFB\u53D6 npm \u7248\u672C\u5217\u8868\u2026" };
    private readonly Button _install = new() { Text = "\u5B89\u88C5", AutoSize = true, Enabled = false };
    private IReadOnlyList<NpmVersionInfo> _allVersions = [];

    public InstallVersionForm(NpmService npm)
    {
        _npm = npm;
        Text = "\u5B89\u88C5 DSH \u7248\u672C";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 560);
        Size = new Size(720, 650);
        Font = new Font("Microsoft YaHei UI", 9F);
        _path.Text = Path.Combine(AppPaths.ManagedInstallRoot, "dsh-runtime");

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var scope = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        scope.Controls.Add(_managed);
        scope.Controls.Add(_global);

        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.Controls.Add(_path, 0, 0);
        pathRow.Controls.Add(_browse, 1, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        var cancel = new Button { Text = "\u53D6\u6D88", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_install);
        buttons.Controls.Add(_status);

        root.Controls.Add(new Label { Text = "\u5B89\u88C5\u65B9\u5F0F", AutoSize = true }, 0, 0);
        root.Controls.Add(scope, 0, 1);
        root.Controls.Add(new Label { Text = "\u5B89\u88C5\u8DEF\u5F84", AutoSize = true, Margin = new Padding(0, 12, 0, 4) }, 0, 2);
        root.Controls.Add(pathRow, 0, 3);
        root.Controls.Add(new Panel { Dock = DockStyle.Fill, Controls = { _versions, _filter } }, 0, 4);
        root.Controls.Add(buttons, 0, 5);
        Controls.Add(root);

        _managed.CheckedChanged += (_, _) => UpdateScope();
        _browse.Click += (_, _) => Browse();
        _filter.TextChanged += (_, _) => ApplyFilter();
        _versions.SelectedIndexChanged += (_, _) => UpdateSuggestedPath();
        _install.Click += async (_, _) => await InstallAsync();
        Shown += async (_, _) => await LoadVersionsAsync();
        AcceptButton = _install;
        CancelButton = cancel;
    }

    public DshInstallation? Installed { get; private set; }

    private async Task LoadVersionsAsync()
    {
        try
        {
            _allVersions = await _npm.GetVersionsAsync();
            ApplyFilter();
            _versions.SelectedIndex = _versions.Items.Cast<NpmVersionInfo>().ToList()
                .FindIndex(item => item.IsLatest);
            if (_versions.SelectedIndex < 0 && _versions.Items.Count > 0) _versions.SelectedIndex = 0;
            _install.Enabled = _versions.Items.Count > 0;
            _status.Text = $"\u5171 {_allVersions.Count} \u4E2A\u53EF\u5B89\u88C5\u7248\u672C";
        }
        catch (Exception error)
        {
            _status.Text = "\u8BFB\u53D6\u7248\u672C\u5931\u8D25";
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyFilter()
    {
        var query = _filter.Text.Trim();
        var selected = _versions.SelectedItem as NpmVersionInfo;
        _versions.BeginUpdate();
        _versions.Items.Clear();
        _versions.Items.AddRange(_allVersions
            .Where(item => query.Length == 0 || item.Version.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Cast<object>()
            .ToArray());
        if (selected is not null)
        {
            var index = _versions.Items.Cast<NpmVersionInfo>()
                .ToList()
                .FindIndex(item => item.Version == selected.Version);
            if (index >= 0) _versions.SelectedIndex = index;
        }
        _versions.EndUpdate();
    }

    private void UpdateScope()
    {
        _path.Enabled = _managed.Checked;
        _browse.Enabled = _managed.Checked;
        UpdateSuggestedPath();
    }

    private void UpdateSuggestedPath()
    {
        if (!_managed.Checked || _versions.SelectedItem is not NpmVersionInfo version) return;
        var current = _path.Text.Trim();
        if (current.Length == 0 || current.StartsWith(AppPaths.ManagedInstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            _path.Text = Path.Combine(AppPaths.ManagedInstallRoot, "dsh-" + SafeFileName(version.Version));
        }
    }

    private static string SafeFileName(string value) =>
        string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "\u9009\u62E9\u4E00\u4E2A\u7A7A\u76EE\u5F55\u5B89\u88C5\u6B64 DSH \u7248\u672C",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_path.Text) ? _path.Text : AppPaths.ManagedInstallRoot
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.SelectedPath;
    }

    private async Task InstallAsync()
    {
        if (_versions.SelectedItem is not NpmVersionInfo version) return;
        var scope = _global.Checked ? DshInstallScope.Global : DshInstallScope.Managed;
        var root = scope == DshInstallScope.Global ? string.Empty : _path.Text.Trim();
        if (scope == DshInstallScope.Managed && root.Length == 0)
        {
            MessageBox.Show(this, "\u8BF7\u9009\u62E9\u5B89\u88C5\u8DEF\u5F84\u3002", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (scope == DshInstallScope.Global &&
            MessageBox.Show(this,
                "\u5168\u5C40 npm \u53EA\u80FD\u4FDD\u7559\u4E00\u4E2A DSH \u7248\u672C\u3002\u7EE7\u7EED\u5C06\u66FF\u6362\u5F53\u524D\u5168\u5C40\u7248\u672C\u3002",
                Text,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        SetBusy(true);
        var progress = new Progress<string>(message => _status.Text = message);
        try
        {
            Installed = await _npm.InstallAsync(scope, root, version.Version, progress);
            DialogResult = DialogResult.OK;
            Close();
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

    private void SetBusy(bool busy)
    {
        _install.Enabled = !busy;
        _managed.Enabled = !busy;
        _global.Enabled = !busy;
        _versions.Enabled = !busy;
        UseWaitCursor = busy;
    }
}
