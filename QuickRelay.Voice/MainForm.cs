using System.ComponentModel;

namespace QuickRelay.Voice;

public sealed class MainForm : Form
{
    private static readonly Color Navy = AppIcons.Navy;
    private static readonly Color Slate = Color.FromArgb(54, 74, 92);
    private static readonly Color Aqua = AppIcons.Aqua;
    private static readonly Color Cloud = Color.FromArgb(237, 242, 246);
    private static readonly Color Ink = Color.FromArgb(23, 40, 55);
    private static readonly (string Text, string Value)[] Thresholds =
    [
        ("震度0", "0"), ("震度1", "1"), ("震度2", "2"), ("震度3", "3"), ("震度4", "4"),
        ("震度5弱", "5-"), ("震度5強", "5+"), ("震度6弱", "6-"),
        ("震度6強", "6+"), ("震度7", "7")
    ];

    private readonly VoiceController controller;
    private readonly Container components = new();
    private readonly NotifyIcon tray;
    private readonly ToolTip tips;
    private readonly Icon windowIcon = AppIcons.Create(32);
    private readonly ToolStripMenuItem trayMute = new("消音") { CheckOnClick = true };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage voiceTab = Page("音声");

    private readonly Label summary = new()
    {
        Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 0, 8, 0)
    };
    private readonly CheckBox muted = new() { Text = "消音", AccessibleName = "消音で受信", AutoSize = true };
    private readonly Button stop = ActionButton("再生停止");

    private readonly TextBox feedUrl = new() { AccessibleName = "WSS 接続先", PlaceholderText = "wss://サーバー名/v1/eew" };
    private readonly TextBox token = new() { AccessibleName = "接続トークン", UseSystemPasswordChar = true };
    private readonly Label tokenHint = Note(string.Empty);

    private readonly CheckBox nationwide = new() { Text = "全国の警報を対象にする", AutoSize = true };
    private readonly TextBox regions = new()
    {
        AccessibleName = "対象地域コード", Multiline = true, ScrollBars = ScrollBars.Vertical,
        Height = 64, PlaceholderText = "区分:コードを1行に1件"
    };
    private readonly CheckBox forecast = new() { Text = "予報も読み上げる", AutoSize = true };
    private readonly ComboBox threshold = new()
    {
        AccessibleName = "予報の最低震度", DropDownStyle = ComboBoxStyle.DropDownList, Width = 110
    };
    private readonly NumericUpDown step = new()
    {
        AccessibleName = "予報続報の震度差", Minimum = 1, Maximum = 9, Value = 1, Width = 64
    };

    private readonly NumericUpDown port = new()
    {
        AccessibleName = "AivisSpeech のポート", Minimum = 1, Maximum = 65535, Value = 10101, Width = 76
    };
    private readonly ComboBox model = new() { AccessibleName = "AivisSpeech のモデル", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox style = new() { AccessibleName = "AivisSpeech のスタイル", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Dictionary<TuningParameter, NumericUpDown> tuning = [];
    private readonly Button listStyles = ActionButton("一覧を取得");
    private readonly Button test = ActionButton("テスト再生");
    private readonly Button resetTuning = ActionButton("既定値に戻す");
    private readonly Button saveVoice = ActionButton("音声設定を保存");

    private readonly CheckBox minimizeToTray = new() { Text = "最小化・閉じるボタンでタスクトレイに格納する", AutoSize = true };
    private readonly CheckBox startInTray = new() { Text = "起動時はウィンドウを開かずタスクトレイに格納する", AutoSize = true };
    private readonly CheckBox autoConnect = new() { Text = "起動時に保存済みの配信先へ接続する", AutoSize = true };

    private readonly Label actionHint = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Slate, AutoEllipsis = true
    };
    private readonly Button connect = ActionButton("保存して接続", primary: true);
    private readonly Button disconnect = ActionButton("切断");

    private readonly ListBox activity = new()
    {
        AccessibleName = "最近の電文と動作", IntegralHeight = false, HorizontalScrollbar = true, Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(248, 250, 252)
    };
    private readonly Label connectionStatus = StatusValue();
    private readonly Label upstreamStatus = StatusValue();
    private readonly Label captureStatus = StatusValue();
    private readonly Label engineStatus = StatusValue();
    private readonly Label outputStatus = StatusValue();
    private readonly Label clockStatus = StatusValue();
    private readonly Label cursorStatus = StatusValue();

    private IReadOnlyList<VoiceStyle> voices = [];
    private VoiceSelection selection = new(null, null, null, null);
    private VoiceStatus lastStatus;
    private Light light = Light.Unknown;
    private Icon? trayIcon;
    private bool voicesLoaded, renderingVoices, connectionAction, engineAction;
    private bool attached, holdHidden, trayHintShown;
    private SynchronizationContext? uiContext;

    public MainForm(VoiceController controller)
    {
        this.controller = controller;
        var saved = controller.Settings;
        lastStatus = controller.Status;
        holdHidden = saved.StartInTray;
        tips = new ToolTip(components) { AutoPopDelay = 15000 };
        tray = new NotifyIcon(components) { ContextMenuStrip = BuildTrayMenu() };

        Text = "QuickRelay Voice";
        Icon = windowIcon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(440, 400);
        ClientSize = new Size(480, 440);
        BackColor = Cloud;
        ForeColor = Ink;

        Controls.Add(BuildLayout());
        LoadSettings(saved);
        UpdateTokenHint();
        RenderStatus(lastStatus);
        UpdateOptions();

        nationwide.CheckedChanged += (_, _) => UpdateOptions();
        forecast.CheckedChanged += (_, _) => UpdateOptions();
        muted.CheckedChanged += (_, _) =>
        {
            trayMute.Checked = muted.Checked;
            controller.Mute(muted.Checked);
            RenderLight();
        };
        feedUrl.TextChanged += (_, _) => UpdateTokenHint();
        connect.Click += async (_, _) => await ConnectAsync();
        disconnect.Click += async (_, _) => await RunConnectionActionAsync(controller.DisconnectAsync, "切断しました。");
        stop.Click += (_, _) => controller.StopAudio();
        model.SelectionChangeCommitted += (_, _) => OnModelPicked();
        style.SelectionChangeCommitted += (_, _) => OnStylePicked();
        listStyles.Click += async (_, _) => await RunEngineActionAsync(async () =>
        {
            var count = await LoadVoicesAsync();
            var models = voices.Select(voice => voice.SpeakerUuid).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            actionHint.Text = $"モデル{models}件・スタイル{count}件を取得しました。";
        });
        test.Click += async (_, _) => await RunEngineActionAsync(async () =>
        {
            await controller.TestAudioAsync(ReadVoice());
            actionHint.Text = "テスト再生が完了しました。";
        });
        resetTuning.Click += (_, _) =>
        {
            LoadTuning(new VoiceTuning());
            actionHint.Text = "音声パラメーターを既定値に戻しました。保存すると反映します。";
        };
        saveVoice.Click += async (_, _) => await RunEngineActionAsync(SaveVoiceAsync, "音声設定を保存しています…");
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (tabs.SelectedTab == voiceTab && !voicesLoaded)
                await LoadVoicesQuietlyAsync();
        };
        minimizeToTray.CheckedChanged += (_, _) => controller.SaveResidency(ReadResidency());
        startInTray.CheckedChanged += (_, _) => controller.SaveResidency(ReadResidency());
        autoConnect.CheckedChanged += (_, _) => controller.SaveResidency(ReadResidency());
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowFromTray();
        };
    }

    /// <summary>Brings the window back, also when another launch of the app asks for it from another thread.</summary>
    public void RequestShow()
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(ShowFromTray));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing; there is nothing left to show.
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        // Starting in the tray: keep the window hidden but create the handle so status updates and the tray work.
        if (value && holdHidden)
        {
            value = false;
            if (!IsHandleCreated)
                CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (attached)
            return;

        attached = true;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        controller.StatusChanged += OnStatusChanged;
        controller.Activity += OnActivity;
        RenderStatus(controller.Status);
        tray.Visible = true;
        BeginInvoke(new Action(() => _ = StartUpAsync()));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && minimizeToTray.Checked && Visible)
            HideToTray();
    }

    protected override void WndProc(ref Message m)
    {
        // The title-bar X, Alt+F4 and the taskbar's "close window" arrive as SC_CLOSE, and only those go to the tray.
        // Form.CloseReason is not used for this: it stays UserClosing after a cancelled close, which would also send a
        // later WM_CLOSE from an installer or Close() from the tray menu to the tray.
        const int WmSysCommand = 0x0112, ScClose = 0xF060;
        if (m.Msg == WmSysCommand && ((int)m.WParam & 0xFFF0) == ScClose && minimizeToTray.Checked)
        {
            HideToTray();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        controller.StatusChanged -= OnStatusChanged;
        controller.Activity -= OnActivity;
        tray.Visible = false;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            components.Dispose();
        base.Dispose(disposing);
        if (disposing)
        {
            trayIcon?.Dispose();
            windowIcon.Dispose();
        }
    }

    private async Task StartUpAsync()
    {
        var saved = controller.Settings;
        if (saved.AutoConnect && !string.IsNullOrWhiteSpace(saved.FeedUrl) && controller.HasToken)
            await RunConnectionActionAsync(() => controller.ConnectAsync(saved, null), "保存済みの設定で接続しました。");
        await LoadVoicesQuietlyAsync();
    }

    private void ShowFromTray()
    {
        holdHidden = false;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        Hide();
        if (trayHintShown)
            return;
        trayHintShown = true;
        tray.ShowBalloonTip(4000, "QuickRelay Voice",
            "タスクトレイで受信を続けています。終了するにはアイコンを右クリックして「終了」を選びます。", ToolTipIcon.Info);
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip(components);
        var open = new ToolStripMenuItem("QuickRelay Voice を表示") { Font = new Font(menu.Font, FontStyle.Bold) };
        open.Click += (_, _) => ShowFromTray();
        trayMute.Click += (_, _) => muted.Checked = trayMute.Checked;
        var halt = new ToolStripMenuItem("再生停止");
        halt.Click += (_, _) => controller.StopAudio();
        var quit = new ToolStripMenuItem("終了");
        quit.Click += (_, _) => Close();
        menu.Items.AddRange([open, new ToolStripSeparator(), trayMute, halt, new ToolStripSeparator(), quit]);
        return menu;
    }

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, BackColor = Navy,
            Margin = Padding.Empty, Padding = new Padding(10, 6, 8, 6)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label
        {
            Text = "QuickRelay Voice", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.White,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold), Margin = Padding.Empty
        };
        muted.ForeColor = Aqua;
        muted.Anchor = AnchorStyles.Right;
        muted.Margin = new Padding(0, 0, 10, 0);
        stop.Anchor = AnchorStyles.Right;
        stop.Margin = Padding.Empty;
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(summary, 1, 0);
        header.Controls.Add(muted, 2, 0);
        header.Controls.Add(stop, 3, 0);
        layout.Controls.Add(header, 0, 0);

        tabs.Margin = new Padding(6, 6, 6, 0);
        tabs.TabPages.Add(BuildStatusPage());
        tabs.TabPages.Add(BuildConnectionPage());
        tabs.TabPages.Add(BuildRangePage());
        tabs.TabPages.Add(BuildVoicePage());
        tabs.TabPages.Add(BuildResidencyPage());
        layout.Controls.Add(tabs, 0, 1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty, Padding = new Padding(10, 6, 6, 6)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionHint.Margin = new Padding(0, 0, 8, 0);
        footer.Controls.Add(actionHint, 0, 0);
        footer.Controls.Add(disconnect, 1, 0);
        footer.Controls.Add(connect, 2, 0);
        layout.Controls.Add(footer, 0, 2);
        return layout;
    }

    private TabPage BuildStatusPage()
    {
        var page = Page("状態");
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var grid = Grid(2);
        AddStatus(grid, "WSS", connectionStatus);
        AddStatus(grid, "上流受信", upstreamStatus);
        AddStatus(grid, "取込", captureStatus);
        AddStatus(grid, "AivisSpeech", engineStatus);
        AddStatus(grid, "再生", outputStatus);
        AddStatus(grid, "時刻", clockStatus);
        AddStatus(grid, "配信位置", cursorStatus);
        body.Controls.Add(grid, 0, 0);
        body.Controls.Add(SectionTitle("最近の電文と動作"), 0, 1);
        activity.Margin = Padding.Empty;
        body.Controls.Add(activity, 0, 2);
        page.Controls.Add(body);
        return page;
    }

    private TabPage BuildConnectionPage()
    {
        var page = Page("接続");
        var grid = Grid(2);
        AddField(grid, "WSS 接続先", feedUrl);
        AddField(grid, "トークン", token);
        AddField(grid, string.Empty, tokenHint);
        AddField(grid, "配信環境", new Label { Text = "本番 (production)", AutoSize = true });
        AddWide(grid, Note("下の「保存して接続」で、すべてのタブの設定を保存して接続します。"));
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildRangePage()
    {
        var page = Page("範囲");
        var grid = Grid(2);
        AddField(grid, "地域", nationwide);
        AddField(grid, "対象地域コード", regions);
        AddField(grid, "予報", forecast);
        AddField(grid, "最低震度", threshold, fill: false);
        AddField(grid, "続報の震度差", step);
        AddField(grid, string.Empty, Note("震度階級の段数。4 → 5弱で1段。"));
        AddWide(grid, Note("範囲の変更は「保存して接続」で反映します。"));
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildVoicePage()
    {
        var grid = Grid(4);
        var row = NextRow(grid);
        grid.Controls.Add(FieldLabel("ポート"), 0, row);
        var portRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        port.Margin = new Padding(0, 3, 8, 0);
        listStyles.Margin = Padding.Empty;
        portRow.Controls.AddRange([port, listStyles]);
        Place(grid, portRow, 1, row, span: 3, fill: false);

        row = NextRow(grid);
        grid.Controls.Add(FieldLabel("モデル"), 0, row);
        Place(grid, model, 1, row, span: 3);
        row = NextRow(grid);
        grid.Controls.Add(FieldLabel("スタイル"), 0, row);
        Place(grid, style, 1, row, span: 3);

        AddWide(grid, SectionTitle("音声パラメーター"));
        var parameters = VoiceTuning.Parameters;
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            var input = new NumericUpDown
            {
                AccessibleName = parameter.Label, DecimalPlaces = 2, Increment = 0.01M, Width = 64,
                Minimum = (decimal)parameter.Min, Maximum = (decimal)parameter.Max
            };
            tuning[parameter] = input;
            var label = FieldLabel(parameter.Label);
            tips.SetToolTip(label, parameter.Hint);
            tips.SetToolTip(input, parameter.Hint);
            row = index % 2 == 0 ? NextRow(grid) : grid.RowCount - 1;
            var column = index % 2 * 2;
            if (column != 0)
                label.Margin = new Padding(12, label.Margin.Top, label.Margin.Right, label.Margin.Bottom);
            grid.Controls.Add(label, column, row);
            Place(grid, input, column + 1, row);
        }

        AddWide(grid, ButtonRow(test, resetTuning, saveVoice));
        AddWide(grid, Note("項目にマウスを重ねると説明を表示します。\n保存すると、接続中でも次の読み上げから反映します。"));
        voiceTab.Controls.Add(grid);
        return voiceTab;
    }

    private TabPage BuildResidencyPage()
    {
        var page = Page("常駐");
        var grid = Grid(1);
        AddWide(grid, minimizeToTray);
        AddWide(grid, startInTray);
        AddWide(grid, autoConnect);
        AddWide(grid, Note("タスクトレイのアイコンをクリックするとウィンドウを表示します。\n"
            + "右クリックのメニューから、消音・再生停止・終了を選べます。\n\n"
            + "アイコン右下の色\n緑: 読み上げ可能　黄: 消音または接続準備中\n赤: 異常　灰: 未接続"));
        page.Controls.Add(grid);
        return page;
    }

    private void LoadSettings(AppSettings settings)
    {
        feedUrl.Text = settings.FeedUrl;
        muted.Checked = settings.Muted;
        trayMute.Checked = settings.Muted;
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
        selection = new VoiceSelection(settings.SpeakerUuid, settings.SpeakerName, settings.StyleId, settings.StyleName);
        RenderVoiceChoices();
        LoadTuning(settings.Tuning);
        minimizeToTray.Checked = settings.MinimizeToTray;
        startInTray.Checked = settings.StartInTray;
        autoConnect.Checked = settings.AutoConnect;
    }

    private void LoadTuning(VoiceTuning values)
    {
        foreach (var (parameter, input) in tuning)
        {
            input.Value = Math.Clamp((decimal)parameter.Read(values), input.Minimum, input.Maximum);
        }
    }

    private VoiceTuning ReadTuning() => tuning.Aggregate(new VoiceTuning(),
        (values, entry) => entry.Key.Write(values, (double)entry.Value.Value));

    private AppSettings ReadVoice() => new()
    {
        AivisPort = (int)port.Value, StyleId = selection.StyleId, SpeakerUuid = selection.SpeakerUuid,
        SpeakerName = selection.SpeakerName, StyleName = selection.StyleName, Tuning = ReadTuning()
    };

    private AppSettings ReadResidency() => new()
    {
        MinimizeToTray = minimizeToTray.Checked, StartInTray = startInTray.Checked, AutoConnect = autoConnect.Checked
    };

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
        var settings = new AppSettings
        {
            FeedUrl = feedUrl.Text.Trim(), Muted = muted.Checked,
            Nationwide = nationwide.Checked, Regions = regionKeys, ForecastEnabled = forecast.Checked,
            ForecastThreshold = Thresholds[threshold.SelectedIndex].Value, ForecastStep = (int)step.Value
        }.WithVoiceFrom(ReadVoice()).WithResidencyFrom(ReadResidency());
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
            tabs.SelectedIndex = 1;
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
        }, "設定を保存しました。接続の状態は「状態」タブに表示します。");
    }

    private string CurrentScope() => new AppSettings
    {
        FeedUrl = feedUrl.Text.Trim()
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
        catch (ArgumentException exception)
        {
            actionHint.Text = exception.Message;
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

    private async Task SaveVoiceAsync()
    {
        var applied = await controller.SaveVoiceAsync(ReadVoice());
        actionHint.Text = applied
            ? "音声設定を保存しました。次の読み上げから新しい設定を使います。"
            : "音声設定を保存しました。次の接続から使います。";
    }

    private async Task<int> LoadVoicesAsync()
    {
        var styles = await controller.ListStylesAsync((int)port.Value);
        voices = styles;
        voicesLoaded = true;
        RenderVoiceChoices();
        return styles.Count;
    }

    /// <summary>Fills the model list in the background. AivisSpeech may still be starting, so failures stay silent.</summary>
    private async Task LoadVoicesQuietlyAsync()
    {
        if (engineAction)
        {
            return;
        }

        engineAction = true;
        SetEngineButtons(false);
        try
        {
            await LoadVoicesAsync();
        }
        catch (Exception)
        {
            // 「一覧を取得」を押したときに原因を表示する。
        }
        finally
        {
            engineAction = false;
            SetEngineButtons(true);
        }
    }

    private void OnModelPicked()
    {
        if (renderingVoices || model.SelectedItem is not ModelChoice picked)
        {
            return;
        }

        selection = new VoiceSelection(picked.Uuid, picked.Name, null, null);
        RenderVoiceChoices();
    }

    private void OnStylePicked()
    {
        if (renderingVoices || style.SelectedItem is not StyleChoice picked || model.SelectedItem is not ModelChoice owner)
        {
            return;
        }

        selection = picked.Value ?? new VoiceSelection(owner.Uuid, owner.Name, null, null);
        if (picked.Value is { SpeakerUuid: not null } && owner.Uuid is null)
        {
            // A style picked from all models also selects its model; refresh after the drop-down closes.
            BeginInvoke(new Action(RenderVoiceChoices));
        }
    }

    private void RenderVoiceChoices()
    {
        renderingVoices = true;
        model.BeginUpdate();
        style.BeginUpdate();
        try
        {
            var anyModel = new ModelChoice(null, null, "自動 (最初のモデル)");
            model.Items.Clear();
            model.Items.Add(anyModel);
            foreach (var group in voices.GroupBy(voice => voice.SpeakerUuid, StringComparer.OrdinalIgnoreCase))
            {
                var name = group.First().SpeakerName;
                model.Items.Add(new ModelChoice(group.Key, name, name));
            }
            var currentModel = model.Items.OfType<ModelChoice>().FirstOrDefault(choice => choice.Uuid is not null
                && string.Equals(choice.Uuid, selection.SpeakerUuid, StringComparison.OrdinalIgnoreCase));
            if (currentModel is null && selection.SpeakerUuid is { } uuid)
            {
                currentModel = new ModelChoice(uuid, selection.SpeakerName, Unlisted(selection.SpeakerName ?? uuid));
                model.Items.Add(currentModel);
            }
            var filter = currentModel ?? anyModel;
            model.SelectedItem = filter;

            var anyStyle = new StyleChoice(null, "自動 (ノーマルを優先)");
            style.Items.Clear();
            style.Items.Add(anyStyle);
            foreach (var voice in voices.Where(voice => filter.Uuid is null
                || string.Equals(voice.SpeakerUuid, filter.Uuid, StringComparison.OrdinalIgnoreCase)))
            {
                style.Items.Add(new StyleChoice(new VoiceSelection(voice.SpeakerUuid, voice.SpeakerName, voice.Id, voice.StyleName),
                    filter.Uuid is null ? $"{voice.SpeakerName} / {voice.StyleName}" : voice.StyleName));
            }
            var currentStyle = selection.StyleId is int id
                ? style.Items.OfType<StyleChoice>().FirstOrDefault(choice => choice.Value?.StyleId == id)
                : null;
            if (currentStyle is null && selection.StyleId is int unlisted)
            {
                currentStyle = new StyleChoice(selection, Unlisted(selection.StyleName ?? $"スタイルID {unlisted}"));
                style.Items.Add(currentStyle);
            }
            style.SelectedItem = currentStyle ?? anyStyle;
        }
        finally
        {
            style.EndUpdate();
            model.EndUpdate();
            renderingVoices = false;
        }
    }

    private string Unlisted(string label) => voicesLoaded ? $"{label} (見つかりません)" : label;

    private async Task RunEngineActionAsync(Func<Task> action, string progress = "AivisSpeech に接続しています…")
    {
        if (engineAction)
        {
            return;
        }

        engineAction = true;
        SetEngineButtons(false);
        actionHint.Text = progress;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            actionHint.Text = exception.Message;
        }
        catch (OperationCanceledException)
        {
            actionHint.Text = "テスト再生を中止しました。";
        }
        catch (Exception)
        {
            actionHint.Text = "AivisSpeech を操作できませんでした。起動状態、ポート、モデルを確認してください。";
        }
        finally
        {
            engineAction = false;
            SetEngineButtons(true);
        }
    }

    private void SetEngineButtons(bool enabled) => listStyles.Enabled = test.Enabled = saveVoice.Enabled = enabled;

    private void OnStatusChanged(VoiceStatus status) => uiContext?.Post(_ =>
    {
        if (!IsDisposed)
        {
            RenderStatus(status);
            UpdateTokenHint();
        }
    }, null);

    private void OnActivity(string entry) => uiContext?.Post(_ =>
    {
        if (IsDisposed)
        {
            return;
        }

        activity.Items.Insert(0, entry);
        if (activity.Items.Count > 200)
        {
            activity.Items.RemoveAt(200);
        }
    }, null);

    private void RenderStatus(VoiceStatus status)
    {
        lastStatus = status;
        SetStatus(connectionStatus, status.Connection);
        SetStatus(upstreamStatus, status.Upstream);
        SetStatus(captureStatus, status.Capture);
        SetStatus(engineStatus, status.Engine);
        SetStatus(outputStatus, status.Output);
        SetStatus(clockStatus, status.Clock);
        SetStatus(cursorStatus, status.Cursor);
        RenderLight();
    }

    private void SetStatus(Label label, string value)
    {
        label.Text = value;
        label.AccessibleDescription = value;
        tips.SetToolTip(label, value);
    }

    /// <summary>Summarises the status in the header and the tray icon, which is all a user sees while it is hidden.</summary>
    private void RenderLight()
    {
        var status = lastStatus;
        var next = status.Connection switch
        {
            "未接続" => Light.Off,
            not ("接続済み" or "接続中" or "同期中") => Light.Fault,
            _ when status.Upstream == "異常" || status.Capture == "異常" || status.Clock.Contains("保留", StringComparison.Ordinal)
                || status.Engine.Contains("エラー", StringComparison.Ordinal)
                || status.Output.Contains("エラー", StringComparison.Ordinal) => Light.Fault,
            "接続済み" when !muted.Checked => Light.Ready,
            _ => Light.Hold
        };
        var color = next switch
        {
            Light.Ready => Color.FromArgb(76, 201, 122),
            Light.Hold => Color.FromArgb(242, 184, 75),
            Light.Fault => Color.FromArgb(240, 98, 98),
            _ => Color.FromArgb(150, 165, 178)
        };

        var line = muted.Checked ? $"{status.Connection}・消音" : status.Connection;
        summary.Text = "● " + line;
        summary.ForeColor = color;
        tips.SetToolTip(summary, line);
        var tip = $"QuickRelay Voice\n{line}";
        tray.Text = tip.Length <= 127 ? tip : tip[..126] + "…";
        if (next == light)
        {
            return;
        }

        light = next;
        var previous = trayIcon;
        trayIcon = AppIcons.Create(SystemInformation.SmallIconSize.Width, color);
        tray.Icon = trayIcon;
        previous?.Dispose();
    }

    private static TabPage Page(string text) => new()
    {
        Text = text, BackColor = Color.White, Padding = new Padding(10, 8, 10, 8), AutoScroll = true
    };

    private static TableLayoutPanel Grid(int columns)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns, GrowStyle = TableLayoutPanelGrowStyle.AddRows, Margin = Padding.Empty
        };
        for (var column = 0; column < columns; column++)
        {
            grid.ColumnStyles.Add(column % 2 == 0 && columns > 1
                ? new ColumnStyle(SizeType.AutoSize)
                : new ColumnStyle(SizeType.Percent, 100f / Math.Max(1, columns / 2)));
        }
        return grid;
    }

    private static int NextRow(TableLayoutPanel grid)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return row;
    }

    private static void AddField(TableLayoutPanel grid, string name, Control input, bool? fill = null)
    {
        var row = NextRow(grid);
        grid.Controls.Add(FieldLabel(name), 0, row);
        Place(grid, input, 1, row, fill: fill);
    }

    private static void AddWide(TableLayoutPanel grid, Control control)
    {
        var row = NextRow(grid);
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, grid.ColumnCount);
    }

    /// <param name="fill">Stretch across the cell; by default only text boxes and drop-downs do.</param>
    private static void Place(TableLayoutPanel grid, Control input, int column, int row, int span = 1, bool? fill = null)
    {
        input.Margin = new Padding(0, 3, 0, 3);
        if (fill ?? input is TextBox or ComboBox)
            input.Dock = DockStyle.Fill;
        else
            input.Anchor = AnchorStyles.Left;
        grid.Controls.Add(input, column, row);
        grid.SetColumnSpan(input, span);
    }

    private static void AddStatus(TableLayoutPanel grid, string name, Label value)
    {
        var row = NextRow(grid);
        var label = FieldLabel(name);
        label.ForeColor = Slate;
        label.Margin = new Padding(0, 3, 12, 3);
        grid.Controls.Add(label, 0, row);
        value.AccessibleName = name;
        grid.Controls.Add(value, 1, row);
    }

    private static Label StatusValue() => new()
    {
        Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Height = 22,
        ForeColor = Ink, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(0, 1, 0, 1)
    };

    private static Label FieldLabel(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6)
    };

    private static Label Note(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Slate, MaximumSize = new Size(380, 0), Margin = new Padding(0, 4, 0, 4)
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Navy, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
        Margin = new Padding(0, 10, 0, 4)
    };

    private static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(72, 28), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Navy : Color.White, ForeColor = primary ? Color.White : Ink,
            Padding = new Padding(6, 0, 6, 0), Margin = new Padding(0, 0, 6, 0)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(171, 191, 205);
        return button;
    }

    private static Control ButtonRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            WrapContents = true, Margin = new Padding(0, 10, 0, 2)
        };
        row.Controls.AddRange(controls);
        return row;
    }

    private enum Light { Unknown, Off, Ready, Hold, Fault }

    private sealed record VoiceSelection(string? SpeakerUuid, string? SpeakerName, int? StyleId, string? StyleName);

    private sealed record ModelChoice(string? Uuid, string? Name, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record StyleChoice(VoiceSelection? Value, string Label)
    {
        public override string ToString() => Label;
    }
}
