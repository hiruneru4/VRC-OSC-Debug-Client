using VRCOscDebugClient.Core;
namespace VRCOscDebugClient.UI;

/// <summary>
/// 軽量なタブ列。全てのタブを 1 つのコントロールで描く (タブごとの部品を作らない)。
/// 角丸やアンチエイリアスを使わない平らな描画で、マウスを動かした時も変わったタブだけ描き直す。
/// 選択中のタブは下線と文字色で示し、動作中の数があればバッジを出す。
/// </summary>
public sealed class TabStrip : Control, IThemeAware
{
    private readonly List<string> _items = new();
    private readonly List<int> _badges = new();
    private int _selected = -1, _hover = -1;
    private Font _bold, _small;

    /// <summary>選択が変わった (新しい番号)</summary>
    public event Action<int> SelectedChanged;

    /// <summary>true = タブを均等な幅で横いっぱいに並べる / false = 文字の幅に合わせる</summary>
    public bool EqualWidth { get; set; } = true;

    public IReadOnlyList<string> Items => _items;

    public TabStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Cursor = Cursors.Hand;
    }

    private float D => DeviceDpi / 96f;
    private int UnderlineH => Math.Max(2, (int)(3 * D));

    public void Add(string text)
    {
        _items.Add(text);
        _badges.Add(0);
        if (_selected < 0) _selected = 0;
        Invalidate();
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _items.Count || value == _selected) return;
            int old = _selected;
            _selected = value;
            InvalidateTab(old);
            InvalidateTab(value);
            SelectedChanged?.Invoke(value);
        }
    }

    public string SelectedText => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    /// <summary>名前でタブを選ぶ</summary>
    public void Select(string text)
    {
        int i = _items.IndexOf(text);
        if (i < 0) return;
        if (i == _selected) SelectedChanged?.Invoke(i); // 同じでもページは表示し直す
        else SelectedIndex = i;
    }

    /// <summary>バッジ (動作中の数)。変わった時だけそのタブを描き直す</summary>
    public void SetBadge(string text, int count)
    {
        int i = _items.IndexOf(text);
        if (i < 0 || _badges[i] == count) return;
        _badges[i] = count;
        InvalidateTab(i);
    }

    public int GetBadge(int index) => index >= 0 && index < _badges.Count ? _badges[index] : 0;

    public void SetBadge(int index, int count)
    {
        if (index < 0 || index >= _badges.Count || _badges[index] == count) return;
        _badges[index] = count;
        InvalidateTab(index);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int h = TextRenderer.MeasureText("あAg", Font).Height + (int)(14 * D);
        int w = 0;
        for (int i = 0; i < _items.Count; i++) w += NaturalWidth(i);
        return new Size(w, h);
    }

    private int NaturalWidth(int i) =>
        TextRenderer.MeasureText(L10n.Auto(_items[i]), BoldFont).Width + (int)(22 * D) + (_badges[i] > 0 ? (int)(18 * D) : 0);

    private Font BoldFont => _bold ??= new Font(Font, FontStyle.Bold);
    private Font SmallFont => _small ??= new Font(Font.FontFamily, Math.Max(6.5f, Font.Size * 0.75f), FontStyle.Bold);

    private Rectangle TabRect(int i)
    {
        if (i < 0 || i >= _items.Count) return Rectangle.Empty;
        if (EqualWidth)
        {
            float w = (float)ClientSize.Width / Math.Max(1, _items.Count);
            int x0 = (int)Math.Round(i * w), x1 = (int)Math.Round((i + 1) * w);
            return new Rectangle(x0, 0, x1 - x0, ClientSize.Height);
        }
        int x = 0;
        for (int k = 0; k < i; k++) x += NaturalWidth(k);
        return new Rectangle(x, 0, NaturalWidth(i), ClientSize.Height);
    }

    private int IndexAt(Point p)
    {
        for (int i = 0; i < _items.Count; i++)
            if (TabRect(i).Contains(p)) return i;
        return -1;
    }

    private void InvalidateTab(int i)
    {
        if (i >= 0 && i < _items.Count) Invalidate(TabRect(i));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Location);
        if (i == _hover) return;
        int old = _hover;
        _hover = i;
        InvalidateTab(old);
        InvalidateTab(i);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        int old = _hover;
        _hover = -1;
        InvalidateTab(old);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int i = IndexAt(e.Location);
        if (i >= 0) SelectedIndex = i;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        _bold?.Dispose(); _bold = null;
        _small?.Dispose(); _small = null;
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        // 下の区切り線
        using (var line = new SolidBrush(Theme.Border))
            g.FillRectangle(line, 0, ClientSize.Height - 1, ClientSize.Width, 1);

        const TextFormatFlags tf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                   TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        for (int i = 0; i < _items.Count; i++)
        {
            var r = TabRect(i);
            if (!r.IntersectsWith(e.ClipRectangle)) continue;
            bool sel = i == _selected;

            if (i == _hover && !sel)
                using (var hb = new SolidBrush(Rounded.Blend(BackColor, Theme.Fore, 0.07f)))
                    g.FillRectangle(hb, r.X, r.Y, r.Width, r.Height - 1);

            int badge = _badges[i];
            int badgeW = badge > 0 ? (int)(18 * D) : 0;
            var textRect = new Rectangle(r.X + 2, r.Y, r.Width - 4 - badgeW, r.Height - UnderlineH);
            TextRenderer.DrawText(g, L10n.Auto(_items[i]), sel ? BoldFont : Font, textRect, sel ? Theme.Fore : Theme.SubFore, tf);

            if (sel)
                using (var ub = new SolidBrush(Theme.Accent))
                    g.FillRectangle(ub, r.X + (int)(6 * D), r.Bottom - UnderlineH, r.Width - (int)(12 * D), UnderlineH);

            if (badge > 0)
            {
                int bh = (int)(15 * D);
                var br = new Rectangle(textRect.Right, r.Y + (r.Height - UnderlineH - bh) / 2, (int)(16 * D), bh);
                using (var bb = new SolidBrush(Theme.Active)) g.FillRectangle(bb, br);
                TextRenderer.DrawText(g, badge > 99 ? "99" : badge.ToString(), SmallFont, br, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
    }

    public void OnThemeChanged() => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bold?.Dispose(); _small?.Dispose(); }
        base.Dispose(disposing);
    }
}
