namespace VRCOscDebugClient.UI;

/// <summary>
/// 同意・確認のダイアログ。常に最前面で開き、メイン画面 (最前面表示中でも) の裏に回らない。
/// </summary>
public static class ConfirmDialog
{
    public static bool Ask(Control from, string title, string message, string okText = "OK", string cancelText = "キャンセル",
        bool warning = true, Font font = null)
    {
        font ??= (from as Form ?? from?.FindForm())?.Font ?? SystemFonts.MessageBoxFont;
        bool noOwner = from == null;
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = noOwner ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = noOwner, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = font, Padding = new Padding(14, 12, 14, 10),
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96F, 96F),
            TopMost = true, // 最前面表示のメイン画面より必ず前に出す
        };

        var icon = new LLabel
        {
            Text = warning ? "⚠" : "ℹ", AutoSize = true, Margin = new Padding(0, 0, 10, 0),
            Font = new Font(font.FontFamily, font.Size * 2.2f), Tag = "keep",
            ForeColor = warning ? Theme.Warn : Theme.Accent,
        };
        var text = new LLabel
        {
            Text = message, AutoSize = true, MaximumSize = new Size(Ui.W(420), 0), Margin = new Padding(0, 4, 0, 10)
        };
        var ok = Ui.Btn(okText, 90);
        var cancel = Ui.Btn(cancelText, 90);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;

        var body = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.Controls.Add(icon, 0, 0);
        body.Controls.Add(text, 1, 0);
        var buttons = Ui.Row(ok, cancel);
        buttons.Anchor = AnchorStyles.Right;
        body.Controls.Add(buttons, 1, 1);
        form.Controls.Add(body);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) =>
        {
            form.TopMost = true;
            form.BringToFront();
            form.Activate();
            if (warning) cancel.Focus(); // 誤って Enter で同意しないように
            else ok.Focus();
        };
        Theme.Apply(form);

        var result = Ui.RunModal(from, owner => owner != null ? form.ShowDialog(owner) : form.ShowDialog());
        return result == DialogResult.OK;
    }
}
