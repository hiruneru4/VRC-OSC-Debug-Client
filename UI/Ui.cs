using VRCOscDebugClient.Core;
using System.Runtime.CompilerServices;

namespace VRCOscDebugClient.UI;

/// <summary>
/// コントロール生成ヘルパー。高さは固定せず AutoSize に任せ、文字が欠けないようにする。
/// 幅は文字サイズ設定に比例 (Scale)、DPI 拡大は Form の AutoScale に任せる。
/// </summary>
public static class Ui
{
    public static float Scale { get; set; } = 1f;

    public static int W(int px) => (int)Math.Round(px * Scale);

    /// <summary>入力欄の幅の倍率 (設定する場所を広めに)</summary>
    public static float InputScale { get; set; } = 1.18f;

    /// <summary>入力欄の幅</summary>
    public static int WI(int px) => (int)Math.Round(px * Scale * InputScale);

    /// <summary>説明文用の小さめのフォント (メイン画面が設定する)</summary>
    public static Font SmallFont { get; set; }

    /// <summary>入力欄の外側に角丸枠を描くための余白</summary>
    public static Padding InputMargin => new(5, 4, 5, 4);

    public static Label L(string text) => new LLabel
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 1, 3)
    };

    /// <summary>説明文: 小さめの文字・補助色。長い文は折り返して幅を取らない</summary>
    public static Label Sub(string text)
    {
        var l = L(text);
        l.Tag = "sub";
        if (SmallFont != null) l.Font = SmallFont;
        l.MaximumSize = new Size(W(440), 0);
        return l;
    }

    /// <summary>頻繁に変わる表示用の軽量ラベル</summary>
    public static FastLabel Info(int logicalWidth, bool sub = true) => new()
    {
        LogicalWidth = logicalWidth, Sub = sub, Anchor = AnchorStyles.Left
    };

    public static RButton Btn(string text, int minWidth = 0) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(W(minWidth), 0), Anchor = AnchorStyles.Left,
        Margin = new Padding(3), Padding = new Padding(8, 3, 8, 3)
    };

    public static RCheck Chk(string text, bool isChecked = false) => new()
    {
        Text = text, Checked = isChecked, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 3, 4, 3)
    };

    public static RToggle Toggle(string text, int minWidth = 0) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(W(minWidth), 0),
        Anchor = AnchorStyles.Left, Margin = new Padding(3), Padding = new Padding(8, 3, 8, 3)
    };

    public static RNumber Nud(decimal min, decimal max, decimal value, int width, decimal inc = 1, int decimals = 0) => new RNumber
    {
        // − / + の分だけ幅を足す
        Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = WI(width) + W(30),
        Increment = inc, DecimalPlaces = decimals, Anchor = AnchorStyles.Left, Margin = InputMargin,
        TextAlign = HorizontalAlignment.Right
    };

    public static TextBox Txt(string text, int width) => new()
    {
        Text = text, Width = WI(width), Anchor = AnchorStyles.Left, Margin = InputMargin
    };

    public static ComboBox Combo(int width, bool editable, params object[] items)
    {
        var c = new ComboBox
        {
            DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
            Width = WI(width), Anchor = AnchorStyles.Left, Margin = new Padding(3)
        };
        c.Items.AddRange(items);
        if (!editable && items.Length > 0) c.SelectedIndex = 0;
        return c;
    }

    /// <summary>横並び (折り返しあり) の自動サイズパネル</summary>
    public static FlowLayoutPanel Flow(bool wrap = true) => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = wrap,
        Margin = new Padding(0, 1, 0, 1), Padding = new Padding(0)
    };

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var f = Flow(false);
        f.Controls.AddRange(controls);
        return f;
    }

    /// <summary>上部に貼り付く折り返しバー</summary>
    public static FlowLayoutPanel TopBar() => new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true, Padding = new Padding(4, 3, 4, 1)
    };

    /// <summary>
    /// 縦に並べてスクロールできるページ本体。
    /// 横幅いっぱいカード (WideCard) と Stretch の印の付いた部品は、ページの幅に合わせて広がる。
    /// </summary>
    public static FlowLayoutPanel PageList(Control parent)
    {
        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true
        };
        EnableDoubleBuffer(list); // スクロール時のちらつきと描画の負荷を減らす
        list.ClientSizeChanged += (_, _) => FitPage(list);
        list.VisibleChanged += (_, _) => { if (list.Visible) FitPage(list); };
        list.ControlAdded += (_, _) => FitPage(list);
        parent.Controls.Add(list);
        return list;
    }

    /// <summary>標準では公開されていないダブルバッファを有効にする (パネルの描き直しを軽くする)</summary>
    public static void EnableDoubleBuffer(Control c)
    {
        try
        {
            typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(c, true);
        }
        catch { }
    }

    private static void FitPage(FlowLayoutPanel list)
    {
        int avail = list.ClientSize.Width - list.Padding.Horizontal;
        if (avail <= 0) return;
        list.SuspendLayout();
        foreach (Control c in list.Controls)
        {
            if (c is RoundPanel rp && rp.FillWidth) rp.FitTo(avail - rp.Margin.Horizontal);
            else ApplyStretch(c, avail);
        }
        list.ResumeLayout();
    }

    // ---------------- 横幅に合わせて広げる印 ----------------
    private static readonly ConditionalWeakTable<Control, StrongBox<int>> _stretch = new();

    /// <summary>この部品をカード (ページ) の横幅に合わせて広げる印を付ける</summary>
    public static T Stretch<T>(T c) where T : Control
    {
        _stretch.AddOrUpdate(c, new StrongBox<int>(-1));
        return c;
    }

    /// <summary>印の付いた部品を avail (余白込み) の幅に広げる。元の幅より狭くはしない</summary>
    public static void ApplyStretch(Control c, int avail)
    {
        if (!_stretch.TryGetValue(c, out var box)) return;
        int w = Math.Max(0, avail - c.Margin.Horizontal);
        if (c.AutoSize)
        {
            if (c.MinimumSize.Width != w) c.MinimumSize = new Size(w, c.MinimumSize.Height);
        }
        else
        {
            if (box.Value < 0) box.Value = c.Width;
            int nw = Math.Max(box.Value, w);
            if (c.Width != nw) c.Width = nw;
        }
    }

    /// <summary>ページの横幅いっぱいに広がる角丸カード (中身は縦並び)</summary>
    public static RoundPanel WideCard(params Control[] children)
    {
        var inner = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), Padding = new Padding(0)
        };
        inner.Controls.AddRange(children);
        var card = new RoundPanel
        {
            Card = true, FillWidth = true, AutoSize = false,
            Padding = new Padding(8, 6, 8, 6), Margin = new Padding(2, 2, 2, 6)
        };
        card.Controls.Add(inner);
        card.Size = card.GetPreferredSize(Size.Empty);
        return card;
    }

    /// <summary>角丸カード (中身は縦並び)</summary>
    public static RoundPanel Card(params Control[] children)
    {
        var inner = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), Padding = new Padding(0)
        };
        inner.Controls.AddRange(children);
        var card = new RoundPanel
        {
            Card = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 6, 8, 6), Margin = new Padding(2, 2, 2, 6)
        };
        card.Controls.Add(inner);
        return card;
    }

    /// <summary>リスト等を角丸枠に入れて Dock=Fill で配置</summary>
    public static RoundPanel FillHost(Control c)
    {
        PrepareHosted(c);
        var host = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(5), Margin = new Padding(2) };
        host.Controls.Add(c);
        return host;
    }

    /// <summary>リスト等を固定サイズの角丸枠に入れる</summary>
    public static RoundPanel SizedHost(Control c, int width, int height)
    {
        PrepareHosted(c);
        var host = new RoundPanel { Size = new Size(W(width), W(height)), Padding = new Padding(5), Margin = new Padding(3) };
        host.Controls.Add(c);
        return host;
    }

    private static void PrepareHosted(Control c)
    {
        c.Dock = DockStyle.Fill;
        c.Margin = new Padding(0);
        switch (c)
        {
            case ListBox lb: lb.BorderStyle = BorderStyle.None; break;
            case ListView lv: lv.BorderStyle = BorderStyle.None; break;
            case TextBoxBase tb: tb.BorderStyle = BorderStyle.None; break;
        }
    }

    /// <summary>開いているダイアログの数 (0 より大きい間はメイン画面を最前面にしない)</summary>
    public static int ModalDepth { get; private set; }

    /// <summary>ダイアログが開いた/閉じた (メイン画面が最前面の状態を見直す)</summary>
    public static event Action ModalStateChanged;

    /// <summary>
    /// ダイアログを開く共通処理 (最前面表示との相性対策)。
    /// 開いている間はメイン画面の最前面を外し、閉じた後の最前面の復帰はメイン画面が判断する。
    /// </summary>
    public static T RunModal<T>(Control from, Func<Form, T> show)
    {
        var owner = from as Form ?? from?.FindForm() ?? Form.ActiveForm;
        while (owner?.Owner != null) owner = owner.Owner;
        ModalDepth++;
        if (owner != null && owner.TopMost) owner.TopMost = false;
        ModalStateChanged?.Invoke();
        try { return show(owner); }
        finally
        {
            ModalDepth--;
            ModalStateChanged?.Invoke();
        }
    }

    /// <summary>メッセージボックス (最前面表示でも裏に回らない)</summary>
    public static DialogResult Msg(Control from, string text, string caption = "VRC OSC Debug Client",
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None) =>
        RunModal(from, owner => owner != null
            ? MessageBox.Show(owner, L10n.Auto(text), L10n.Auto(caption), buttons, icon)
            : MessageBox.Show(L10n.Auto(text), L10n.Auto(caption), buttons, icon));

    /// <summary>ファイル/フォルダ選択などの共通ダイアログ</summary>
    public static DialogResult ShowCommon(Control from, CommonDialog dlg) =>
        RunModal(from, owner => owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog());

    /// <summary>実際の描画 DPI でのテキスト行の高さ (オーナードロー ListBox 用)</summary>
    public static int LineHeight(Control c)
    {
        try
        {
            using var g = c.CreateGraphics();
            return TextRenderer.MeasureText(g, "あAgy|", c.Font).Height + 4;
        }
        catch { return c.Font.Height + 4; }
    }
}
