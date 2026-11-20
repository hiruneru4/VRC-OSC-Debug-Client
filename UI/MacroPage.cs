using VRCOscDebugClient.Core;
using VRCOscDebugClient.Osc;

namespace VRCOscDebugClient.UI;

/// <summary>マクロページが外部 (メイン画面) から受け取る情報・操作</summary>
public sealed class MacroEnvironment
{
    /// <summary>アバターパラメータ一覧 (UI スレッド)</summary>
    public Func<List<(string Name, string Address, string Type)>> Params;
    /// <summary>カスタムプリセット一覧 (UI スレッド)</summary>
    public Func<List<CustomPreset>> Customs;
    /// <summary>カスタムプリセット検索 (どのスレッドからでも)</summary>
    public Func<string, CustomPreset> FindCustom;
    /// <summary>カテゴリ操作 (カテゴリ, "ON"/"OFF"/"切替") (どのスレッドからでも)</summary>
    public Action<string, string> Category;
}

/// <summary>マクロ編集・実行ページ。他のマクロからの呼び出し窓口 (IMacroHost) も兼ねる</summary>
public sealed class MacroPage : UserControl, IThemeAware, IMacroHost
{
    private sealed class TargetItem
    {
        public string Id;
        public string Text;
        public string Type;
        public override string ToString() => Text;
    }

    private readonly OscClient _osc;
    private readonly InputArbiter _arb;
    private readonly AppSettings _s;
    private readonly ToolTip _tip;
    private readonly MacroEnvironment _env;
    private readonly Dictionary<Macro, MacroRunner> _runners = new();
    private readonly List<TargetItem> _targets = new();

    private readonly ComboBox _cmbMacro;
    private readonly TextBox _txtName;
    private readonly RNumber _nudRepeat;
    private readonly HotkeyBox _hk;
    private readonly RButton _btnRun, _btnPause;
    private readonly FastLabel _lblState;
    private readonly ListBox _lstSteps;
    private readonly ComboBox _cmbKind, _cmbTarget, _cmbType;
    private readonly TextBox _txtValue;
    private readonly RNumber _nudMs, _nudCount;
    private readonly Label _lblValue, _lblTarget;
    private readonly RCheck _chkParallel;
    private readonly Label _titleLabel, _helpTitleLabel;
    private bool _loading;

    /// <summary>マクロのホットキーやリストが変わった (再登録が必要)</summary>
    public event Action HotkeysChanged;
    /// <summary>マクロを開始してよいか (マクロ機能が無効なら false)</summary>
    public Func<bool> CanRun;
    /// <summary>機能が無効のため開始できなかった</summary>
    public event Action Blocked;

    public IReadOnlyList<Macro> Macros => _s.Macros;
    public int RunningCount { get { lock (_runners) return _runners.Values.Count(r => r.IsRunning); } }
    private Macro Current => _cmbMacro.SelectedItem as Macro;

    public MacroPage(OscClient osc, InputArbiter arb, AppSettings s, ToolTip tip, MacroEnvironment env)
    {
        _osc = osc;
        _arb = arb;
        _s = s;
        _tip = tip;
        _env = env;
        Dock = DockStyle.Fill;

        // ---- マクロ選択 ----
        _cmbMacro = Ui.Combo(170, false);
        var btnNew = Ui.Btn("新規");
        var btnDup = Ui.Btn("複製");
        var btnDel = Ui.Btn("削除");
        var r0 = Ui.Row(Ui.L("マクロ"), _cmbMacro, btnNew, btnDup, btnDel);

        // ---- 名前・繰り返し・キー ----
        _txtName = Ui.Txt("", 130);
        _nudRepeat = Ui.Nud(0, 99999, 1, 60);
        _hk = new HotkeyBox { Width = Ui.W(120), Anchor = AnchorStyles.Left, Margin = Ui.InputMargin };
        var r1 = Ui.Row(Ui.L("名前"), _txtName, Ui.L("繰返し"), _nudRepeat, Ui.L("キー"), _hk);

        // ---- 実行 ----
        _btnRun = Ui.Btn("■ 停止する", 80);
        _btnRun.FaceText = "▶ 実行";
        _btnPause = Ui.Btn("▶ 再開する", 80);
        _btnPause.FaceText = "⏸ 一時停止";
        var btnStopAll = Ui.Btn("■ 全マクロ停止");
        _lblState = Ui.Info(230);
        var r2 = Ui.Row(_btnRun, _btnPause, btnStopAll, _lblState);

        // 幅が狭い時は折り返す (はみ出して崩れないように)
        foreach (var row in new[] { r0, r1, r2 })
        {
            row.WrapContents = true;
            row.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }

        // ---- ステップ一覧 ----
        _lstSteps = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, HorizontalScrollbar = true };
        var side = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(2, 0, 0, 0)
        };
        var btnUp = Ui.Btn("↑ 上へ", 64);
        var btnDown = Ui.Btn("↓ 下へ", 64);
        var btnCopy = Ui.Btn("複製", 64);
        var btnRemove = Ui.Btn("削除", 64);
        var btnClear = Ui.Btn("全削除", 64);
        side.Controls.AddRange(new Control[] { btnUp, btnDown, btnCopy, btnRemove, btnClear });

        var stepsArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 2, 0, 2) };
        stepsArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stepsArea.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        stepsArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stepsArea.Controls.Add(Ui.FillHost(_lstSteps), 0, 0);
        stepsArea.Controls.Add(side, 1, 0);

        // ---- ステップ編集 ----
        _cmbKind = Ui.Combo(110, false, MacroStep.KindNames);
        _cmbKind.MaxDropDownItems = MacroStep.KindNames.Length;
        _lblTarget = Ui.L("対象");
        _cmbTarget = Ui.Combo(220, true);
        _cmbTarget.DropDownWidth = Ui.W(280);
        _cmbTarget.MaxDropDownItems = 20;
        _cmbType = Ui.Combo(64, false, OscValue.Types);
        _lblValue = Ui.L("値");
        _txtValue = Ui.Txt("", 150);
        _nudMs = Ui.Nud(0, 3600000, 100, 74, 50);
        _nudCount = Ui.Nud(0, 99999, 5, 56);
        _chkParallel = Ui.Chk("並行 (待たずに次へ)");
        var btnAdd = Ui.Btn("追加");
        var btnInsert = Ui.Btn("挿入");
        var btnUpdate = Ui.Btn("上書き");

        // =========== 右: ステップの追加・編集と説明 ===========
        var title = new LLabel { Text = "ステップの追加・編集", AutoSize = true, Margin = new Padding(3, 2, 3, 4) };
        _titleLabel = title;
        var e1 = Ui.Row(Ui.L("種類"), _cmbKind);
        var e1b = Ui.Row(_lblTarget, _cmbTarget);
        var e2 = Ui.Row(Ui.L("型"), _cmbType, _lblValue, _txtValue);
        var e3 = Ui.Row(Ui.L("ms"), _nudMs, Ui.L("回数"), _nudCount);
        var e3b = Ui.Row(_chkParallel);
        var e4 = Ui.Row(btnAdd, btnInsert, btnUpdate);
        var helpTitle = new LLabel { Text = "説明", AutoSize = true, Margin = new Padding(3, 10, 3, 2) };
        _helpTitleLabel = helpTitle;
        var help = new Control[]
        {
            Ui.Sub("対象: 操作 / [P] アバターパラメータ / [C] カスタムプリセット / [Cam] カメラ / マクロ / カテゴリ"),
            Ui.Sub("押す: 押しっぱなし (ダッシュ等)。「離す」か マクロ終了まで押したまま"),
            Ui.Sub("1回押す / 長押し: ms の間押して離す。連打: 回数 (0=無限) × 間隔 ms"),
            Ui.Sub("値を送信: パラメータ等に値を送る (離さない)。チャット: 文字を送信 (\\n で改行)"),
            Ui.Sub("[並行]: 終わるのを待たずに次へ進む。「並行待ち」か周回の最後で終わるのを待つ"),
            Ui.Sub("マクロ実行: 別のマクロを実行 (並行なら待たない)。停止・一時停止・再開も可"),
            Ui.Sub("割込み実行: 動いている他のマクロを一時停止 → 対象を実行 → 終わったら再開"),
            Ui.Sub("カテゴリ操作: 移動などのカテゴリを ON / OFF / 切替 (「対象」チェックの行が動く)"),
            Ui.Sub("同じ入力を複数のマクロ・操作が押しても、全員が離すまで押されたまま"),
        };

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(2)
        };
        right.Controls.AddRange(new Control[] { title, e1, e1b, e2, e3, e3b, e4, helpTitle });
        right.Controls.AddRange(help);
        var rightCard = new RoundPanel { Card = true, Dock = DockStyle.Fill, Padding = new Padding(8, 6, 4, 6), Margin = new Padding(4, 0, 0, 0) };
        rightCard.Controls.Add(right);

        // =========== 左: マクロの選択・実行・ステップ一覧 ===========
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(0), Margin = new Padding(0) };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(r0, 0, 0);
        left.Controls.Add(r1, 0, 1);
        left.Controls.Add(r2, 0, 2);
        left.Controls.Add(stepsArea, 0, 3);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(rightCard, 1, 0);
        Controls.Add(root);

        // ---- ツールチップ ----
        _tip.SetToolTip(_nudRepeat, "繰り返し回数 (0 = 停止するまで無限ループ)");
        _tip.SetToolTip(_hk, "クリックしてキーを押すと、このマクロの開始/停止キーになります (Back/Delete で解除)");
        _tip.SetToolTip(_btnPause, "実行中のマクロを一時停止 / 再開 (押しているものは押したまま)");
        _tip.SetToolTip(_cmbKind,
            "押す: 押しっぱなし / 離す: 押したものを離す\n1回押す: ms 押して離す / 長押し: ms 押し続けて離す\n" +
            "連打: 回数 (0=無限) × 間隔ms / 待機: ms 待つ\nチャット: 文字を送信 / 値を送信: 対象に値を送る (離さない)\n" +
            "全て離す: このマクロで押しているものを全部離す / 並行待ち: [並行] のステップが全部終わるまで待つ\n" +
            "マクロ実行: 別のマクロを実行 (並行なら待たない) / マクロ停止・一時停止・再開: 別のマクロを操作\n" +
            "割込み実行: 他のマクロを一時停止して対象を実行し、終わったら再開 / カテゴリ操作: ON・OFF・切替");
        _tip.SetToolTip(_txtValue, "軸: -1〜1 / ボタン: 空欄でOK / パラメータ: 型に合わせた値 / [C]: 空欄ならプリセットの値\nチャット: 文字 / カテゴリ操作: ON・OFF・切替");
        _tip.SetToolTip(_lstSteps, "クリック: 編集欄に読込 / Delete: 削除 / Ctrl+↑↓: 移動");
        _tip.SetToolTip(btnInsert, "選択中のステップの前に挿入");
        _tip.SetToolTip(btnUpdate, "選択中のステップを編集欄の内容で置き換え");
        _tip.SetToolTip(_chkParallel, "ON: このステップの終了を待たずに次のステップへ進む (複数操作の同時実行)\n1回押す / 長押し / 連打 / 待機 / マクロ実行 / 割込み実行 で使えます");

        // ---- イベント ----
        btnNew.Click += (_, _) => AddMacro(new Macro { Name = UniqueName("マクロ") });
        btnDup.Click += (_, _) =>
        {
            if (Current == null) return;
            var c = Current.Clone();
            c.Name = UniqueName(c.Name);
            c.Hotkey = "";
            AddMacro(c);
        };
        btnDel.Click += (_, _) =>
        {
            var m = Current;
            if (m == null) return;
            if (Ui.Msg(this, $"マクロ「{m.Name}」を削除しますか？", "マクロ", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question) != DialogResult.OK) return;
            lock (_runners)
            {
                if (_runners.TryGetValue(m, out var r)) { r.Stop(); _runners.Remove(m); }
            }
            _s.Macros.Remove(m);
            int idx = _cmbMacro.SelectedIndex;
            _cmbMacro.Items.Remove(m);
            if (_cmbMacro.Items.Count > 0) _cmbMacro.SelectedIndex = Math.Min(idx, _cmbMacro.Items.Count - 1);
            else LoadMacro(null);
            HotkeysChanged?.Invoke();
        };
        _cmbMacro.SelectedIndexChanged += (_, _) => { if (!_loading) LoadMacro(Current); };

        _txtName.Leave += (_, _) => RenameCurrent();
        _txtName.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { RenameCurrent(); e.SuppressKeyPress = true; } };
        _nudRepeat.ValueChanged += (_, _) => { if (!_loading && Current != null) Current.Repeat = (int)_nudRepeat.Value; };
        _hk.HotkeyChanged += (_, _) =>
        {
            if (Current == null) return;
            Current.Hotkey = _hk.Hotkey.ToString();
            RefreshMacroItem();
            HotkeysChanged?.Invoke();
        };

        _btnRun.Click += (_, _) => { if (Current != null) Toggle(Current); };
        _btnPause.Click += (_, _) =>
        {
            var m = Current;
            if (m == null) return;
            var r = GetRunner(m);
            if (!r.IsRunning) return;
            if (r.IsPaused) { r.Resume(); OpLog.Write($"▶ マクロ再開: {m.Name}"); }
            else { r.Pause(); OpLog.Write($"⏸ マクロ一時停止: {m.Name}"); }
            UpdateRunUi();
        };
        btnStopAll.Click += (_, _) => { StopAll(); OpLog.Write("■ 全マクロ停止"); };

        _lstSteps.DrawItem += DrawStep;
        _lstSteps.SelectedIndexChanged += (_, _) =>
        {
            if (Current != null && _lstSteps.SelectedIndex >= 0 && _lstSteps.SelectedIndex < Current.Steps.Count)
                LoadEditor(Current.Steps[_lstSteps.SelectedIndex]);
        };
        _lstSteps.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { RemoveStep(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Up) { MoveStep(-1); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Down) { MoveStep(1); e.Handled = true; }
        };

        btnUp.Click += (_, _) => MoveStep(-1);
        btnDown.Click += (_, _) => MoveStep(1);
        btnCopy.Click += (_, _) =>
        {
            var m = Current;
            int i = _lstSteps.SelectedIndex;
            if (m == null || i < 0) return;
            m.Steps.Insert(i + 1, m.Steps[i].Clone());
            RefreshSteps(i + 1);
        };
        btnRemove.Click += (_, _) => RemoveStep();
        btnClear.Click += (_, _) =>
        {
            if (Current == null || Current.Steps.Count == 0) return;
            if (Ui.Msg(this, "全ステップを削除しますか？", "マクロ", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            Current.Steps.Clear();
            RefreshSteps(-1);
        };

        _cmbKind.SelectedIndexChanged += (_, _) => OnKindChanged();
        _cmbTarget.DropDown += (_, _) => RefreshTargets();
        _cmbTarget.SelectionChangeCommitted += (_, _) =>
        {
            if (_cmbTarget.SelectedItem is TargetItem t && t.Type != null)
            {
                _cmbType.SelectedItem = t.Type;
                // 値が空だと Bool は false が送られてしまうので、分かりやすい初期値を入れる
                if (string.IsNullOrWhiteSpace(_txtValue.Text))
                    _txtValue.Text = t.Type switch { "Bool" => "true", "Int" => "1", "Float" => "1.0", _ => "" };
            }
        };
        btnAdd.Click += (_, _) => AddStep(false);
        btnInsert.Click += (_, _) => AddStep(true);
        btnUpdate.Click += (_, _) =>
        {
            var m = Current;
            int i = _lstSteps.SelectedIndex;
            if (m == null || i < 0) return;
            var st = ReadEditor();
            if (st == null) return;
            m.Steps[i] = st;
            RefreshSteps(i);
        };

        // ---- 初期データ (初回のみサンプル) ----
        if (!_s.MacroSampleAdded)
        {
            _s.MacroSampleAdded = true;
            if (_s.Macros.Count == 0)
            {
                _s.Macros.Add(new Macro
                {
                    Name = "サンプル: 走りながらジャンプ",
                    Repeat = 1,
                    Steps =
                    {
                        new MacroStep { Kind = MacroStepKind.Press, Target = "MoveForward" },
                        new MacroStep { Kind = MacroStepKind.Press, Target = "Run" },
                        new MacroStep { Kind = MacroStepKind.Rapid, Target = "Jump", Ms = 600, Count = 4, Parallel = true },
                        new MacroStep { Kind = MacroStepKind.Hold, Target = "LookRight", Ms = 800, Parallel = true },
                        new MacroStep { Kind = MacroStepKind.WaitParallel },
                        new MacroStep { Kind = MacroStepKind.ReleaseAll },
                        new MacroStep { Kind = MacroStepKind.Chat, Value = "マクロ完了！" },
                    }
                });
            }
        }
        _loading = true;
        foreach (var m in _s.Macros) _cmbMacro.Items.Add(m);
        _loading = false;
        if (_cmbMacro.Items.Count > 0) _cmbMacro.SelectedIndex = 0; else LoadMacro(null);
        _cmbKind.SelectedIndex = (int)MacroStepKind.Tap;
        OnKindChanged();
    }

    // =================================================================
    // マクロ管理
    // =================================================================
    private string UniqueName(string baseName)
    {
        string name = baseName;
        int n = 2;
        while (_s.Macros.Any(m => m.Name == name)) name = $"{baseName} {n++}";
        return name;
    }

    private void AddMacro(Macro m)
    {
        _s.Macros.Add(m);
        _cmbMacro.Items.Add(m);
        _cmbMacro.SelectedItem = m;
        HotkeysChanged?.Invoke();
    }

    private void RenameCurrent()
    {
        var m = Current;
        if (_loading || m == null) return;
        string name = _txtName.Text.Trim();
        if (name.Length == 0 || name == m.Name) { _txtName.Text = m.Name; return; }
        if (_s.Macros.Any(x => x != m && x.Name == name))
        {
            Ui.Msg(this, "同じ名前のマクロがあります", "マクロ");
            _txtName.Text = m.Name;
            return;
        }
        // このマクロを対象にしているステップの名前も合わせる
        foreach (var st in _s.Macros.SelectMany(x => x.Steps))
            if (MacroStep.IsMacroKind(st.Kind) && st.Target == m.Name) st.Target = name;
        m.Name = name;
        RefreshMacroItem();
        _lstSteps.Invalidate();
    }

    private void RefreshMacroItem()
    {
        int i = _cmbMacro.SelectedIndex;
        if (i < 0) return;
        _loading = true;
        try
        {
            _cmbMacro.Items[i] = _cmbMacro.Items[i]; // 表示名を更新
            _cmbMacro.SelectedIndex = i;
        }
        finally { _loading = false; }
    }

    private void LoadMacro(Macro m)
    {
        _loading = true;
        try
        {
            bool has = m != null;
            _txtName.Enabled = _nudRepeat.Enabled = _hk.Enabled = _btnRun.Enabled = _btnPause.Enabled = has;
            _txtName.Text = m?.Name ?? "";
            _nudRepeat.Value = Math.Clamp(m?.Repeat ?? 1, 0, 99999);
            _hk.Hotkey = Hotkey.Parse(m?.Hotkey);
        }
        finally { _loading = false; }
        RefreshSteps(-1);
        UpdateRunUi();
    }

    /// <summary>ステップを追加 (パラメータパネルの「マクロに追加」用)</summary>
    public void AddSendStep(string address, string type, string value)
    {
        if (Current == null) AddMacro(new Macro { Name = UniqueName("マクロ") });
        var m = Current;
        m.Steps.Add(new MacroStep { Kind = MacroStepKind.Send, Target = address, Type = type, Value = value });
        RefreshSteps(m.Steps.Count - 1);
        OpLog.Write($"＋ マクロ「{m.Name}」に追加: 値を送信 {MacroStep.TargetName(address)} = {value}");
    }

    public void Toggle(Macro m)
    {
        var r = GetRunner(m);
        if (r.IsRunning)
        {
            r.Stop();
            OpLog.Write($"■ マクロ停止: {m.Name}");
        }
        else if (CanRun != null && !CanRun())
        {
            Blocked?.Invoke();
        }
        else
        {
            OpLog.Write($"▶ マクロ開始: {m.Name}  (繰返し {(m.Repeat <= 0 ? "∞" : m.Repeat.ToString())})");
            r.Start(m);
        }
    }

    /// <summary>マクロ一覧を _s.Macros から作り直す (フォルダの再読込・切替の後)</summary>
    public void ReloadList(Macro select = null)
    {
        StopAll();
        lock (_runners) _runners.Clear();
        var keep = select ?? Current;
        _loading = true;
        try
        {
            _cmbMacro.BeginUpdate();
            _cmbMacro.Items.Clear();
            foreach (var m in _s.Macros) _cmbMacro.Items.Add(m);
            _cmbMacro.EndUpdate();
        }
        finally { _loading = false; }
        var pick = keep != null ? _s.Macros.FirstOrDefault(m => m == keep) ?? _s.Macros.FirstOrDefault(m => m.Name == keep.Name) : null;
        if (pick != null) _cmbMacro.SelectedItem = pick;
        else if (_cmbMacro.Items.Count > 0) _cmbMacro.SelectedIndex = 0;
        else LoadMacro(null);
        if (_cmbMacro.SelectedItem != null) LoadMacro(Current);
        HotkeysChanged?.Invoke();
    }

    /// <summary>新しく読み込んだマクロを一覧に足す (フォルダに置かれたファイル)</summary>
    public void AppendMacros(IEnumerable<Macro> list)
    {
        foreach (var m in list)
        {
            _s.Macros.Add(m);
            _cmbMacro.Items.Add(m);
        }
        if (Current == null && _cmbMacro.Items.Count > 0) _cmbMacro.SelectedIndex = 0;
        HotkeysChanged?.Invoke();
    }

    public void StopAll()
    {
        List<MacroRunner> list;
        lock (_runners) list = _runners.Values.ToList();
        foreach (var r in list) r.Stop();
    }

    /// <summary>動作中のマクロ (名前, 状況)</summary>
    public List<(string Name, string Detail)> ActiveMacros()
    {
        lock (_runners)
        {
            return _runners.Where(kv => kv.Value.IsRunning)
                .Select(kv => (kv.Key.Name, kv.Value.Describe()))
                .ToList();
        }
    }

    private MacroRunner GetRunner(Macro m)
    {
        lock (_runners)
        {
            if (_runners.TryGetValue(m, out var r)) return r;
            r = new MacroRunner(m.Name, _osc, _arb, this);
            r.StateChanged += _ => InvokeSafe(UpdateRunUi);
            r.StepChanged += () => InvokeSafe(() => { if (Current == m) _lstSteps.Invalidate(); });
            r.StepStarted += (i, st) => OpLog.Write($"🔁 {m.Name}  {i + 1}. {st.Describe()}");
        r.RepeatStarted += (n, total) =>
        {
            OpLog.Write($"🔂 {m.Name}  {n}/{(total <= 0 ? "∞" : total.ToString())} 回目");
            InvokeSafe(UpdateRunUi);
        };
        r.StateChanged += running =>
        {
            if (!running && r.CompletedRepeats > 0)
                OpLog.Write($"✅ {m.Name}  {r.CompletedRepeats}/{(r.RepeatTotal <= 0 ? "∞" : r.RepeatTotal.ToString())} 回 実行しました");
        };
            _runners[m] = r;
            return r;
        }
    }

    private Macro[] MacroSnapshot()
    {
        for (int i = 0; i < 3; i++)
        {
            try { return _s.Macros.ToArray(); } catch (InvalidOperationException) { }
        }
        return Array.Empty<Macro>();
    }

    private Macro FindMacro(string name) => MacroSnapshot().FirstOrDefault(m => m.Name == name);

    private void InvokeSafe(Action a)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private void UpdateRunUi()
    {
        var m = Current;
        MacroRunner r = null;
        if (m != null) lock (_runners) _runners.TryGetValue(m, out r);
        bool run = r != null && r.IsRunning;
        bool paused = run && r.IsPaused;
        _btnRun.FaceText = run ? "■ 停止" : "▶ 実行";
        _btnRun.BackColor = run ? Theme.Active : Theme.ButtonBack;
        _btnPause.FaceText = paused ? "▶ 再開" : "⏸ 一時停止";
        _btnPause.Enabled = run;
        _btnPause.BackColor = paused ? Theme.Warn : Theme.ButtonBack;
        // 何回目か (繰り返し 3 回なら 1/3, 2/3, 3/3)
        string total = r == null ? "" : r.RepeatTotal <= 0 ? "∞" : r.RepeatTotal.ToString();
        string text;
        if (run) text = $"▶ {r.RepeatIndex + 1}/{total} 回目" + (paused ? " (一時停止中)" : "");
        else if (r != null && r.CompletedRepeats > 0) text = $"完了 {r.CompletedRepeats}/{total} 回";
        else text = "停止中";
        int others = RunningCount - (run ? 1 : 0);
        if (others > 0) text += $"  ほか {others} 件実行中";
        _lblState.Text = text;
        _lblState.ForeColor = run ? Theme.Ok : Theme.SubFore;
        _lstSteps.Invalidate();
    }

    // =================================================================
    // IMacroHost: マクロから他のマクロを操作 (どのスレッドからでも呼ばれる)
    // =================================================================
    public async Task RunMacroAsync(string name, bool wait, MacroRunner caller, CancellationToken ct)
    {
        var m = FindMacro(name);
        if (m == null) { OpLog.Write($"⚠ マクロ「{name}」が見つかりません"); return; }
        if (MacroRunner.InCallChain(m.Name))
        {
            OpLog.Write($"⚠ マクロ「{m.Name}」は呼び出し中のため実行しません (循環呼び出し防止)");
            return;
        }
        var r = GetRunner(m);
        OpLog.Write($"▶ マクロ開始: {m.Name}  (呼出元: {caller?.Name})");
        var task = r.Start(m);
        if (!wait) return;
        try { await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            r.Stop(); // 呼び出し元が止まったら、待っていたマクロも止める
            throw;
        }
    }

    private List<(Macro Macro, MacroRunner Runner)> ResolveTargets(string name, MacroRunner caller)
    {
        var list = new List<(Macro, MacroRunner)>();
        lock (_runners)
        {
            foreach (var kv in _runners)
            {
                if (kv.Value == caller || !kv.Value.IsRunning) continue;
                if (MacroRunner.InCallChain(kv.Key.Name)) continue; // 呼び出し元 (親) は操作しない
                if (name == MacroStep.AllMacros || kv.Key.Name == name) list.Add((kv.Key, kv.Value));
            }
        }
        return list;
    }

    public void StopMacro(string name, MacroRunner caller)
    {
        var targets = ResolveTargets(name, caller);
        foreach (var (m, r) in targets) r.Stop();
        OpLog.Write(targets.Count == 0
            ? $"■ マクロ停止: 「{name}」は動いていません"
            : $"■ マクロ停止: {string.Join(", ", targets.Select(t => t.Macro.Name))}  (呼出元: {caller?.Name})");
    }

    public void PauseMacro(string name, bool pause, MacroRunner caller)
    {
        var targets = ResolveTargets(name, caller);
        foreach (var (_, r) in targets)
        {
            if (pause) r.Pause(); else r.Resume();
        }
        if (targets.Count > 0)
            OpLog.Write($"{(pause ? "⏸ 一時停止" : "▶ 再開")}: {string.Join(", ", targets.Select(t => t.Macro.Name))}");
        InvokeSafe(UpdateRunUi);
    }

    public async Task InterruptAsync(string name, MacroRunner caller, CancellationToken ct)
    {
        // 動いている他のマクロ (呼び出し元と対象以外) を一時停止
        var paused = new List<MacroRunner>();
        foreach (var (m, r) in ResolveTargets(MacroStep.AllMacros, caller))
        {
            if (m.Name == name || r.IsPaused) continue;
            r.Pause();
            paused.Add(r);
        }
        OpLog.Write($"⚡ 割込み: 「{name}」を実行" + (paused.Count > 0 ? $" ({paused.Count} 件を一時停止)" : ""));
        InvokeSafe(UpdateRunUi);
        try
        {
            await RunMacroAsync(name, true, caller, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var r in paused) r.Resume();
            if (paused.Count > 0) OpLog.Write($"⚡ 割込み終了: {paused.Count} 件を再開");
            InvokeSafe(UpdateRunUi);
        }
    }

    public void CategoryCommand(string category, string command) => _env.Category?.Invoke(category, command);

    public CustomPreset FindCustom(string name) => _env.FindCustom?.Invoke(name);

    // =================================================================
    // ステップ編集
    // =================================================================
    private void RefreshSteps(int select)
    {
        _lstSteps.BeginUpdate();
        _lstSteps.Items.Clear();
        if (Current != null)
            foreach (var st in Current.Steps) _lstSteps.Items.Add(st);
        _lstSteps.EndUpdate();
        if (select >= 0 && select < _lstSteps.Items.Count) _lstSteps.SelectedIndex = select;
    }

    private void AddStep(bool insert)
    {
        if (Current == null) AddMacro(new Macro { Name = UniqueName("マクロ") });
        var m = Current;
        var st = ReadEditor();
        if (st == null) return;
        int i = _lstSteps.SelectedIndex;
        if (insert && i >= 0)
        {
            m.Steps.Insert(i, st);
            RefreshSteps(i);
        }
        else
        {
            int at = i >= 0 ? i + 1 : m.Steps.Count;
            m.Steps.Insert(at, st);
            RefreshSteps(at);
        }
    }

    private void RemoveStep()
    {
        var m = Current;
        int i = _lstSteps.SelectedIndex;
        if (m == null || i < 0) return;
        m.Steps.RemoveAt(i);
        RefreshSteps(Math.Min(i, m.Steps.Count - 1));
    }

    private void MoveStep(int dir)
    {
        var m = Current;
        int i = _lstSteps.SelectedIndex;
        int j = i + dir;
        if (m == null || i < 0 || j < 0 || j >= m.Steps.Count) return;
        (m.Steps[i], m.Steps[j]) = (m.Steps[j], m.Steps[i]);
        RefreshSteps(j);
    }

    private MacroStepKind Kind => (MacroStepKind)Math.Max(0, _cmbKind.SelectedIndex);

    /// <summary>種類に合わせて対象の候補を作り直す</summary>
    private void RefreshTargets()
    {
        var k = Kind;
        string keep = _cmbTarget.Text;
        _targets.Clear();

        if (MacroStep.IsMacroKind(k))
        {
            if (k is MacroStepKind.MacroStop or MacroStepKind.MacroPause or MacroStepKind.MacroResume)
                _targets.Add(new TargetItem { Id = MacroStep.AllMacros, Text = L10n.Auto("* 全マクロ (自分以外)") });
            foreach (var m in _s.Macros)
                if (m != Current) _targets.Add(new TargetItem { Id = m.Name, Text = m.Name });
        }
        else if (k == MacroStepKind.Category)
        {
            foreach (var c in ActionCatalog.Categories) _targets.Add(new TargetItem { Id = c, Text = L10n.Auto(c) });
        }
        else if (MacroStep.IsInputKind(k))
        {
            // 並び: 操作 (移動 → 視点 → 手・アイテム → システム) → カメラ → パラメータ → カスタム
            foreach (var cat in new[] { ActionCatalog.CatMove, ActionCatalog.CatLook, ActionCatalog.CatHand, ActionCatalog.CatSystem })
                foreach (var d in ActionCatalog.All.Where(x => x.Category == cat))
                    _targets.Add(new TargetItem { Id = d.Id, Text = $"[{L10n.Auto(d.Category)}] {L10n.Auto(d.Name)}" });
            foreach (var c in CameraCatalog.MacroTargets())
                _targets.Add(new TargetItem { Id = c.Address, Text = "[Cam] " + L10n.Auto(c.Name), Type = c.Type });
            foreach (var p in _env.Params?.Invoke() ?? new List<(string Name, string Address, string Type)>())
                _targets.Add(new TargetItem { Id = p.Address, Text = "[P] " + p.Name, Type = p.Type });
            foreach (var c in _env.Customs?.Invoke() ?? new List<CustomPreset>())
                _targets.Add(new TargetItem { Id = MacroStep.CustomPrefix + c.Name, Text = "[C] " + c.Name });
        }

        _cmbTarget.BeginUpdate();
        _cmbTarget.Items.Clear();
        _cmbTarget.Items.AddRange(_targets.Cast<object>().ToArray());
        _cmbTarget.EndUpdate();
        _cmbTarget.Text = keep;
    }

    private void OnKindChanged()
    {
        var k = Kind;
        bool target = MacroStep.UsesTarget(k);
        bool input = MacroStep.IsInputKind(k);
        _cmbTarget.Enabled = target;
        _cmbType.Enabled = input;
        _txtValue.Enabled = k is MacroStepKind.Press or MacroStepKind.Tap or MacroStepKind.Hold or MacroStepKind.Rapid
            or MacroStepKind.Send or MacroStepKind.Chat or MacroStepKind.Category;
        _nudMs.Enabled = k is MacroStepKind.Tap or MacroStepKind.Hold or MacroStepKind.Rapid or MacroStepKind.Wait;
        _nudCount.Enabled = k == MacroStepKind.Rapid;
        _chkParallel.Enabled = MacroStep.CanParallel(k);
        _lblValue.Text = k switch
        {
            MacroStepKind.Chat => "文字",
            MacroStepKind.Category => "ON/OFF/切替",
            _ => "値"
        };
        _lblTarget.Text = MacroStep.IsMacroKind(k) ? "マクロ" : k == MacroStepKind.Category ? "カテゴリ" : "対象";

        if (!_loading)
        {
            _nudMs.Value = k switch
            {
                MacroStepKind.Tap => 100,
                MacroStepKind.Hold => 1000,
                MacroStepKind.Rapid => 150,
                MacroStepKind.Wait => 500,
                _ => _nudMs.Value
            };
            _cmbTarget.Text = "";
            if (k == MacroStepKind.Category) _txtValue.Text = "切替";
        }
        RefreshTargets();
    }

    private string ResolveTarget()
    {
        string text = (_cmbTarget.Text ?? "").Trim();
        var item = _targets.FirstOrDefault(t => t.Text == text);
        return item != null ? item.Id : text;
    }

    private string TargetDisplay(string target)
    {
        var item = _targets.FirstOrDefault(t => string.Equals(t.Id, target, StringComparison.OrdinalIgnoreCase));
        return item != null ? item.Text : target ?? "";
    }

    private MacroStep ReadEditor()
    {
        var k = Kind;
        var st = new MacroStep
        {
            Kind = k,
            Type = (_cmbType.SelectedItem as string) ?? "Bool",
            Value = _txtValue.Enabled ? _txtValue.Text : "",
            Ms = (int)_nudMs.Value,
            Count = (int)_nudCount.Value,
            Parallel = _chkParallel.Enabled && _chkParallel.Checked,
            Target = MacroStep.UsesTarget(k) ? ResolveTarget() : ""
        };

        if (MacroStep.IsInputKind(k))
        {
            bool isAction = ActionCatalog.Find(st.Target) != null;
            bool isAddr = st.Target.StartsWith("/");
            bool isCustom = st.Target.StartsWith(MacroStep.CustomPrefix);
            if (!isAction && !isAddr && !isCustom)
            {
                Warn("対象を一覧から選ぶか、/ から始まる OSC アドレスを入力してください。");
                return null;
            }
            if (isCustom && FindCustom(st.Target.Substring(MacroStep.CustomPrefix.Length)) == null)
            {
                Warn("カスタムプリセットが見つかりません。");
                return null;
            }
            if (isAddr && !OscValue.TryParse(st.Type, st.Value, out _))
            {
                Warn($"値「{st.Value}」を {st.Type} に変換できません。");
                return null;
            }
        }
        else if (MacroStep.IsMacroKind(k))
        {
            if (string.IsNullOrWhiteSpace(st.Target)) { Warn("対象のマクロを選んでください。"); return null; }
            if (st.Target != MacroStep.AllMacros && FindMacro(st.Target) == null) { Warn($"マクロ「{st.Target}」が見つかりません。"); return null; }
            if (Current != null && st.Target == Current.Name) { Warn("自分自身は対象にできません (繰り返しは「繰返し」を使ってください)。"); return null; }
        }
        else if (k == MacroStepKind.Category)
        {
            if (!ActionCatalog.Categories.Contains(st.Target)) { Warn("カテゴリを選んでください。"); return null; }
            var v = (st.Value ?? "").Trim().ToUpperInvariant();
            if (v is not ("" or "ON" or "OFF" or "切替")) { Warn("値は ON / OFF / 切替 のどれかにしてください。"); return null; }
            st.Value = v.Length == 0 ? "切替" : v;
        }
        return st;
    }

    private void LoadEditor(MacroStep s)
    {
        _loading = true;
        try
        {
            _cmbKind.SelectedIndex = (int)s.Kind;
            OnKindChanged(); // 対象候補を作る (読込中なので値は上書きしない)
            _cmbTarget.Text = TargetDisplay(s.Target);
            _cmbType.SelectedItem = OscValue.Types.Contains(s.Type) ? s.Type : "Bool";
            _txtValue.Text = s.Value ?? "";
            _nudMs.Value = Math.Clamp(s.Ms, (int)_nudMs.Minimum, (int)_nudMs.Maximum);
            _nudCount.Value = Math.Clamp(s.Count, (int)_nudCount.Minimum, (int)_nudCount.Maximum);
            _chkParallel.Checked = s.Parallel;
        }
        finally { _loading = false; }
    }

    private void Warn(string msg) => Ui.Msg(this, msg, "マクロ", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // =================================================================
    // 描画・テーマ
    // =================================================================
    private void DrawStep(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _lstSteps.Items.Count) return;
        var st = (MacroStep)_lstSteps.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;

        int current = -1;
        bool paused = false;
        var m = Current;
        if (m != null)
        {
            MacroRunner r;
            lock (_runners) _runners.TryGetValue(m, out r);
            if (r != null && r.IsRunning) { current = r.CurrentStep; paused = r.IsPaused; }
        }
        bool isCurrent = e.Index == current;

        Color back = sel ? Theme.Selection : isCurrent ? (paused ? Theme.Warn : Theme.Active) : Theme.InputBack;
        using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);

        Color fore = isCurrent && !sel ? Color.White : Theme.Fore;
        if (!isCurrent && !sel)
        {
            if (st.Kind is MacroStepKind.Wait or MacroStepKind.WaitParallel && !st.Parallel) fore = Theme.SubFore;
            if (st.Parallel && MacroStep.CanParallel(st.Kind)) fore = Theme.Accent;
            if (MacroStep.IsMacroKind(st.Kind) || st.Kind == MacroStepKind.Category) fore = Theme.Warn;
        }
        string mark = isCurrent ? (paused ? "⏸" : "▶") : " ";
        string text = $"{mark} {e.Index + 1,2}.  {st.Describe()}";
        TextRenderer.DrawText(e.Graphics, L10n.Auto(text), _lstSteps.Font, e.Bounds, fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    private void UpdateItemHeight()
    {
        if (!_lstSteps.IsHandleCreated) return;
        _lstSteps.ItemHeight = Math.Clamp(Ui.LineHeight(_lstSteps), 12, 255);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _lstSteps.HandleCreated += (_, _) => UpdateItemHeight();
        _lstSteps.DpiChangedAfterParent += (_, _) => UpdateItemHeight();
        UpdateItemHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_lstSteps != null) UpdateItemHeight();
        if (_titleLabel != null)
        {
            _titleLabel.Font = new Font(Font, FontStyle.Bold);
            _helpTitleLabel.Font = new Font(Font, FontStyle.Bold);
        }
    }

    public void OnThemeChanged() => UpdateRunUi();
}
