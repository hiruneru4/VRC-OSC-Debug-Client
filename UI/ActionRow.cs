using VRCOscDebugClient.Core;
using VRCOscDebugClient.Osc;

namespace VRCOscDebugClient.UI;

/// <summary>
/// 1操作分のコントロール群。ActionTable (TableLayoutPanel) の1行に配置され、
/// 列幅は中身に合わせて自動調整されるので文字が欠けない。
/// </summary>
public sealed class ActionRow
{
    public static readonly string[] ModeNames = { "1回", "長押し", "連打", "押下中" };

    private readonly InputArbiter _arb;
    private readonly ToolTip _tip;
    private readonly Label _name;
    private readonly ComboBox _mode;
    private readonly RNumber _value, _time, _count;
    private readonly RButton _run;
    private readonly RCheck _mark;
    private bool _loading;
    private float _runValue;

    public OscActionDef Def { get; }
    public ActionRunner Runner { get; }
    public PressMode Mode => (PressMode)Math.Max(0, _mode.SelectedIndex);
    public Control[] Cells => new Control[] { _mark, _name, _mode, _value, _time, _count, _run };

    /// <summary>カテゴリのショートカットで起動する対象か</summary>
    public bool Marked => _mark.Checked;

    public ActionRow(OscActionDef def, InputArbiter arb, ToolTip tip)
    {
        Def = def;
        _arb = arb;
        _tip = tip;

        _mark = Ui.Chk("");
        _mark.Margin = new Padding(4, 3, 0, 3);
        _name = new LLabel { Text = def.Name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 6, 3) };
        _mode = Ui.Combo(70, false, ModeNames);
        _value = Ui.Nud(-1, 1, (decimal)def.DefaultValue, 58, 0.1M, 2);
        _value.Enabled = def.Kind == ActionKind.Axis;
        _time = Ui.Nud(0, 600000, 100, 68, 50);
        _count = Ui.Nud(0, 99999, 0, 56);
        _count.Enabled = false;
        _run = Ui.Btn("▶", 36);

        Runner = new ActionRunner(def.Name);
        Runner.StateChanged += running =>
        {
            OpLog.Write(running
                ? $"▶ {def.Category}/{def.Name}  {ModeNames[(int)Runner.CurrentMode]}" + (def.Kind == ActionKind.Axis ? $"  値 {_runValue:0.00}" : "")
                : $"■ {def.Category}/{def.Name}  終了");
            if (_run.IsHandleCreated && !_run.IsDisposed)
            {
                try { _run.BeginInvoke(new Action(UpdateRunVisual)); } catch (InvalidOperationException) { }
            }
        };

        _mode.SelectedIndexChanged += (_, _) => OnModeChanged();
        _run.Click += (_, _) =>
        {
            if (Mode == PressMode.Momentary) return;
            if (Runner.IsRunning) Runner.Stop(); else Trigger();
        };
        _run.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && Mode == PressMode.Momentary) StartWith(PressMode.Hold, 0, 0);
        };
        _run.MouseUp += (_, _) => { if (Mode == PressMode.Momentary) Runner.Stop(); };

        _tip.SetToolTip(_name, $"{def.Tip}\n{def.Address}  ({(def.Kind == ActionKind.Axis ? "float" : "int 0/1")})");
        _tip.SetToolTip(_value, def.Kind == ActionKind.Axis ? "軸の値 (-1.0〜1.0)" : "ボタンは 1/0 固定");
        _tip.SetToolTip(_count, "連打回数 (0 = 停止するまで無限)");
        _tip.SetToolTip(_mark, "チェックした操作は、このカテゴリのショートカットキーでまとめて ON/OFF されます");
        UpdateTips();
    }

    private void OnModeChanged()
    {
        var m = Mode;
        _count.Enabled = m == PressMode.Rapid;
        _time.Enabled = m != PressMode.Momentary;
        if (!_loading)
        {
            _time.Value = m switch
            {
                PressMode.Tap => 100,
                PressMode.Hold => 0,
                PressMode.Rapid => 200,
                _ => _time.Value
            };
        }
        UpdateTips();
    }

    private void UpdateTips()
    {
        string t = Mode switch
        {
            PressMode.Tap => "押下時間 ms (1回押して離す)",
            PressMode.Hold => "保持時間 ms (0 = もう一度押すまで保持)",
            PressMode.Rapid => "連打間隔 ms (押下はその半分)",
            _ => "押下中: ▶ ボタンを押している間だけ ON"
        };
        _tip.SetToolTip(_time, t);
        _tip.SetToolTip(_run, Mode == PressMode.Momentary ? "押している間だけ ON" : "実行 / 停止");
    }

    /// <summary>行の設定どおりに実行 (コンボからも呼ばれる)。押下中モードは長押し(無期限)扱い</summary>
    public void Trigger()
    {
        var m = Mode == PressMode.Momentary ? PressMode.Hold : Mode;
        int time = Mode == PressMode.Momentary ? 0 : (int)_time.Value;
        StartWith(m, time, (int)_count.Value);
    }

    public void StartWith(PressMode mode, int timeMs, int count)
    {
        var def = Def;
        var arb = _arb;
        var owner = this;
        float v = (float)_value.Value;
        _runValue = v;
        object onValue = def.Kind == ActionKind.Axis ? (object)v : 1;
        object offValue = def.Kind == ActionKind.Axis ? (object)0f : 0;
        Runner.Start(mode, timeMs, count, on =>
        {
            // 他の操作 (マクロ等) が同じ入力を押していても調停される。離すは必ず届ける
            if (on) arb.Press(owner, def.Address, onValue, offValue);
            else arb.Release(owner, def.Address);
        });
    }

    /// <summary>動作パネル用の状況 (例: "連打 3/10" / "長押し 1.2s  値 0.50")</summary>
    public string ActivityDetail()
    {
        string d = Runner.Describe();
        if (Def.Kind == ActionKind.Axis) d += $"  値 {_runValue:0.00}";
        return d;
    }

    public void UpdateRunVisual()
    {
        bool running = Runner.IsRunning;
        _run.FaceText = running ? "■" : "▶";
        _run.BackColor = running ? Theme.Active : Theme.ButtonBack;
        _name.ForeColor = running ? Theme.Ok : Theme.Fore;
    }

    public RowSetting GetSetting() => new()
    {
        Mode = _mode.SelectedIndex, Value = _value.Value, Time = _time.Value, Count = _count.Value, Marked = _mark.Checked
    };

    public void ApplySetting(RowSetting s)
    {
        if (s == null) return;
        _loading = true;
        try
        {
            _mode.SelectedIndex = Math.Clamp(s.Mode, 0, ModeNames.Length - 1);
            _value.Value = Math.Clamp(s.Value, _value.Minimum, _value.Maximum);
            _time.Value = Math.Clamp(s.Time, _time.Minimum, _time.Maximum);
            _count.Value = Math.Clamp(s.Count, _count.Minimum, _count.Maximum);
            _mark.Checked = s.Marked;
            OnModeChanged(); // 読込中なので保存値は上書きされない
        }
        finally { _loading = false; }
    }
}

/// <summary>操作行を並べる表。列幅は全行の中身に合わせて自動</summary>
public sealed class ActionTable : TableLayoutPanel, IThemeAware
{
    private readonly List<ActionRow> _rows = new();

    public ActionTable()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 7;
        Ui.EnableDoubleBuffer(this);
        RowCount = 0;
        Margin = new Padding(0, 2, 0, 0);
        Padding = new Padding(0);
        // 名前の列 (1 列目) が余った幅を受け持つ (カードの幅いっぱいに広がった時に右側が空かないように)
        for (int i = 0; i < ColumnCount; i++)
            ColumnStyles.Add(i == 1 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));

        string[] header = { "対象", "操作", "モード", "値", "時間ms", "回数", "実行" };
        AddRowStyle();
        for (int i = 0; i < header.Length; i++)
        {
            var h = new LLabel
            {
                Text = header[i], AutoSize = true, Tag = "sub",
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(3, 0, 3, 0)
            };
            if (Ui.SmallFont != null) h.Font = Ui.SmallFont;
            Controls.Add(h, i, 0);
        }
    }

    private int AddRowStyle()
    {
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowCount++;
        return RowCount - 1;
    }

    public void AddRow(ActionRow row)
    {
        int y = AddRowStyle();
        var cells = row.Cells;
        for (int i = 0; i < cells.Length; i++) Controls.Add(cells[i], i, y);
        _rows.Add(row);
    }

    public void OnThemeChanged()
    {
        foreach (var r in _rows) r.UpdateRunVisual();
    }
}
