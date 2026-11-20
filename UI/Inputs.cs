using System.Drawing.Drawing2D;
using System.Globalization;

namespace VRCOscDebugClient.UI;

/// <summary>
/// スライダー (パラメータタブと同じ見た目: 細い溝 + 塗り + 白いつまみ)。TrackBar の代わり。
/// ドラッグ・クリック・矢印キー・ホイール (フォーカス中) で操作。
/// </summary>
public class RSlider : Control
{
    private int _min, _max = 100, _value;
    private bool _drag;

    public int SmallChange { get; set; } = 1;
    public int LargeChange { get; set; } = 10;
    /// <summary>TrackBar との互換用 (目盛りは描かない)</summary>
    public TickStyle TickStyle { get; set; } = TickStyle.None;

    /// <summary>値が変わった</summary>
    public event EventHandler ValueChanged;
    /// <summary>ユーザーが動かした</summary>
    public event EventHandler Scroll;

    public RSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(120, 26);
    }

    public int Minimum
    {
        get => _min;
        set { _min = value; if (_max < _min) _max = _min; Value = _value; Invalidate(); }
    }

    public int Maximum
    {
        get => _max;
        set { _max = value; if (_min > _max) _min = _max; Value = _value; Invalidate(); }
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private float D => DeviceDpi / 96f;
    private float Knob => 14 * D;
    private (float X0, float X1) Range => (Knob / 2 + 2 * D, Width - Knob / 2 - 2 * D);

    private float Pos(int v)
    {
        var (x0, x1) = Range;
        return _max == _min ? x0 : x0 + (v - _min) / (float)(_max - _min) * (x1 - x0);
    }

    private int ValueAt(int x)
    {
        var (x0, x1) = Range;
        float t = x1 <= x0 ? 0 : Math.Clamp((x - x0) / (x1 - x0), 0f, 1f);
        return _min + (int)Math.Round(t * (_max - _min));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Rounded.ParentBack(this);
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var (x0, x1) = Range;
        float cy = Height / 2f, th = 4 * D;
        float pos = Pos(_value);
        Color track = Theme.ButtonBack, fill = Theme.Accent, knob = Color.White;
        if (!Enabled) { fill = Rounded.Blend(fill, back, 0.5f); knob = Rounded.Blend(knob, back, 0.5f); }

        using (var path = Rounded.Path(new RectangleF(x0, cy - th / 2, Math.Max(1, x1 - x0), th), th / 2))
        using (var b = new SolidBrush(track))
            g.FillPath(b, path);
        if (pos - x0 > 0.5f)
            using (var path = Rounded.Path(new RectangleF(x0, cy - th / 2, pos - x0, th), th / 2))
            using (var b = new SolidBrush(fill))
                g.FillPath(b, path);

        float k = Knob;
        using (var kb = new SolidBrush(knob)) g.FillEllipse(kb, pos - k / 2, cy - k / 2, k, k);
        using (var kp = new Pen(fill, (Focused ? 2.6f : 2f) * D)) g.DrawEllipse(kp, pos - k / 2, cy - k / 2, k, k);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        _drag = true;
        SetByUser(ValueAt(e.X));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag) SetByUser(ValueAt(e.X));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _drag = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Focused && Enabled) SetByUser(_value + Math.Sign(e.Delta) * SmallChange);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int v = e.KeyCode switch
        {
            Keys.Left or Keys.Down => _value - SmallChange,
            Keys.Right or Keys.Up => _value + SmallChange,
            Keys.PageDown => _value - LargeChange,
            Keys.PageUp => _value + LargeChange,
            Keys.Home => _min,
            Keys.End => _max,
            _ => int.MinValue
        };
        if (v != int.MinValue) { SetByUser(v); e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    private void SetByUser(int v)
    {
        int before = _value;
        Value = v;
        if (_value != before) Scroll?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// 数値入力 (パラメータタブと同じ見た目: 角丸の枠に − 数字 + )。NumericUpDown の代わり。
/// − / + のクリック (押し続けで連続)・数字の直接入力・↑↓キー・ホイール (フォーカス中) で変更。
/// </summary>
public class RNumber : Control, IThemeAware
{
    private readonly TextBox _box;
    private readonly System.Windows.Forms.Timer _repeat = new();
    private decimal _min, _max = 100, _value, _inc = 1;
    private int _decimals;
    private int _repeatDir;
    private bool _hoverMinus, _hoverPlus;

    public event EventHandler ValueChanged;

    public RNumber()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        _box = new TextBox
        {
            BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center, Tag = "keep",
            BackColor = Theme.InputBack, ForeColor = Theme.Fore
        };
        Controls.Add(_box);
        _box.KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Up: Step(1); e.Handled = e.SuppressKeyPress = true; return;
                case Keys.Down: Step(-1); e.Handled = e.SuppressKeyPress = true; return;
                case Keys.Enter: Commit(); break;
                case Keys.Escape: ShowValue(); e.SuppressKeyPress = true; return;
            }
            OnKeyDown(e); // 外側の KeyDown (Enter で送信など) に渡す
        };
        _box.Leave += (_, _) => Commit();
        _box.MouseWheel += (_, e) => { if (_box.Focused) Step(Math.Sign(e.Delta)); };
        _repeat.Tick += (_, _) =>
        {
            _repeat.Interval = 60;
            if (_repeatDir != 0) Step(_repeatDir);
        };
        ShowValue();
        Height = PreferredHeight;
    }

    // ---------------- NumericUpDown と同じ使い方 ----------------
    public decimal Minimum { get => _min; set { _min = value; if (_max < _min) _max = _min; Value = _value; } }
    public decimal Maximum { get => _max; set { _max = value; if (_min > _max) _min = _max; Value = _value; } }
    public decimal Increment { get => _inc; set => _inc = value <= 0 ? 1 : value; }
    public int DecimalPlaces { get => _decimals; set { _decimals = Math.Clamp(value, 0, 6); ShowValue(); } }
    public HorizontalAlignment TextAlign { get => _box.TextAlign; set => _box.TextAlign = value == HorizontalAlignment.Right ? HorizontalAlignment.Center : value; }

    public decimal Value
    {
        get => _value;
        set
        {
            decimal v = Math.Round(Math.Clamp(value, _min, _max), _decimals);
            if (v == _value) { ShowValue(); return; }
            _value = v;
            ShowValue();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>中の入力欄を含めてフォーカスがあるか</summary>
    public new bool Focused => ContainsFocus;

    public override string Text
    {
        get => _box?.Text ?? base.Text;
        set { if (_box != null) _box.Text = value; else base.Text = value; }
    }

    // ---------------- 表示 ----------------
    private float D => DeviceDpi / 96f;
    private int ButtonW => (int)Math.Round(20 * D);
    private int PreferredHeight => _box.PreferredHeight + (int)Math.Round(8 * D);

    private void ShowValue()
    {
        string t = _value.ToString("F" + _decimals, CultureInfo.InvariantCulture);
        if (_box.Text != t) _box.Text = t;
    }

    private void Commit()
    {
        string t = _box.Text.Trim();
        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ||
            decimal.TryParse(t, NumberStyles.Number, CultureInfo.CurrentCulture, out v))
            Value = v;
        else ShowValue();
    }

    private void Step(int dir)
    {
        if (!Enabled) return;
        Commit();
        Value = _value + dir * _inc;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _box.Font = Font;
        Height = PreferredHeight;
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int bw = ButtonW, h = _box.PreferredHeight;
        _box.SetBounds(bw, Math.Max(0, (Height - h) / 2), Math.Max(10, Width - bw * 2), h);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _box.Enabled = Enabled;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    public void OnThemeChanged()
    {
        _box.BackColor = Theme.InputBack;
        _box.ForeColor = Theme.Fore;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Rounded.ParentBack(this);
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Color fill = Theme.InputBack, border = ContainsFocus ? Theme.Accent : Theme.Border;
        Color fore = Enabled ? Theme.Fore : Theme.SubFore;
        if (!Enabled) { fill = Rounded.Blend(fill, back, 0.4f); border = Rounded.Blend(border, back, 0.5f); }
        using (var path = Rounded.Path(r, 6 * D))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var p = new Pen(border)) g.DrawPath(p, path);
        }
        int bw = ButtonW;
        var minus = new Rectangle(0, 0, bw, Height);
        var plus = new Rectangle(Width - bw, 0, bw, Height);
        using (var hb = new SolidBrush(Rounded.Blend(fill, Theme.Fore, 0.08f)))
        {
            if (_hoverMinus && Enabled) g.FillRectangle(hb, Rectangle.Inflate(minus, -2, -2));
            if (_hoverPlus && Enabled) g.FillRectangle(hb, Rectangle.Inflate(plus, -2, -2));
        }
        const TextFormatFlags cf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, "−", Font, minus, fore, cf);
        TextRenderer.DrawText(g, "+", Font, plus, fore, cf);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool m = e.X < ButtonW, p = e.X >= Width - ButtonW;
        if (m != _hoverMinus || p != _hoverPlus) { _hoverMinus = m; _hoverPlus = p; Invalidate(); }
        Cursor = m || p ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverMinus = _hoverPlus = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        int dir = e.X < ButtonW ? -1 : e.X >= Width - ButtonW ? 1 : 0;
        if (dir == 0) { _box.Focus(); return; }
        Step(dir);
        _repeatDir = dir;
        _repeat.Interval = 400; // 押し続けると連続で変わる
        _repeat.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _repeatDir = 0;
        _repeat.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _repeat.Dispose();
        base.Dispose(disposing);
    }
}
