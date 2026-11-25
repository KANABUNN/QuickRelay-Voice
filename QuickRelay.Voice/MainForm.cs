namespace QuickRelay.Voice;

public sealed class MainForm : Form
{
    private static readonly Color Navy = Color.FromArgb(19, 33, 49);
    private static readonly Color Slate = Color.FromArgb(54, 74, 92);
    private static readonly Color Aqua = Color.FromArgb(148, 234, 223);
    private static readonly Color Cloud = Color.FromArgb(237, 242, 246);
    private static readonly Color Ink = Color.FromArgb(23, 40, 55);
    private static readonly (string Text, string Value)[] Environments =
    [
        ("本番 (production)", "production"), ("訓練 (training)", "training"), ("試験 (test)", "test")
    ];
    private static readonly (string Text, string Value)[] Thresholds =
    [
        ("震度0", "0"), ("震度1", "1"), ("震度2", "2"), ("震度3", "3"), ("震度4", "4"),
        ("震度5弱", "5-"), ("震度5強", "5+"), ("震度6弱", "6-"),
        ("震度6強", "6+"), ("震度7", "7")
    ];

    private readonly VoiceController controller;
    private readonly TextBox feedUrl = new() { AccessibleName = "WSS 接続先", PlaceholderText = "wss://サーバー名/v1/eew" };
    private readonly TextBox token = new() { AccessibleName = "接続トークン", UseSystemPasswordChar = true };
    private readonly ComboBox environment = new() { AccessibleName = "配信環境", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox muted = new() { Text = "消音で受信", AccessibleName = "消音で受信", AutoSize = true };
    private readonly CheckBox nationwide = new() { Text = "全国の警報を対象にする", AutoSize = true };
    private readonly TextBox regions = new()
    {
        AccessibleName = "対象地域コード", Multiline = true, ScrollBars = ScrollBars.Vertical,
        Height = 60, PlaceholderText = "区分:コードを1行に1件"
    };
    private readonly CheckBox forecast = new() { Text = "予報も読み上げる", AutoSize = true };
    private readonly ComboBox threshold = new() { AccessibleName = "予報の最低震度", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown step = new() { AccessibleName = "予報続報の震度差", Minimum = 1, Maximum = 9, Value = 1 };
    private readonly NumericUpDown port = new() { AccessibleName = "AivisSpeech のポート", Minimum = 1, Maximum = 65535, Value = 10101 };
    private readonly ComboBox style = new() { AccessibleName = "AivisSpeech のスタイル", DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button connect = ActionButton("設定を保存して接続", primary: true);
    private readonly Button disconnect = ActionButton("切断");
    private readonly Button stop = ActionButton("再生停止");
    private readonly Button listStyles = ActionButton("一覧を取得");
    private readonly Button test = ActionButton("テスト再生");
    private readonly ListBox activity = new() { AccessibleName = "最近の電文と動作", IntegralHeight = false, HorizontalScrollbar = true };
    private readonly Label tokenHint = new() { AutoSize = true, ForeColor = Slate };
    private readonly Label actionHint = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Slate };
    private readonly Label connectionStatus = StatusValue();
    private readonly Label upstreamStatus = StatusValue();
    private readonly Label captureStatus = StatusValue();
    private readonly Label engineStatus = StatusValue();
    private readonly Label outputStatus = StatusValue();
    private readonly Label cursorStatus = StatusValue();
    private readonly Label clockStatus = StatusValue();
    private bool connectionAction;
    private bool engineAction;
    private SynchronizationContext? uiContext;

    public MainForm(VoiceController controller)
    {
        this.controller = controller;
        Text = "QuickRelay Voice";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(840, 620);
        ClientSize = new Size(1000, 730);
        Font = new Font("Segoe UI", 10F);
        BackColor = Cloud;
        ForeColor = Ink;

        Controls.Add(BuildLayout());
        LoadSettings(controller.Settings);
        UpdateTokenHint();
        RenderStatus(controller.Status);
        UpdateOptions();

        nationwide.CheckedChanged += (_, _) => UpdateOptions();
        forecast.CheckedChanged += (_, _) => UpdateOptions();
        muted.CheckedChanged += (_, _) => controller.Mute(muted.Checked);
        feedUrl.TextChanged += (_, _) => UpdateTokenHint();
        environment.SelectedIndexChanged += (_, _) => UpdateTokenHint();
        connect.Click += async (_, _) => await ConnectAsync();
        disconnect.Click += async (_, _) => await RunConnectionActionAsync(controller.DisconnectAsync, "切断しました。");
        stop.Click += (_, _) => controller.StopAudio();
        listStyles.Click += async (_, _) => await RunEngineActionAsync(FillStylesAsync);
        test.Click += async (_, _) => await RunEngineActionAsync(async () =>
        {
            await controller.TestAudioAsync((int)port.Value, ReadStyleId());
            actionHint.Text = "テスト再生が完了しました。";
        });
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        controller.StatusChanged += OnStatusChanged;
        controller.Activity += OnActivity;
        RenderStatus(controller.Status);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        controller.StatusChanged -= OnStatusChanged;
        controller.Activity -= OnActivity;
        base.OnFormClosed(e);
    }

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Navy, Margin = Padding.Empty,
            Padding = new Padding(22, 14, 22, 14)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label
        {
            Text = "QuickRelay Voice", Font = new Font(Font.FontFamily, 24F, FontStyle.Bold),
            ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        };
        var quickControls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true,
            WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty
        };
        muted.ForeColor = Aqua;
        muted.Margin = new Padding(0, 10, 14, 0);
        stop.Margin = Padding.Empty;
        quickControls.Controls.Add(muted);
        quickControls.Controls.Add(stop);
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(quickControls, 1, 0);
        layout.Controls.Add(header, 0, 0);

        var statuses = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Padding = new Padding(18, 12, 18, 10),
            Margin = Padding.Empty
        };
        AddStatus(statuses, 0, "WSS", connectionStatus, 16);
        AddStatus(statuses, 1, "上流受信", upstreamStatus, 13);
        AddStatus(statuses, 2, "取込", captureStatus, 13);
        AddStatus(statuses, 3, "AivisSpeech", engineStatus, 15);
        AddStatus(statuses, 4, "再生", outputStatus, 15);
        AddStatus(statuses, 5, "時刻", clockStatus, 12);
        AddStatus(statuses, 6, "配信位置", cursorStatus, 16);
        layout.Controls.Add(statuses, 0, 1);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(18, 0, 18, 0), Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        var settingsPanel = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White,
            Margin = new Padding(0, 0, 8, 0), Padding = new Padding(13, 0, 13, 0)
        };
        settingsPanel.Controls.Add(BuildSettings());
        content.Controls.Add(settingsPanel, 0, 0);

        var historyPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White,
            Padding = new Padding(13, 0, 13, 12), Margin = new Padding(8, 0, 0, 0)
        };
        historyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        historyPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        historyPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        historyPanel.Controls.Add(SectionTitle("最近の電文と動作"), 0, 0);
        activity.Dock = DockStyle.Fill;
        activity.BorderStyle = BorderStyle.None;
        activity.BackColor = Color.FromArgb(248, 250, 252);
        historyPanel.Controls.Add(activity, 0, 1);
        content.Controls.Add(historyPanel, 1, 0);
        layout.Controls.Add(content, 0, 2);

        actionHint.Margin = new Padding(20, 0, 20, 0);
        layout.Controls.Add(actionHint, 0, 3);
        return layout;
    }

    private Control BuildSettings()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Padding = new Padding(0, 0, 0, 12), Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddWideRow(grid, SectionTitle("配信先"));
        AddSetting(grid, "WSS 接続先", feedUrl);
        AddSetting(grid, "トークン", token);
        AddSetting(grid, string.Empty, tokenHint);
        AddSetting(grid, "配信環境", environment);
        AddWideRow(grid, ButtonRow(connect, disconnect));

        AddWideRow(grid, SectionTitle("読み上げ範囲"));
        AddSetting(grid, "地域", nationwide);
        AddSetting(grid, "対象地域コード", regions);
        AddSetting(grid, "予報", forecast);
        AddSetting(grid, "最低震度", threshold);
        AddSetting(grid, "続報の震度差", step);
        var stepHint = new Label { Text = "震度階級の段数。4 → 5弱で1段。", AutoSize = true, ForeColor = Slate };
        AddSetting(grid, string.Empty, stepHint);

        AddWideRow(grid, SectionTitle("AivisSpeech"));
        AddSetting(grid, "ポート", port);
        AddSetting(grid, "スタイルID", style);
        AddWideRow(grid, ButtonRow(listStyles, test));
        return grid;
    }

    private void LoadSettings(AppSettings settings)
    {
        feedUrl.Text = settings.FeedUrl;
        environment.Items.AddRange(Environments.Select(value => value.Text).ToArray());
        var selectedEnvironment = Array.FindIndex(Environments, value => value.Value == settings.Environment);
        environment.SelectedIndex = selectedEnvironment >= 0 ? selectedEnvironment : 0;
        muted.Checked = settings.Muted;
        nationwide.Checked = settings.Nationwide;
        regions.Lines = settings.Regions;
        forecast.Checked = settings.ForecastEnabled;
        foreach (var value in Thresholds)
        {
            threshold.Items.Add(value.Text);
        }
        var selected = Array.FindIndex(Thresholds, value => value.Value == settings.ForecastThreshold);
        threshold.SelectedIndex = selected >= 0 ? selected : 4;
        step.Value = Math.Clamp(settings.ForecastStep, (int)step.Minimum, (int)step.Maximum);
        port.Value = Math.Clamp(settings.AivisPort, (int)port.Minimum, (int)port.Maximum);
        style.Text = settings.StyleId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void UpdateOptions()
    {
        regions.Enabled = !nationwide.Checked;
        threshold.Enabled = forecast.Checked;
        step.Enabled = forecast.Checked;
    }

    private void UpdateTokenHint()
    {
        var sameScope = CurrentScope() == controller.Settings.Scope;
        tokenHint.Text = controller.HasToken && sameScope
            ? "保存済み。空欄で同じトークンを使用。"
            : "接続先ごとのトークンを入力してください。";
    }

    private async Task ConnectAsync()
    {
        var regionKeys = regions.Lines.Select(line => line.Trim()).Where(line => line.Length != 0).Distinct().ToArray();
        int? styleId;
        try
        {
            styleId = ReadStyleId();
        }
        catch (FormatException exception)
        {
            actionHint.Text = exception.Message;
            style.Focus();
            return;
        }

        var settings = new AppSettings
        {
            FeedUrl = feedUrl.Text.Trim(), Environment = Environments[environment.SelectedIndex].Value, Muted = muted.Checked,
            Nationwide = nationwide.Checked, Regions = regionKeys, ForecastEnabled = forecast.Checked,
            ForecastThreshold = Thresholds[threshold.SelectedIndex].Value, ForecastStep = (int)step.Value,
            AivisPort = (int)port.Value, StyleId = styleId
        };
        try
        {
            settings.Validate();
        }
        catch (ArgumentException exception)
        {
            actionHint.Text = exception.Message;
            return;
        }

        var replacement = string.IsNullOrWhiteSpace(token.Text) ? null : token.Text.Trim();
        if (replacement is null && (!controller.HasToken || settings.Scope != controller.Settings.Scope))
        {
            actionHint.Text = "この配信先の接続トークンを入力してください。";
            token.Focus();
            return;
        }

        await RunConnectionActionAsync(async () =>
        {
            await controller.ConnectAsync(settings, replacement);
            if (settings.Muted != muted.Checked)
            {
                controller.Mute(muted.Checked);
            }
            token.Clear();
            UpdateTokenHint();
        }, "設定を保存しました。WSS の状態は上部に表示します。");
    }

    private string CurrentScope() => new AppSettings
    {
        FeedUrl = feedUrl.Text.Trim(), Environment = Environments[environment.SelectedIndex].Value
    }.Scope;

    private async Task RunConnectionActionAsync(Func<Task> action, string success)
    {
        if (connectionAction)
        {
            return;
        }

        connectionAction = true;
        connect.Enabled = disconnect.Enabled = false;
        try
        {
            await action();
            actionHint.Text = success;
        }
        catch (Exception)
        {
            actionHint.Text = "接続設定を適用できませんでした。接続先とトークンを確認してください。";
        }
        finally
        {
            connectionAction = false;
            connect.Enabled = disconnect.Enabled = true;
        }
    }

    private int? ReadStyleId()
    {
        if (style.SelectedItem is StyleChoice choice && choice.ToString() == style.Text)
        {
            return choice.Id;
        }

        if (string.IsNullOrWhiteSpace(style.Text))
        {
            return null;
        }

        if (int.TryParse(style.Text, out var id))
        {
            return id;
        }

        throw new FormatException("スタイルIDを整数で入力するか、一覧から選んでください。");
    }

    private async Task FillStylesAsync()
    {
        var selectedId = ReadStyleId();
        var styles = await controller.ListStylesAsync((int)port.Value);
        style.Items.Clear();
        foreach (var item in styles)
        {
            var option = new StyleChoice(item.Id, item.Name);
            style.Items.Add(option);
            if (item.Id == selectedId)
            {
                style.SelectedItem = option;
            }
        }
        if (style.SelectedItem is null)
        {
            style.Text = selectedId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
        actionHint.Text = $"スタイルを{styles.Count}件取得しました。空欄の場合は利用可能な初期スタイルを使います。";
    }

    private async Task RunEngineActionAsync(Func<Task> action)
    {
        if (engineAction)
        {
            return;
        }

        engineAction = true;
        listStyles.Enabled = test.Enabled = false;
        actionHint.Text = "AivisSpeech に接続しています…";
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            actionHint.Text = exception.Message;
        }
        catch (OperationCanceledException)
        {
            actionHint.Text = "テスト再生を中止しました。";
        }
        catch (Exception)
        {
            actionHint.Text = "AivisSpeech を操作できませんでした。起動状態、ポート、スタイルIDを確認してください。";
        }
        finally
        {
            engineAction = false;
            listStyles.Enabled = test.Enabled = true;
        }
    }

    private void OnStatusChanged(VoiceStatus status) => uiContext?.Post(_ =>
    {
        if (!IsDisposed)
        {
            RenderStatus(status);
        }
    }, null);

    private void OnActivity(string entry) => uiContext?.Post(_ =>
    {
        if (IsDisposed)
        {
            return;
        }

        activity.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {entry}");
        if (activity.Items.Count > 200)
        {
            activity.Items.RemoveAt(200);
        }
    }, null);

    private void RenderStatus(VoiceStatus status)
    {
        SetStatus(connectionStatus, status.Connection);
        SetStatus(upstreamStatus, status.Upstream);
        SetStatus(captureStatus, status.Capture);
        SetStatus(engineStatus, status.Engine);
        SetStatus(outputStatus, status.Output);
        SetStatus(clockStatus, status.Clock);
        SetStatus(cursorStatus, status.Cursor);
    }

    private static void SetStatus(Label label, string value)
    {
        label.Text = value;
        label.AccessibleDescription = label.Text;
    }

    private static void AddStatus(TableLayoutPanel panel, int column, string name, Label value, float weight)
    {
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, weight));
        var tile = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.White,
            Padding = new Padding(10, 5, 8, 5), Margin = new Padding(0, 0, 4, 0)
        };
        tile.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
        tile.RowStyles.Add(new RowStyle(SizeType.Percent, 57));
        tile.Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, ForeColor = Slate }, 0, 0);
        value.AccessibleName = name;
        tile.Controls.Add(value, 0, 1);
        panel.Controls.Add(tile, column, 0);
    }

    private static Label StatusValue() => new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        ForeColor = Ink, Font = new Font("Segoe UI", 11F, FontStyle.Bold)
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, Height = 36, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Navy, Font = new Font("Segoe UI", 13F, FontStyle.Bold), Margin = new Padding(0, 4, 0, 0)
    };

    private static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(82, 34), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Navy : Color.White, ForeColor = primary ? Color.White : Ink,
            Padding = new Padding(8, 0, 8, 0), Margin = new Padding(0, 0, 8, 0)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(171, 191, 205);
        return button;
    }

    private static Control ButtonRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            WrapContents = false, Margin = new Padding(0, 5, 0, 4)
        };
        row.Controls.AddRange(controls);
        return row;
    }

    private static void AddSetting(TableLayoutPanel grid, string name, Control input)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Height = 24, Margin = new Padding(0, 1, 5, 1)
        };
        grid.Controls.Add(label, 0, row);
        input.Margin = new Padding(0, 2, 0, 2);
        input.Dock = input is CheckBox or Label ? DockStyle.Left : DockStyle.Fill;
        grid.Controls.Add(input, 1, row);
    }

    private static void AddWideRow(TableLayoutPanel grid, Control control)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 2);
    }

    private sealed record StyleChoice(int Id, string Name)
    {
        public override string ToString() => $"{Name} ({Id})";
    }
}
