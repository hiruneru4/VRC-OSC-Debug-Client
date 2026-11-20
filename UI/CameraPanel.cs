using System.Collections.Concurrent;
using System.Globalization;
using VRCOscDebugClient.Core;
using VRCOscDebugClient.Osc;

namespace VRCOscDebugClient.UI;

/// <summary>
/// 公式カメラ (/usercamera/...) とカメラドリー (/dolly/...) の操作。
/// VRChat から届いた値でスイッチ・スライダーの表示も更新する。
/// </summary>
public sealed class CameraPanel : UserControl
{
    private readonly OscClient _osc;
    private readonly AppSettings _s;
    private readonly ComboBox _cmbMode;
    private readonly Dictionary<string, RToggle> _toggles = new();
    private readonly Dictionary<string, (CamSlider Def, RSlider Bar, FastLabel Label)> _sliders = new();
    private readonly FastLabel _lblPose, _lblDolly;
    private readonly ComboBox _cmbSlot;
    private readonly RToggle _chkDollyPlay;
    private readonly RNumber _nudDelay;
    private readonly ConcurrentDictionary<string, object[]> _incoming = new();
    private float[] _pose;
    private bool _suppress;

    public CameraPanel(OscClient osc, AppSettings s, ToolTip tip)
    {
        _osc = osc;
        _s = s;
        Dock = DockStyle.Fill;
        var list = Ui.PageList(this);

        // ---- モード・撮影 (横幅いっぱい) ----
        _cmbMode = Ui.Combo(120, false, CameraCatalog.Modes);
        var capture = Ui.Btn("📷 撮影");
        var delayed = Ui.Btn("⏲ タイマー撮影");
        var close = Ui.Btn("✖ カメラを閉じる");
        _cmbMode.SelectionChangeCommitted += (_, _) => Send(CameraCatalog.Mode, _cmbMode.SelectedIndex, $"モード: {_cmbMode.SelectedItem}");
        capture.Click += (_, _) => Send(CameraCatalog.Capture, true, "撮影");
        delayed.Click += (_, _) => Send(CameraCatalog.CaptureDelayed, true, "タイマー撮影");
        close.Click += (_, _) => Send(CameraCatalog.Close, true, "カメラを閉じる");
        list.Controls.Add(Ui.WideCard(
            Ui.Row(Ui.L("モード"), _cmbMode, capture, delayed, close),
            Ui.Sub("※ VRChat 側でカメラを開いている時だけ操作できます (OSC ではカメラを開けません)")));

        // ---- 左: スイッチ (小さめのボタン) ----
        var tgrid = Grid(3);
        int i = 0;
        foreach (var t in CameraCatalog.Toggles)
        {
            var tg = Ui.Toggle(t.Name);
            if (Ui.SmallFont != null) tg.Font = Ui.SmallFont;
            tg.Padding = new Padding(3, 0, 3, 0);
            tg.Margin = new Padding(2);
            tg.CheckedColor = Theme.Active;
            tg.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            string addr = CameraCatalog.Cam + t.Id;
            tg.CheckedChanged += (_, _) => { if (!_suppress) Send(addr, tg.Checked, $"{t.Name}: {(tg.Checked ? "ON" : "OFF")}"); };
            tip.SetToolTip(tg, addr);
            AddToGrid(tgrid, tg, i % 3, i / 3);
            _toggles[addr] = tg;
            i++;
        }
        var left = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 10, 0)
        };
        left.Controls.Add(Ui.Sub("スイッチ (VRChat 側の状態に合わせて表示)"));
        left.Controls.Add(tgrid);

        // ---- 右: スライダー (バーは残りの幅いっぱい) ----
        var sgrid = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4,
            Dock = DockStyle.Fill, Margin = new Padding(0)
        };
        sgrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sgrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sgrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sgrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var resetAll = Ui.Btn("全て既定に戻す");
        var sliderTitle = Ui.Sub("スライダー");
        AddToGrid(sgrid, sliderTitle, 0, 0);
        AddToGrid(sgrid, resetAll, 3, 0);
        sgrid.SetColumnSpan(resetAll, 1);
        int row = 1;
        foreach (var sl in CameraCatalog.Sliders)
        {
            var name = Ui.L(sl.Name);
            var bar = new RSlider
            {
                Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, AutoSize = false,
                Width = Ui.W(120), Height = 26, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 1, 0, 1),
                SmallChange = 5, LargeChange = 50, Value = ToBar(sl, sl.Default)
            };
            var label = Ui.Info(52, false);
            label.Text = Format(sl, sl.Default);
            var reset = Ui.Btn("既定");
            if (Ui.SmallFont != null) reset.Font = Ui.SmallFont;
            reset.Padding = new Padding(4, 0, 4, 0);
            string addr = CameraCatalog.Cam + sl.Id;
            bar.ValueChanged += (_, _) =>
            {
                float v = FromBar(sl, bar.Value);
                label.Text = Format(sl, v);
                if (!_suppress) _osc.Send(addr, v);
            };
            reset.Click += (_, _) => bar.Value = ToBar(sl, sl.Default);
            tip.SetToolTip(name, $"{addr}  ({sl.Min}〜{sl.Max}, 既定 {sl.Default})");
            AddToGrid(sgrid, name, 0, row);
            AddToGrid(sgrid, bar, 1, row);
            AddToGrid(sgrid, label, 2, row);
            AddToGrid(sgrid, reset, 3, row);
            _sliders[addr] = (sl, bar, label);
            row++;
        }
        resetAll.Click += (_, _) => { foreach (var v in _sliders.Values) v.Bar.Value = ToBar(v.Def, v.Def.Default); };

        // 左右に並べる表 (カードの幅いっぱいに広がり、スライダーの列が伸びる)
        var split = Ui.Stretch(new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = new Padding(0)
        });
        split.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        split.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        split.Controls.Add(left, 0, 0);
        split.Controls.Add(sgrid, 1, 0);
        list.Controls.Add(Ui.WideCard(split));

        // ---- ポーズ (位置・向き) ----
        _lblPose = Ui.Info(420, false);
        _lblPose.Text = "(VRChat からまだ受信していません)";
        _cmbSlot = Ui.Combo(110, false);
        RefreshSlots();
        var save = Ui.Btn("今のポーズを保存");
        var load = Ui.Btn("呼び出し");
        var del = Ui.Btn("削除");
        tip.SetToolTip(save, "VRChat から受信した現在のカメラ位置・向きを、選んだ番号に保存");
        tip.SetToolTip(load, "保存した位置・向きにカメラを移動 (/usercamera/Pose)");
        save.Click += (_, _) =>
        {
            if (_pose == null) { Ui.Msg(this, "カメラのポーズをまだ受信していません。\nVRChat でカメラを開いて少し動かしてください。"); return; }
            _s.CameraPoses[SlotKey] = _pose.ToArray();
            RefreshSlots();
            OpLog.Write($"📷 ポーズを保存: {SlotKey} ({PoseText(_pose)})");
        };
        load.Click += (_, _) =>
        {
            if (!_s.CameraPoses.TryGetValue(SlotKey, out var p) || p == null || p.Length == 0) { Ui.Msg(this, "この番号には保存されていません"); return; }
            Send(CameraCatalog.Pose, p.Cast<object>().ToArray(), $"ポーズ呼び出し: {SlotKey}");
        };
        del.Click += (_, _) => { _s.CameraPoses.Remove(SlotKey); RefreshSlots(); };
        list.Controls.Add(Ui.WideCard(
            Ui.Sub("ポーズ (カメラの位置・向き)"),
            Ui.Row(Ui.L("現在"), _lblPose),
            Ui.Row(Ui.L("番号"), _cmbSlot, save, load, del)));

        // ---- ドリー ----
        _chkDollyPlay = Ui.Toggle("■ 停止", 80);
        _chkDollyPlay.OnText = "■ 停止";
        _chkDollyPlay.OffText = "▶ 再生";
        _chkDollyPlay.CheckedColor = Theme.Active;
        _chkDollyPlay.CheckedChanged += (_, _) => { if (!_suppress) Send(CameraCatalog.DollyPlay, _chkDollyPlay.Checked, _chkDollyPlay.Checked ? "ドリー再生" : "ドリー停止"); };
        _nudDelay = Ui.Nud(0, 600, 3, 60, 1, 1);
        var playDelayed = Ui.Btn("⏲ 遅延再生");
        playDelayed.Click += (_, _) => Send(CameraCatalog.DollyPlayDelayed, (float)_nudDelay.Value, $"ドリー遅延再生 {_nudDelay.Value} 秒");
        var export = Ui.Btn("📤 書き出し");
        export.Click += (_, _) => Send(CameraCatalog.DollyExport, null, "ドリー経路を書き出し");
        var import = Ui.Btn("📥 読み込み…");
        import.Click += (_, _) => ImportDolly();
        _lblDolly = Ui.Info(420, true);
        _lblDolly.Text = "書き出し先: ドキュメント\\VRChat\\CameraPaths";
        tip.SetToolTip(export, "今のドリー経路を JSON に書き出し (ドキュメント\\VRChat\\CameraPaths)");
        tip.SetToolTip(import, "JSON のドリー経路を読み込み (再生中は無視されます)");
        list.Controls.Add(Ui.WideCard(
            Ui.Sub("カメラドリー (VRC+)"),
            Ui.Row(_chkDollyPlay, Ui.L("遅延"), _nudDelay, Ui.L("秒"), playDelayed, export, import),
            _lblDolly));
    }

    private string SlotKey => (_cmbSlot.SelectedIndex + 1).ToString(CultureInfo.InvariantCulture);

    private void RefreshSlots()
    {
        int sel = Math.Max(0, _cmbSlot.SelectedIndex);
        _cmbSlot.BeginUpdate();
        _cmbSlot.Items.Clear();
        for (int n = 1; n <= 8; n++)
            _cmbSlot.Items.Add(_s.CameraPoses.ContainsKey(n.ToString(CultureInfo.InvariantCulture)) ? $"{n} (保存済)" : $"{n}");
        _cmbSlot.EndUpdate();
        _cmbSlot.SelectedIndex = sel;
    }

    private static TableLayoutPanel Grid(int columns)
    {
        var g = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = columns, Margin = new Padding(0) };
        for (int c = 0; c < columns; c++) g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return g;
    }

    private static void AddToGrid(TableLayoutPanel g, Control c, int col, int row)
    {
        while (g.RowCount <= row)
        {
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            g.RowCount++;
        }
        g.Controls.Add(c, col, row);
    }

    private static int ToBar(CamSlider s, float v) =>
        Math.Clamp((int)Math.Round((v - s.Min) / (s.Max - s.Min) * 1000f), 0, 1000);

    private static float FromBar(CamSlider s, int bar) =>
        (float)Math.Round(s.Min + (s.Max - s.Min) * bar / 1000f, s.Decimals);

    private static string Format(CamSlider s, float v) =>
        v.ToString(s.Decimals == 0 ? "0" : "0." + new string('0', s.Decimals), CultureInfo.InvariantCulture);

    private static string PoseText(float[] p) =>
        p.Length >= 6
            ? $"位置 {p[0]:0.00}, {p[1]:0.00}, {p[2]:0.00}  /  向き {p[3]:0.0}, {p[4]:0.0}, {p[5]:0.0}"
            : string.Join(", ", p.Select(x => x.ToString("0.00", CultureInfo.InvariantCulture)));

    private void Send(string addr, object value, string log)
    {
        bool ok = value switch
        {
            null => _osc.Send(addr),
            object[] arr => _osc.Send(addr, arr),
            _ => _osc.Send(addr, value)
        };
        if (!_osc.IsConnected) OpLog.Write($"⚠ 未接続のため送信できません: {log}");
        else if (ok) OpLog.Write($"📷 {log}");
    }

    private void ImportDolly()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VRChat", "CameraPaths");
        using var dlg = new OpenFileDialog
        {
            InitialDirectory = Directory.Exists(folder) ? folder : "",
            Filter = "ドリー経路 (*.json)|*.json|すべて (*.*)|*.*"
        };
        if (Ui.ShowCommon(this, dlg) != DialogResult.OK) return;
        try
        {
            string json = File.ReadAllText(dlg.FileName);
            Send(CameraCatalog.DollyImport, json, $"ドリー経路を読み込み: {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) { Ui.Msg(this, "読み込めませんでした: " + ex.Message); }
    }

    // =================================================================
    // 受信
    // =================================================================
    public void Feed(OscMessage m)
    {
        if (m.Address.StartsWith(CameraCatalog.Cam, StringComparison.Ordinal) ||
            m.Address.StartsWith("/dolly/", StringComparison.Ordinal))
            _incoming[m.Address] = m.Args;
    }

    /// <summary>UI スレッドから定期的に呼ぶ</summary>
    public void Tick()
    {
        if (_incoming.IsEmpty) return;
        _suppress = true;
        try
        {
            foreach (var key in _incoming.Keys.ToArray())
            {
                if (!_incoming.TryRemove(key, out var args) || args.Length == 0) continue;
                var a0 = args[0];
                if (key == CameraCatalog.Mode)
                {
                    int mode = (int)ParamGrid.ToFloat(a0);
                    if (mode >= 0 && mode < _cmbMode.Items.Count && _cmbMode.SelectedIndex != mode) _cmbMode.SelectedIndex = mode;
                }
                else if (key == CameraCatalog.Pose)
                {
                    _pose = args.Select(ParamGrid.ToFloat).ToArray();
                    _lblPose.Text = PoseText(_pose);
                }
                else if (key == CameraCatalog.DollyPlay)
                {
                    bool playing = ParamGrid.ToBool(a0);
                    if (_chkDollyPlay.Checked != playing) _chkDollyPlay.Checked = playing;
                }
                else if (key == CameraCatalog.DollyExport && a0 is string path)
                {
                    _lblDolly.Text = "書き出しました: " + path;
                    OpLog.Write("📤 ドリー経路を書き出し: " + path);
                }
                else if (_toggles.TryGetValue(key, out var tg))
                {
                    bool on = ParamGrid.ToBool(a0);
                    if (tg.Checked != on) tg.Checked = on;
                }
                else if (_sliders.TryGetValue(key, out var sl))
                {
                    if (sl.Bar.Capture) continue; // ドラッグ中は上書きしない
                    int v = ToBar(sl.Def, ParamGrid.ToFloat(a0));
                    if (sl.Bar.Value != v) sl.Bar.Value = v;
                }
            }
        }
        finally { _suppress = false; }
    }
}
