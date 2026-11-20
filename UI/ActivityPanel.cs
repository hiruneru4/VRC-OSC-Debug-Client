using System.Collections.Concurrent;
using System.Text;
using VRCOscDebugClient.Core;

namespace VRCOscDebugClient.UI;

/// <summary>動作パネルの1行 (Header=true はカテゴリ見出し)</summary>
public sealed record ActiveLine(bool Header, string Name, string Detail);

/// <summary>
/// 右側の動作パネル。
/// 上: カテゴリごとに動作中の操作と状況 (回数・時間・値)
/// 下: 「操作ログ」(このツールが実行した操作) と「VRC 動作履歴」(VRChat 側の動き) を切り替えて表示
/// </summary>
public sealed class ActivityPanel : UserControl, IThemeAware
{
    private sealed class HistEntry
    {
        public DateTime Time;
        public HistKind Kind;
        public string Text;
        public override string ToString() => $"{Time:HH:mm:ss}  {Text}";
    }

    private const int MaxHistory = 5000;

    private readonly AppSettings _s;
    private readonly ListBox _active;
    private readonly TextBox _log, _hist;
    private readonly TabStrip _viewTabs; // 0 = 操作ログ / 1 = VRC 動作履歴
    private readonly RCheck _chkRaw, _chkRecv, _chkPause;
    private readonly RButton _btnKinds;
    private readonly FastLabel _lblCount;
    private readonly Label _title1;
    private readonly ContextMenuStrip _kindMenu = new();
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentQueue<HistEntry> _histQueue = new();
    private readonly List<HistEntry> _history = new();
    private readonly HashSet<HistKind> _hidden = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private volatile bool _raw, _recv;
    private int _lineCount, _histLines, _unread;

    public bool LogRaw => _raw;
    public bool LogRecv => _recv;

    public ActivityPanel(AppSettings s, ToolTip tip)
    {
        _s = s;
        Dock = DockStyle.Fill;
        foreach (var name in s.HistHidden)
            if (Enum.TryParse<HistKind>(name, out var k)) _hidden.Add(k);

        // ---- 動作中 ----
        _title1 = new LLabel { Text = "動作中", AutoSize = true, Margin = new Padding(3, 4, 3, 2) };
        _lblCount = Ui.Info(70);
        var head1 = Ui.Row(_title1, _lblCount);
        head1.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        _active = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, SelectionMode = SelectionMode.None };
        _active.DrawItem += DrawActive;

        // ---- ログの切替 ----
        _viewTabs = new TabStrip { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        _viewTabs.Add("操作ログ");
        _viewTabs.Add("VRC 動作履歴");
        var tabs = _viewTabs;

        _chkRaw = Ui.Chk("送信", s.LogRawOsc);
        _chkRecv = Ui.Chk("受信", s.LogRecvOsc);
        _btnKinds = Ui.Btn("種類 ▼");
        _chkPause = Ui.Chk("停止");
        var copy = Ui.Btn("コピー");
        var save = Ui.Btn("保存");
        var clear = Ui.Btn("消去");
        var opts = Ui.Flow(true);
        opts.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        opts.Controls.AddRange(new Control[] { _chkRaw, _chkRecv, _btnKinds, _chkPause, copy, save, clear });

        _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, HideSelection = false };
        _hist = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, HideSelection = false, Visible = false };
        var logHost = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(5), Margin = new Padding(2) };
        _log.Dock = DockStyle.Fill; _log.BorderStyle = BorderStyle.None;
        _hist.Dock = DockStyle.Fill; _hist.BorderStyle = BorderStyle.None;
        logHost.Controls.Add(_log);
        logHost.Controls.Add(_hist);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(2, 0, 2, 2) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 66));
        root.Controls.Add(head1, 0, 0);
        root.Controls.Add(Ui.FillHost(_active), 0, 1);
        root.Controls.Add(tabs, 0, 2);
        root.Controls.Add(opts, 0, 3);
        root.Controls.Add(logHost, 0, 4);
        Controls.Add(root);

        // 種類の絞り込みメニュー
        foreach (var (kind, name) in HistKinds.All)
        {
            var item = new ToolStripMenuItem(name) { Checked = !_hidden.Contains(kind), CheckOnClick = true, Tag = kind };
            item.CheckedChanged += (_, _) =>
            {
                if (item.Checked) _hidden.Remove(kind); else _hidden.Add(kind);
                _s.HistHidden = _hidden.Select(k => k.ToString()).ToList();
                RenderHistory();
            };
            _kindMenu.Items.Add(item);
        }
        _kindMenu.Items.Add(new ToolStripSeparator());
        var all = new ToolStripMenuItem("全て表示");
        all.Click += (_, _) =>
        {
            foreach (ToolStripItem it in _kindMenu.Items) if (it is ToolStripMenuItem mi && mi.Tag is HistKind) mi.Checked = true;
        };
        _kindMenu.Items.Add(all);
        _kindMenu.AutoClose = true;
        _btnKinds.Click += (_, _) =>
        {
            ThemedMenu.Apply(_kindMenu);
            _kindMenu.Show(_btnKinds, new Point(0, _btnKinds.Height));
        };

        tip.SetToolTip(_viewTabs, "操作ログ: このツールが実行した操作・送信した OSC\nVRC 動作履歴: VRChat 側の動き (移動・ジャンプ・ジェスチャー・声・ミュート・AFK・アバター・カメラ・ワールド・プレイヤー等)");
        tip.SetToolTip(_chkRaw, "送信した OSC メッセージも記録する");
        tip.SetToolTip(_chkRecv, "VRChat から受信した OSC も記録する (量が多いので注意)");
        tip.SetToolTip(_btnKinds, "VRC 動作履歴に出す種類を選ぶ");
        tip.SetToolTip(_chkPause, "表示を一時停止 (再開すると溜まった分を表示)");
        tip.SetToolTip(save, "表示中のログを txt に保存");

        _raw = _chkRaw.Checked;
        _recv = _chkRecv.Checked;
        _chkRaw.CheckedChanged += (_, _) => { _raw = _chkRaw.Checked; _s.LogRawOsc = _raw; };
        _chkRecv.CheckedChanged += (_, _) => { _recv = _chkRecv.Checked; _s.LogRecvOsc = _recv; };
        _viewTabs.SelectedChanged += _ => UpdateView();
        copy.Click += (_, _) =>
        {
            var tb = Current;
            try { if (tb.TextLength > 0) Clipboard.SetText(tb.SelectionLength > 0 ? tb.SelectedText : tb.Text); } catch { }
        };
        save.Click += (_, _) => SaveCurrent();
        clear.Click += (_, _) =>
        {
            if (HistView) { lock (_history) _history.Clear(); _hist.Clear(); _histLines = 0; }
            else { _log.Clear(); _lineCount = 0; }
        };

        OpLog.Written += OnWritten;
        _timer.Tick += (_, _) => Flush();
        _timer.Start();

        UpdateView();
        SetActive(new List<ActiveLine>());
    }

    private bool HistView => _viewTabs.SelectedIndex == 1;
    private TextBox Current => HistView ? _hist : _log;

    private void UpdateView()
    {
        bool hist = HistView;
        _hist.Visible = hist;
        _log.Visible = !hist;
        _chkRaw.Visible = _chkRecv.Visible = !hist;
        _btnKinds.Visible = hist;
        if (hist) { _unread = 0; _viewTabs.SetBadge(1, 0); }
    }

    // ---------------- 受け付け (どのスレッドからでも) ----------------
    private void OnWritten(string text)
    {
        _queue.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {text}");
        while (_queue.Count > 3000 && _queue.TryDequeue(out _)) { }
    }

    /// <summary>VRC 動作履歴に追加</summary>
    public void AddHistory(HistKind kind, string text)
    {
        _histQueue.Enqueue(new HistEntry { Time = DateTime.Now, Kind = kind, Text = text });
        while (_histQueue.Count > 3000 && _histQueue.TryDequeue(out _)) { }
    }

    private void Flush()
    {
        if (!Visible) return; // 非表示中は溜めるだけ

        // VRC 動作履歴
        if (!_histQueue.IsEmpty)
        {
            var sb = new StringBuilder();
            int shown = 0;
            while (_histQueue.TryDequeue(out var e))
            {
                lock (_history)
                {
                    _history.Add(e);
                    if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
                }
                if (_hidden.Contains(e.Kind)) continue;
                sb.Append(e).Append("\r\n");
                shown++;
            }
            if (shown > 0 && !_chkPause.Checked)
            {
                _histLines += shown;
                if (_histLines > 1500) RenderHistory();
                else _hist.AppendText(sb.ToString());
            }
            if (shown > 0 && !HistView)
            {
                _unread += shown;
                _viewTabs.SetBadge(1, Math.Min(_unread, 99));
            }
        }

        // 操作ログ
        if (_chkPause.Checked || _queue.IsEmpty) return;
        var lb = new StringBuilder();
        int n = 0;
        while (n < 500 && _queue.TryDequeue(out var line))
        {
            lb.Append(line).Append("\r\n");
            n++;
        }
        if (n == 0) return;
        _lineCount += n;
        if (_lineCount > 1500)
        {
            string all = _log.Text + lb;
            int cut = all.IndexOf('\n', all.Length / 2);
            _log.Text = cut >= 0 ? all.Substring(cut + 1) : all;
            _lineCount /= 2;
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
        else _log.AppendText(lb.ToString());
    }

    /// <summary>履歴を絞り込み条件で描き直す (最新 1000 件)</summary>
    private void RenderHistory()
    {
        List<HistEntry> list;
        lock (_history) list = _history.Where(e => !_hidden.Contains(e.Kind)).ToList();
        if (list.Count > 1000) list = list.GetRange(list.Count - 1000, 1000);
        var sb = new StringBuilder();
        foreach (var e in list) sb.Append(e).Append("\r\n");
        _hist.Text = sb.ToString();
        _hist.SelectionStart = _hist.TextLength;
        _hist.ScrollToCaret();
        _histLines = list.Count;
    }

    private void SaveCurrent()
    {
        bool hist = HistView;
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppSettings.AppFolderName, hist ? "History" : "OperationLog");
        try { Directory.CreateDirectory(folder); } catch { }
        using var dlg = new SaveFileDialog
        {
            InitialDirectory = folder,
            FileName = $"{(hist ? "VRC動作履歴" : "操作ログ")}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Filter = "テキスト (*.txt)|*.txt"
        };
        if (Ui.ShowCommon(this, dlg) != DialogResult.OK) return;
        try
        {
            string text;
            if (hist)
            {
                // 保存は表示中の種類の全履歴 (日付付き)
                List<HistEntry> list;
                lock (_history) list = _history.Where(e => !_hidden.Contains(e.Kind)).ToList();
                text = string.Join("\r\n", list.Select(e => $"{e.Time:yyyy/MM/dd HH:mm:ss}  {e.Text}"));
            }
            else text = _log.Text;
            File.WriteAllText(dlg.FileName, text, new UTF8Encoding(true));
            OpLog.Write($"💾 {(hist ? "VRC 動作履歴" : "操作ログ")}を保存: {dlg.FileName}");
        }
        catch (Exception ex) { Ui.Msg(this, "保存できませんでした: " + ex.Message); }
    }

    // ---------------- 動作中リスト ----------------
    public void SetActive(List<ActiveLine> lines)
    {
        int running = lines.Count(l => !l.Header);
        _lblCount.Text = running == 0 ? "" : $"{running} 件";
        _lblCount.ForeColor = Theme.Ok;
        if (lines.Count == 0) lines = new List<ActiveLine> { new(false, "", "動作中の操作はありません") };

        if (_active.Items.Count != lines.Count)
        {
            _active.BeginUpdate();
            _active.Items.Clear();
            foreach (var l in lines) _active.Items.Add(l);
            _active.EndUpdate();
            return;
        }
        for (int i = 0; i < lines.Count; i++)
            if (!Equals(_active.Items[i], lines[i])) _active.Items[i] = lines[i];
    }

    private void DrawActive(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _active.Items.Count) return;
        var line = (ActiveLine)_active.Items[e.Index];
        using (var b = new SolidBrush(Theme.InputBack)) e.Graphics.FillRectangle(b, e.Bounds);

        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        var g = e.Graphics;
        var font = _active.Font;
        int pad = (int)(4 * Rounded.Dpi(this));

        if (line.Header)
        {
            var r = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, e.Bounds.Width - pad * 2, e.Bounds.Height);
            using var bold = new Font(font, FontStyle.Bold);
            TextRenderer.DrawText(g, L10n.Auto(line.Name), bold, r, Theme.Accent, flags | TextFormatFlags.Left);
            TextRenderer.DrawText(g, L10n.Auto(line.Detail), font, r, Theme.SubFore, flags | TextFormatFlags.Right);
            return;
        }
        if (string.IsNullOrEmpty(L10n.Auto(line.Name)))
        {
            var r0 = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, e.Bounds.Width - pad * 2, e.Bounds.Height);
            TextRenderer.DrawText(g, L10n.Auto(line.Detail), font, r0, Theme.SubFore, flags | TextFormatFlags.Left);
            return;
        }

        int x = e.Bounds.X + pad * 3;
        float dot = e.Bounds.Height * 0.32f;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var db = new SolidBrush(Theme.Active))
            g.FillEllipse(db, x, e.Bounds.Y + (e.Bounds.Height - dot) / 2f, dot, dot);
        x += (int)dot + pad;

        int detailW = Math.Min(TextRenderer.MeasureText(g, L10n.Auto(line.Detail), font, Size.Empty, flags).Width,
            (int)((e.Bounds.Right - x) * 0.62));
        var nameRect = new Rectangle(x, e.Bounds.Y, e.Bounds.Right - x - detailW - pad * 2, e.Bounds.Height);
        var detRect = new Rectangle(e.Bounds.Right - detailW - pad, e.Bounds.Y, detailW, e.Bounds.Height);
        TextRenderer.DrawText(g, L10n.Auto(line.Name), font, nameRect, Theme.Fore, flags | TextFormatFlags.Left);
        TextRenderer.DrawText(g, L10n.Auto(line.Detail), font, detRect, Theme.Ok, flags | TextFormatFlags.Right);
    }

    private void UpdateItemHeight()
    {
        if (!_active.IsHandleCreated) return;
        _active.ItemHeight = Math.Clamp(Ui.LineHeight(_active) + 2, 12, 255);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _active.HandleCreated += (_, _) => UpdateItemHeight();
        _active.DpiChangedAfterParent += (_, _) => UpdateItemHeight();
        UpdateItemHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_title1 == null) return; // 基底コンストラクタ中
        _title1.Font = new Font(Font, FontStyle.Bold);
        UpdateItemHeight();
    }

    public void OnThemeChanged() => _active.Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            OpLog.Written -= OnWritten;
            _timer.Dispose();
            _kindMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
