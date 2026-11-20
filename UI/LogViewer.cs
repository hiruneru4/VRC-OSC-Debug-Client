using System.Text;
using System.Text.RegularExpressions;
using VRCOscDebugClient.Core;

namespace VRCOscDebugClient.UI;

/// <summary>
/// VRChat の output_log をリアルタイムに追従表示 (キーワード検索・絞り込み・スクロール停止)。
/// ・記録: 開始〜停止の間に流れた行を txt に保存
/// ・live=false でファイルを開いて見るだけのビューアーになる
/// </summary>
public sealed class LogViewer : UserControl, IThemeAware
{
    private const int MaxLines = 20000;
    private const int MaxStaticLines = 100000;
    private const int TrimSlack = 3000;
    private const long InitialTailBytes = 1024 * 1024;
    private const int ReadChunk = 4 * 1024 * 1024;

    private readonly AppSettings _s;
    private readonly TextBox _txtFolder = Ui.Txt("", 220);
    private readonly Button _btnBrowse = Ui.Btn("…");
    private readonly Button _btnOpen = Ui.Btn("ファイル");
    private readonly CheckBox _chkWatch = Ui.Toggle("監視", 52);
    private readonly CheckBox _chkLatest = Ui.Chk("最新追従", true);
    private readonly Button _btnClear = Ui.Btn("クリア");

    private readonly ComboBox _cmbSearch = Ui.Combo(170, true);
    private readonly CheckBox _chkFilter = Ui.Chk("絞込");
    private readonly CheckBox _chkCase = Ui.Chk("Aa");
    private readonly CheckBox _chkRegex = Ui.Chk(".*");
    private readonly Button _btnPrev = Ui.Btn("▲");
    private readonly Button _btnNext = Ui.Btn("▼");
    private readonly RToggle _chkPause = Ui.Toggle("スクロール停止");
    private readonly FastLabel _lblInfo = Ui.Info(150);
    private readonly FastLabel _lblFile = new() { Dock = DockStyle.Fill, Sub = true, Margin = new Padding(0) };

    // ---- 記録・ビューアー・別ウィンドウ ----
    private readonly bool _live;
    private readonly RToggle _chkRec = Ui.Toggle("■ 記録停止");
    private readonly RCheck _chkRecMatch = Ui.Chk("一致行だけ");
    private readonly FastLabel _lblRec = Ui.Info(230);
    private readonly RButton _btnViewer = Ui.Btn("📂 ログビューアー");
    private readonly RButton _btnPop = Ui.Btn("↩ メイン画面に戻す");
    private readonly RButton _btnFolderOpen = Ui.Btn("保存先");
    private List<string> _rec;
    private DateTime _recStart;
    private string _lastSaved;

    /// <summary>「別ウィンドウ」ボタンが押された</summary>
    public event Action PopOutRequested;

    /// <summary>新しく流れてきた行 (ファイルを開いた直後の過去分は含まない)</summary>
    public event Action<List<string>> LinesAdded;
    private bool _skipEvents;

    public static string RecordFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppSettings.AppFolderName, "LogRecords");

    private readonly ListBox _list = new()
    {
        DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false,
        SelectionMode = SelectionMode.MultiExtended
    };

    private readonly List<string> _all = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 250 };
    private int _hits;            // 一致行数 (追加分だけ数える)
    private bool _viewDirty;      // 非表示中に溜まった行がある
    private long _nextScan;       // 次に最新ファイルを探す時刻

    private string _file;
    private long _pos;
    private Decoder _decoder;
    private string _partial = "";
    private bool _dropFirstPartial;

    private string _term = "";
    private Regex _regex;
    private StringComparison _cmp = StringComparison.OrdinalIgnoreCase;

    public LogViewer(AppSettings s, ToolTip tip, bool live = true)
    {
        _s = s;
        _live = live;
        Dock = DockStyle.Fill;

        var bar = Ui.TopBar();
        bar.Padding = new Padding(0, 0, 0, 2);
        if (live)
        {
            bar.Controls.AddRange(new Control[] { _chkWatch, _chkLatest, _txtFolder, _btnBrowse, _btnOpen, _btnClear });
            bar.SetFlowBreak(_btnClear, true);
            bar.Controls.AddRange(new Control[] { _chkRec, _chkRecMatch, _lblRec, _btnFolderOpen, _btnViewer, _btnPop });
            bar.SetFlowBreak(_btnPop, true);
        }
        else
        {
            _btnOpen.Text = "📂 ファイルを開く…";
            bar.Controls.AddRange(new Control[] { _btnOpen, _btnClear });
            bar.SetFlowBreak(_btnClear, true);
        }
        bar.Controls.AddRange(new Control[] { _cmbSearch, _btnPrev, _btnNext, _chkFilter, _chkCase, _chkRegex, _chkPause, _lblInfo });

        _chkRec.OnText = "■ 記録停止";
        _chkRec.OffText = "● 記録開始";
        _chkRec.CheckedColor = Theme.Danger;
        _btnPop.FaceText = "⧉ 別ウィンドウ";
        tip.SetToolTip(_chkRec, "記録開始〜停止の間に流れたログを txt に保存します (保存先: ドキュメント\\VRC OSC Debug Client\\LogRecords)");
        tip.SetToolTip(_chkRecMatch, "検索キーワードに一致する行だけを記録");
        tip.SetToolTip(_btnViewer, "保存したログ (txt) を見るビューアーを開く (直前に記録したものを表示)");
        tip.SetToolTip(_btnPop, "ログを別ウィンドウで表示 / メイン画面に戻す");
        tip.SetToolTip(_btnFolderOpen, "記録の保存フォルダを開く");
        _chkRec.CheckedChanged += (_, _) => { if (_chkRec.Checked) StartRecording(); else StopRecording(); };
        _btnViewer.Click += (_, _) => OpenViewer(_lastSaved);
        _btnPop.Click += (_, _) => PopOutRequested?.Invoke();
        _btnFolderOpen.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(RecordFolder);
                System.Diagnostics.Process.Start("explorer.exe", RecordFolder);
            }
            catch { }
        };

        // ファイル名表示 (高さは文字に合わせて自動)
        var fileBar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 1, Padding = new Padding(2, 2, 2, 0)
        };
        fileBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fileBar.Controls.Add(_lblFile, 0, 0);

        Controls.Add(Ui.FillHost(_list));
        Controls.Add(fileBar);
        Controls.Add(bar);

        _cmbSearch.Items.AddRange(new object[]
        {
            "OnPlayerJoined", "OnPlayerLeft", "Joining wrld_", "Entering Room", "Switching", "[Video Playback]",
            "Error", "Exception", "Warning", "OSC", "Avatar", "Udon"
        });

        _txtFolder.Text = string.IsNullOrWhiteSpace(s.LogFolder) ? DefaultFolder() : s.LogFolder;

        tip.SetToolTip(_chkWatch, "ログ監視の開始 / 停止");
        tip.SetToolTip(_chkLatest, "新しい output_log が作られたら自動で切り替え");
        tip.SetToolTip(_txtFolder, "VRChat ログフォルダ (既定: %LOCALAPPDATA%Low\\VRChat\\VRChat)");
        tip.SetToolTip(_btnOpen, "ログファイルを手動で選択 (最新追従はOFFになります)");
        tip.SetToolTip(_cmbSearch, "キーワード (Enter で次を検索)");
        tip.SetToolTip(_chkFilter, "一致行のみ表示");
        tip.SetToolTip(_chkCase, "大文字小文字を区別");
        tip.SetToolTip(_chkRegex, "正規表現");
        tip.SetToolTip(_chkPause, "自動スクロールを停止 (読み込みは継続)");
        tip.SetToolTip(_list, "Ctrl+C: コピー / ダブルクリック: 行の全文表示");

        _timer.Tick += (_, _) => Poll();
        _chkWatch.CheckedChanged += (_, _) =>
        {
            _timer.Enabled = _chkWatch.Checked;
            _chkWatch.Text = _chkWatch.Checked ? "停止" : "監視";
            if (_chkWatch.Checked) Poll();
        };
        _btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _txtFolder.Text };
            if (Ui.ShowCommon(this, dlg) == DialogResult.OK) _txtFolder.Text = dlg.SelectedPath;
        };
        _txtFolder.TextChanged += (_, _) => _s.LogFolder = _txtFolder.Text;
        _btnOpen.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                InitialDirectory = Directory.Exists(_txtFolder.Text) ? _txtFolder.Text : "",
                Filter = "VRChat ログ (output_log_*.txt)|output_log_*.txt|テキスト (*.txt)|*.txt|すべて (*.*)|*.*"
            };
            if (!_live) dlg.InitialDirectory = Directory.Exists(RecordFolder) ? RecordFolder : dlg.InitialDirectory;
            if (Ui.ShowCommon(this, dlg) != DialogResult.OK) return;
            if (!_live) { LoadFile(dlg.FileName); return; }
            _chkLatest.Checked = false;
            OpenFile(dlg.FileName);
            _chkWatch.Checked = true;
        };
        _btnClear.Click += (_, _) => { _all.Clear(); _list.Items.Clear(); _hits = 0; UpdateInfo(); };

        _cmbSearch.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); OnSearchChanged(); };
        VisibleChanged += (_, _) => { if (Visible && _viewDirty) RebuildView(); };
        _chkCase.CheckedChanged += (_, _) => OnSearchChanged();
        _chkRegex.CheckedChanged += (_, _) => OnSearchChanged();
        _chkFilter.CheckedChanged += (_, _) => RebuildView();
        _cmbSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { FindNext(e.Shift ? -1 : 1); e.SuppressKeyPress = true; }
        };
        _btnPrev.Click += (_, _) => FindNext(-1);
        _btnNext.Click += (_, _) => FindNext(1);
        _chkPause.CheckedChanged += (_, _) => { if (!_chkPause.Checked) ScrollToEnd(); };

        _list.DrawItem += DrawItem;
        _list.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C) { CopySelected(); e.SuppressKeyPress = true; }
            if (e.Control && e.KeyCode == Keys.F) { _cmbSearch.Focus(); e.SuppressKeyPress = true; }
        };
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedItem is string line) Ui.Msg(this, line, "ログ行", MessageBoxButtons.OK);
        };
        _list.MouseWheel += (_, e) =>
        {
            // 上にスクロールしたら自動スクロールを止める
            if (e.Delta > 0 && !_chkPause.Checked) _chkPause.Checked = true;
        };
    }

    public void StartIfConfigured()
    {
        if (_live && _s.LogAutoStart) _chkWatch.Checked = true;
    }

    public void StopWatching()
    {
        _timer.Stop();
        if (_chkRec.Checked) _chkRec.Checked = false; // 終了時は記録を保存
    }

    /// <summary>別ウィンドウ表示中かどうかでボタンの表示を変える</summary>
    public void SetPoppedOut(bool popped) =>
        _btnPop.FaceText = popped ? "↩ メイン画面に戻す" : "⧉ 別ウィンドウ";

    // ---------------- 記録 ----------------
    private void StartRecording()
    {
        _rec = new List<string>();
        _recStart = DateTime.Now;
        _lblRec.ForeColor = Theme.Error;
        _lblRec.Text = "● 記録中 0 行";
        OpLog.Write("● ログ記録を開始");
        if (!_chkWatch.Checked) _chkWatch.Checked = true; // 監視していないと何も流れないので
    }

    private void StopRecording()
    {
        var rec = _rec;
        _rec = null;
        if (rec == null) return;
        var end = DateTime.Now;
        try
        {
            Directory.CreateDirectory(RecordFolder);
            string path = Path.Combine(RecordFolder, $"VRChatLog_{_recStart:yyyyMMdd_HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"# VRChat ログ記録  {_recStart:yyyy/MM/dd HH:mm:ss} 〜 {end:HH:mm:ss}  ({(end - _recStart):hh\\:mm\\:ss})");
            sb.AppendLine($"# 元ファイル: {_file}");
            if (_chkRecMatch.Checked && _term.Length > 0) sb.AppendLine($"# 一致行だけ: {_term}");
            sb.AppendLine($"# {rec.Count} 行");
            sb.AppendLine();
            foreach (var l in rec) sb.AppendLine(l);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            _lastSaved = path;
            _lblRec.ForeColor = Theme.Ok;
            _lblRec.Text = $"保存: {Path.GetFileName(path)} ({rec.Count} 行)";
            OpLog.Write($"■ ログ記録を保存: {path}  ({rec.Count} 行)");
        }
        catch (Exception ex)
        {
            _lblRec.ForeColor = Theme.Error;
            _lblRec.Text = "保存に失敗: " + ex.Message;
        }
    }

    private void OpenViewer(string path)
    {
        var f = new LogFileViewerForm(_s, FindForm()?.Font ?? Font, path);
        f.Show();
    }

    /// <summary>ビューアー用: ファイル全体を読み込む</summary>
    public void LoadFile(string path)
    {
        try
        {
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                    if (line.Length > 0) lines.Add(line);
            }
            if (lines.Count > MaxStaticLines) lines.RemoveRange(0, lines.Count - MaxStaticLines);
            _all.Clear();
            _all.AddRange(lines);
            _hits = _term.Length > 0 ? _all.Count(IsMatch) : 0;
            _file = path;
            _lblFile.Text = path;
            _chkPause.Checked = true; // 見るだけなので先頭から
            RebuildView();
            if (_list.Items.Count > 0) _list.TopIndex = 0;
        }
        catch (Exception ex)
        {
            Ui.Msg(this, "開けませんでした: " + ex.Message, "ログビューアー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", "VRChat", "VRChat");

    // ---------------- 読み込み ----------------
    private string FindLatest()
    {
        try
        {
            var dir = new DirectoryInfo(_txtFolder.Text.Trim());
            if (!dir.Exists) return null;
            return dir.GetFiles("output_log_*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private void OpenFile(string path)
    {
        _file = path;
        _decoder = Encoding.UTF8.GetDecoder();
        _partial = "";
        long len = 0;
        try { len = new FileInfo(path).Length; } catch { }
        _pos = Math.Max(0, len - InitialTailBytes);
        _dropFirstPartial = _pos > 0;
        _skipEvents = true; // 開いた直後に読む過去分は履歴に出さない
        AddLines(new List<string> { $"===== {Path.GetFileName(path)} =====" });
        _lblFile.Text = path;
    }

    private void Poll()
    {
        if (_chkLatest.Checked && (_file == null || Environment.TickCount64 >= _nextScan))
        {
            _nextScan = Environment.TickCount64 + 5000; // フォルダの確認は 5 秒に 1 回
            var latest = FindLatest();
            if (latest != null && !string.Equals(latest, _file, StringComparison.OrdinalIgnoreCase))
                OpenFile(latest);
        }
        if (_file == null)
        {
            _lblFile.Text = "ログファイルが見つかりません: " + _txtFolder.Text;
            return;
        }

        try
        {
            var fi = new FileInfo(_file);
            if (!fi.Exists) return;
            if (fi.Length < _pos) { _pos = 0; _decoder = Encoding.UTF8.GetDecoder(); _partial = ""; }
            if (fi.Length == _pos) return;

            using var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_pos, SeekOrigin.Begin);
            int toRead = (int)Math.Min(fi.Length - _pos, ReadChunk);
            var buf = new byte[toRead];
            int n = fs.Read(buf, 0, toRead);
            if (n <= 0) return;
            _pos += n;

            var chars = new char[_decoder.GetCharCount(buf, 0, n)];
            _decoder.GetChars(buf, 0, n, chars, 0);
            string text = _partial + new string(chars);

            var parts = text.Split('\n');
            _partial = parts[^1];
            var lines = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (i == 0 && _dropFirstPartial) { _dropFirstPartial = false; continue; }
                var l = parts[i].TrimEnd('\r');
                if (l.Length > 0) lines.Add(l);
            }
            if (lines.Count > 0)
            {
                AddLines(lines);
                if (!_skipEvents && _live) LinesAdded?.Invoke(lines);
            }
            if (_pos >= fi.Length) _skipEvents = false; // 過去分を読み終えた
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void AddLines(List<string> lines)
    {
        _all.AddRange(lines);
        if (_rec != null)
        {
            if (_chkRecMatch.Checked && _term.Length > 0) _rec.AddRange(lines.Where(IsMatch));
            else _rec.AddRange(lines);
            _lblRec.Text = $"● 記録中 {_rec.Count:N0} 行  {(DateTime.Now - _recStart):mm\\:ss}";
        }
        if (_term.Length > 0) _hits += lines.Count(IsMatch); // 追加分だけ数える
        if (_all.Count > MaxLines + TrimSlack)
        {
            _all.RemoveRange(0, _all.Count - MaxLines);
            _hits = _term.Length > 0 ? _all.Count(IsMatch) : 0;
            if (Visible) RebuildView(); else _viewDirty = true;
            return;
        }

        if (!Visible)
        {
            // 非表示中はリストを触らず、表示された時にまとめて作る
            _viewDirty = true;
            return;
        }

        var add = _chkFilter.Checked && _term.Length > 0 ? lines.Where(IsMatch).ToArray() : lines.ToArray();
        if (add.Length > 0)
        {
            _list.BeginUpdate();
            _list.Items.AddRange(add);
            _list.EndUpdate();
            ScrollToEnd();
        }
        UpdateInfo();
    }

    private void RebuildView()
    {
        _viewDirty = false;
        int top = _list.TopIndex;
        _list.BeginUpdate();
        _list.Items.Clear();
        var src = _chkFilter.Checked && _term.Length > 0 ? _all.Where(IsMatch) : _all;
        _list.Items.AddRange(src.Cast<object>().ToArray());
        _list.EndUpdate();
        if (_chkPause.Checked && _list.Items.Count > 0) _list.TopIndex = Math.Min(top, _list.Items.Count - 1);
        else ScrollToEnd();
        UpdateInfo();
    }

    private void ScrollToEnd()
    {
        if (_chkPause.Checked || _list.Items.Count == 0) return;
        _list.TopIndex = _list.Items.Count - 1;
    }

    // ---------------- 検索 ----------------
    private void OnSearchChanged()
    {
        _term = _cmbSearch.Text ?? "";
        _cmp = _chkCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _regex = null;
        _cmbSearch.BackColor = Theme.InputBack;
        if (_chkRegex.Checked && _term.Length > 0)
        {
            try
            {
                _regex = new Regex(_term, (_chkCase.Checked ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException)
            {
                _cmbSearch.BackColor = Theme.Danger;
                _term = "";
            }
        }
        _hits = _term.Length > 0 ? _all.Count(IsMatch) : 0; // 検索語が変わった時だけ全体を数える
        if (_chkFilter.Checked) RebuildView();
        else { _list.Invalidate(); UpdateInfo(); }
    }

    private bool IsMatch(string line)
    {
        if (_term.Length == 0) return false;
        try { return _regex != null ? _regex.IsMatch(line) : line.Contains(_term, _cmp); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private List<(int start, int len)> Matches(string line)
    {
        var res = new List<(int, int)>();
        if (_term.Length == 0) return res;
        try
        {
            if (_regex != null)
            {
                foreach (Match m in _regex.Matches(line))
                    if (m.Length > 0) res.Add((m.Index, m.Length));
            }
            else
            {
                int i = 0;
                while ((i = line.IndexOf(_term, i, _cmp)) >= 0)
                {
                    res.Add((i, _term.Length));
                    i += _term.Length;
                }
            }
        }
        catch (RegexMatchTimeoutException) { }
        return res;
    }

    private void FindNext(int dir)
    {
        if (_list.Items.Count == 0 || _term.Length == 0) return;
        _chkPause.Checked = true;
        int start = _list.SelectedIndex >= 0 ? _list.SelectedIndex : (dir > 0 ? -1 : _list.Items.Count);
        for (int i = start + dir; i >= 0 && i < _list.Items.Count; i += dir)
        {
            if (IsMatch((string)_list.Items[i]))
            {
                _list.ClearSelected();
                _list.SelectedIndex = i;
                return;
            }
        }
        System.Media.SystemSounds.Beep.Play();
    }

    private void UpdateInfo()
    {
        if (_term.Length == 0)
        {
            _lblInfo.Text = $"{_all.Count:N0} 行";
            return;
        }
        _lblInfo.Text = $"{_hits:N0} 件 / {_all.Count:N0} 行";
    }

    private void CopySelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, _list.SelectedItems.Cast<string>())); } catch { }
    }

    // ---------------- 描画 ----------------
    private void DrawItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        string line = (string)_list.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;

        using (var b = new SolidBrush(sel ? Theme.Selection : Theme.InputBack))
            e.Graphics.FillRectangle(b, e.Bounds);

        Color fore = Theme.Fore;
        if (line.StartsWith("=====")) fore = Theme.Ok;
        else if (line.Contains(" Error ") || line.Contains("Exception")) fore = Theme.Error;
        else if (line.Contains(" Warning ")) fore = Theme.Warn;

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                                      TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
        var g = e.Graphics;
        var font = _list.Font;
        int x = e.Bounds.X + 2;
        int right = e.Bounds.Right;

        int Seg(string s, Color fc, Color bc)
        {
            if (s.Length == 0 || x > right) return 0;
            var sz = TextRenderer.MeasureText(g, s, font, new Size(int.MaxValue, e.Bounds.Height), flags);
            var r = new Rectangle(x, e.Bounds.Y, sz.Width, e.Bounds.Height);
            if (bc != Color.Empty) using (var hb = new SolidBrush(bc)) g.FillRectangle(hb, r);
            TextRenderer.DrawText(g, s, font, r, fc, flags);
            return sz.Width;
        }

        if (line.Length > 1000) line = line.Substring(0, 1000) + " …";
        int pos = 0;
        foreach (var (st, len) in Matches(line))
        {
            if (st < pos) continue;
            x += Seg(line.Substring(pos, st - pos), fore, Color.Empty);
            x += Seg(line.Substring(st, len), Theme.HighlightFore, Theme.HighlightBack);
            pos = st + len;
        }
        if (pos < line.Length) Seg(line.Substring(pos), fore, Color.Empty);
    }

    private void UpdateItemHeight()
    {
        if (!_list.IsHandleCreated) return;
        _list.ItemHeight = Math.Clamp(Ui.LineHeight(_list), 12, 255);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _list.HandleCreated += (_, _) => UpdateItemHeight();
        _list.DpiChangedAfterParent += (_, _) => UpdateItemHeight();
        UpdateItemHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateItemHeight();
    }

    public void OnThemeChanged()
    {
        _chkPause.CheckedColor = Theme.Danger;
        _list.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _searchTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
