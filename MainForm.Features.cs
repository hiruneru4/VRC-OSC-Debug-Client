using System.Diagnostics;
using VRCOscDebugClient.Core;
using VRCOscDebugClient.Osc;
using VRCOscDebugClient.UI;

namespace VRCOscDebugClient;

/// <summary>
/// ・マクロ / コンボのファイル保存 (フォルダ指定・自動読込・自動保存)
/// ・マクロ / コンボ機能の有効化 (自己責任の同意) と ON/OFF (ショートカットキー)
/// ・アバターの大きさ (OSC Avatar Scaling: /avatar/eyeheight)
/// </summary>
public sealed partial class MainForm
{
    // =====================================================================
    // マクロ・コンボのファイル保存
    // =====================================================================
    private PresetStore<Macro> _macroStore;
    private PresetStore<ComboPreset> _comboStore;
    private readonly System.Windows.Forms.Timer _presetTimer = new() { Interval = 5000 };
    private TextBox _txtMacroFolder, _txtComboFolder;

    private const string Disclaimer =
        "この機能を使用してのトラブル等発生しても自己責任でお願い致します。";

    private void InitPresetStores()
    {
        _macroStore = new PresetStore<Macro>(m => m.Name);
        _comboStore = new PresetStore<ComboPreset>(c => c.Name);
        string mf = _s.MacroFolderPath, cf = _s.ComboFolderPath;

        // 以前の settings.json に入っていたマクロ・コンボをフォルダへ移す (初回のみ)
        if (!_s.PresetFilesMigrated)
        {
            bool ok = true;
            if (_s.Macros.Count > 0 && !PresetStore<Macro>.HasFiles(mf))
            {
                _macroStore.Load(mf);
                _macroStore.Save(_s.Macros);
                ok &= _macroStore.Errors.Count == 0;
            }
            if (_s.Combos.Count > 0 && !PresetStore<ComboPreset>.HasFiles(cf))
            {
                _comboStore.Load(cf);
                _comboStore.Save(_s.Combos);
                ok &= _comboStore.Errors.Count == 0;
            }
            _s.PresetFilesMigrated = ok; // 失敗したら settings.json 側にも残しておく
        }

        var macros = _macroStore.Load(mf);
        ReportStoreErrors("マクロ", _macroStore);
        _s.Macros.Clear();
        _s.Macros.AddRange(UniqueNames(macros, m => m.Name, (m, n) => m.Name = n, Enumerable.Empty<string>()));

        var combos = _comboStore.Load(cf);
        ReportStoreErrors("コンボ", _comboStore);
        _s.Combos = UniqueNames(combos, c => c.Name, (c, n) => c.Name = n, Enumerable.Empty<string>());

        // フォルダに新しいファイルが置かれたら自動で取り込む
        _macroStore.FilesAdded += () => BeginInvokeSafe(LoadNewMacroFiles);
        _comboStore.FilesAdded += () => BeginInvokeSafe(LoadNewComboFiles);

        // 編集内容はこまめにファイルへ (変更があったものだけ書く)
        _presetTimer.Tick += (_, _) => SavePresetFiles(false);
        _presetTimer.Start();
        FormClosed += (_, _) =>
        {
            _presetTimer.Stop();
            _macroStore.Dispose();
            _comboStore.Dispose();
        };
    }

    /// <summary>名前が重複していたら「名前 (2)」のように付け直す</summary>
    private static List<T> UniqueNames<T>(List<T> list, Func<T, string> get, Action<T, string> set, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken);
        foreach (var item in list)
        {
            string baseName = get(item).Trim();
            string name = baseName;
            for (int n = 2; used.Contains(name); n++) name = $"{baseName} ({n})";
            if (name != get(item)) set(item, name);
            used.Add(name);
        }
        return list;
    }

    private void ReportStoreErrors(string kind, PresetStore<Macro> store) => ReportErrors(kind, store.Errors);
    private void ReportStoreErrors(string kind, PresetStore<ComboPreset> store) => ReportErrors(kind, store.Errors);

    private void ReportErrors(string kind, List<string> errors)
    {
        if (errors.Count == 0) return;
        foreach (var e in errors) OpLog.Write($"⚠ {kind}ファイル: {e}");
        _lastError = $"{kind}ファイルの読み書きに失敗 ({errors.Count} 件) — 操作ログを確認してください";
    }

    /// <summary>マクロ・コンボをファイルへ保存 (変更のあったものだけ)</summary>
    private void SavePresetFiles(bool log)
    {
        if (_macroStore == null) return;
        int m = _macroStore.Save(_s.Macros.ToList());
        ReportStoreErrors("マクロ", _macroStore);
        int c = 0;
        if (_cmbComboPreset != null)
        {
            _s.Combos = _cmbComboPreset.Items.Cast<ComboPreset>().ToList();
            c = _comboStore.Save(_s.Combos);
            ReportStoreErrors("コンボ", _comboStore);
        }
        if (log) OpLog.Write($"💾 マクロ {m} 件 / コンボ {c} 件をファイルに保存");
    }

    private void LoadNewMacroFiles()
    {
        if (_macroPage == null) return;
        var list = _macroStore.LoadNew();
        ReportStoreErrors("マクロ", _macroStore);
        if (list.Count == 0) return;
        UniqueNames(list, m => m.Name, (m, n) => m.Name = n, _s.Macros.Select(x => x.Name));
        _macroPage.AppendMacros(list);
        OpLog.Write($"📂 マクロを読み込み: {string.Join(", ", list.Select(m => m.Name))}");
    }

    private void LoadNewComboFiles()
    {
        if (_cmbComboPreset == null) return;
        var list = _comboStore.LoadNew();
        ReportStoreErrors("コンボ", _comboStore);
        if (list.Count == 0) return;
        UniqueNames(list, c => c.Name, (c, n) => c.Name = n, _cmbComboPreset.Items.Cast<ComboPreset>().Select(x => x.Name));
        foreach (var c in list) _cmbComboPreset.Items.Add(c);
        _s.Combos = _cmbComboPreset.Items.Cast<ComboPreset>().ToList();
        OpLog.Write($"📂 コンボを読み込み: {string.Join(", ", list.Select(c => c.Name))}");
    }

    /// <summary>フォルダの内容で一覧を読み直す (アプリ内の未保存の変更は捨てる)</summary>
    private void ReloadMacros()
    {
        _macroPage.StopAll();
        var list = _macroStore.Load(_s.MacroFolderPath);
        ReportStoreErrors("マクロ", _macroStore);
        UniqueNames(list, m => m.Name, (m, n) => m.Name = n, Enumerable.Empty<string>());
        _s.Macros.Clear();
        _s.Macros.AddRange(list);
        _macroPage.ReloadList();
        OpLog.Write($"📂 マクロを再読込: {list.Count} 件 ({_s.MacroFolderPath})");
    }

    private void ReloadCombos()
    {
        StopComboQuiet();
        var list = _comboStore.Load(_s.ComboFolderPath);
        ReportStoreErrors("コンボ", _comboStore);
        UniqueNames(list, c => c.Name, (c, n) => c.Name = n, Enumerable.Empty<string>());
        _s.Combos = list;
        _cmbComboPreset.BeginUpdate();
        _cmbComboPreset.Items.Clear();
        foreach (var c in list) _cmbComboPreset.Items.Add(c);
        _cmbComboPreset.EndUpdate();
        _cmbComboPreset.Text = "";
        OpLog.Write($"📂 コンボを再読込: {list.Count} 件 ({_s.ComboFolderPath})");
    }

    /// <summary>保存先フォルダを変える。新しいフォルダが空なら今の一覧をコピー、ファイルがあればそれを読み込む</summary>
    private void ChangePresetFolder(bool macro)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = macro ? "マクロの保存先フォルダ" : "コンボの保存先フォルダ",
            UseDescriptionForTitle = true,
            SelectedPath = macro ? _s.MacroFolderPath : _s.ComboFolderPath,
            ShowNewFolderButton = true,
        };
        if (Ui.ShowCommon(this, dlg) != DialogResult.OK) return;
        string path = dlg.SelectedPath;
        string current = macro ? _s.MacroFolderPath : _s.ComboFolderPath;
        if (string.Equals(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(current).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)) return;

        SavePresetFiles(false); // 今の内容は元のフォルダに保存しておく
        string kind = macro ? "マクロ" : "コンボ";
        bool hasFiles = macro ? PresetStore<Macro>.HasFiles(path) : PresetStore<ComboPreset>.HasFiles(path);
        string def = Path.Combine(AppSettings.DataFolder, macro ? "Macros" : "Combos");
        string stored = string.Equals(path, def, StringComparison.OrdinalIgnoreCase) ? "" : path;
        if (macro) _s.MacroFolder = stored; else _s.ComboFolder = stored;

        if (hasFiles)
        {
            if (macro) ReloadMacros(); else ReloadCombos();
            Ui.Msg(this, $"新しいフォルダの{kind}を読み込みました。\n(切り替える前の{kind}は元のフォルダに保存してあります)\n\n{path}",
                kind, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            // 空のフォルダ → 今の一覧をそのまま新しいフォルダへ保存
            if (macro)
            {
                _macroStore.Load(path);
                _macroStore.Save(_s.Macros.ToList());
                ReportStoreErrors(kind, _macroStore);
            }
            else
            {
                _comboStore.Load(path);
                _comboStore.Save(_s.Combos);
                ReportStoreErrors(kind, _comboStore);
            }
            OpLog.Write($"📂 {kind}の保存先を変更 (今の{kind}をコピー): {path}");
        }
        UpdateFolderBoxes();
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ui.Msg(this, "フォルダを開けません: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateFolderBoxes()
    {
        if (_txtMacroFolder != null) _txtMacroFolder.Text = _s.MacroFolderPath;
        if (_txtComboFolder != null) _txtComboFolder.Text = _s.ComboFolderPath;
    }

    // =====================================================================
    // マクロ・コンボ機能の有効化 / ON・OFF
    // =====================================================================
    private RCheck _chkFeatureEnable;
    private readonly List<RToggle> _macroToggles = new(), _comboToggles = new();
    private readonly List<FastLabel> _macroNotes = new(), _comboNotes = new();
    private bool _featureSync;

    /// <summary>マクロを実行できる (有効化済み かつ マクロ ON)</summary>
    private bool MacroActive => _s.MacroComboEnabled && _s.MacroActive;
    private bool ComboActive => _s.MacroComboEnabled && _s.ComboActive;

    /// <summary>自己責任の同意を取って有効化。同意しなければ false</summary>
    private bool EnableWithConsent()
    {
        if (_s.MacroComboEnabled) return true;
        bool ok = ConfirmDialog.Ask(this, "マクロ・コンボ機能の有効化",
            Disclaimer, "同意して有効化", "キャンセル");
        if (!ok) return false;
        _s.MacroComboEnabled = true;
        OpLog.Write("✅ マクロ・コンボ機能を有効化 (自己責任に同意)");
        RegisterHotkeys();
        RefreshFeatureUi();
        return true;
    }

    private void DisableFeatures()
    {
        if (!_s.MacroComboEnabled) return;
        _s.MacroComboEnabled = false;
        _macroPage?.StopAll();
        StopComboQuiet();
        OpLog.Write("⛔ マクロ・コンボ機能を無効化");
        RegisterHotkeys();
        RefreshFeatureUi();
    }

    /// <summary>マクロ / コンボ機能の ON・OFF。OFF にすると動作中のものも止める</summary>
    private void SetFeatureActive(bool macro, bool on, string by)
    {
        if (on && !_s.MacroComboEnabled)
        {
            RefreshFeatureUi();
            return;
        }
        if (macro)
        {
            _s.MacroActive = on;
            if (!on) _macroPage?.StopAll();
        }
        else
        {
            _s.ComboActive = on;
            if (!on) StopComboQuiet();
        }
        OpLog.Write($"{(on ? "▶" : "■")} {(macro ? "マクロ" : "コンボ")}機能 {(on ? "ON" : "OFF")} ({by})");
        if (macro) RegisterHotkeys(); // 各マクロのキーは ON の間だけ登録
        RefreshFeatureUi();
    }

    private void StopComboQuiet()
    {
        _comboCts?.Cancel();
        _comboRunning = false;
        if (_clbCombo != null) foreach (var r in CheckedComboRows()) r.Runner.Stop();
    }

    /// <summary>コンボを実行してよいか (だめならメッセージ)</summary>
    private bool CheckComboActive(bool showMessage)
    {
        if (ComboActive) return true;
        FeatureBlocked(false, showMessage);
        return false;
    }

    private void FeatureBlocked(bool macro, bool showMessage = false)
    {
        string kind = macro ? "マクロ" : "コンボ";
        string why = !_s.MacroComboEnabled
            ? $"{kind}機能が有効化されていません (「設定」→「マクロ・コンボ」で有効化)"
            : $"{kind}機能が OFF です";
        OpLog.Write("⚠ " + why);
        _lastError = why;
        if (showMessage) Ui.Msg(this, why, kind, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private RToggle FeatureToggle(bool macro)
    {
        string kind = macro ? "マクロ" : "コンボ";
        var t = Ui.Toggle($"{kind}機能 OFF", 110);
        t.OnText = $"{kind}機能 ON";
        t.OffText = $"{kind}機能 OFF";
        t.CheckedColor = Theme.Active;
        t.CheckedChanged += (_, _) =>
        {
            if (_featureSync) return;
            if (t.Checked && !EnableWithConsent())
            {
                RefreshFeatureUi();
                return;
            }
            SetFeatureActive(macro, t.Checked, "ボタン");
        };
        (macro ? _macroToggles : _comboToggles).Add(t);
        _tip.SetToolTip(t, $"{kind}機能の ON / OFF (OFF にすると動作中の{kind}も停止)");
        return t;
    }

    /// <summary>マクロ / コンボのタブ上部: 機能の ON/OFF と状態</summary>
    private Control BuildFeatureBar(bool macro)
    {
        var t = FeatureToggle(macro);
        var note = Ui.Info(420);
        (macro ? _macroNotes : _comboNotes).Add(note);
        var bar = Ui.TopBar();
        bar.Padding = new Padding(0, 0, 0, 3);
        bar.Controls.AddRange(new Control[] { t, note });
        RefreshFeatureUi();
        return bar;
    }

    private void RefreshFeatureUi()
    {
        _featureSync = true;
        try
        {
            if (_chkFeatureEnable != null) _chkFeatureEnable.Checked = _s.MacroComboEnabled;
            foreach (var t in _macroToggles) t.Checked = MacroActive;
            foreach (var t in _comboToggles) t.Checked = ComboActive;
            foreach (var l in _macroNotes) l.Text = FeatureNote(true);
            foreach (var l in _comboNotes) l.Text = FeatureNote(false);
        }
        finally { _featureSync = false; }
    }

    private string FeatureNote(bool macro)
    {
        string kind = macro ? "マクロ" : "コンボ";
        if (!_s.MacroComboEnabled) return $"無効: 実行するには「設定」→「マクロ・コンボ」で有効化してください (編集はできます)";
        string key = macro ? _s.MacroToggleHotkey : _s.ComboToggleHotkey;
        string k = string.IsNullOrEmpty(key) ? "" : $"   ON/OFF キー: {key}";
        return ((macro ? _s.MacroActive : _s.ComboActive) ? $"ON: {kind}を実行できます" : $"OFF: {kind}は実行されません") + k;
    }

    /// <summary>設定タブ: マクロ・コンボ (有効化・ON/OFF キー・保存先)</summary>
    private Control BuildFeatureSettingsCard()
    {
        _chkFeatureEnable = Ui.Chk("マクロ・コンボ機能を有効化する (自己責任)", _s.MacroComboEnabled);
        _chkFeatureEnable.CheckedChanged += (_, _) =>
        {
            if (_featureSync) return;
            if (_chkFeatureEnable.Checked)
            {
                if (!EnableWithConsent()) RefreshFeatureUi();
            }
            else DisableFeatures();
        };
        _tip.SetToolTip(_chkFeatureEnable, "有効化しないとマクロ・コンボは実行されません (ショートカットキーも登録されません)");

        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void AddRow(string label, Control c)
        {
            int y = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(Ui.L(label), 0, y);
            grid.Controls.Add(c, 1, y);
        }

        HotkeyBox KeyBox(bool macro)
        {
            var hk = new HotkeyBox
            {
                Width = Ui.W(150), Anchor = AnchorStyles.Left, Margin = Ui.InputMargin,
                Hotkey = Hotkey.Parse(macro ? _s.MacroToggleHotkey : _s.ComboToggleHotkey)
            };
            hk.HotkeyChanged += (_, _) =>
            {
                if (macro) _s.MacroToggleHotkey = hk.Hotkey.ToString();
                else _s.ComboToggleHotkey = hk.Hotkey.ToString();
                RegisterHotkeys();
                RefreshFeatureUi();
            };
            _tip.SetToolTip(hk, $"{(macro ? "マクロ" : "コンボ")}機能を ON/OFF するキー (Back/Delete で解除)\n有効化している間だけ登録されます");
            return hk;
        }
        AddRow("マクロ", Ui.Row(FeatureToggle(true), Ui.L("ON/OFF キー"), KeyBox(true)));
        AddRow("コンボ", Ui.Row(FeatureToggle(false), Ui.L("ON/OFF キー"), KeyBox(false)));

        Control FolderRow(bool macro)
        {
            var box = Ui.Txt("", 330);
            box.ReadOnly = true;
            if (macro) _txtMacroFolder = box; else _txtComboFolder = box;
            var browse = Ui.Btn("変更");
            var open = Ui.Btn("開く");
            var reload = Ui.Btn("再読込");
            browse.Click += (_, _) => ChangePresetFolder(macro);
            open.Click += (_, _) => OpenFolder(macro ? _s.MacroFolderPath : _s.ComboFolderPath);
            reload.Click += (_, _) =>
            {
                string kind = macro ? "マクロ" : "コンボ";
                if (Ui.Msg(this, $"フォルダのファイルから{kind}を読み直します。\n動作中の{kind}は停止します。", kind,
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                if (macro) ReloadMacros(); else ReloadCombos();
            };
            _tip.SetToolTip(browse, "保存先フォルダを選ぶ\n空のフォルダ: 今の一覧をコピー / ファイルがあるフォルダ: その内容を読み込む");
            _tip.SetToolTip(reload, "フォルダのファイルから一覧を読み直す (ファイルを直接編集した時など)");
            return Ui.Row(box, browse, open, reload);
        }
        AddRow("マクロの保存先", FolderRow(true));
        AddRow("コンボの保存先", FolderRow(false));
        UpdateFolderBoxes();
        RefreshFeatureUi();

        return Ui.WideCard(
            Ui.Sub("マクロ・コンボ"),
            _chkFeatureEnable,
            grid,
            Ui.Sub("※ 有効化しないとマクロ・コンボは実行されません (編集はできます)"),
            Ui.Sub("※ 1 件 = 1 ファイル (*.json)。フォルダに置いたファイルは自動で読み込み、編集内容は自動で保存されます"),
            Ui.Sub("※ アプリ内で削除したマクロ・コンボは、ファイルも削除されます"));
    }

    // =====================================================================
    // アバターの大きさ (OSC Avatar Scaling)
    // =====================================================================
    private const string EyeHeight = "/avatar/eyeheight";
    private volatile float _eyeHeight = float.NaN, _eyeMin = float.NaN, _eyeMax = float.NaN, _eyeOriginal = float.NaN;
    private volatile int _scaleAllowed = -1;
    private volatile bool _scaleAwaitOriginal = true;
    private int _scaleVer, _scaleShownVer = -1;
    private FastLabel _lblScaleNow, _lblScaleWarn;
    private NumericUpDown _nudScale, _nudScaleStep;
    private TrackBar _trkScale;
    private bool _scaleSuppress;
    private long _scaleLastSend;

    // スライダーは 0.1〜100 m を対数で (小さい側も細かく動かせるように)
    private const double ScaleLogMin = -1, ScaleLogMax = 2;
    private static int ScaleToBar(double m) =>
        (int)Math.Round((Math.Log10(Math.Clamp(m, 0.1, 100)) - ScaleLogMin) / (ScaleLogMax - ScaleLogMin) * 1000);
    private static float BarToScale(int v) =>
        (float)Math.Round(Math.Pow(10, ScaleLogMin + v / 1000.0 * (ScaleLogMax - ScaleLogMin)), 3);

    private Control BuildScaleCard()
    {
        _lblScaleNow = Ui.Info(470, false);
        _lblScaleNow.Text = "現在: ? (受信を有効にして接続すると表示)";
        _lblScaleWarn = Ui.Info(470);
        _nudScale = Ui.Nud(0.01M, 10000M, 1.5M, 80, 0.05M, 3);
        var send = Ui.Btn("送信");
        var minus = Ui.Btn("−", 30);
        var plus = Ui.Btn("+", 30);
        _nudScaleStep = Ui.Nud(0.01M, 100M, Math.Clamp(_s.ScaleStep, 0.01M, 100M), 64, 0.05M, 2);
        var half = Ui.Btn("×0.5");
        var dbl = Ui.Btn("×2");
        var orig = Ui.Btn("元に戻す");
        _trkScale = new TrackBar
        {
            Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, AutoSize = false,
            Width = Ui.W(260), Height = 26, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 1, 0, 1),
            SmallChange = 5, LargeChange = 50, Value = ScaleToBar(1.5)
        };

        var presets = new List<Control>();
        foreach (var m in new[] { 0.3f, 0.5f, 1.0f, 1.5f, 2.0f, 3.0f, 5.0f, 10f })
        {
            var b = Ui.Btn(m.ToString("0.0#") + "m");
            b.Click += (_, _) => SendScale(m, "プリセット");
            presets.Add(b);
        }

        send.Click += (_, _) => SendScale((float)_nudScale.Value, "入力");
        _nudScale.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            SendScale((float)_nudScale.Value, "入力");
            e.SuppressKeyPress = true;
        };
        minus.Click += (_, _) => SendScale(CurrentScale() - (float)_nudScaleStep.Value, "−");
        plus.Click += (_, _) => SendScale(CurrentScale() + (float)_nudScaleStep.Value, "+");
        half.Click += (_, _) => SendScale(CurrentScale() * 0.5f, "×0.5");
        dbl.Click += (_, _) => SendScale(CurrentScale() * 2f, "×2");
        orig.Click += (_, _) =>
        {
            float o = _eyeOriginal;
            if (float.IsNaN(o)) { Ui.Msg(this, "元の大きさをまだ受信していません", Text); return; }
            SendScale(o, "元に戻す");
        };
        _nudScaleStep.ValueChanged += (_, _) => _s.ScaleStep = _nudScaleStep.Value;
        _trkScale.ValueChanged += (_, _) =>
        {
            if (_scaleSuppress) return;
            float v = BarToScale(_trkScale.Value);
            SetNudScale(v);
            // ドラッグ中は送りすぎないよう間引く (離した時に最後の値を送る)
            long now = Environment.TickCount64;
            if (now - _scaleLastSend >= 80) { _scaleLastSend = now; SendScale(v, null); }
        };
        _trkScale.MouseUp += (_, _) => SendScale(BarToScale(_trkScale.Value), "スライダー");

        _tip.SetToolTip(send, $"入力した目の高さ (m) を {EyeHeight} に送信 (Enter でも送信)");
        _tip.SetToolTip(minus, "現在の大きさから刻み分だけ小さく");
        _tip.SetToolTip(plus, "現在の大きさから刻み分だけ大きく");
        _tip.SetToolTip(_nudScaleStep, "＋/− で変える量 (m)");
        _tip.SetToolTip(orig, "アバターを読み込んだ時の大きさに戻す");
        _tip.SetToolTip(_trkScale, "0.1〜100 m (対数)。動かすとすぐ送信");

        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddRow(string label, Control c)
        {
            int y = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(Ui.L(label), 0, y);
            grid.Controls.Add(c, 1, y);
        }
        AddRow("目の高さ", Ui.Row(_nudScale, Ui.L("m"), send, minus, plus, Ui.L("刻み"), _nudScaleStep, Ui.L("m"), half, dbl, orig));
        AddRow("スライダー", _trkScale);
        AddRow("プリセット", Ui.Row(presets.ToArray()));

        return Ui.WideCard(
            Ui.Sub("アバターの大きさ (目の高さ)"),
            _lblScaleNow,
            Ui.Stretch(grid),
            _lblScaleWarn,
            Ui.Sub("※ VRChat の公式サポートは 0.1〜100 m (範囲外は VRChat の画面に警告が出ます)。OSC では 0.01〜10000 m まで指定できます"),
            Ui.Sub("※ ワールドで大きさの変更が禁止されていると無視されます。ワールドに制限がある場合はその範囲に戻されます"),
            Ui.Sub("※ マクロでは「値を送信」で /avatar/eyeheight (Float) を指定すると変更できます"));
    }

    private float CurrentScale()
    {
        float v = _eyeHeight;
        return float.IsNaN(v) ? (float)_nudScale.Value : v;
    }

    private void SetNudScale(float v)
    {
        decimal d = Math.Clamp((decimal)Math.Round(v, 3), _nudScale.Minimum, _nudScale.Maximum);
        if (_nudScale.Value != d) _nudScale.Value = d;
    }

    private void SendScale(float meters, string by)
    {
        meters = (float)Math.Round(Math.Clamp(meters, 0.01f, 10000f), 3);
        SetNudScale(meters);
        _scaleSuppress = true;
        try { _trkScale.Value = ScaleToBar(meters); }
        finally { _scaleSuppress = false; }
        if (!_osc.IsConnected)
        {
            if (by != null) _lastError = "未接続のため大きさを送信できません";
            return;
        }
        _osc.Send(EyeHeight, meters);
        if (by != null) OpLog.Write($"📏 大きさ: 目の高さ {meters:0.###} m ({by})");
        if (_scaleAllowed == 0 && by != null) _lastError = "このワールドでは大きさの変更が禁止されています";
    }

    /// <summary>受信スレッドから呼ばれる</summary>
    private void OnScaleReceived(OscMessage m)
    {
        switch (m.Address)
        {
            case "/avatar/change":
                _scaleAwaitOriginal = true; // 次に来た目の高さを「元の大きさ」にする
                break;
            case EyeHeight:
                if (!TryFloat(m, out var h)) return;
                _eyeHeight = h;
                if (_scaleAwaitOriginal || float.IsNaN(_eyeOriginal)) { _eyeOriginal = h; _scaleAwaitOriginal = false; }
                break;
            case "/avatar/eyeheightmin":
                if (TryFloat(m, out var mn)) _eyeMin = mn; else return;
                break;
            case "/avatar/eyeheightmax":
                if (TryFloat(m, out var mx)) _eyeMax = mx; else return;
                break;
            case "/avatar/eyeheightscalingallowed":
                if (m.Args.Length == 0) return;
                _scaleAllowed = m.Args[0] is bool b ? (b ? 1 : 0) : (TryFloat(m, out var f) ? (f != 0 ? 1 : 0) : -1);
                break;
            default:
                return;
        }
        Interlocked.Increment(ref _scaleVer);
    }

    private static bool TryFloat(OscMessage m, out float v)
    {
        v = float.NaN;
        if (m.Args.Length == 0) return false;
        try
        {
            v = m.Args[0] switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                long l => l,
                bool b => b ? 1 : 0,
                _ => Convert.ToSingle(m.Args[0], System.Globalization.CultureInfo.InvariantCulture)
            };
            return !float.IsNaN(v);
        }
        catch { return false; }
    }

    private void UpdateScaleUi()
    {
        if (_lblScaleNow == null) return;
        int ver = Volatile.Read(ref _scaleVer);
        if (ver == _scaleShownVer) return;
        _scaleShownVer = ver;

        float h = _eyeHeight, mn = _eyeMin, mx = _eyeMax, o = _eyeOriginal;
        int allowed = _scaleAllowed;
        string F(float v) => float.IsNaN(v) ? "?" : v.ToString("0.00");
        string allow = allowed < 0 ? "?" : allowed == 1 ? "可" : "禁止 (ワールド)";
        _lblScaleNow.Text = $"現在: {F(h)} m   元: {F(o)} m   メニューの範囲: {F(mn)}〜{F(mx)} m   変更: {allow}";

        string warn = "";
        if (allowed == 0) warn = "⚠ このワールドでは大きさの変更が禁止されています (送信しても無視されます)";
        else if (!float.IsNaN(h) && (h < 0.1f || h > 100f)) warn = "⚠ 公式サポート範囲 (0.1〜100 m) の外です";
        _lblScaleWarn.Text = warn;
        _lblScaleWarn.ForeColor = Theme.Warn;

        // 受信した値に表示を合わせる (入力中・ドラッグ中は触らない)
        if (!float.IsNaN(h) && !_nudScale.Focused && !_trkScale.Capture)
        {
            SetNudScale(h);
            _scaleSuppress = true;
            try { _trkScale.Value = ScaleToBar(h); }
            finally { _scaleSuppress = false; }
        }
    }
}
