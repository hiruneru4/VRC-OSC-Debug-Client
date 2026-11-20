using VRCOscDebugClient.Core;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace VRCOscDebugClient.UI;

/// <summary>角丸描画の共通処理</summary>
public static class Rounded
{
    public static float Dpi(Control c) => c.DeviceDpi / 96f;

    public static float Radius(Control c, float px = 8f) => px * Dpi(c);

    public static GraphicsPath Path(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(255,
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>角の外側を塗る色 (親の背景色。角丸パネル内ならその塗り色)</summary>
    public static Color ParentBack(Control c)
    {
        var p = c.Parent;
        while (p != null)
        {
            if (p is RoundPanel rp) return rp.FillColor;
            if (p.BackColor.A == 255) return p.BackColor;
            p = p.Parent;
        }
        return Theme.Back;
    }

    /// <summary>角丸ボタンの描画</summary>
    public static void PaintButton(Control c, Graphics g, Color fill, Color fore, bool hover, bool down, bool border, string text = null)
    {
        var parentBack = ParentBack(c);
        g.Clear(parentBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (!c.Enabled)
        {
            fill = Blend(fill, parentBack, 0.45f);
            fore = Theme.SubFore;
        }
        else if (down) fill = Blend(fill, Theme.Fore, 0.18f);
        else if (hover) fill = Blend(fill, Theme.Fore, 0.09f);

        var rect = new RectangleF(0.5f, 0.5f, c.Width - 1f, c.Height - 1f);
        using (var path = Path(rect, Radius(c)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (border)
                using (var p = new Pen(Blend(fill, Theme.Border, 0.7f))) g.DrawPath(p, path);
        }
        TextRenderer.DrawText(g, L10n.Auto(text ?? c.Text), c.Font, c.ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>角丸の枠。Card=true でカード色、false で入力欄色 (リスト等の枠)</summary>
public class RoundPanel : Panel
{
    public bool Card { get; set; }
    public Color FillColor => Card ? Theme.Card : Theme.InputBack;

    /// <summary>
    /// true = ページの横幅いっぱいに広がるカード (AutoSize は使わず、FitTo で幅と高さを決める)。
    /// 中の部品のうち Ui.Stretch で印を付けたものは、カードの幅に合わせて広がる。
    /// </summary>
    public bool FillWidth { get; set; }

    public RoundPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(6);
    }

    private Control SingleFreeChild =>
        Controls.Count == 1 && Controls[0].Dock == DockStyle.None ? Controls[0] : null;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var c = SingleFreeChild;
        if (c == null) return base.GetPreferredSize(proposedSize);
        var s = c.GetPreferredSize(Size.Empty);
        return new Size(s.Width + Padding.Horizontal, s.Height + Padding.Vertical);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var c = SingleFreeChild;
        if (c == null) return;
        var s = c.GetPreferredSize(Size.Empty);
        c.SetBounds(Padding.Left, Padding.Top, s.Width, s.Height);
    }

    private bool _fitting, _hooked;

    /// <summary>横幅いっぱいカード: 幅を決めて、中身に合わせて高さを決める</summary>
    public void FitTo(int width)
    {
        var c = SingleFreeChild;
        if (c == null || _fitting) return;
        _fitting = true;
        try
        {
            if (!_hooked)
            {
                _hooked = true;
                c.SizeChanged += (_, _) => { if (!_fitting) FitTo(Width); }; // 中身が変わったら高さを合わせ直す
            }
            int inner = width - Padding.Horizontal;
            foreach (Control ch in c.Controls) Ui.ApplyStretch(ch, inner);
            var pref = c.GetPreferredSize(Size.Empty);
            int w = Math.Max(width, pref.Width + Padding.Horizontal);
            int h = pref.Height + Padding.Vertical;
            if (Width != w || Height != h) Size = new Size(w, h);
            c.SetBounds(Padding.Left, Padding.Top, pref.Width, pref.Height);
        }
        finally { _fitting = false; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Rounded.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Rounded.Path(rect, Rounded.Radius(this, Card ? 10f : 7f));
        using (var b = new SolidBrush(FillColor)) g.FillPath(b, path);
        using (var p = new Pen(Rounded.Blend(Theme.Border, FillColor, Card ? 0.35f : 0f))) g.DrawPath(p, path);
    }
}

/// <summary>角丸ボタン</summary>
public class RButton : Button
{
    /// <summary>英語表示の時は設定した文字を訳す</summary>
    public override string Text { get => base.Text; set => base.Text = L10n.Auto(value); }

    private bool _hover, _down;
    private string _face;

    /// <summary>
    /// 表示だけ変える文字 (Text はサイズ計算用のまま)。
    /// Text を変えるとレイアウトの再計算が走るので、頻繁に切り替える ▶/■ などはこちらを使う
    /// </summary>
    public string FaceText
    {
        get => _face;
        set { if (_face != value) { _face = value; Invalidate(); } }
    }

    public RButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e) =>
        Rounded.PaintButton(this, e.Graphics, BackColor, ForeColor, _hover, _down, true, _face);
}

/// <summary>角丸トグルボタン (CheckBox)</summary>
public class RToggle : CheckBox
{
    /// <summary>英語表示の時は設定した文字を訳す</summary>
    public override string Text { get => base.Text; set => base.Text = L10n.Auto(value); }

    private bool _hover, _down;

    /// <summary>ON の時の色 (未設定ならアクセント色)</summary>
    public Color CheckedColor { get; set; } = Color.Empty;

    /// <summary>設定すると ON/OFF で表示文字を切替 (Text は変えないのでレイアウトが走らない)</summary>
    public string OnText { get; set; }
    public string OffText { get; set; }

    public RToggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Appearance = Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TextAlign = ContentAlignment.MiddleCenter;
        Cursor = Cursors.Hand;
    }

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color fill = Checked ? (CheckedColor.IsEmpty ? Theme.Accent : CheckedColor) : BackColor;
        Color fore = Checked ? Color.White : ForeColor;
        Rounded.PaintButton(this, e.Graphics, fill, fore, _hover, _down, !Checked, Checked ? OnText : OffText);
    }
}

/// <summary>角丸タブ (RadioButton)。右側に動作数バッジを表示</summary>
public class RTab : RadioButton
{
    /// <summary>英語表示の時は設定した文字を訳す</summary>
    public override string Text { get => base.Text; set => base.Text = L10n.Auto(value); }

    private bool _hover;
    private int _badge;
    private Font _badgeFont;

    public int Badge
    {
        get => _badge;
        set
        {
            if (_badge == value) return;
            bool layout = (_badge > 0) != (value > 0);
            _badge = value;
            Invalidate();
            // 自動サイズの時だけ、バッジの分の幅を出し入れする
            if (layout && Dock == DockStyle.None && Parent != null) Parent.PerformLayout(this, "Bounds");
        }
    }

    public RTab()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Appearance = Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
        Cursor = Cursors.Hand;
    }

    private int BadgeSpace => (int)(22 * Rounded.Dpi(this));
    private int PadX => (int)(9 * Rounded.Dpi(this));

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + PadX * 2 + (_badge > 0 ? BadgeSpace : 0), t.Height + (int)(9 * Rounded.Dpi(this)));
    }

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnFontChanged(EventArgs e)
    {
        _badgeFont?.Dispose();
        _badgeFont = null;
        base.OnFontChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _badgeFont?.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Rounded.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill = Checked ? Theme.Accent : Theme.ButtonBack;
        if (_hover && !Checked) fill = Rounded.Blend(fill, Theme.Fore, 0.09f);
        Color fore = Checked ? Color.White : Theme.Fore;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = Rounded.Path(rect, Height / 2f))
        using (var b = new SolidBrush(fill))
            g.FillPath(b, path);

        // 文字は中央。バッジがある時だけ右側にバッジ (動作中の数)
        int badgeW = _badge > 0 ? BadgeSpace : 0;
        var textRect = new Rectangle(PadX / 3, 0, Width - badgeW - PadX / 3 * 2, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

        if (_badge > 0)
        {
            float bh = Height * 0.62f;
            float bx = Width - BadgeSpace + (BadgeSpace - bh) / 2f - 2 * Rounded.Dpi(this);
            var br = new RectangleF(bx, (Height - bh) / 2f, bh, bh);
            using (var b = new SolidBrush(Theme.Active)) g.FillEllipse(b, br);
            _badgeFont ??= new Font(Font.FontFamily, Math.Max(6f, Font.Size * 0.78f), FontStyle.Bold);
            TextRenderer.DrawText(g, _badge > 99 ? "99" : _badge.ToString(), _badgeFont, Rectangle.Round(br), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>角丸チェックボックス</summary>
public class RCheck : CheckBox
{
    /// <summary>英語表示の時は設定した文字を訳す</summary>
    public override string Text { get => base.Text; set => base.Text = L10n.Auto(value); }

    public RCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    // スイッチ (パラメータタブと同じ見た目: ON = 緑、つまみが右へ)
    private float D => Rounded.Dpi(this);
    private int SwW => (int)Math.Round(34 * D);
    private int SwH => (int)Math.Round(18 * D);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(string.IsNullOrEmpty(Text) ? " " : Text, Font);
        int textW = string.IsNullOrEmpty(Text) ? 0 : t.Width + (int)(6 * D);
        return new Size(SwW + 3 + textW, Math.Max(SwH, t.Height) + 4);
    }

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Rounded.ParentBack(this);
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new RectangleF(1.5f, (Height - SwH) / 2f, SwW - 2f, SwH);
        Color fill = Checked ? Theme.Active : Theme.ButtonBack;
        Color knob = Checked ? Color.White : Theme.SubFore;
        if (!Enabled) { fill = Rounded.Blend(fill, back, 0.5f); knob = Rounded.Blend(knob, back, 0.5f); }
        using (var path = Rounded.Path(r, r.Height / 2f))
        using (var b = new SolidBrush(fill))
            g.FillPath(b, path);
        float k = r.Height - 5 * D;
        float kx = Checked ? r.Right - k - 2.5f * D : r.X + 2.5f * D;
        using (var kb = new SolidBrush(knob)) g.FillEllipse(kb, kx, r.Y + 2.5f * D, k, k);
        if (Focused && ShowFocusCues)
            using (var p = new Pen(Theme.Accent)) g.DrawPath(p, Rounded.Path(RectangleF.Inflate(r, 1, 1), r.Height / 2f + 1));

        if (!string.IsNullOrEmpty(Text))
        {
            int x = SwW + (int)(6 * D);
            var tr = new Rectangle(x, 0, Width - x, Height);
            TextRenderer.DrawText(g, Text, Font, tr, Enabled ? ForeColor : Theme.SubFore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
}

/// <summary>
/// TextBox / NumericUpDown の周囲に角丸の枠を描く (枠線なしにした入力欄を親が装飾する)。
/// 入力欄の外側 3px を使うので Margin は 3 以上にしておく。
/// </summary>
public static class RoundDecor
{
    private static readonly ConditionalWeakTable<Control, object> _parents = new();
    private static readonly ConditionalWeakTable<Control, object> _children = new();

    public static bool IsDecorated(Control c) =>
        (c is TextBoxBase || c is NumericUpDown) && c.Dock == DockStyle.None && c.Parent is not RoundPanel;

    public static void Attach(Control parent)
    {
        if (parent == null || _parents.TryGetValue(parent, out _)) return;
        _parents.Add(parent, new object());
        parent.Paint += OnParentPaint;
        parent.ControlAdded += (_, e) => Hook(e.Control);
        foreach (Control ch in parent.Controls) Hook(ch);
    }

    private static void Hook(Control ch)
    {
        if (ch == null || _children.TryGetValue(ch, out _)) return;
        _children.Add(ch, new object());
        EventHandler inv = (_, _) => ch.Parent?.Invalidate();
        ch.LocationChanged += inv;
        ch.SizeChanged += inv;
        ch.VisibleChanged += inv;
        ch.EnabledChanged += inv;
        ch.Enter += inv;
        ch.Leave += inv;
    }

    private static void OnParentPaint(object sender, PaintEventArgs e)
    {
        var parent = (Control)sender;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (Control ch in parent.Controls)
        {
            if (!ch.Visible || !IsDecorated(ch)) continue;
            var r = Rectangle.Inflate(ch.Bounds, 3, 3);
            var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
            using var path = Rounded.Path(rf, Rounded.Radius(parent, 6f));
            using (var b = new SolidBrush(ch.BackColor)) g.FillPath(b, path);
            bool focus = ch.ContainsFocus;
            using var p = new Pen(focus ? Theme.Accent : Theme.Border, focus ? 1.6f : 1f);
            g.DrawPath(p, path);
        }
    }
}
