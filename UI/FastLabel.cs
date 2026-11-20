using VRCOscDebugClient.Core;
namespace VRCOscDebugClient.UI;

/// <summary>
/// 軽量ラベル。文字を変えても再描画だけで、レイアウトの再計算を起こさない
/// (通常の AutoSize Label は文字が変わるたびに親全体を再レイアウトするため、
///  頻繁に更新する表示 (ステータス・値・件数) で UI が重くなる原因になる)。
/// 高さはフォントから、幅は LogicalWidth (96dpi 基準) から決まる。Dock=Fill でも使える。
/// </summary>
public sealed class FastLabel : Control
{
    /// <summary>英語表示の時は設定した文字を訳す</summary>
    public override string Text { get => base.Text; set => base.Text = L10n.Auto(value); }

    private int _logicalWidth = 80;

    /// <summary>true = 補助色で表示</summary>
    public bool Sub { get; set; }

    /// <summary>幅 (96dpi 基準の px)。Dock 時は無視</summary>
    public int LogicalWidth
    {
        get => _logicalWidth;
        set { _logicalWidth = value; PerformLayoutIfNeeded(); }
    }

    public ContentAlignment TextAlign { get; set; } = ContentAlignment.MiddleLeft;

    public FastLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AutoSize = true;
        Margin = new Padding(3, 3, 3, 3);
    }

    private void PerformLayoutIfNeeded()
    {
        if (Parent != null && IsHandleCreated) Parent.PerformLayout(this, "Bounds");
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int h = TextRenderer.MeasureText("あAgy|", Font).Height + 2;
        int w = (int)Math.Round(_logicalWidth * Ui.Scale * DeviceDpi / 96f);
        return new Size(w, h);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate(); // レイアウトはしない
        base.OnTextChanged(e);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        PerformLayoutIfNeeded();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor.A == 255 ? BackColor : Rounded.ParentBack(this));
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    TextFormatFlags.VerticalCenter;
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleCenter or ContentAlignment.TopCenter or ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter,
            ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.Left
        };
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
    }
}

/// <summary>右クリックメニューのテーマ</summary>
public static class ThemedMenu
{
    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.Accent;
        public override Color MenuItemSelected => Theme.Selection;
        public override Color MenuItemSelectedGradientBegin => Theme.Selection;
        public override Color MenuItemSelectedGradientEnd => Theme.Selection;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Card;
    }

    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new Colors()) { RoundedEdges = true };
        menu.BackColor = Theme.Card;
        Localize.Menu(menu.Items);
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = Theme.Fore;
            item.BackColor = Theme.Card;
        }
    }
}

/// <summary>小さな入力ダイアログ</summary>
public static class InputDialog
{
    /// <summary>
    /// 文字を入力 (choices を渡すと種類の選択欄も出す)。キャンセルで null。
    /// original を渡すと「元の値」として表示し、入力欄にも最初から入れておく。
    /// </summary>
    public static (string Text, string Choice)? Ask(Control from, Font font, string title, string prompt,
        string initial, string[] choices = null, string initialChoice = null, string original = null)
    {
        var ownerForm = from as Form ?? from?.FindForm();
        bool wasTop = ownerForm != null && ownerForm.TopMost;

        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = font, Padding = new Padding(10),
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96F, 96F),
            TopMost = wasTop // メイン画面が最前面なら、ダイアログも最前面に
        };
        var label = Ui.L(prompt);
        var text = Ui.Txt(string.IsNullOrEmpty(initial) ? original ?? "" : initial, 260);
        ComboBox combo = null;
        if (choices != null)
        {
            combo = Ui.Combo(90, false, choices.Cast<object>().ToArray());
            if (initialChoice != null && choices.Contains(initialChoice)) combo.SelectedItem = initialChoice;
        }
        var ok = Ui.Btn("OK", 70);
        var cancel = Ui.Btn("キャンセル", 70);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;

        var list = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Dock = DockStyle.Fill
        };
        list.Controls.Add(label);
        if (original != null)
        {
            var orig = Ui.Sub($"元の値: {(original.Length == 0 ? "(未受信)" : original)}");
            orig.Font = font;
            list.Controls.Add(orig);
        }
        list.Controls.Add(combo != null ? Ui.Row(text, combo) : text);
        list.Controls.Add(Ui.Row(ok, cancel));
        form.Controls.Add(list);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) =>
        {
            form.Activate();
            text.Focus();
            text.SelectAll(); // すぐ上書き入力できるように
        };
        Theme.Apply(form);

        var result = Ui.RunModal(from, owner => owner != null ? form.ShowDialog(owner) : form.ShowDialog());
        if (result != DialogResult.OK) return null;
        return (text.Text, combo?.SelectedItem as string);
    }
}
