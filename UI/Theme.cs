using VRCOscDebugClient.Core;
namespace VRCOscDebugClient.UI;

public interface IThemeAware
{
    void OnThemeChanged();
}

/// <summary>ライト / ダーク テーマ。Label.Tag="sub" で補助色、Tag="keep" で色を変更しない</summary>
public static class Theme
{
    public static bool Dark { get; set; } = true;

    public static Color Back => Dark ? C(0x1B1C20) : C(0xECEEF2);
    public static Color Card => Dark ? C(0x25272D) : C(0xFFFFFF);
    public static Color InputBack => Dark ? C(0x30333A) : C(0xF5F6F8);
    public static Color Fore => Dark ? C(0xE8E8EA) : C(0x1E1F22);
    public static Color SubFore => Dark ? C(0x9A9DA5) : C(0x6A6D75);
    public static Color ButtonBack => Dark ? C(0x3A3D45) : C(0xE2E5EA);
    public static Color Border => Dark ? C(0x4A4E57) : C(0xC6CAD2);
    public static Color Accent => Dark ? C(0x5B8CFF) : C(0x3B6FE0);
    public static Color Selection => Dark ? C(0x2F4E86) : C(0xC9DBFF);
    public static Color Active => Dark ? C(0x2E9D62) : C(0x34B26E);
    public static Color Danger => Dark ? C(0xC6464A) : C(0xE05555);
    public static Color Ok => Dark ? C(0x5AD88C) : C(0x1E8E3E);
    public static Color Off => Dark ? C(0x6B6F76) : C(0x9E9E9E);
    public static Color Error => Dark ? C(0xFF7A7A) : C(0xC62828);
    public static Color Warn => Dark ? C(0xFFB74D) : C(0xA65E00);
    public static Color HighlightBack => C(0xFFD54F);
    public static Color HighlightFore => Color.Black;

    private static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    /// <summary>フォーム全体に適用</summary>
    public static void Apply(Control root) => Apply(root, Back);

    /// <summary>bg = このコントロールが置かれている面の色</summary>
    public static void Apply(Control root, Color bg)
    {
        ApplyTree(root, bg);
        root.Invalidate(true);
    }

    private static void ApplyTree(Control c, Color bg)
    {
        Localize.Apply(c); // 英語表示の時は文字を訳す
        ApplyOne(c, bg);
        Color childBg = c is RoundPanel rp ? rp.FillColor : bg;
        foreach (Control ch in c.Controls) ApplyTree(ch, childBg);
        if (c is IThemeAware t) t.OnThemeChanged();
    }

    private static void ApplyOne(Control c, Color bg)
    {
        if (c.Tag is string tag && tag == "keep") return;

        switch (c)
        {
            case RToggle t:
                t.BackColor = ButtonBack;
                t.ForeColor = Fore;
                break;
            case RTab rt:
                rt.BackColor = bg;
                rt.ForeColor = Fore;
                break;
            case Button b:
                b.BackColor = ButtonBack;
                b.ForeColor = Fore;
                if (b is not RButton)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Border;
                }
                break;
            case TextBoxBase tb:
                tb.BackColor = InputBack;
                tb.ForeColor = Fore;
                tb.BorderStyle = BorderStyle.None;
                if (RoundDecor.IsDecorated(tb)) RoundDecor.Attach(tb.Parent);
                break;
            case NumericUpDown n:
                n.BackColor = InputBack;
                n.ForeColor = Fore;
                n.BorderStyle = BorderStyle.None;
                if (RoundDecor.IsDecorated(n)) RoundDecor.Attach(n.Parent);
                break;
            case ListBox or ListView:
                c.BackColor = InputBack;
                c.ForeColor = Fore;
                break;
            case ComboBox cmb:
                cmb.FlatStyle = FlatStyle.Flat;
                cmb.BackColor = InputBack;
                cmb.ForeColor = Fore;
                break;
            case FastLabel fl:
                fl.BackColor = bg;
                fl.ForeColor = fl.Sub ? SubFore : Fore;
                break;
            case Label l:
                l.BackColor = bg;
                l.ForeColor = (l.Tag is string s && s == "sub") ? SubFore : Fore;
                break;
            default:
                c.BackColor = bg;
                c.ForeColor = Fore;
                break;
        }
    }

    /// <summary>
    /// ListView をテーマ色で描画する (ヘッダー・行・選択行)。
    /// 最後の列を残りの幅いっぱいに広げ、ヘッダー右側に白い余白が出ないようにする。
    /// </summary>
    public static void MakeThemedListView(ListView lv)
    {
        lv.OwnerDraw = true;
        lv.DrawColumnHeader += (s, e) =>
        {
            using (var b = new SolidBrush(InputBack)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, L10n.Auto(e.Header.Text), lv.Font, r, SubFore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        lv.DrawItem += (s, e) => { }; // 行は DrawSubItem で描く
        lv.DrawSubItem += (s, e) =>
        {
            bool sel = e.Item.Selected;
            using (var b = new SolidBrush(sel ? Selection : InputBack)) e.Graphics.FillRectangle(b, e.Bounds);
            Color fore = e.Item.ForeColor;
            if (fore.IsEmpty || fore == lv.ForeColor || fore == SystemColors.WindowText) fore = Fore;
            var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 6), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, lv.Font, r, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        lv.Resize += (_, _) => FitLastColumn(lv);
        lv.ClientSizeChanged += (_, _) => FitLastColumn(lv);
        lv.HandleCreated += (_, _) => FitLastColumn(lv);
        lv.ColumnWidthChanged += (_, e) =>
        {
            if (e.ColumnIndex != lv.Columns.Count - 1) FitLastColumn(lv);
        };
    }

    /// <summary>最後の列を残りの幅いっぱいにする</summary>
    public static void FitLastColumn(ListView lv)
    {
        int n = lv.Columns.Count;
        if (n == 0 || !lv.IsHandleCreated) return;
        int others = 0;
        for (int i = 0; i < n - 1; i++) others += lv.Columns[i].Width;
        int w = lv.ClientSize.Width - others - 1;
        if (w > 40 && lv.Columns[n - 1].Width != w) lv.Columns[n - 1].Width = w;
    }
}
