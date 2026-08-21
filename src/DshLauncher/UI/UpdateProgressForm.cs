using DshLauncher.Models;

namespace DshLauncher.UI;

internal sealed class UpdateProgressForm : Form, IProgress<OperationProgress>
{
    private bool _allowUserClose;

    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoEllipsis = true
    };
    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill,
        Height = 22,
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 28
    };
    private readonly Label _detail = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoEllipsis = true,
        ForeColor = SystemColors.GrayText
    };
    private readonly Label _percentage = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleRight
    };

    public UpdateProgressForm(string title)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(440, 126);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var progressRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 10, 0, 4)
        };
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        progressRow.Controls.Add(_progress, 0, 0);
        progressRow.Controls.Add(_percentage, 1, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 16, 18, 14),
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_status, 0, 0);
        root.Controls.Add(progressRow, 0, 1);
        root.Controls.Add(_detail, 0, 2);
        Controls.Add(root);

        Report(new OperationProgress("正在准备更新…"));
    }

    public void ShowFor(IWin32Window? owner = null)
    {
        if (owner is null)
        {
            StartPosition = FormStartPosition.CenterScreen;
            Show();
        }
        else
        {
            StartPosition = FormStartPosition.CenterParent;
            Show(owner);
        }

        Activate();
    }

    public void CloseWhenFinished()
    {
        _allowUserClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowUserClose &&
            e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    public void Report(OperationProgress value)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => Report(value));
            return;
        }

        _status.Text = value.Message;
        _detail.Text = value.Detail ?? string.Empty;
        if (value.Percentage.HasValue)
        {
            var percentage = Math.Clamp(value.Percentage.Value, 0, 100);
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = percentage;
            _percentage.Text = percentage + "%";
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 28;
            _percentage.Text = string.Empty;
        }
    }
}
