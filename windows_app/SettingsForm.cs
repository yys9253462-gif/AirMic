using System;
using System.Drawing;
using System.Windows.Forms;

namespace AirMic.Windows;

public sealed class SettingsForm : Form
{
    private readonly ComboBox _cboProvider = new();
    private readonly TextBox _txtApiUrl = new();
    private readonly TextBox _txtApiKey = new();
    private readonly ComboBox _cboAsrModel = new();
    private readonly ComboBox _cboTransModel = new();
    private readonly CheckBox _chkAutoTranslate = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnCancel = new();

    private static readonly Color BgColor = Color.FromArgb(248, 250, 252);
    private static readonly Color CardBg = Color.White;
    private static readonly Color CardBorder = Color.FromArgb(226, 232, 240);
    private static readonly Color TextMain = Color.FromArgb(15, 23, 42);
    private static readonly Color TextSub = Color.FromArgb(71, 85, 105);
    private static readonly Color AccentBlue = Color.FromArgb(37, 99, 235);

    public event Action<AppConfig>? ConfigSaved;

    public SettingsForm(AppConfig currentConfig)
    {
        Text = "AI 大模型与字幕配置中心";
        ClientSize = new Size(580, 460);
        MinimumSize = new Size(520, 440);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = BgColor;
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        BuildUi();
        LoadConfig(currentConfig);
    }

    private void BuildUi()
    {
        var panel = new Panel
        {
            Location = new Point(20, 20),
            Size = new Size(540, 365),
            BackColor = CardBg
        };
        panel.Paint += (s, e) =>
        {
            var p = (Panel)s!;
            using var pen = new Pen(CardBorder, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        Controls.Add(panel);

        int y = 20;

        // 服务商
        AddRow(panel, "模型服务商：", _cboProvider, y);
        _cboProvider.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboProvider.Items.AddRange(new object[] { "OpenAI 官方", "DeepSeek / 国内主流中转", "自定义 API 端点" });
        y += 46;

        // API 接口地址
        AddRow(panel, "API 接口地址：", _txtApiUrl, y);
        _txtApiUrl.PlaceholderText = "例如 https://api.openai.com/v1";
        y += 46;

        // API Key
        AddRow(panel, "API 密钥 (Key)：", _txtApiKey, y);
        _txtApiKey.PasswordChar = '●';
        _txtApiKey.PlaceholderText = "请输入您的 API Key (如 sk-...)";
        y += 46;

        // 语音识别模型 (ASR)
        AddRow(panel, "语音识别模型：", _cboAsrModel, y);
        _cboAsrModel.Items.AddRange(new object[] {
            "whisper-1 (推荐 / 官方标准)",
            "whisper-large-v3",
            "whisper-base",
            "whisper-small"
        });
        y += 46;

        // 翻译大模型 (LLM)
        AddRow(panel, "翻译大模型：", _cboTransModel, y);
        _cboTransModel.Items.AddRange(new object[] {
            "gpt-4o-mini (极速高性价比 / 默认)",
            "gpt-4o (旗舰精准翻译)",
            "deepseek-chat (DeepSeek V3)",
            "deepseek-reasoner (DeepSeek R1)",
            "qwen-plus (通义千问)",
            "claude-3-5-sonnet"
        });
        y += 46;

        // 自动翻译复选框
        _chkAutoTranslate.Text = "开启语音识别后自动翻译为英文";
        _chkAutoTranslate.AutoSize = true;
        _chkAutoTranslate.Location = new Point(140, y + 4);
        _chkAutoTranslate.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        _chkAutoTranslate.ForeColor = Color.FromArgb(16, 185, 129);
        panel.Controls.Add(_chkAutoTranslate);

        // 底部保存与取消按钮
        _btnSave.Text = "💾 保存配置";
        _btnSave.SetBounds(320, 400, 115, 38);
        _btnSave.BackColor = AccentBlue;
        _btnSave.ForeColor = Color.White;
        _btnSave.FlatStyle = FlatStyle.Flat;
        _btnSave.FlatAppearance.BorderSize = 0;
        _btnSave.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        _btnSave.Cursor = Cursors.Hand;
        _btnSave.Click += (_, _) => OnSave();
        Controls.Add(_btnSave);

        _btnCancel.Text = "取消";
        _btnCancel.SetBounds(445, 400, 115, 38);
        _btnCancel.BackColor = Color.White;
        _btnCancel.ForeColor = TextSub;
        _btnCancel.FlatStyle = FlatStyle.Flat;
        _btnCancel.FlatAppearance.BorderColor = CardBorder;
        _btnCancel.Font = new Font("Microsoft YaHei UI", 9.5F);
        _btnCancel.Cursor = Cursors.Hand;
        _btnCancel.Click += (_, _) => Close();
        Controls.Add(_btnCancel);

        _cboProvider.SelectedIndexChanged += (_, _) =>
        {
            switch (_cboProvider.SelectedIndex)
            {
                case 0:
                    if (string.IsNullOrWhiteSpace(_txtApiUrl.Text) || _txtApiUrl.Text.Contains("deepseek"))
                        _txtApiUrl.Text = "https://api.openai.com/v1";
                    _cboAsrModel.Text = "whisper-1";
                    _cboTransModel.Text = "gpt-4o-mini";
                    break;
                case 1:
                    if (string.IsNullOrWhiteSpace(_txtApiUrl.Text) || _txtApiUrl.Text.Contains("openai"))
                        _txtApiUrl.Text = "https://api.deepseek.com/v1";
                    _cboAsrModel.Text = "whisper-1";
                    _cboTransModel.Text = "deepseek-chat";
                    break;
            }
        };
    }

    private static void AddRow(Control parent, string caption, Control input, int y)
    {
        var lbl = new Label
        {
            Text = caption,
            AutoSize = true,
            Location = new Point(20, y + 4),
            ForeColor = TextSub,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        parent.Controls.Add(lbl);

        input.SetBounds(140, y, 370, 26);
        parent.Controls.Add(input);
    }

    private void LoadConfig(AppConfig config)
    {
        _cboProvider.SelectedIndex = Math.Clamp(config.ProviderIndex, 0, _cboProvider.Items.Count - 1);
        _txtApiUrl.Text = config.ApiUrl;
        _txtApiKey.Text = config.ApiKey;
        _cboAsrModel.Text = config.AsrModel;
        _cboTransModel.Text = config.TranslationModel;
        _chkAutoTranslate.Checked = config.AutoTranslate;
    }

    private void OnSave()
    {
        string asrModel = _cboAsrModel.Text.Split(' ')[0].Trim();
        string transModel = _cboTransModel.Text.Split(' ')[0].Trim();

        var config = new AppConfig
        {
            ProviderIndex = _cboProvider.SelectedIndex,
            ApiUrl = _txtApiUrl.Text.Trim(),
            ApiKey = _txtApiKey.Text.Trim(),
            AsrModel = string.IsNullOrWhiteSpace(asrModel) ? "whisper-1" : asrModel,
            TranslationModel = string.IsNullOrWhiteSpace(transModel) ? "gpt-4o-mini" : transModel,
            AutoTranslate = _chkAutoTranslate.Checked
        };

        ConfigSaved?.Invoke(config);
        MessageBox.Show("大模型与 API 配置已成功保存！", "配置已保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }
}
