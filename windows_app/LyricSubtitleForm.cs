using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AirMic.Windows;

public sealed class LyricSubtitleForm : Form
{
    private readonly Label _lblChinese = new();
    private readonly Label _lblEnglish = new();
    private readonly Button _btnClose = new();
    private readonly CheckBox _chkTopMost = new();

    // 支持无边框拖拽移动
    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 0x2;

    public LyricSubtitleForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Size = new Size(800, 110);
        BackColor = Color.FromArgb(18, 18, 22);

        // 默认居中偏下悬浮 (如桌面底部 120px 处，仿现代音乐播放器/视频字幕)
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point((screen.Width - Width) / 2, screen.Bottom - Height - 80);

        BuildUi();
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    private void BuildUi()
    {
        // 顶部操作栏小卡片
        var dragBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 24,
            BackColor = Color.FromArgb(28, 28, 35),
            Cursor = Cursors.SizeAll
        };
        dragBar.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        };
        Controls.Add(dragBar);

        var title = new Label
        {
            Text = "✨ AirMic 桌面歌词式实时 AI 语音字幕 (按住拖拽 · 自动翻译)",
            Font = new Font("Microsoft YaHei UI", 8.5F),
            ForeColor = Color.FromArgb(160, 165, 180),
            AutoSize = true,
            Location = new Point(10, 3)
        };
        title.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        };
        dragBar.Controls.Add(title);

        _btnClose.Text = "✕";
        _btnClose.ForeColor = Color.FromArgb(180, 180, 190);
        _btnClose.BackColor = Color.Transparent;
        _btnClose.FlatStyle = FlatStyle.Flat;
        _btnClose.FlatAppearance.BorderSize = 0;
        _btnClose.Size = new Size(26, 24);
        _btnClose.Dock = DockStyle.Right;
        _btnClose.Cursor = Cursors.Hand;
        _btnClose.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
        _btnClose.Click += (_, _) => Hide();
        dragBar.Controls.Add(_btnClose);

        _chkTopMost.Text = "📌 置顶";
        _chkTopMost.Checked = true;
        _chkTopMost.ForeColor = Color.FromArgb(59, 130, 246);
        _chkTopMost.Dock = DockStyle.Right;
        _chkTopMost.Width = 65;
        _chkTopMost.Font = new Font("Microsoft YaHei UI", 8F);
        _chkTopMost.Cursor = Cursors.Hand;
        _chkTopMost.CheckedChanged += (_, _) => TopMost = _chkTopMost.Checked;
        dragBar.Controls.Add(_chkTopMost);

        // 主中文歌词高亮行 (加粗大字号，荧光高亮)
        _lblChinese.Text = "（等待手机麦克风语音输入...）";
        _lblChinese.Font = new Font("Microsoft YaHei UI", 13.5F, FontStyle.Bold);
        _lblChinese.ForeColor = Color.FromArgb(255, 255, 255);
        _lblChinese.TextAlign = ContentAlignment.MiddleCenter;
        _lblChinese.Dock = DockStyle.Top;
        _lblChinese.Height = 44;
        _lblChinese.AutoEllipsis = true;
        Controls.Add(_lblChinese);

        // 英文翻译高亮行 (柔和青蓝高亮)
        _lblEnglish.Text = "(Waiting for speech input...)";
        _lblEnglish.Font = new Font("Segoe UI", 10.5F, FontStyle.Regular);
        _lblEnglish.ForeColor = Color.FromArgb(56, 189, 248); // #38BDF8 天空蓝
        _lblEnglish.TextAlign = ContentAlignment.MiddleCenter;
        _lblEnglish.Dock = DockStyle.Fill;
        _lblEnglish.AutoEllipsis = true;
        Controls.Add(_lblEnglish);

        // 拖动窗体本身也支持移动
        MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        };
        _lblChinese.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        };
        _lblEnglish.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        };
    }

    public void UpdateSubtitle(string chinese, string english)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateSubtitle(chinese, english));
            return;
        }

        if (!string.IsNullOrWhiteSpace(chinese))
        {
            _lblChinese.Text = chinese;
        }
        if (!string.IsNullOrWhiteSpace(english))
        {
            _lblEnglish.Text = english;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // 绘制质感渐变边框与内阴影
        using var pen = new Pen(Color.FromArgb(70, 80, 105), 1.5f);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
