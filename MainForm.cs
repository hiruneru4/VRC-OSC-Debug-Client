using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using VRCOscDebugClient.Core;
using VRCOscDebugClient.Osc;
using VRCOscDebugClient.UI;

namespace VRCOscDebugClient;

public sealed partial class MainForm : Form
{
    // ---------------- 基本 ----------------
    private readonly AppSettings _s = AppSettings.Load();
    private readonly OscClient _osc = new();
    private readonly InputArbiter _arb;
    private readonly ValueLocker _locker;
    private readonly ToolTip _tip = new() { InitialDelay = 400, AutoPopDelay = 20000, ReshowDelay = 100 };
    private readonly List<ActionRow> _rows = new();
    private readonly Dictionary<string, Panel> _pages = new();
    private TabStrip _tabs;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 50 };

    private Panel _content, _sideHost;
    private Splitter _splitter;

    private FastLabel _status;
    private ActivityPanel _activity;

    // ---------------- 上部 ----------------
    private TextBox _txtHost;
    private RNumber _nudSend, _nudRecv;
    private RCheck _chkListen, _chkTop, _chkAutoConnect;
    private RToggle _chkOutput, _chkSide;
    private RButton _btnConnect, _btnStop, _btnTheme;
    private Label _lamp, _lblOpacity;
    private RSlider _trkOpacity;

    // ---------------- 全停止 / ホットキー ----------------
    private Hotkey _stopKey, _outputKey;
    private readonly List<int> _registeredIds = new();
    private readonly List<Macro> _hotkeyMacros = new();
    private bool _stopping, _pollPrev;
    private long _lastStopTick, _quietUntil;
    private DateTime _flashUntil = DateTime.MinValue;
    private HotkeyBox _hkStop, _hkOutput;
    private RCheck _chkStopDisables, _chkPollStop, _chkLogAuto;
    private Label _lblHotkeyStatus;
    private ComboBox _cmbFont;
    private RNumber _nudFontSize;
    private ComboBox _cmbZoom, _cmbLang;

    // ---------------- 受信 ----------------
    // 受信値は生のまま保存し、表示する時だけ文字にする (受信スレッドの負荷を減らす)
    private sealed class RecvEntry
    {
        public object[] Args = Array.Empty<object>();
        public long Ticks;
        public volatile bool Dirty;
    }
    private readonly ConcurrentDictionary<string, RecvEntry> _recv = new();
    private readonly Dictionary<string, ListViewItem> _lvItems = new();
    private ListView _lvRecv;
    private TextBox _txtRecvFilter;
    private RCheck _chkRecvPause, _chkRecvLockedOnly;
    private FastLabel _lblRecvAddr;
    private ComboBox _cmbRecvType;
    private TextBox _txtRecvValue;
    private RButton _btnRecvLock;
    private string _recvAddr;
    private Label _lblMicSys;
    private int _muteSelf = -1; // -1 = 不明
    private volatile OscMessage _lastSentMsg;
    private volatile string _lastError = "";

    // ---------------- チャット ----------------
    private readonly ActionRunner _chatRunner = new("chat");
    private TextBox _txtChat;
    private Label _lblChatCount;
    private RCheck _chkChatImmediate, _chkChatSound, _chkChatTyping;
    private ComboBox _cmbChatMode;
    private RNumber _nudChatInterval, _nudChatCount;
    private RButton _btnChatSend;
    private ListBox _lstChatHistory;
    private string _chatText = "";

    // ---------------- カスタム ----------------
    private readonly ActionRunner _customRunner = new("custom");
    private TextBox _txtCustAddr, _txtCustVal, _txtCustReset, _txtCustName;
    private ComboBox _cmbCustType, _cmbCustMode;
    private RNumber _nudCustTime, _nudCustCount;
    private RButton _btnCustRun, _btnCustLock;
    private ListBox _lstCustPresets;
    private volatile CustomPreset[] _customSnapshot = Array.Empty<CustomPreset>(); // マクロ (別スレッド) 用
    private string _custAddrRunning = "";

    // ---------------- コンボ ----------------
    private CheckedListBox _clbCombo;
    private RNumber _nudComboDelay;
    private RCheck _chkComboLoop;
    private ComboBox _cmbComboPreset;
    private CancellationTokenSource _comboCts;
    private bool _comboRunning;
    private int _comboIndex, _comboTotal, _comboLoop;

    // ---------------- マクロ / パラメータ / ログ ----------------
    private MacroPage _macroPage;
    private CameraPanel _cameraPanel;
    private readonly VrcMonitor _monitor = new();
    private StateBoard _stateBoard;
    private Form _logWindow;
    private Panel _logPage;
    private Control _logPlaceholder;
    private ParamPanel _paramPanel;
    private LogViewer _logViewer;

    // ---------------- Win32 ----------------
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    private static readonly uint _pid = (uint)Environment.ProcessId;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HK_STOP = 1, HK_OUTPUT = 2, HK_MACRO_TOGGLE = 3, HK_COMBO_TOGGLE = 4, HK_CAT_BASE = 50, HK_MACRO_BASE = 100;

    private sealed class ComboItem
    {
        public ActionRow Row;
        public override string ToString() => $"[{Row.Def.Category}] {Row.Def.Name}";
    }

    public const string AppName = "VRC OSC Debug Client";

    private static readonly string[] FontChoices = { "Meiryo UI", "Yu Gothic UI", "BIZ UDPGothic", "MS UI Gothic", "Segoe UI" };

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        // UI の大きさ (%) は文字と各部の大きさの両方にかける
        float zoom = Math.Clamp(_s.UiZoom, 70, 130) / 100f;
        Font = CreateUiFont(_s.FontName, Math.Clamp(_s.FontSize, 7f, 16f) * zoom);
        Ui.Scale = Math.Clamp(_s.FontSize, 7f, 16f) / 9f * zoom;
        Ui.SmallFont = new Font(Font.FontFamily, Math.Max(7f, Font.Size * 0.84f)); // 説明文は小さめ
        Text = AppName;
        ClientSize = new Size(Ui.W(1170), Ui.W(593)); // UI 90% で 約 1050×535
        MinimumSize = new Size(Ui.W(420), Ui.W(340));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        Theme.Dark = _s.Dark;
        _arb = new InputArbiter(_osc);
        _monitor.Event += (kind, text) => _activity?.AddHistory(kind, text);
        _monitor.AvatarName = id => _paramPanel?.AvatarName(id);
        _locker = new ValueLocker(_osc);
        _locker.Changed += () => BeginInvokeSafe(() =>
        {
            RefreshLockColumn();
            UpdateCustomLockButton();
        });
        _stopKey = Hotkey.Parse(_s.StopHotkey);
        _outputKey = Hotkey.Parse(_s.OutputHotkey);
        Localize.Hook(_tip); // 英語表示ならツールチップも訳す

        InitPresetStores(); // マクロ・コンボのファイルを読み込む (ページを作る前に)
        BuildLayout();
        ResumeLayout(false);
        PerformLayout();

        _osc.MessageReceived += OnOscReceived;
        FormClosed += (_, _) => StopOscQuery();
        _osc.MessageSent += OnOscSent;
        _osc.Error += msg => { _lastError = msg; OpLog.Write("⚠ " + msg); };

        _uiTimer.Tick += (_, _) => UiTick();
        _uiTimer.Start();
        _pollTimer.Tick += (_, _) => PollStopKey();
        _pollTimer.Start();

        HotkeyBox.CapturingChanged += OnHotkeyCapturing;

        RestoreWindow();
        Theme.Apply(this);
        ApplyThemeExtras();
    }

    private static Font CreateUiFont(string name, float size)
    {
        size = Math.Clamp(size, 7f, 16f);
        try
        {
            var f = new Font(name, size);
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
        }
        catch { }
        return new Font("Yu Gothic UI", size);
    }

    // =====================================================================
    // レイアウト
    // =====================================================================
    private void BuildLayout()
    {
        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 2, 2, 2) };

        // ステータスバー (文字の高さに合わせて自動)
        var statusBar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 1, Padding = new Padding(6, 2, 6, 3)
        };
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _status = new FastLabel { Dock = DockStyle.Fill, Sub = true, Margin = new Padding(0) };
        statusBar.Controls.Add(_status, 0, 0);

        // タブ: 全タブを 1 つの部品で描く軽量なタブ (均等な幅、ボタン表示はしない)
        _tabs = new TabStrip { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0) };
        _tabs.SelectedChanged += i => ShowPage(_tabs.Items[i]);

        // ---- 接続 (部品は「接続」タブに置く) ----
        _txtHost = Ui.Txt(_s.Host, 100);
        _nudSend = Ui.Nud(1, 65535, _s.SendPort, 66);
        _nudRecv = Ui.Nud(1, 65535, _s.RecvPort, 66);
        _chkListen = Ui.Chk("受信", _s.Listen);
        _btnConnect = Ui.Btn("接続", 56);
        _lamp = new LLabel { Text = "●", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 3, 0, 3), Tag = "keep" };
        _chkAutoConnect = Ui.Chk("自動接続", _s.AutoConnect);
        _tip.SetToolTip(_txtHost, "VRChat を動かしている PC の IP (同じPCなら 127.0.0.1)");
        _tip.SetToolTip(_nudSend, "送信ポート (VRChat の受信ポート / 既定 9000)");
        _tip.SetToolTip(_nudRecv, "受信ポート (VRChat の送信先ポート / 既定 9001)");
        _tip.SetToolTip(_chkListen, "VRChat からの OSC (アバターパラメータ等) を受信する");
        _tip.SetToolTip(_chkAutoConnect, "起動時に自動接続");
        _btnConnect.Click += (_, _) => ToggleConnect();

        // ---- 左上: 全体操作 (大きめのボタン) ----
        _btnStop = Ui.Btn("■ 全停止", 70);
        _btnStop.Tag = "keep";
        _btnStop.Font = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold);
        _chkOutput = Ui.Toggle("出力 ON", 70);
        _chkOutput.Checked = true;
        _chkSide = Ui.Toggle("動作パネル");
        _chkSide.Checked = _s.ShowSidePanel;
        _chkTop = Ui.Chk("最前面", _s.TopMost);
        _trkOpacity = new RSlider
        {
            Minimum = 20, Maximum = 100, Value = Math.Clamp(_s.Opacity, 20, 100), TickStyle = TickStyle.None,
            AutoSize = false, Width = Ui.W(80), Height = 26, Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 0, 0),
            SmallChange = 5, LargeChange = 10
        };
        _lblOpacity = Ui.Sub("100%");
        _btnTheme = Ui.Btn("", 64);
        // 最前面・透過・ライト/ダークは「設定」タブへ移動
        _tip.SetToolTip(_chkOutput, "OFF: 全停止したうえで、以降の送信をすべて遮断");
        _tip.SetToolTip(_chkSide, "右側の動作パネル (動作中の操作・操作ログ) の表示切替");
        _tip.SetToolTip(_trkOpacity, "ウィンドウの不透明度 (20〜100%)");

        _btnStop.Click += (_, _) => StopAll("ボタン");
        _chkOutput.CheckedChanged += (_, _) =>
        {
            if (!_chkOutput.Checked) StopAll("出力OFF");
            _osc.OutputEnabled = _chkOutput.Checked;
            OpLog.Write(_chkOutput.Checked ? "🔈 出力 ON" : "🔇 出力 OFF (送信を遮断)");
            UpdateOutputUi();
        };
        _chkSide.CheckedChanged += (_, _) => UpdateSideVisible();
        _chkTop.CheckedChanged += (_, _) => UpdateTopMost();
        _trkOpacity.ValueChanged += (_, _) => ApplyOpacity();
        _btnTheme.Click += (_, _) =>
        {
            Theme.Dark = !Theme.Dark;
            Theme.Apply(this);
            if (_logWindow != null) Theme.Apply(_logWindow);
            ApplyTitleBar(true);
            ApplyThemeExtras();
        };
        TopMost = _chkTop.Checked;
        ApplyOpacity();
        Ui.ModalStateChanged += UpdateTopMost;
        Activated += (_, _) => UpdateTopMost();
        // 別ウィンドウに切り替わった直後に判定 (新しいウィンドウが前面になってから)
        Deactivate += (_, _) => BeginInvokeSafe(UpdateTopMost);

        // ---- 右: 動作パネル ----
        _activity = new ActivityPanel(_s, _tip);
        _sideHost = new Panel { Dock = DockStyle.Right, Width = Ui.W(Math.Clamp(_s.SidePanelWidth, 180, 600)), Padding = new Padding(0, 2, 4, 2) };
        _sideHost.Controls.Add(_activity);
        _splitter = new Splitter { Dock = DockStyle.Right, Width = 6, MinSize = Ui.W(170), MinExtra = Ui.W(300) };

        // ---- 右上: 現在の状態 ----
        _stateBoard = new StateBoard { Dock = DockStyle.Fill };
        _tip.SetToolTip(_stateBoard, "VRChat から受信した現在の状態\n移動 (向き・速さ) / 状態 (VR・デスクトップ) / マイク / 身長 (目の高さ) / ジェスチャー (右・左) / AFK");
        // 左: 1 段目に全停止 (横いっぱい)、2 段目に出力・動作パネル。高さは右の状態表示に合わせて広がる
        var leftStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 4, 0), Padding = new Padding(0),
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        leftStack.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.W(118)));
        leftStack.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.W(118)));
        leftStack.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        leftStack.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        foreach (Control b in new Control[] { _btnStop, _chkOutput, _chkSide })
        {
            b.Dock = DockStyle.Fill;
            b.AutoSize = false;
            b.Margin = new Padding(2);
        }
        leftStack.Controls.Add(_btnStop, 0, 0);
        leftStack.SetColumnSpan(_btnStop, 2);
        leftStack.Controls.Add(_chkOutput, 0, 1);
        leftStack.Controls.Add(_chkSide, 1, 1);
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Padding = new Padding(4, 3, 4, 0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(leftStack, 0, 0);
        header.Controls.Add(_stateBoard, 1, 0);

        // ---- ページ (並び: 接続 → 操作 → カメラ → システム・パラメータ → 自動化 → 確認・設定) ----
        BuildConnectionPage();
        BuildActionPage(ActionCatalog.CatMove);
        BuildActionPage(ActionCatalog.CatLook);
        BuildActionPage(ActionCatalog.CatHand);
        BuildChatPage();
        BuildCameraPage();
        BuildActionPage(ActionCatalog.CatSystem);
        BuildParamPage();
        BuildMacroPage();
        BuildComboPage();
        BuildCustomPage();
        BuildRecvPage();
        BuildLogPage();
        BuildSettingsPage();

        // Dock 順: Fill → Right → Bottom → Top(下から順に)
        Controls.Add(_content);
        Controls.Add(_splitter);
        Controls.Add(_sideHost);
        Controls.Add(statusBar);
        Controls.Add(_tabs);
        Controls.Add(header);
        UpdateSideVisible();

        // 起動時は必ず「接続」タブを開く
        _tabs.Select(ConnectionPage);
    }

    /// <summary>上部の 1 行 (左側に並べる)</summary>
    private static FlowLayoutPanel HeaderRow() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
        Margin = new Padding(0, 0, 0, 1), Padding = new Padding(0)
    };

    private void UpdateSideVisible()
    {
        _sideHost.Visible = _chkSide.Checked;
        _splitter.Visible = _chkSide.Checked;
    }

    private Panel NewPage(string key)
    {
        var page = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(2) };
        _pages[key] = page;
        _content.Controls.Add(page);

        _tabs.Add(key);
        return page;
    }

    private void ShowPage(string key)
    {
        // 表示を切り替える間はレイアウトをまとめて 1 回にする
        _content.SuspendLayout();
        if (_pages.TryGetValue(key, out var next)) next.Visible = true;
        foreach (var kv in _pages)
            if (kv.Key != key && kv.Value.Visible) kv.Value.Visible = false;
        _content.ResumeLayout();
        _s.LastPage = key;
    }

    // =====================================================================
    // 操作ページ (移動 / 視点 / 手・アイテム / システム)
    // =====================================================================
    private void BuildActionPage(string cat)
    {
        var page = NewPage(cat);
        var list = Ui.PageList(page);

        var stop = Ui.Btn("カテゴリ停止");
        stop.Click += (_, _) =>
        {
            foreach (var r in _rows.Where(r => r.Def.Category == cat)) r.Runner.Stop();
            OpLog.Write($"■ カテゴリ停止: {cat}");
        };
        _s.CategoryHotkeys.TryGetValue(cat, out var hkText);
        var hk = new HotkeyBox { Width = Ui.W(130), Anchor = AnchorStyles.Left, Margin = Ui.InputMargin };
        hk.Hotkey = Hotkey.Parse(hkText);
        hk.HotkeyChanged += (_, _) => _s.CategoryHotkeys[cat] = hk.Hotkey.ToString();
        var run = Ui.Btn("ON/OFF");
        run.Click += (_, _) => ToggleCategory(cat, "ボタン");
        _tip.SetToolTip(hk, "このカテゴリの ON/OFF キー\nON: 「対象」にチェックした操作をまとめて開始 / OFF: このカテゴリを全停止");
        _tip.SetToolTip(run, "「対象」にチェックした操作をまとめて開始 (動作中なら全停止)");
        var tools = Ui.Row(stop, run, Ui.L("キー"), hk);

        var table = new ActionTable();
        table.SuspendLayout();
        foreach (var def in ActionCatalog.All.Where(d => d.Category == cat))
        {
            var row = new ActionRow(def, _arb, _tip);
            if (_s.Rows.TryGetValue(def.Id, out var rs)) row.ApplySetting(rs);
            _rows.Add(row);
            table.AddRow(row);
        }
        table.ResumeLayout();

        var cardItems = new List<Control> { tools };
        if (ActionCatalog.Hints.TryGetValue(cat, out var hint)) cardItems.Add(Ui.Sub(hint));
        cardItems.Add(Ui.Stretch(table));
        list.Controls.Add(Ui.WideCard(cardItems.ToArray()));

        if (cat == ActionCatalog.CatSystem)
        {
            var on = Ui.Btn("マイクON");
            var off = Ui.Btn("マイクOFF");
            _lblMicSys = StatusLabel();
            on.Click += (_, _) => SetMic(true);
            off.Click += (_, _) => SetMic(false);
            _tip.SetToolTip(on, "受信した MuteSelf を見て、ミュート中なら Voice を1回押す (トグル設定時)");
            _tip.SetToolTip(off, "受信した MuteSelf を見て、ON なら Voice を1回押す (トグル設定時)");
            list.Controls.Add(Ui.WideCard(
                Ui.Row(on, off, _lblMicSys),
                Ui.Sub("※ マイク状態の取得には「受信」を有効にして接続してください"),
                Ui.Sub("※ セーフモードは周囲のアバターを全非表示にします")));
            list.Controls.Add(BuildScaleCard());
        }
    }

    /// <summary>カテゴリの ON/OFF (動作中なら全停止、止まっていれば「対象」をまとめて開始)</summary>
    private void ToggleCategory(string cat, string by)
    {
        var rows = _rows.Where(r => r.Def.Category == cat).ToList();
        if (rows.Any(r => r.Runner.IsRunning))
        {
            foreach (var r in rows) r.Runner.Stop();
            OpLog.Write($"⌨ {cat} OFF ({by})");
            return;
        }
        var marked = rows.Where(r => r.Marked).ToList();
        if (marked.Count == 0)
        {
            OpLog.Write($"⌨ {cat}: 「対象」にチェックされた操作がありません");
            return;
        }
        OpLog.Write($"⌨ {cat} ON ({by})  {string.Join(", ", marked.Select(r => r.Def.Name))}");
        foreach (var r in marked) r.Trigger();
    }

    /// <summary>カテゴリ操作: ON = 対象を開始 / OFF = 停止 / 切替</summary>
    private void CategoryCommand(string cat, string cmd, string by)
    {
        if (!ActionCatalog.Categories.Contains(cat)) return;
        var rows = _rows.Where(r => r.Def.Category == cat).ToList();
        switch ((cmd ?? "").Trim().ToUpperInvariant())
        {
            case "ON":
                var marked = rows.Where(r => r.Marked && !r.Runner.IsRunning).ToList();
                foreach (var r in marked) r.Trigger();
                OpLog.Write($"⌨ {cat} ON ({by})  {marked.Count} 件");
                break;
            case "OFF":
                foreach (var r in rows) r.Runner.Stop();
                OpLog.Write($"⌨ {cat} OFF ({by})");
                break;
            default:
                ToggleCategory(cat, by);
                break;
        }
    }

    private void SetMic(bool wantOn)
    {
        var voice = _rows.FirstOrDefault(r => r.Def.Id == "Voice");
        if (voice == null) return;
        int mute = Volatile.Read(ref _muteSelf);
        if (mute < 0)
        {
            Ui.Msg(this, "マイク状態が不明です。\n「受信」を有効にして接続し、VRChat 側で OSC を有効にしてください。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        bool isOn = mute == 0;
        if (isOn != wantOn) voice.StartWith(PressMode.Tap, 100, 0);
    }

    // =====================================================================
    // パラメータ
    // =====================================================================
    private void BuildParamPage()
    {
        var page = NewPage("パラメータ");
        _paramPanel = new ParamPanel(_osc, _locker, _s, _tip);
        _paramPanel.SendToCustom += SendParamToCustom;
        _paramPanel.AddToCustomPreset += AddCustomPreset;
        _paramPanel.AddToMacro += (addr, type, value) =>
        {
            _macroPage.AddSendStep(addr, type, value);
            _tabs.Select("マクロ");
        };
        page.Controls.Add(_paramPanel);
    }

    // =====================================================================
    // カメラ
    // =====================================================================
    private void BuildCameraPage()
    {
        var page = NewPage("カメラ");
        _cameraPanel = new CameraPanel(_osc, _s, _tip);
        page.Controls.Add(_cameraPanel);
    }

    // =====================================================================
    // チャット
    // =====================================================================
    private void BuildChatPage()
    {
        var page = NewPage("チャット");
        var list = Ui.PageList(page);

        _txtChat = new TextBox
        {
            Multiline = true, Width = Ui.W(430), Height = Ui.W(64), ScrollBars = ScrollBars.Vertical,
            MaxLength = 144, Margin = Ui.InputMargin, AcceptsReturn = false
        };
        _lblChatCount = Ui.Sub("0 / 144");
        _txtChat.TextChanged += (_, _) => _lblChatCount.Text = $"{_txtChat.TextLength} / 144";
        _txtChat.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (e.Shift)
            {
                int p = _txtChat.SelectionStart;
                _txtChat.SelectedText = "\r\n";
                _txtChat.SelectionStart = p + 2;
            }
            else ChatSendClicked();
        };
        _tip.SetToolTip(_txtChat, "Enter: 送信 / Shift+Enter: 改行 (最大144文字)");

        _chkChatImmediate = Ui.Chk("即時表示", _s.ChatImmediate);
        _chkChatSound = Ui.Chk("通知音", _s.ChatSound);
        _chkChatTyping = Ui.Chk("入力中表示");
        var opt = Ui.Row(_chkChatImmediate, _chkChatSound, _chkChatTyping);
        _tip.SetToolTip(_chkChatImmediate, "OFF: VRChat のキーボードに入力されるだけ (送信しない)");
        _tip.SetToolTip(_chkChatSound, "チャットボックスの通知音を鳴らす");
        _tip.SetToolTip(_chkChatTyping, "頭上に入力中インジケーターを表示 (/chatbox/typing)");
        _chkChatTyping.CheckedChanged += (_, _) => _osc.Send("/chatbox/typing", _chkChatTyping.Checked);

        _cmbChatMode = Ui.Combo(66, false, "1回", "連打");
        _nudChatInterval = Ui.Nud(1500, 600000, _s.ChatInterval, 72, 500);
        _nudChatCount = Ui.Nud(0, 99999, 0, 56);
        _btnChatSend = Ui.Btn("送信", 56);
        _btnChatSend.FaceText = "送信";
        var clear = Ui.Btn("消去");
        var mode = Ui.Row(_cmbChatMode, Ui.L("間隔ms"), _nudChatInterval, Ui.L("回数"), _nudChatCount, _btnChatSend, clear);
        _tip.SetToolTip(_nudChatInterval, "連打間隔 (VRChat のレート制限対策で最小 1500ms)");
        _tip.SetToolTip(_nudChatCount, "連打回数 (0 = 停止するまで)");
        _tip.SetToolTip(clear, "チャットボックスを空にする");
        _btnChatSend.Click += (_, _) => ChatSendClicked();
        clear.Click += (_, _) =>
        {
            _chatRunner.Stop();
            _osc.Send("/chatbox/input", "", true, false);
            OpLog.Write("💬 チャット消去");
        };
        _chatRunner.StateChanged += running =>
        {
            if (!running) OpLog.Write("■ チャット連打 終了");
            BeginInvokeSafe(UpdateChatButton);
        };

        _lstChatHistory = new ListBox { IntegralHeight = false, HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
        // 旧形式 (文字だけ) の履歴を取り込む
        if (_s.ChatLog.Count == 0 && _s.ChatHistory.Count > 0)
            _s.ChatLog = _s.ChatHistory.Select(t => new ChatEntry { Text = t }).ToList();
        foreach (var h in _s.ChatLog) _lstChatHistory.Items.Add(h);
        _lstChatHistory.DoubleClick += (_, _) => ReuseChat();
        _lstChatHistory.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { RemoveSelectedChat(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.C) { CopyChat(false); e.SuppressKeyPress = true; }
        };
        _tip.SetToolTip(_lstChatHistory, "ダブルクリック: 再利用 / Ctrl+C: テキストをコピー / 右クリック: コピー・削除 / Delete: 削除");

        var chatMenu = new ContextMenuStrip();
        var cmReuse = new ToolStripMenuItem("入力欄に戻す (再利用)");
        var cmCopy = new ToolStripMenuItem("テキストをコピー");
        var cmCopyTime = new ToolStripMenuItem("送信時間付きでコピー");
        var cmCopyAll = new ToolStripMenuItem("履歴を全てコピー (時間付き)");
        var cmDelete = new ToolStripMenuItem("削除");
        var cmClear = new ToolStripMenuItem("履歴を全て消去");
        chatMenu.Items.AddRange(new ToolStripItem[] { cmReuse, new ToolStripSeparator(), cmCopy, cmCopyTime, cmCopyAll, new ToolStripSeparator(), cmDelete, cmClear });
        chatMenu.Opening += (_, _) =>
        {
            bool any = _lstChatHistory.SelectedItems.Count > 0;
            cmReuse.Enabled = cmCopy.Enabled = cmCopyTime.Enabled = cmDelete.Enabled = any;
            ThemedMenu.Apply(chatMenu);
        };
        cmReuse.Click += (_, _) => ReuseChat();
        cmCopy.Click += (_, _) => CopyChat(false);
        cmCopyTime.Click += (_, _) => CopyChat(true);
        cmCopyAll.Click += (_, _) =>
            CopyText(string.Join(Environment.NewLine, _lstChatHistory.Items.Cast<ChatEntry>().Select(ChatLine)));
        cmDelete.Click += (_, _) => RemoveSelectedChat();
        cmClear.Click += (_, _) =>
        {
            if (Ui.Msg(this, "送信履歴を全て消去しますか？", Text, MessageBoxButtons.OKCancel) == DialogResult.OK)
                _lstChatHistory.Items.Clear();
        };
        _lstChatHistory.ContextMenuStrip = chatMenu;
        _lstChatHistory.MouseDown += (_, e) =>
        {
            // 右クリックした行を選択してからメニューを出す
            if (e.Button != MouseButtons.Right) return;
            int idx = _lstChatHistory.IndexFromPoint(e.Location);
            if (idx >= 0 && !_lstChatHistory.GetSelected(idx))
            {
                _lstChatHistory.ClearSelected();
                _lstChatHistory.SetSelected(idx, true);
            }
        };

        list.Controls.Add(Ui.WideCard(BuildChatTools()));
        list.Controls.Add(Ui.WideCard(Ui.Stretch(_txtChat), _lblChatCount, opt, mode));
        list.Controls.Add(BuildChatPresetCard());
        list.Controls.Add(Ui.WideCard(Ui.Sub("送信履歴 (右クリックでコピー等)"), Ui.Stretch(Ui.SizedHost(_lstChatHistory, 430, 220))));
    }

    private void ChatSendClicked()
    {
        if (_chatRunner.IsRunning) { _chatRunner.Stop(); return; }
        StartChat(_txtChat.Text, true);
    }

    /// <summary>チャットを送信 (モードが「連打」なら連打を開始)。fromInput = 入力欄の文字 (送信後に消す)</summary>
    private void StartChat(string raw, bool fromInput)
    {
        _chatRunner.Stop();
        string text = (raw ?? "").Replace("\r\n", "\n");
        if (text.Length > 144) text = text.Substring(0, 144);
        if (text.Length == 0) return;
        bool immediate = _chkChatImmediate.Checked;
        bool sound = _chkChatSound.Checked;
        AddChatHistory(text);
        _chatText = text.Replace("\n", " ");

        if (_cmbChatMode.SelectedIndex == 0)
        {
            OpLog.Write($"💬 チャット送信: {_chatText}");
            _osc.Send("/chatbox/input", text, immediate, sound);
            if (fromInput) _txtChat.Clear();
        }
        else
        {
            OpLog.Write($"▶ チャット連打: {_chatText}");
            _chatRunner.Start(PressMode.Rapid, (int)_nudChatInterval.Value, (int)_nudChatCount.Value, on =>
            {
                if (on) _osc.Send("/chatbox/input", text, immediate, sound);
            });
        }
    }

    private void UpdateChatButton()
    {
        bool run = _chatRunner.IsRunning;
        _btnChatSend.FaceText = run ? "停止" : "送信";
        _btnChatSend.BackColor = run ? Theme.Active : Theme.ButtonBack;
    }

    /// <summary>送信履歴に追加 (送信時間付き。新しいものが上、最大 200 件)</summary>
    private void AddChatHistory(string text)
    {
        _lstChatHistory.Items.Insert(0, new ChatEntry { Time = DateTime.Now, Text = text });
        while (_lstChatHistory.Items.Count > 200) _lstChatHistory.Items.RemoveAt(_lstChatHistory.Items.Count - 1);
    }

    private static string ChatLine(ChatEntry e) =>
        (e.Time == default ? "" : e.Time.ToString("yyyy/MM/dd HH:mm:ss") + "\t") + e.Text;

    private void CopyChat(bool withTime)
    {
        var items = _lstChatHistory.SelectedItems.Cast<ChatEntry>().ToList();
        if (items.Count == 0) return;
        CopyText(string.Join(Environment.NewLine, items.Select(e => withTime ? ChatLine(e) : e.Text)));
    }

    private void ReuseChat()
    {
        if (_lstChatHistory.SelectedItem is not ChatEntry e) return;
        _txtChat.Text = (e.Text ?? "").Replace("\n", "\r\n");
        _txtChat.Focus();
        _txtChat.SelectionStart = _txtChat.TextLength;
    }

    private void RemoveSelectedChat()
    {
        foreach (var e in _lstChatHistory.SelectedItems.Cast<ChatEntry>().ToList()) _lstChatHistory.Items.Remove(e);
    }

    // =====================================================================
    // カスタム (任意アドレス)
    // =====================================================================
    private void BuildCustomPage()
    {
        var page = NewPage("カスタム");
        var list = Ui.PageList(page);

        _txtCustAddr = Ui.Txt("/avatar/parameters/", 300);
        var pasteParam = Ui.Btn("📋 貼り付け");
        var r1 = Ui.Row(Ui.L("アドレス"), _txtCustAddr, pasteParam);
        _tip.SetToolTip(pasteParam,
            "クリップボードのパラメータを貼り付け\n" +
            "対応: パラメータタブ・受信タブの「名前・型・値をコピー」/ 「名前=値」/ アドレスや名前だけ");
        pasteParam.Click += (_, _) => PasteParamToCustom();

        _cmbCustType = Ui.Combo(70, false, OscValue.Types);
        _txtCustVal = Ui.Txt("true", 84);
        _txtCustReset = Ui.Txt("false", 84);
        var r2 = Ui.Row(Ui.L("型"), _cmbCustType, Ui.L("値"), _txtCustVal, Ui.L("戻り値"), _txtCustReset);
        _tip.SetToolTip(_txtCustReset, "1回 / 長押し / 連打 の解除時に送る値");
        _cmbCustType.SelectedIndexChanged += (_, _) =>
        {
            switch (_cmbCustType.SelectedItem as string)
            {
                case "Bool": _txtCustVal.Text = "true"; _txtCustReset.Text = "false"; break;
                case "Int": _txtCustVal.Text = "1"; _txtCustReset.Text = "0"; break;
                case "Float": _txtCustVal.Text = "1.0"; _txtCustReset.Text = "0.0"; break;
                default: _txtCustVal.Text = "text"; _txtCustReset.Text = ""; break;
            }
        };

        _cmbCustMode = Ui.Combo(70, false, ActionRow.ModeNames);
        _nudCustTime = Ui.Nud(0, 600000, 100, 68, 50);
        _nudCustCount = Ui.Nud(0, 99999, 0, 56);
        _nudCustCount.Enabled = false;
        _btnCustRun = Ui.Btn("▶", 36);
        _btnCustRun.FaceText = "▶";
        var sendOnce = Ui.Btn("値のみ送信");
        _btnCustLock = Ui.Btn("🔓 固定を解除");
        _btnCustLock.FaceText = "🔒 固定";
        var r3 = Ui.Row(_cmbCustMode, Ui.L("ms"), _nudCustTime, Ui.L("回数"), _nudCustCount, _btnCustRun, sendOnce, _btnCustLock);
        _tip.SetToolTip(_btnCustLock, "このアドレスを「値」で固定 (VRChat 側で変わっても戻す) / 固定中なら解除");
        _btnCustLock.Click += (_, _) =>
        {
            string a = _txtCustAddr.Text.Trim();
            if (!a.StartsWith("/")) { Ui.Msg(this, "アドレスは / から始めてください", Text); return; }
            if (_locker.IsLocked(a)) _locker.Unlock(a);
            else if (TryParseCustom(_txtCustVal.Text, out var lv)) _locker.Lock(a, lv);
        };
        _txtCustAddr.TextChanged += (_, _) => UpdateCustomLockButton();
        _tip.SetToolTip(sendOnce, "値を1回だけ送信 (戻り値は送らない)");
        _tip.SetToolTip(_nudCustTime, "1回=押下時間 / 長押し=保持時間(0=無限) / 連打=間隔");
        _cmbCustMode.SelectedIndexChanged += (_, _) =>
        {
            _nudCustTime.Value = _cmbCustMode.SelectedIndex switch { 0 => 100, 1 => 0, 2 => 200, _ => _nudCustTime.Value };
            _nudCustCount.Enabled = _cmbCustMode.SelectedIndex == 2;
        };

        _btnCustRun.Click += (_, _) =>
        {
            if (_cmbCustMode.SelectedIndex == (int)PressMode.Momentary) return;
            if (_customRunner.IsRunning) _customRunner.Stop();
            else StartCustom((PressMode)_cmbCustMode.SelectedIndex);
        };
        _btnCustRun.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _cmbCustMode.SelectedIndex == (int)PressMode.Momentary) StartCustom(PressMode.Hold, 0);
        };
        _btnCustRun.MouseUp += (_, _) =>
        {
            if (_cmbCustMode.SelectedIndex == (int)PressMode.Momentary) _customRunner.Stop();
        };
        sendOnce.Click += (_, _) =>
        {
            if (!TryParseCustom(_txtCustVal.Text, out var v)) return;
            OpLog.Write($"📤 カスタム送信: {_txtCustAddr.Text.Trim()} = {OscMessage.FormatArg(v)}");
            _osc.Send(_txtCustAddr.Text.Trim(), v);
        };
        _customRunner.StateChanged += running =>
        {
            if (!running) OpLog.Write($"■ カスタム 終了: {_custAddrRunning}");
            BeginInvokeSafe(() =>
            {
                bool run = _customRunner.IsRunning;
                _btnCustRun.FaceText = run ? "■" : "▶";
                _btnCustRun.BackColor = run ? Theme.Active : Theme.ButtonBack;
            });
        };

        // プリセット
        _txtCustName = Ui.Txt("", 150);
        _txtCustName.PlaceholderText = "プリセット名";
        var save = Ui.Btn("保存");
        var del = Ui.Btn("削除");
        var bulk = Ui.Btn("📋 一括追加");
        var r4 = Ui.Row(_txtCustName, save, del, bulk);
        _tip.SetToolTip(bulk, "クリップボードの複数パラメータ (1行1つ) をまとめてプリセットに追加\nパラメータタブの「全コピー」の内容をそのまま使えます");
        bulk.Click += (_, _) => BulkAddPresets();
        _lstCustPresets = new ListBox { IntegralHeight = false };
        foreach (var p in _s.Customs) _lstCustPresets.Items.Add(p);
        _tip.SetToolTip(_lstCustPresets, "クリック: 読込 / ダブルクリック: 読込して実行");

        save.Click += (_, _) =>
        {
            string name = string.IsNullOrWhiteSpace(_txtCustName.Text) ? _txtCustAddr.Text.Split('/').Last() : _txtCustName.Text.Trim();
            var preset = new CustomPreset
            {
                Name = name, Address = _txtCustAddr.Text.Trim(), Type = (string)_cmbCustType.SelectedItem,
                Value = _txtCustVal.Text, ResetValue = _txtCustReset.Text
            };
            var existing = _lstCustPresets.Items.Cast<CustomPreset>().FirstOrDefault(p => p.Name == name);
            if (existing != null) _lstCustPresets.Items.Remove(existing);
            _lstCustPresets.Items.Add(preset);
            UpdateCustomSnapshot();
        };
        del.Click += (_, _) =>
        {
            if (_lstCustPresets.SelectedItem != null) _lstCustPresets.Items.Remove(_lstCustPresets.SelectedItem);
            UpdateCustomSnapshot();
        };
        UpdateCustomSnapshot();
        _lstCustPresets.SelectedIndexChanged += (_, _) =>
        {
            if (_lstCustPresets.SelectedItem is not CustomPreset p) return;
            _txtCustName.Text = p.Name;
            _txtCustAddr.Text = p.Address;
            _cmbCustType.SelectedItem = p.Type;
            _txtCustVal.Text = p.Value;
            _txtCustReset.Text = p.ResetValue;
        };
        _lstCustPresets.DoubleClick += (_, _) =>
        {
            if (_lstCustPresets.SelectedItem is CustomPreset && _cmbCustMode.SelectedIndex != (int)PressMode.Momentary)
                StartCustom((PressMode)_cmbCustMode.SelectedIndex);
        };

        list.Controls.Add(Ui.WideCard(r1, r2, r3,
            Ui.Sub("例: /avatar/parameters/パラメータ名  (受信タブの行をダブルクリックで入力)"),
            Ui.Sub("※ アバターのパラメータは「パラメータ」タブの方が簡単に操作できます")));
        list.Controls.Add(Ui.WideCard(Ui.Sub("プリセット"), r4, Ui.Stretch(Ui.SizedHost(_lstCustPresets, 430, 200))));
    }

    private void UpdateCustomLockButton()
    {
        if (_btnCustLock == null) return;
        bool locked = _locker.IsLocked(_txtCustAddr.Text.Trim());
        _btnCustLock.FaceText = locked ? "🔓 固定を解除" : "🔒 固定";
        _btnCustLock.BackColor = locked ? Theme.Accent : Theme.ButtonBack;
        _btnCustLock.ForeColor = locked ? Color.White : Theme.Fore;
    }

    /// <summary>パラメータ1つをカスタムタブの入力欄へ</summary>
    private void SendParamToCustom(string addr, string type, string value)
    {
        if (string.IsNullOrWhiteSpace(addr)) return;
        _txtCustAddr.Text = addr;
        if (type != null && OscValue.Types.Contains(type)) _cmbCustType.SelectedItem = type; // 戻り値も型に合わせて初期化される
        if (!string.IsNullOrEmpty(value)) _txtCustVal.Text = value;
        _txtCustName.Text = ValueLocker.Short(addr);
        _tabs.Select("カスタム");
    }

    /// <summary>カスタムのプリセットに追加 (同名は上書き)</summary>
    private void AddCustomPreset(string name, string addr, string type, string value)
    {
        if (string.IsNullOrWhiteSpace(addr)) return;
        type = OscValue.Types.Contains(type) ? type : (ValueLocker.InferType(value) ?? "Float");
        var preset = new CustomPreset
        {
            Name = string.IsNullOrWhiteSpace(name) ? ValueLocker.Short(addr) : name,
            Address = addr, Type = type,
            Value = string.IsNullOrEmpty(value) ? ValueLocker.RawText(OscValue.Zero(type)) : value,
            ResetValue = ValueLocker.RawText(OscValue.Zero(type))
        };
        var existing = _lstCustPresets.Items.Cast<CustomPreset>().FirstOrDefault(p => p.Name == preset.Name);
        if (existing != null) _lstCustPresets.Items.Remove(existing);
        _lstCustPresets.Items.Add(preset);
        UpdateCustomSnapshot();
        OpLog.Write($"＋ カスタムのプリセットに追加: {preset.Name} = {preset.Value}");
    }

    /// <summary>
    /// クリップボードの1行をパラメータとして解釈する。
    /// 「名前/アドレス [Tab] 型 [Tab] 値」「名前/アドレス [Tab] 値」「名前=値」「名前/アドレスだけ」に対応
    /// </summary>
    private static bool ParseParamLine(string line, out string name, out string addr, out string type, out string value)
    {
        name = addr = type = value = null;
        line = (line ?? "").Trim('\r', ' ', '\t');
        if (line.Length == 0) return false;

        string key;
        var tab = line.Split('\t');
        if (tab.Length >= 3) { key = tab[0]; type = tab[1].Trim(); value = tab[2].Trim(); }
        else if (tab.Length == 2) { key = tab[0]; value = tab[1].Trim(); }
        else if (line.Contains('=')) { key = line[..line.IndexOf('=')]; value = line[(line.IndexOf('=') + 1)..].Trim(); }
        else key = line;

        key = key.Trim();
        if (key.Length == 0 || key.Contains(' ')) return false;
        addr = key.StartsWith("/") ? key : "/avatar/parameters/" + key;
        name = ValueLocker.Short(addr);
        if (!OscValue.Types.Contains(type)) type = ValueLocker.InferType(value);
        return true;
    }

    private static string ClipboardText()
    {
        try { return Clipboard.GetText() ?? ""; } catch { return ""; }
    }

    private void PasteParamToCustom()
    {
        var line = ClipboardText().Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
        if (line == null || !ParseParamLine(line, out var name, out var addr, out var type, out var value))
        {
            Ui.Msg(this, "クリップボードにパラメータがありません。\nパラメータタブや受信タブで右クリック →「〜をコピー」してから使ってください。", Text);
            return;
        }
        SendParamToCustom(addr, type, value);
        _txtCustName.Text = name;
    }

    private void BulkAddPresets()
    {
        int n = 0;
        foreach (var line in ClipboardText().Split('\n'))
        {
            if (!ParseParamLine(line, out var name, out var addr, out var type, out var value)) continue;
            AddCustomPreset(name, addr, type, value);
            n++;
        }
        if (n == 0) Ui.Msg(this, "クリップボードにパラメータがありません", Text);
    }

    private void UpdateCustomSnapshot() =>
        _customSnapshot = _lstCustPresets.Items.Cast<CustomPreset>().ToArray();

    private void StartCustom(PressMode mode, int? timeOverride = null)
    {
        if (!TryParseCustom(_txtCustVal.Text, out var on)) return;
        if (!TryParseCustom(_txtCustReset.Text, out var off)) return;
        string addr = _txtCustAddr.Text.Trim();
        if (!addr.StartsWith("/")) { Ui.Msg(this, "アドレスは / から始めてください", Text); return; }
        int time = timeOverride ?? (int)_nudCustTime.Value;
        _custAddrRunning = addr;
        OpLog.Write($"▶ カスタム: {addr}  {ActionRow.ModeNames[(int)mode]}  {OscMessage.FormatArg(on)}");
        var owner = _customRunner;
        _customRunner.Start(mode, time, (int)_nudCustCount.Value, pressed =>
        {
            if (pressed) _arb.Press(owner, addr, on, off); else _arb.Release(owner, addr);
        });
    }

    private bool TryParseCustom(string text, out object value)
    {
        var type = (_cmbCustType.SelectedItem as string) ?? "Bool";
        if (OscValue.TryParse(type, text, out value)) return true;
        Ui.Msg(this, $"値 \"{text}\" を {type} に変換できません", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    // =====================================================================
    // コンボ (組み合わせ・同時実行・順次実行)
    // =====================================================================
    private void BuildComboPage()
    {
        var page = NewPage("コンボ");

        var top = Ui.TopBar();
        top.Padding = new Padding(0, 0, 0, 3);
        var runAll = Ui.Btn("同時実行");
        var runSeq = Ui.Btn("順次実行");
        var stop = Ui.Btn("停止");
        _nudComboDelay = Ui.Nud(0, 600000, _s.ComboDelay, 72, 50);
        _chkComboLoop = Ui.Chk("ループ");
        top.Controls.AddRange(new Control[] { runAll, runSeq, stop, Ui.L("間隔ms"), _nudComboDelay, _chkComboLoop });
        top.SetFlowBreak(_chkComboLoop, true);

        _cmbComboPreset = Ui.Combo(160, true);
        foreach (var p in _s.Combos) _cmbComboPreset.Items.Add(p);
        var save = Ui.Btn("保存");
        var del = Ui.Btn("削除");
        var none = Ui.Btn("全解除");
        top.Controls.AddRange(new Control[] { Ui.L("プリセット"), _cmbComboPreset, save, del, none });
        top.SetFlowBreak(none, true);
        top.Controls.Add(Ui.Sub("各操作は個別タブで設定したモード/時間/回数で実行されます"));

        _clbCombo = new CheckedListBox { CheckOnClick = true, IntegralHeight = false };
        foreach (var r in _rows) _clbCombo.Items.Add(new ComboItem { Row = r });

        page.Controls.Add(Ui.FillHost(_clbCombo));
        page.Controls.Add(top);
        page.Controls.Add(BuildFeatureBar(false));

        _tip.SetToolTip(runAll, "チェックした操作をすべて同時に開始");
        _tip.SetToolTip(runSeq, "チェックした操作を上から順に、間隔ms ごとに開始");

        runAll.Click += (_, _) =>
        {
            if (!CheckComboActive(true)) return;
            _comboCts?.Cancel();
            var rows = CheckedComboRows();
            OpLog.Write($"▶ コンボ 同時実行: {string.Join(", ", rows.Select(r => r.Def.Name))}");
            foreach (var r in rows) r.Trigger();
        };
        runSeq.Click += async (_, _) => await RunComboSequential();
        stop.Click += (_, _) => StopCombo();
        none.Click += (_, _) => { for (int i = 0; i < _clbCombo.Items.Count; i++) _clbCombo.SetItemChecked(i, false); };

        save.Click += (_, _) =>
        {
            string name = _cmbComboPreset.Text.Trim();
            if (name.Length == 0) { Ui.Msg(this, "プリセット名を入力してください", Text); return; }
            var preset = new ComboPreset { Name = name, Ids = CheckedComboRows().Select(r => r.Def.Id).ToList() };
            var existing = _cmbComboPreset.Items.Cast<ComboPreset>().FirstOrDefault(p => p.Name == name);
            if (existing != null) _cmbComboPreset.Items.Remove(existing);
            _cmbComboPreset.Items.Add(preset);
            _cmbComboPreset.SelectedItem = preset;
        };
        del.Click += (_, _) =>
        {
            if (_cmbComboPreset.SelectedItem is ComboPreset p) { _cmbComboPreset.Items.Remove(p); _cmbComboPreset.Text = ""; }
        };
        _cmbComboPreset.SelectionChangeCommitted += (_, _) =>
        {
            if (_cmbComboPreset.SelectedItem is not ComboPreset p) return;
            for (int i = 0; i < _clbCombo.Items.Count; i++)
                _clbCombo.SetItemChecked(i, p.Ids.Contains(((ComboItem)_clbCombo.Items[i]).Row.Def.Id));
        };
    }

    private List<ActionRow> CheckedComboRows() =>
        _clbCombo.CheckedItems.Cast<ComboItem>().Select(c => c.Row).ToList();

    private async Task RunComboSequential()
    {
        if (!CheckComboActive(true)) return;
        _comboCts?.Cancel();
        var cts = new CancellationTokenSource();
        _comboCts = cts;
        var rows = CheckedComboRows();
        if (rows.Count == 0) return;
        _comboRunning = true;
        _comboTotal = rows.Count;
        _comboLoop = 0;
        OpLog.Write($"▶ コンボ 順次実行: {string.Join(" → ", rows.Select(r => r.Def.Name))}");
        try
        {
            do
            {
                _comboLoop++;
                for (int i = 0; i < rows.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    _comboIndex = i;
                    rows[i].Trigger();
                    await Task.Delay(Math.Max(10, (int)_nudComboDelay.Value), cts.Token);
                }
            } while (_chkComboLoop.Checked);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_comboCts == cts)
            {
                _comboRunning = false;
                OpLog.Write("■ コンボ 順次実行 終了");
            }
        }
    }

    private void StopCombo()
    {
        _comboCts?.Cancel();
        _comboRunning = false;
        foreach (var r in CheckedComboRows()) r.Runner.Stop();
        OpLog.Write("■ コンボ停止");
    }

    // =====================================================================
    // マクロ
    // =====================================================================
    private void BuildMacroPage()
    {
        var page = NewPage("マクロ");
        var env = new MacroEnvironment
        {
            Params = () => _paramPanel.ParamList(),
            Customs = () => _customSnapshot.ToList(),
            FindCustom = name => _customSnapshot.FirstOrDefault(p => p.Name == name),
            Category = (cat, cmd) => BeginInvokeSafe(() => CategoryCommand(cat, cmd, "マクロ")),
        };
        _macroPage = new MacroPage(_osc, _arb, _s, _tip, env);
        _macroPage.HotkeysChanged += RegisterHotkeys;
        _macroPage.CanRun = () => MacroActive;
        _macroPage.Blocked += () => FeatureBlocked(true, true);
        page.Controls.Add(_macroPage);
        page.Controls.Add(BuildFeatureBar(true));
    }

    // =====================================================================
    // 受信モニター
    // =====================================================================
    private void BuildRecvPage()
    {
        var page = NewPage("受信");

        var st = Ui.TopBar();
        st.Padding = new Padding(0, 0, 0, 3);
        _txtRecvFilter = Ui.Txt("", 180);
        _txtRecvFilter.PlaceholderText = "アドレスで絞込";
        _chkRecvPause = Ui.Chk("一時停止");
        _chkRecvLockedOnly = Ui.Chk("固定のみ");
        var clear = Ui.Btn("クリア");
        st.Controls.AddRange(new Control[] { _txtRecvFilter, _chkRecvPause, _chkRecvLockedOnly, clear });
        st.SetFlowBreak(clear, true);

        // 選択中のアドレスの値を変更・固定
        _lblRecvAddr = Ui.Info(230, false);
        _lblRecvAddr.Text = "(一覧から選択)";
        _cmbRecvType = Ui.Combo(64, false, OscValue.Types);
        _txtRecvValue = Ui.Txt("", 90);
        var send = Ui.Btn("送信");
        _btnRecvLock = Ui.Btn("🔓 固定を解除");
        _btnRecvLock.FaceText = "🔒 固定";
        var unlockAll = Ui.Btn("全解除");
        st.Controls.AddRange(new Control[] { _lblRecvAddr, _cmbRecvType, _txtRecvValue, send, _btnRecvLock, unlockAll });
        _tip.SetToolTip(send, "入力した値を1回送信");
        _tip.SetToolTip(_btnRecvLock, "入力した値で固定 (VRChat 側で変わっても戻す) / 固定中なら解除");
        _tip.SetToolTip(unlockAll, "全ての固定を解除");
        _tip.SetToolTip(_txtRecvValue, "Enter: 送信 / Ctrl+Enter: 固定");

        _lvRecv = new ListView
        {
            View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        _lvRecv.Columns.Add("アドレス", Ui.W(250));
        _lvRecv.Columns.Add("値", Ui.W(80));
        _lvRecv.Columns.Add("固定", Ui.W(80));
        _lvRecv.Columns.Add("時刻", Ui.W(70));
        Theme.MakeThemedListView(_lvRecv);
        _tip.SetToolTip(_lvRecv, "クリック: 値の変更欄に読込 / 右クリック: 変更・固定・コピー / ダブルクリック: カスタムタブへ");

        page.Controls.Add(Ui.FillHost(_lvRecv));
        page.Controls.Add(st);

        _txtRecvFilter.TextChanged += (_, _) => RebuildRecvList();
        _chkRecvLockedOnly.CheckedChanged += (_, _) => RebuildRecvList();
        clear.Click += (_, _) => { _recv.Clear(); _lvItems.Clear(); _lvRecv.Items.Clear(); };
        _lvRecv.VisibleChanged += (_, _) => { if (_lvRecv.Visible) RefreshLockColumn(); };
        _lvRecv.SelectedIndexChanged += (_, _) =>
        {
            if (_lvRecv.SelectedItems.Count == 0) return;
            SelectRecv(_lvRecv.SelectedItems[0].Text);
        };
        _lvRecv.DoubleClick += (_, _) =>
        {
            if (_lvRecv.SelectedItems.Count == 0) return;
            string addr = _lvRecv.SelectedItems[0].Text;
            SendParamToCustom(addr, (string)_cmbRecvType.SelectedItem, _txtRecvValue.Text);
        };
        _lvRecv.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C && _lvRecv.SelectedItems.Count > 0)
            {
                var it = _lvRecv.SelectedItems[0];
                try { Clipboard.SetText($"{it.Text}\t{_cmbRecvType.SelectedItem}\t{it.SubItems[1].Text}"); } catch { }
            }
        };
        send.Click += (_, _) => RecvSend(false);
        _btnRecvLock.Click += (_, _) =>
        {
            if (_recvAddr != null && _locker.IsLocked(_recvAddr)) _locker.Unlock(_recvAddr);
            else RecvSend(true);
        };
        unlockAll.Click += (_, _) => _locker.UnlockAll("受信タブ");
        _txtRecvValue.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            RecvSend(e.Control);
        };

        // 右クリックメニュー
        var menu = new ContextMenuStrip();
        var mEdit = new ToolStripMenuItem("値を変更して送信…");
        var mEditLock = new ToolStripMenuItem("値を変更して固定…");
        var mLock = new ToolStripMenuItem("今の値で固定");
        var mCopyAddr = new ToolStripMenuItem("アドレスをコピー");
        var mCopyValue = new ToolStripMenuItem("値をコピー");
        var mCopyLine = new ToolStripMenuItem("アドレス・型・値をコピー");
        var mCustom = new ToolStripMenuItem("カスタムタブへ送る");
        var mPreset = new ToolStripMenuItem("カスタムのプリセットに追加");
        menu.Items.AddRange(new ToolStripItem[]
        {
            mEdit, mEditLock, mLock, new ToolStripSeparator(), mCopyAddr, mCopyValue, mCopyLine,
            new ToolStripSeparator(), mCustom, mPreset
        });
        _lvRecv.ContextMenuStrip = menu;
        menu.Opening += (_, e) =>
        {
            if (_lvRecv.SelectedItems.Count == 0) { e.Cancel = true; return; }
            SelectRecv(_lvRecv.SelectedItems[0].Text);
            mLock.Text = _locker.IsLocked(_recvAddr) ? "🔓 固定を解除" : "🔒 今の値で固定";
            ThemedMenu.Apply(menu);
        };
        mEdit.Click += (_, _) => RecvEditDialog(false);
        mEditLock.Click += (_, _) => RecvEditDialog(true);
        mLock.Click += (_, _) =>
        {
            if (_locker.IsLocked(_recvAddr)) _locker.Unlock(_recvAddr);
            else RecvSend(true);
        };
        mCopyAddr.Click += (_, _) => CopyText(_recvAddr);
        mCopyValue.Click += (_, _) => CopyText(_txtRecvValue.Text);
        mCopyLine.Click += (_, _) => CopyText($"{_recvAddr}\t{_cmbRecvType.SelectedItem}\t{_txtRecvValue.Text}");
        mCustom.Click += (_, _) => SendParamToCustom(_recvAddr, (string)_cmbRecvType.SelectedItem, _txtRecvValue.Text);
        mPreset.Click += (_, _) => AddCustomPreset(ValueLocker.Short(_recvAddr), _recvAddr, (string)_cmbRecvType.SelectedItem, _txtRecvValue.Text);
    }

    /// <summary>受信一覧で選んだアドレスを値の変更欄に読み込む</summary>
    private void SelectRecv(string addr)
    {
        _recvAddr = addr;
        _lblRecvAddr.Text = ValueLocker.Short(addr);
        _tip.SetToolTip(_lblRecvAddr, addr);
        object v = null;
        if (_locker.TryGet(addr, out var lv)) v = lv;
        else if (_recv.TryGetValue(addr, out var e) && e.Args.Length > 0) v = e.Args[0];
        if (v != null)
        {
            _cmbRecvType.SelectedItem = ValueLocker.TypeOf(v);
            _txtRecvValue.Text = ValueLocker.RawText(v);
        }
        UpdateRecvLockButton();
    }

    private void UpdateRecvLockButton()
    {
        bool locked = _recvAddr != null && _locker.IsLocked(_recvAddr);
        _btnRecvLock.FaceText = locked ? "🔓 固定を解除" : "🔒 固定";
        _btnRecvLock.BackColor = locked ? Theme.Accent : Theme.ButtonBack;
        _btnRecvLock.ForeColor = locked ? Color.White : Theme.Fore;
    }

    private void RecvSend(bool lockIt)
    {
        if (string.IsNullOrEmpty(_recvAddr)) { Ui.Msg(this, "受信一覧からアドレスを選んでください", Text); return; }
        var type = (string)_cmbRecvType.SelectedItem ?? "Float";
        if (!OscValue.TryParse(type, _txtRecvValue.Text, out var v))
        {
            Ui.Msg(this, $"「{_txtRecvValue.Text}」を {type} に変換できません", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (lockIt || _locker.IsLocked(_recvAddr)) _locker.Lock(_recvAddr, v);
        else
        {
            _osc.Send(_recvAddr, v);
            OpLog.Write($"📤 送信: {_recvAddr} = {OscMessage.FormatArg(v)}");
        }
        UpdateRecvLockButton();
    }

    private void RecvEditDialog(bool lockIt)
    {
        if (string.IsNullOrEmpty(_recvAddr)) return;
        // 元の値: 固定値 → 最後に受信した値
        string original = "";
        if (_locker.TryGet(_recvAddr, out var lv)) original = ValueLocker.RawText(lv) + "  (固定中)";
        else if (_recv.TryGetValue(_recvAddr, out var e) && e.Args.Length > 0) original = ValueLocker.RawText(e.Args[0]);
        var res = InputDialog.Ask(this, Font, lockIt ? "値を変更して固定" : "値を変更して送信", _recvAddr, _txtRecvValue.Text,
            OscValue.Types, (string)_cmbRecvType.SelectedItem, original);
        if (res == null) return;
        _txtRecvValue.Text = res.Value.Text;
        if (res.Value.Choice != null) _cmbRecvType.SelectedItem = res.Value.Choice;
        RecvSend(lockIt);
    }

    /// <summary>固定列と固定ボタンの表示を更新</summary>
    private void RefreshLockColumn()
    {
        UpdateRecvLockButton();
        if (!_lvRecv.Visible) return;
        if (_chkRecvLockedOnly.Checked) { RebuildRecvList(); return; }
        _lvRecv.BeginUpdate();
        foreach (var kv in _lvItems)
        {
            string text = _locker.TryGet(kv.Key, out var v) ? "🔒 " + OscMessage.FormatArg(v) : "";
            if (kv.Value.SubItems[2].Text != text) kv.Value.SubItems[2].Text = text;
            var fore = text.Length > 0 ? Theme.Accent : Theme.Fore;
            if (kv.Value.ForeColor != fore) kv.Value.ForeColor = fore;
        }
        _lvRecv.EndUpdate();
    }

    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { }
    }

    private static Label StatusLabel() => new LLabel
    {
        AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 6, 3), Tag = "keep", Padding = new Padding(6, 2, 6, 2)
    };

    private void OnOscReceived(OscMessage m)
    {
        // 受信スレッド
        var e = _recv.GetOrAdd(m.Address, _ => new RecvEntry());
        e.Args = m.Args;
        e.Ticks = DateTime.UtcNow.Ticks;
        e.Dirty = true;

        _locker.OnReceived(m); // 固定と違う値が来たら送り直す
        _monitor.Feed(m);      // VRC 動作履歴・状態
        _paramPanel?.Feed(m);
        _cameraPanel?.Feed(m);
        if (_activity != null && _activity.LogRecv) OpLog.Write("← " + m);

        switch (m.Address)
        {
            case "/avatar/parameters/MuteSelf": Volatile.Write(ref _muteSelf, ArgState(m)); break;
        }
        if (m.Address.StartsWith("/avatar/", StringComparison.Ordinal)) OnScaleReceived(m);
        if (m.Address == "/avatar/change" && !_queryFeeding) OnQueryAvatarChanged();
    }

    private void OnOscSent(OscMessage m)
    {
        _lastSentMsg = m; // 文字にするのは表示する時だけ
        // 全停止直後のリセット送信 (大量) はログに出さない
        if (_activity != null && _activity.LogRaw && Environment.TickCount64 > Interlocked.Read(ref _quietUntil))
            OpLog.Write("→ " + m);
    }

    private static int ArgState(OscMessage m)
    {
        if (m.Args.Length == 0) return -1;
        return m.Args[0] switch
        {
            bool b => b ? 1 : 0,
            int i => i != 0 ? 1 : 0,
            float f => f != 0 ? 1 : 0,
            _ => -1
        };
    }

    private void RebuildRecvList()
    {
        _lvItems.Clear();
        _lvRecv.Items.Clear();
        foreach (var kv in _recv) kv.Value.Dirty = true;
        UpdateRecvList();
    }

    private void UpdateRecvList()
    {
        // 受信タブを見ていない時は何もしない (表示した時にまとめて反映)
        if (_chkRecvPause.Checked || !_lvRecv.Visible) return;
        string filter = _txtRecvFilter.Text.Trim();
        bool lockedOnly = _chkRecvLockedOnly.Checked;
        bool any = false;
        foreach (var kv in _recv)
        {
            var e = kv.Value;
            if (!e.Dirty) continue;
            e.Dirty = false;
            bool show = (filter.Length == 0 || kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        && (!lockedOnly || _locker.IsLocked(kv.Key));
            if (!show) continue;

            if (!any) { _lvRecv.BeginUpdate(); any = true; }
            var args = e.Args;
            string value = args.Length == 0 ? "" : args.Length == 1 ? OscMessage.FormatArg(args[0]) : string.Join(", ", args.Select(OscMessage.FormatArg));
            string locked = _locker.TryGet(kv.Key, out var lv) ? "🔒 " + OscMessage.FormatArg(lv) : "";
            string time = new DateTime(e.Ticks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss");
            if (!_lvItems.TryGetValue(kv.Key, out var item))
            {
                item = new ListViewItem(new[] { kv.Key, value, locked, time });
                if (locked.Length > 0) item.ForeColor = Theme.Accent;
                _lvItems[kv.Key] = item;
                _lvRecv.Items.Add(item);
            }
            else
            {
                if (item.SubItems[1].Text != value) item.SubItems[1].Text = value;
                if (item.SubItems[2].Text != locked) item.SubItems[2].Text = locked;
                if (item.SubItems[3].Text != time) item.SubItems[3].Text = time;
            }
        }
        if (any)
        {
            _lvRecv.EndUpdate();
            Theme.FitLastColumn(_lvRecv); // スクロールバーの出入りで幅が変わるため
        }
    }

    private static void SetState(Label l, string name, int state, string on, string off, bool onIsGood)
    {
        string text = $"{name}: {(state < 0 ? "?" : state == 1 ? on : off)}";
        if (l.Text != text) l.Text = text;
        Color back = state < 0 ? Theme.ButtonBack : ((state == 1) == onIsGood ? Theme.Active : Theme.Danger);
        Color fore = state < 0 ? Theme.SubFore : Color.White;
        if (l.BackColor != back) l.BackColor = back;
        if (l.ForeColor != fore) l.ForeColor = fore;
    }

    // =====================================================================
    // ログ
    // =====================================================================
    private void BuildLogPage()
    {
        var page = NewPage("ログ");
        _logPage = page;
        _logViewer = new LogViewer(_s, _tip);
        _logViewer.PopOutRequested += ToggleLogWindow;
        _logViewer.LinesAdded += lines => _monitor.FeedLogLines(lines); // ワールド・プレイヤー・動画・エラー
        page.Controls.Add(_logViewer);

        // 別ウィンドウ表示中にページに出す案内
        var back = Ui.Btn("↩ メイン画面に戻す");
        back.Click += (_, _) => ToggleLogWindow();
        var holder = Ui.Card(Ui.L("ログは別ウィンドウで表示中です"), back);
        holder.Visible = false;
        _logPlaceholder = holder;
        page.Controls.Add(holder);
    }

    /// <summary>ログを別ウィンドウに出す / メイン画面に戻す</summary>
    private void ToggleLogWindow()
    {
        if (_logWindow != null)
        {
            _logWindow.Close(); // FormClosed で戻す
            return;
        }

        var win = new Form
        {
            Text = "VRChat ログ", AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96F, 96F),
            Font = Font, ClientSize = new Size(Ui.W(820), Ui.W(560)), StartPosition = FormStartPosition.CenterScreen,
            Padding = new Padding(6), Icon = Icon
        };
        var top = Ui.Chk("最前面");
        top.CheckedChanged += (_, _) => win.TopMost = top.Checked;
        var bar = Ui.TopBar();
        bar.Controls.Add(top);

        _logPage.Controls.Remove(_logViewer);
        win.Controls.Add(_logViewer);
        win.Controls.Add(bar);
        _logViewer.BringToFront();
        _logPlaceholder.Visible = true;
        _logViewer.SetPoppedOut(true);

        win.FormClosed += (_, _) =>
        {
            win.Controls.Remove(_logViewer);
            _logPage.Controls.Add(_logViewer);
            _logPlaceholder.Visible = false;
            _logViewer.BringToFront();
            _logViewer.SetPoppedOut(false);
            _logWindow = null;
            OpLog.Write("ログをメイン画面に戻しました");
        };
        win.HandleCreated += (_, _) =>
        {
            try
            {
                NativeDwm.SetAttribute(win.Handle, 20, Theme.Dark ? 1 : 0);
                NativeDwm.SetAttribute(win.Handle, 33, 2);
            }
            catch { }
        };
        Theme.Apply(win);
        _logWindow = win;
        win.Show();
        OpLog.Write("ログを別ウィンドウで表示");
    }

    // =====================================================================
    // 設定 (ショートカット・文字)
    // =====================================================================
    private void BuildSettingsPage()
    {
        var page = NewPage("設定");
        var list = Ui.PageList(page);

        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void AddRow(string label, Control c)
        {
            int y = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(Ui.L(label), 0, y);
            grid.Controls.Add(c, 1, y);
        }

        _hkStop = new HotkeyBox { Width = Ui.W(150), Anchor = AnchorStyles.Left, Margin = Ui.InputMargin, Hotkey = _stopKey };
        _hkOutput = new HotkeyBox { Width = Ui.W(150), Anchor = AnchorStyles.Left, Margin = Ui.InputMargin, Hotkey = _outputKey };
        var resetKeys = Ui.Btn("既定に戻す");
        _hkStop.HotkeyChanged += (_, _) => { _stopKey = _hkStop.Hotkey; UpdateStopTooltip(); };
        _hkOutput.HotkeyChanged += (_, _) => _outputKey = _hkOutput.Hotkey;
        resetKeys.Click += (_, _) =>
        {
            _hkStop.Hotkey = _stopKey = Hotkey.Parse("Ctrl+Alt+F12");
            _hkOutput.Hotkey = _outputKey = Hotkey.Parse("Ctrl+Alt+F11");
            UpdateStopTooltip();
            RegisterHotkeys();
        };
        AddRow("OSC 全停止", Ui.Row(_hkStop, resetKeys));
        AddRow("出力 ON/OFF", _hkOutput);

        _chkStopDisables = Ui.Chk("全停止したら出力も OFF にする", _s.StopDisablesOutput);
        _chkPollStop = Ui.Chk("全停止キーを常時監視 (他アプリにキーを取られても効く)", _s.PollStopKey);
        AddRow("", _chkStopDisables);
        AddRow("", _chkPollStop);

        _lblHotkeyStatus = new LLabel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3), Tag = "keep" };
        AddRow("登録状態", _lblHotkeyStatus);

        var grid2 = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0) };
        grid2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _cmbFont = Ui.Combo(150, false, FontChoices);
        _cmbFont.SelectedItem = FontChoices.Contains(_s.FontName) ? _s.FontName : FontChoices[0];
        _nudFontSize = Ui.Nud(7, 16, (decimal)_s.FontSize, 60, 0.5M, 1);
        var restart = Ui.Btn("適用して再起動");
        int[] zooms = { 70, 80, 90, 100, 110, 120, 130 };
        _cmbZoom = Ui.Combo(70, false, zooms.Select(z => (object)$"{z}%").ToArray());
        _cmbZoom.SelectedIndex = Math.Max(0, Array.IndexOf(zooms, Math.Clamp((int)Math.Round(_s.UiZoom / 10.0) * 10, 70, 130)));
        _cmbLang = Ui.Combo(100, false, "日本語", "English");
        _cmbLang.SelectedIndex = L10n.En ? 1 : 0;
        void Restart()
        {
            _s.FontName = (string)_cmbFont.SelectedItem;
            _s.FontSize = (float)_nudFontSize.Value;
            _s.UiZoom = zooms[Math.Max(0, _cmbZoom.SelectedIndex)];
            _s.Language = _cmbLang.SelectedIndex == 1 ? L10n.English : L10n.Japanese;
            Environment.SetEnvironmentVariable(Program.RestartFlag, "1"); // 再起動時は起動時の確認を出さない
            Application.Restart(); // 終了処理で設定は保存される
        }
        restart.Click += (_, _) => Restart();
        _tip.SetToolTip(_cmbZoom, "画面全体の大きさ (文字・ボタン・余白)。小さくすると画面を広く使えます");
        _tip.SetToolTip(_cmbLang, "表示する言語 (Language)");
        _chkLogAuto = Ui.Chk("起動時にログ監視を開始", _s.LogAutoStart);
        for (int r = 0; r < 8; r++) grid2.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid2.RowCount = 8;
        grid2.Controls.Add(Ui.L("テーマ"), 0, 0);
        grid2.Controls.Add(_btnTheme, 1, 0);
        grid2.Controls.Add(Ui.L("ウィンドウ"), 0, 1);
        grid2.Controls.Add(_chkTop, 1, 1);
        grid2.Controls.Add(Ui.L("透過"), 0, 2);
        grid2.Controls.Add(Ui.Row(_trkOpacity, _lblOpacity), 1, 2);
        grid2.Controls.Add(Ui.L("文字"), 0, 3);
        grid2.Controls.Add(Ui.Row(_cmbFont, _nudFontSize, Ui.L("pt")), 1, 3);
        grid2.Controls.Add(Ui.L("UI の大きさ"), 0, 4);
        grid2.Controls.Add(_cmbZoom, 1, 4);
        grid2.Controls.Add(Ui.L("言語 (Language)"), 0, 5);
        grid2.Controls.Add(_cmbLang, 1, 5);
        grid2.Controls.Add(Ui.L("ログ"), 0, 6);
        grid2.Controls.Add(_chkLogAuto, 1, 6);
        grid2.Controls.Add(Ui.L("反映"), 0, 7);
        grid2.Controls.Add(restart, 1, 7);
        _tip.SetToolTip(restart, "文字・UI の大きさ・言語の変更を反映するため、ツールを再起動します");
        _trkOpacity.Width = Ui.W(200);

        list.Controls.Add(Ui.WideCard(
            Ui.Sub("ショートカットキー"), grid,
            Ui.Sub("※ 欄をクリックしてキーを押すと変更 (Back/Delete で解除)。入力中はホットキーが一時無効"),
            Ui.Sub("※ アプリ内では Shift+Esc でも全停止できます"),
            Ui.Sub("※ カテゴリの ON/OFF キーは各カテゴリのタブで設定"),
            Ui.Sub("※ マクロ機能・コンボ機能の ON/OFF キーは下の「マクロ・コンボ」で設定")));
        list.Controls.Add(Ui.WideCard(Ui.Sub("表示 (テーマ・最前面・透過・文字・大きさ・言語)"), grid2,
            Ui.Sub("※ 文字・UI の大きさ・言語は「適用して再起動」で反映されます")));
        list.Controls.Add(BuildFeatureSettingsCard());

        UpdateStopTooltip();
    }

    private void UpdateStopTooltip()
    {
        string k = _stopKey.IsEmpty ? "未設定" : _stopKey.ToString();
        _tip.SetToolTip(_btnStop, $"実行中の操作・連打・コンボ・マクロ・チャット連打をすべて止め、\n全ボタン=0 / 全軸=0 を強制送信します  [{k}]");
        _btnStop.Text = _stopKey.IsEmpty ? "■ 全停止" : $"■ 全停止 ({_stopKey})";
    }

    // =====================================================================
    // 接続 / 出力 / 全停止
    // =====================================================================
    private void ToggleConnect()
    {
        if (_osc.IsConnected)
        {
            StopAll("切断");
            StopOscQuery();
            _osc.Disconnect();
            OpLog.Write("🔌 切断");
        }
        else
        {
            try
            {
                _lastError = "";
                bool query = _chkOscQuery.Checked;
                // OSCQuery: 受信ポートは空いているものを自動で使い、VRChat に知らせる
                // OSCQuery の時も、VRChat が通常の受信ポートへ送る分を予備として受け取る (空いていれば)
                _osc.Connect(_txtHost.Text.Trim(), (int)_nudSend.Value, query ? 0 : (int)_nudRecv.Value, query || _chkListen.Checked,
                    query ? (int)_nudRecv.Value : 0);
                OpLog.Write($"🔌 接続: {_txtHost.Text.Trim()}:{_nudSend.Value}" + (_osc.IsListening ? $"  受信:{_osc.LocalRecvPort}" : "") +
                            (_osc.ExtraRecvPort > 0 ? $" + {_osc.ExtraRecvPort}" : "") + (query ? "  (OSCQuery)" : ""));
                if (query) StartOscQuery();
            }
            catch (Exception ex)
            {
                Ui.Msg(this, "接続できません: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        UpdateConnectionUi();
    }

    private void UpdateConnectionUi()
    {
        bool c = _osc.IsConnected;
        _btnConnect.Text = c ? "切断" : "接続";
        _lamp.ForeColor = !c ? Theme.Off : (_chkListen.Checked && !_osc.IsListening ? Theme.Warn : Theme.Ok);
        _lamp.BackColor = Theme.Back;
        _tip.SetToolTip(_lamp, !c ? "未接続" : _osc.IsListening ? "接続中 (送信+受信)" : "接続中 (送信のみ)");
        bool q = _chkOscQuery.Checked;
        _txtHost.Enabled = _nudSend.Enabled = _chkListen.Enabled = _chkOscQuery.Enabled = !c;
        _nudRecv.Enabled = !c;
        _chkListen.Enabled = !c && !q;
        UpdateOscQueryUi();
    }

    private void UpdateOutputUi()
    {
        bool on = _chkOutput.Checked;
        _chkOutput.Text = on ? "出力 ON" : "出力 OFF";
        _chkOutput.CheckedColor = Theme.Active;
        _chkOutput.BackColor = Theme.Danger;
        _chkOutput.ForeColor = Color.White;
        _chkOutput.Invalidate();
    }

    /// <summary>
    /// OSC 操作の全停止。
    /// 1) 300ms の間「押す」送信を遮断 (暴走中の処理が再び押せないように)
    /// 2) 操作・連打・コンボ・マクロ・チャット連打・カスタムをすべて停止
    /// 3) 全ボタン=0 / 全軸=0 / typing=false を強制送信 (直後・150ms後・450ms後の3回)
    /// </summary>
    private void StopAll(string by)
    {
        if (_stopping) return;
        long now = Environment.TickCount64;
        if (now - _lastStopTick < 250) return; // ホットキーとキー監視の二重発火を無視
        _lastStopTick = now;
        _stopping = true;
        try
        {
            _osc.SuppressFor(300);
            Interlocked.Exchange(ref _quietUntil, now + 700);
            OpLog.Write($"⛔ 全停止 ({by})");

            _comboCts?.Cancel();
            _comboRunning = false;
            _macroPage?.StopAll();
            foreach (var r in _rows) r.Runner.Stop();
            _customRunner.Stop();
            _chatRunner.Stop();
            _arb.Clear();
            _locker.UnlockAll("全停止");
            if (_chkChatTyping != null && _chkChatTyping.Checked) _chkChatTyping.Checked = false;

            SendZeros();
            _ = Task.Delay(150).ContinueWith(_ => SendZeros());
            _ = Task.Delay(450).ContinueWith(_ => SendZeros());

            if (_chkStopDisables != null && _chkStopDisables.Checked && _chkOutput.Checked)
                _chkOutput.Checked = false;

            _flashUntil = DateTime.Now.AddSeconds(2.5);
        }
        finally { _stopping = false; }
    }

    private void SendZeros()
    {
        if (!_osc.IsConnected) return;
        foreach (var d in ActionCatalog.All)
        {
            if (d.Kind == ActionKind.Axis) _osc.SendForce(d.Address, 0f);
            else _osc.SendForce(d.Address, 0);
        }
        _osc.SendForce("/chatbox/typing", false);
    }

    // =====================================================================
    // ホットキー
    // =====================================================================
    private void RegisterHotkeys()
    {
        if (!IsHandleCreated) return;
        UnregisterHotkeys();
        if (HotkeyBox.Capturing) return;

        var fails = new List<string>();
        Reg(HK_STOP, _stopKey, "全停止", fails);
        Reg(HK_OUTPUT, _outputKey, "出力切替", fails);
        if (_s.MacroComboEnabled)
        {
            Reg(HK_MACRO_TOGGLE, Hotkey.Parse(_s.MacroToggleHotkey), "マクロ ON/OFF", fails);
            Reg(HK_COMBO_TOGGLE, Hotkey.Parse(_s.ComboToggleHotkey), "コンボ ON/OFF", fails);
        }

        for (int i = 0; i < ActionCatalog.Categories.Length; i++)
        {
            var cat = ActionCatalog.Categories[i];
            if (_s.CategoryHotkeys.TryGetValue(cat, out var text))
                Reg(HK_CAT_BASE + i, Hotkey.Parse(text), "カテゴリ:" + cat, fails);
        }

        RegisterChatHotkeys(fails);

        _hotkeyMacros.Clear();
        if (_macroPage != null && MacroActive) // 無効中はマクロのキーを登録しない (他アプリで使えるように)
        {
            foreach (var m in _macroPage.Macros)
            {
                var hk = Hotkey.Parse(m.Hotkey);
                if (hk.IsEmpty) continue;
                int id = HK_MACRO_BASE + _hotkeyMacros.Count;
                _hotkeyMacros.Add(m);
                Reg(id, hk, "マクロ:" + m.Name, fails);
            }
        }

        if (_lblHotkeyStatus != null)
        {
            _lblHotkeyStatus.Text = fails.Count == 0
                ? $"✓ 登録OK ({_registeredIds.Count} 件)"
                : "⚠ 登録できません (他アプリが使用中/重複): " + string.Join(", ", fails);
            _lblHotkeyStatus.ForeColor = fails.Count == 0 ? Theme.Ok : Theme.Warn;
            _lblHotkeyStatus.BackColor = Theme.Card;
        }
        if (fails.Count > 0) _lastError = "ホットキー登録失敗: " + string.Join(", ", fails);
    }

    private void Reg(int id, Hotkey hk, string name, List<string> fails)
    {
        if (hk.IsEmpty) return;
        if (RegisterHotKey(Handle, id, hk.Win32Modifiers | MOD_NOREPEAT, (uint)hk.Key)) _registeredIds.Add(id);
        else fails.Add($"{name}({hk})");
    }

    private void UnregisterHotkeys()
    {
        if (!IsHandleCreated) return;
        foreach (var id in _registeredIds) UnregisterHotKey(Handle, id);
        _registeredIds.Clear();
    }

    private void OnHotkeyCapturing(bool capturing)
    {
        if (capturing) UnregisterHotkeys();
        else RegisterHotkeys();
    }

    /// <summary>RegisterHotKey が取られていても効くよう、全停止キーだけはキー状態も監視する</summary>
    private void PollStopKey()
    {
        if (_chkPollStop == null || !_chkPollStop.Checked || _stopKey.IsEmpty || HotkeyBox.Capturing)
        {
            _pollPrev = false;
            return;
        }
        bool down = IsDown(_stopKey.Key)
                    && (!_stopKey.Ctrl || IsDown(Keys.ControlKey))
                    && (!_stopKey.Alt || IsDown(Keys.Menu))
                    && (!_stopKey.Shift || IsDown(Keys.ShiftKey));
        if (down && !_pollPrev) StopAll("キー監視");
        _pollPrev = down;
    }

    private static bool IsDown(Keys k) => (GetAsyncKeyState((int)k) & 0x8000) != 0;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (HandleChatHotkey(id)) { }
            else if (id == HK_STOP) StopAll("ホットキー");
            else if (id == HK_OUTPUT) _chkOutput.Checked = !_chkOutput.Checked;
            else if (id == HK_MACRO_TOGGLE) SetFeatureActive(true, !_s.MacroActive, "キー");
            else if (id == HK_COMBO_TOGGLE) SetFeatureActive(false, !_s.ComboActive, "キー");
            else if (id >= HK_CAT_BASE && id - HK_CAT_BASE < ActionCatalog.Categories.Length)
                ToggleCategory(ActionCatalog.Categories[id - HK_CAT_BASE], "キー");
            else if (id >= HK_MACRO_BASE && id - HK_MACRO_BASE < _hotkeyMacros.Count)
            {
                var macro = _hotkeyMacros[id - HK_MACRO_BASE];
                if (_s.Macros.Contains(macro) && MacroActive) _macroPage.Toggle(macro);
            }
        }
        base.WndProc(ref m);
    }

    // =====================================================================
    // UI 更新・外観
    // =====================================================================
    private void UiTick()
    {
        _locker.Tick();     // 固定値の定期送信
        _monitor.Tick();    // 話し終わり・移動などの確定
        if (_chkTop.Checked) UpdateTopMost(); // 別ウィンドウの開閉を見張る
        _paramPanel.Tick(); // 受信値の取り込み (表示中の行だけ更新)
        _cameraPanel.Tick();
        if (WindowState == FormWindowState.Minimized) return; // 最小化中は表示更新しない
        _stateBoard.SetState(_monitor.Snapshot(), _osc.IsConnected, _eyeHeight, _eyeOriginal);

        UpdateRecvList();

        int mute = Volatile.Read(ref _muteSelf);
        if (_lblMicSys != null) SetState(_lblMicSys, "現在", mute, "ミュート", "ON", false);
        UpdateScaleUi();
        UpdateOscQueryUi();

        int running = UpdateActivity();

        string text;
        Color color;
        if (DateTime.Now < _flashUntil)
        {
            text = "⛔ 全停止しました (全ボタン/軸を 0 にリセット)";
            color = Theme.Error;
        }
        else
        {
            var target = _osc.Target;
            string conn = !_osc.IsConnected || target == null ? "未接続"
                : $"{target.Address}:{target.Port}" + (_osc.IsListening ? $" / 受信:{_osc.LocalRecvPort}" : "") + (_oscQuery != null ? " (OSCQuery)" : "");
            string err = _lastError;
            var last = _lastSentMsg;
            text = $"{conn} | 送信 {_osc.SentCount} 受信 {_osc.RecvCount} | 実行中 {running} | " +
                   (!string.IsNullOrEmpty(err) ? "⚠ " + err : last != null ? "→ " + last : "");
            color = string.IsNullOrEmpty(err) ? Theme.SubFore : Theme.Warn;
        }
        if (_status.Text != text) _status.Text = text;
        if (_status.ForeColor != color) _status.ForeColor = color;

        if (_osc.IsConnected && _chkListen.Checked && !_osc.IsListening && _lamp.ForeColor != Theme.Warn)
            UpdateConnectionUi();
    }

    /// <summary>動作パネルとタブのバッジを更新。動作中の総数を返す</summary>
    private int UpdateActivity()
    {
        var lines = new List<ActiveLine>();
        int total = 0;

        foreach (var cat in ActionCatalog.Categories)
        {
            var running = _rows.Where(r => r.Def.Category == cat && r.Runner.IsRunning).ToList();
            _tabs.SetBadge(cat, running.Count);
            if (running.Count == 0) continue;
            total += running.Count;
            lines.Add(new ActiveLine(true, cat, $"{running.Count} 件"));
            foreach (var r in running) lines.Add(new ActiveLine(false, r.Def.Name, r.ActivityDetail()));
        }

        bool chat = _chatRunner.IsRunning;
        _tabs.SetBadge("チャット", chat ? 1 : 0);
        if (chat)
        {
            total++;
            lines.Add(new ActiveLine(true, "チャット", "1 件"));
            lines.Add(new ActiveLine(false, _chatText, _chatRunner.Describe()));
        }

        bool cust = _customRunner.IsRunning;
        _tabs.SetBadge("カスタム", cust ? 1 : 0);
        if (cust)
        {
            total++;
            lines.Add(new ActiveLine(true, "カスタム", "1 件"));
            lines.Add(new ActiveLine(false, _custAddrRunning.Replace("/avatar/parameters/", ""), _customRunner.Describe()));
        }

        _tabs.SetBadge("コンボ", _comboRunning ? 1 : 0);
        if (_comboRunning)
        {
            total++;
            lines.Add(new ActiveLine(true, "コンボ", "順次実行"));
            lines.Add(new ActiveLine(false, $"{_comboIndex + 1}/{_comboTotal} 番目", $"周回 {_comboLoop}" + (_chkComboLoop.Checked ? "/∞" : "")));
        }

        var locks = _locker.Snapshot();
        _tabs.SetBadge("パラメータ", locks.Length);
        if (locks.Length > 0)
        {
            lines.Add(new ActiveLine(true, "🔒 固定", $"{locks.Length} 件"));
            foreach (var kv in locks.OrderBy(k => k.Key))
                lines.Add(new ActiveLine(false, ValueLocker.Short(kv.Key), OscMessage.FormatArg(kv.Value)));
        }

        var macros = _macroPage.ActiveMacros();
        _tabs.SetBadge("マクロ", macros.Count);
        if (macros.Count > 0)
        {
            total += macros.Count;
            lines.Add(new ActiveLine(true, "マクロ", $"{macros.Count} 件"));
            foreach (var (name, detail) in macros) lines.Add(new ActiveLine(false, name, detail));
        }

        if (_sideHost.Visible) _activity.SetActive(lines);
        return total;
    }

    private void ApplyThemeExtras()
    {
        _btnStop.BackColor = Theme.Danger;
        _btnStop.ForeColor = Color.White;
        _btnTheme.Text = Theme.Dark ? "☀ ライト" : "🌙 ダーク";
        UpdateConnectionUi();
        UpdateOutputUi();
        UpdateChatButton();
        if (_lblHotkeyStatus != null) _lblHotkeyStatus.BackColor = Theme.Card;
        Invalidate(true);
    }

    /// <summary>
    /// 最前面の判定。「最前面」が ON でも、このアプリの別ウィンドウ (編集・確認・ファイル選択など) が
    /// 前面にある間とダイアログを開いている間は最前面を外し、メイン画面に戻ったら最前面に戻す。
    /// (メイン画面が最前面のままだと、別ウィンドウが裏に回って操作できなくなるため)
    /// </summary>
    private void UpdateTopMost()
    {
        if (IsDisposed || !IsHandleCreated) return;
        bool want = _chkTop.Checked && Ui.ModalDepth == 0 && !OwnOtherWindowInFront();
        if (TopMost != want) TopMost = want;
    }

    private bool OwnOtherWindowInFront()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == _pid; // このアプリの別ウィンドウが前面
    }

    private void ApplyOpacity()
    {
        Opacity = _trkOpacity.Value / 100.0;
        _lblOpacity.Text = $"{_trkOpacity.Value}%";
    }

    private void ApplyTitleBar(bool refresh)
    {
        if (!IsHandleCreated) return;
        try
        {
            int v = Theme.Dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND (Win11)
            if (refresh && WindowState == FormWindowState.Normal)
            {
                var w = Width;
                Width = w + 1;
                Width = w;
            }
        }
        catch { }
    }

    private void BeginInvokeSafe(Action a)
    {
        if (IsHandleCreated && !IsDisposed)
        {
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }
    }

    private void RestoreWindow()
    {
        // 既定のウィンドウサイズを変えた版: 一度だけ保存済みのサイズを使わない
        if (_s.LayoutVersion < 4)
        {
            _s.LayoutVersion = 4;
            return;
        }
        if (_s.W > 0 && _s.H > 0)
        {
            var r = new Rectangle(_s.X, _s.Y, _s.W, _s.H);
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = r;
            }
        }
    }

    // =====================================================================
    // ライフサイクル
    // =====================================================================
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _registeredIds.Clear();
        RegisterHotkeys();
        ApplyTitleBar(false);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 起動直後にショートカット入力欄へフォーカスが入るとホットキーが一時解除されるので避ける
        ActiveControl = _btnConnect;
        RegisterHotkeys();
        OpLog.Write("起動しました");
        if (_s.AutoConnect) ToggleConnect();
        _logViewer.StartIfConfigured();
        _paramPanel.Initialize();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && e.Shift) { StopAll("Shift+Esc"); e.Handled = true; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _uiTimer.Stop();
        _pollTimer.Stop();
        _logViewer.StopWatching();
        if (_logWindow != null) { var w = _logWindow; w.Close(); }
        _lastStopTick = 0;
        StopAll("終了");
        SaveSettings();
        HotkeyBox.CapturingChanged -= OnHotkeyCapturing;
        Ui.ModalStateChanged -= UpdateTopMost;
        UnregisterHotkeys();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _osc.Dispose();
        base.OnFormClosed(e);
    }

    private void SaveSettings()
    {
        _s.Host = _txtHost.Text.Trim();
        _s.SendPort = (int)_nudSend.Value;
        _s.RecvPort = (int)_nudRecv.Value;
        _s.Listen = _chkListen.Checked;
        _s.UseOscQuery = _chkOscQuery.Checked;
        _s.OscQueryAutoFetch = _chkQueryAutoFetch.Checked;
        _s.AutoConnect = _chkAutoConnect.Checked;
        _s.Dark = Theme.Dark;
        _s.TopMost = _chkTop.Checked;
        _s.Opacity = _trkOpacity.Value;
        _s.ShowSidePanel = _chkSide.Checked;
        _s.SidePanelWidth = (int)Math.Round(_sideHost.Width / (DeviceDpi / 96f) / Ui.Scale);

        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _s.X = b.X; _s.Y = b.Y; _s.W = b.Width; _s.H = b.Height;

        _s.ChatImmediate = _chkChatImmediate.Checked;
        _s.ChatSound = _chkChatSound.Checked;
        _s.ChatInterval = (int)_nudChatInterval.Value;
        _s.ChatLog = _lstChatHistory.Items.Cast<ChatEntry>().ToList();
        _s.ChatHistory = new List<string>();
        _s.ChatPresets = ChatPresetList();
        _s.ComboDelay = (int)_nudComboDelay.Value;

        _s.StopHotkey = _stopKey.ToString();
        _s.OutputHotkey = _outputKey.ToString();
        _s.StopDisablesOutput = _chkStopDisables.Checked;
        _s.PollStopKey = _chkPollStop.Checked;
        _s.LogAutoStart = _chkLogAuto.Checked;

        _s.Rows = _rows.ToDictionary(r => r.Def.Id, r => r.GetSetting());
        _s.Customs = _lstCustPresets.Items.Cast<CustomPreset>().ToList();
        _s.Combos = _cmbComboPreset.Items.Cast<ComboPreset>().ToList();
        // _s.Macros / CategoryHotkeys はそれぞれのページが直接編集している
        SavePresetFiles(false); // マクロ・コンボはフォルダのファイルへ
        _s.Save();
    }
}
