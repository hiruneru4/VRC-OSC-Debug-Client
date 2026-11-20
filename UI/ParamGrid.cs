using VRCOscDebugClient.Core;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace VRCOscDebugClient.UI;

/// <summary>パラメータ1つ分のデータ (表示は ParamGrid が描く)</summary>
public sealed class ParamItem
{
    public string Name, Address, Type;
    public bool Writable;
    public object Value;
    public bool Locked;
}

/// <summary>
/// パラメータ一覧を 1 つのコントロールで描く軽量な表。
/// 行ごとにスライダー等の部品を作らず、見えている行だけを描画するので、数百件でも一瞬で表示できる。
/// Bool = スイッチ (クリック) / Int = − 値 + (値部分クリックで入力) / Float = スライダー (ドラッグ)
/// </summary>
public sealed class ParamGrid : Control
{
    private readonly VScrollBar _vs = new() { Dock = DockStyle.Right };
    private List<ParamItem> _items = new();
    private readonly Dictionary<ParamItem, int> _index = new();
    private ParamItem _hover, _selected, _drag;
    private int _rowH = 30;

    /// <summary>ユーザーが値を変えた (項目, 新しい値)</summary>
    public event Action<ParamItem, object> ValueChanging;
    /// <summary>値の入力ダイアログを開きたい</summary>
    public event Action<ParamItem> EditRequested;
    /// <summary>右クリック (項目, 位置)</summary>
    public event Action<ParamItem, Point> MenuRequested;

    public ParamItem Selected => _selected;
    public int Count => _items.Count;

    public ParamGrid()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _vs.ValueChanged += (_, _) => Invalidate();
        Controls.Add(_vs);
    }

    private float D => DeviceDpi / 96f;
    private int Pad => (int)(8 * D);
    private int EditorW => (int)(190 * D * Ui.Scale);
    private int ValueW => (int)(90 * D * Ui.Scale);
    private int ContentW => ClientSize.Width - (_vs.Visible ? _vs.Width : 0);
    private int MaxScroll => Math.Max(0, _vs.Maximum - _vs.LargeChange + 1);

    public void SetItems(List<ParamItem> items)
    {
        _items = items;
        _index.Clear();
        for (int i = 0; i < items.Count; i++) _index[items[i]] = i;
        if (_hover != null && !_index.ContainsKey(_hover)) _hover = null;
        if (_selected != null && !_index.ContainsKey(_selected)) _selected = null;
        _drag = null;
        UpdateScroll();
        Invalidate();
    }

    /// <summary>1 行だけ描き直す (値の変化用、見えていなければ何もしない)</summary>
    public void InvalidateItem(ParamItem it)
    {
        if (!_index.TryGetValue(it, out int i)) return;
        int y = i * _rowH - _vs.Value;
        if (y + _rowH < 0 || y > ClientSize.Height) return;
        Invalidate(new Rectangle(0, y, ContentW, _rowH));
    }

    private void UpdateRowHeight()
    {
        _rowH = TextRenderer.MeasureText("あAg", Font).Height + (int)(12 * D);
    }

    private void UpdateScroll()
    {
        int total = _items.Count * _rowH;
        int h = Math.Max(1, ClientSize.Height);
        _vs.Visible = total > h;
        _vs.Minimum = 0;
        _vs.Maximum = Math.Max(0, total - 1);
        _vs.LargeChange = h;
        _vs.SmallChange = _rowH;
        if (_vs.Value > MaxScroll) _vs.Value = MaxScroll;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScroll(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateRowHeight(); UpdateScroll(); Invalidate(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateRowHeight(); UpdateScroll(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateRowHeight(); UpdateScroll(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int v = _vs.Value - Math.Sign(e.Delta) * _rowH * 3;
        _vs.Value = Math.Clamp(v, 0, MaxScroll);
    }

    // ---------------- 位置計算 ----------------
    private int IndexAt(int y)
    {
        int i = (y + _vs.Value) / _rowH;
        return i >= 0 && i < _items.Count ? i : -1;
    }

    private Rectangle RowRect(int i) => new(0, i * _rowH - _vs.Value, ContentW, _rowH);

    private Rectangle EditorRect(int i)
    {
        var r = RowRect(i);
        return new Rectangle(ContentW - Pad - ValueW - EditorW, r.Y, EditorW, r.Height);
    }

    private Rectangle ValueRect(int i)
    {
        var r = RowRect(i);
        return new Rectangle(ContentW - Pad - ValueW, r.Y, ValueW, r.Height);
    }

    private (float X0, float X1) SliderRange(Rectangle er) => (er.X + 10 * D, er.Right - 10 * D);
    private int IntButtonW => (int)(26 * D);
    private int IntBoxW => (int)(120 * D);

    // ---------------- マウス ----------------
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = IndexAt(e.Y);
        if (i < 0) return;
        var it = _items[i];
        var old = _selected;
        _selected = it;
        if (old != null) InvalidateItem(old);
        InvalidateItem(it);

        if (e.Button == MouseButtons.Right)
        {
            MenuRequested?.Invoke(it, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left || !it.Writable) return;

        var er = EditorRect(i);
        if (!er.Contains(e.Location)) return;
        switch (it.Type)
        {
            case "Bool":
                ValueChanging?.Invoke(it, !ToBool(it.Value));
                break;
            case "Int":
                int x = e.X - er.X;
                int cur = (int)Math.Round(ToFloat(it.Value));
                if (x < IntButtonW) ValueChanging?.Invoke(it, Math.Max(0, cur - 1));
                else if (x >= IntBoxW - IntButtonW && x < IntBoxW) ValueChanging?.Invoke(it, Math.Min(255, cur + 1));
                else if (x < IntBoxW) EditRequested?.Invoke(it);
                break;
            default:
                _drag = it;
                Capture = true;
                SetFloatFromX(it, er, e.X);
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag != null)
        {
            if (_index.TryGetValue(_drag, out int di)) SetFloatFromX(_drag, EditorRect(di), e.X);
            return;
        }
        int i = IndexAt(e.Y);
        var h = i >= 0 ? _items[i] : null;
        if (h == _hover) return;
        var old = _hover;
        _hover = h;
        if (old != null) InvalidateItem(old);
        if (h != null) InvalidateItem(h);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = null;
        Capture = false;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        var old = _hover;
        _hover = null;
        if (old != null) InvalidateItem(old);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = IndexAt(e.Y);
        if (i < 0 || e.Button != MouseButtons.Left) return;
        if (_items[i].Type == "Float" && EditorRect(i).Contains(e.Location)) return; // スライダー操作中
        if (_items[i].Type == "Bool" && EditorRect(i).Contains(e.Location)) return;
        EditRequested?.Invoke(_items[i]);
    }

    private void SetFloatFromX(ParamItem it, Rectangle er, int x)
    {
        var (x0, x1) = SliderRange(er);
        float t = Math.Clamp((x - x0) / Math.Max(1f, x1 - x0), 0f, 1f);
        float v = (float)Math.Round(-1f + 2f * t, 2);
        if (Math.Abs(v - ToFloat(it.Value)) >= 0.005f || it.Value == null) ValueChanging?.Invoke(it, v);
    }

    // ---------------- 描画 ----------------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, L10n.Auto("パラメータがありません (アバターを選ぶか、VRChat から受信してください)"), Font,
                new Rectangle(Pad, Pad, ContentW - Pad * 2, _rowH), Theme.SubFore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int first = Math.Max(0, (_vs.Value + e.ClipRectangle.Top) / _rowH);
        int last = Math.Min(_items.Count - 1, (_vs.Value + e.ClipRectangle.Bottom) / _rowH);
        for (int i = first; i <= last; i++) DrawRow(g, i);
    }

    private void DrawRow(Graphics g, int i)
    {
        var it = _items[i];
        var row = RowRect(i);
        var bg = BackColor;

        if (it == _selected || it == _hover)
        {
            var hl = it == _selected ? Rounded.Blend(bg, Theme.Selection, 0.6f) : Rounded.Blend(bg, Theme.Fore, 0.05f);
            var rr = new RectangleF(row.X + 2, row.Y + 1, row.Width - 4, row.Height - 2);
            using var path = Rounded.Path(rr, 6 * D);
            using var b = new SolidBrush(hl);
            g.FillPath(b, path);
        }

        const TextFormatFlags tf = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                   TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        var er = EditorRect(i);
        var nameRect = new Rectangle(Pad, row.Y, er.X - Pad * 2, row.Height);
        Color nameColor = it.Locked ? Theme.Accent : it.Writable ? Theme.Fore : Theme.SubFore;
        TextRenderer.DrawText(g, it.Name, Font, nameRect, nameColor, tf | TextFormatFlags.Left);

        float alpha = it.Writable ? 1f : 0.45f;
        switch (it.Type)
        {
            case "Bool": DrawSwitch(g, er, ToBool(it.Value), alpha, bg); break;
            case "Int": DrawInt(g, er, (int)Math.Round(ToFloat(it.Value)), alpha, bg); break;
            default: DrawSlider(g, er, ToFloat(it.Value), alpha, bg); break;
        }

        string val = it.Value == null ? "-" : it.Type switch
        {
            "Bool" => ToBool(it.Value) ? "ON" : "OFF",
            "Int" => ((int)Math.Round(ToFloat(it.Value))).ToString(CultureInfo.InvariantCulture),
            _ => ToFloat(it.Value).ToString("0.00", CultureInfo.InvariantCulture)
        };
        if (it.Locked) val = "🔒 " + val;
        TextRenderer.DrawText(g, val, Font, ValueRect(i), it.Locked ? Theme.Accent : Theme.SubFore, tf | TextFormatFlags.Right);
    }

    private Color Fade(Color c, float alpha, Color bg) => alpha >= 1f ? c : Rounded.Blend(bg, c, alpha);

    private void DrawSwitch(Graphics g, Rectangle er, bool on, float alpha, Color bg)
    {
        float w = 44 * D, h = 22 * D;
        var r = new RectangleF(er.X + 4 * D, er.Y + (er.Height - h) / 2f, w, h);
        using (var path = Rounded.Path(r, h / 2f))
        using (var b = new SolidBrush(Fade(on ? Theme.Active : Theme.ButtonBack, alpha, bg)))
            g.FillPath(b, path);
        float k = h - 6 * D;
        float kx = on ? r.Right - k - 3 * D : r.X + 3 * D;
        using var kb = new SolidBrush(Fade(on ? Color.White : Theme.SubFore, alpha, bg));
        g.FillEllipse(kb, kx, r.Y + 3 * D, k, k);
    }

    private void DrawInt(Graphics g, Rectangle er, int value, float alpha, Color bg)
    {
        float h = 24 * D;
        var box = new RectangleF(er.X, er.Y + (er.Height - h) / 2f, IntBoxW, h);
        using (var path = Rounded.Path(box, 6 * D))
        {
            using (var b = new SolidBrush(Fade(Theme.InputBack, alpha, bg))) g.FillPath(b, path);
            using (var p = new Pen(Fade(Theme.Border, alpha, bg))) g.DrawPath(p, path);
        }
        var minus = new Rectangle((int)box.X, (int)box.Y, IntButtonW, (int)box.Height);
        var plus = new Rectangle((int)(box.Right - IntButtonW), (int)box.Y, IntButtonW, (int)box.Height);
        var mid = new Rectangle(minus.Right, (int)box.Y, plus.X - minus.Right, (int)box.Height);
        const TextFormatFlags cf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        var fore = Fade(Theme.Fore, alpha, bg);
        TextRenderer.DrawText(g, "−", Font, minus, fore, cf);
        TextRenderer.DrawText(g, "+", Font, plus, fore, cf);
        TextRenderer.DrawText(g, value.ToString(CultureInfo.InvariantCulture), Font, mid, fore, cf);
    }

    private void DrawSlider(Graphics g, Rectangle er, float value, float alpha, Color bg)
    {
        var (x0, x1) = SliderRange(er);
        float cy = er.Y + er.Height / 2f;
        float th = 4 * D;
        float pos = x0 + (Math.Clamp(value, -1f, 1f) + 1f) / 2f * (x1 - x0);
        float zero = (x0 + x1) / 2f;

        using (var path = Rounded.Path(new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2))
        using (var b = new SolidBrush(Fade(Theme.ButtonBack, alpha, bg)))
            g.FillPath(b, path);
        float a = Math.Min(zero, pos), w = Math.Abs(pos - zero);
        if (w > 0.5f)
        {
            using var fb = new SolidBrush(Fade(Theme.Accent, alpha, bg));
            g.FillRectangle(fb, a, cy - th / 2, w, th);
        }
        using (var tick = new Pen(Fade(Theme.Border, alpha, bg))) g.DrawLine(tick, zero, cy - 5 * D, zero, cy + 5 * D);
        float k = 14 * D;
        using (var kb = new SolidBrush(Fade(Color.White, alpha, bg))) g.FillEllipse(kb, pos - k / 2, cy - k / 2, k, k);
        using (var kp = new Pen(Fade(Theme.Accent, alpha, bg), 2 * D)) g.DrawEllipse(kp, pos - k / 2, cy - k / 2, k, k);
    }

    public static bool ToBool(object v) => ToFloat(v) != 0f;

    public static float ToFloat(object v) => v switch
    {
        bool b => b ? 1f : 0f,
        int i => i,
        float f => f,
        _ => 0f
    };
}
