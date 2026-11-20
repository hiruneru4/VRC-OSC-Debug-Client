using System.Runtime.CompilerServices;
using VRCOscDebugClient.Core;

namespace VRCOscDebugClient.UI;

/// <summary>
/// 画面の部品を今の言語で表示する (英語の時だけ働く)。
/// Theme.Apply から全部品に対して呼ばれるので、ダイアログ・別ウィンドウも同じ仕組みで訳される。
/// 入力欄の中身 (ユーザーの文字) は訳さない。
/// </summary>
public static class Localize
{
    private static readonly ConditionalWeakTable<Control, object> Hooked = new();

    public static void Apply(Control c)
    {
        if (!L10n.En) return;
        if (c.ContextMenuStrip != null) Menu(c.ContextMenuStrip.Items);
        switch (c)
        {
            case TextBox tb:
                if (!string.IsNullOrEmpty(tb.PlaceholderText)) tb.PlaceholderText = L10n.Auto(tb.PlaceholderText);
                return;
            case TextBoxBase or NumericUpDown:
                return;
            case ComboBox cmb:
                // 選択専用の欄だけ (入力できる欄は入力された文字で探す処理があるので訳さない)
                if (cmb.DropDownStyle == ComboBoxStyle.DropDownList && Once(cmb))
                {
                    cmb.FormattingEnabled = true;
                    cmb.Format += (_, e) => { if (e.ListItem is string) e.Value = L10n.Auto(Convert.ToString(e.Value)); };
                    cmb.Invalidate();
                }
                return;
            case ListBox lb:
                if (lb.DrawMode == DrawMode.Normal && lb is CheckedListBox && Once(lb))
                {
                    lb.FormattingEnabled = true;
                    lb.Format += (_, e) => e.Value = L10n.Auto(Convert.ToString(e.Value));
                }
                return;
            case ListView lv:
                foreach (ColumnHeader col in lv.Columns) col.Text = L10n.Auto(col.Text);
                return;
            case Form f:
                f.Text = L10n.Auto(f.Text);
                return;
            case Label or ButtonBase or FastLabel or GroupBox:
                if (!string.IsNullOrEmpty(c.Text)) c.Text = L10n.Auto(c.Text);
                return;
        }
    }

    private static bool Once(Control c)
    {
        if (Hooked.TryGetValue(c, out _)) return false;
        Hooked.Add(c, new object());
        return true;
    }

    public static void Menu(ToolStripItemCollection items)
    {
        if (!L10n.En) return;
        foreach (ToolStripItem item in items)
        {
            item.Text = L10n.Auto(item.Text);
            if (!string.IsNullOrEmpty(item.ToolTipText)) item.ToolTipText = L10n.Auto(item.ToolTipText);
            if (item is ToolStripDropDownItem dd && dd.HasDropDownItems) Menu(dd.DropDownItems);
        }
    }

    /// <summary>ツールチップを訳して描く (英語の時だけ)</summary>
    public static void Hook(ToolTip tip)
    {
        if (!L10n.En) return;
        tip.OwnerDraw = true;
        var font = SystemFonts.StatusFont ?? SystemFonts.DefaultFont;
        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left;
        tip.Popup += (_, e) =>
        {
            string text = L10n.Auto(tip.GetToolTip(e.AssociatedControl));
            var sz = TextRenderer.MeasureText(text, font, new Size(520, 0), flags);
            e.ToolTipSize = new Size(sz.Width + 12, sz.Height + 8);
        };
        tip.Draw += (_, e) =>
        {
            e.DrawBackground();
            e.DrawBorder();
            var r = Rectangle.Inflate(e.Bounds, -6, -4);
            TextRenderer.DrawText(e.Graphics, L10n.Auto(e.ToolTipText), font, r, SystemColors.InfoText, flags);
        };
    }
}

/// <summary>文字を設定した時に今の言語へ変えるラベル (後から文字を変えるラベル用)</summary>
public class LLabel : Label
{
    public override string Text
    {
        get => base.Text;
        set => base.Text = L10n.Auto(value);
    }
}
