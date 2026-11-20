using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using VRCOscDebugClient.Core;
using VRCOscDebugClient.Osc;

namespace VRCOscDebugClient.UI;

/// <summary>
/// アバターパラメータ操作パネル。
/// VRChat が作るアバターの OSC 設定ファイル
/// (%LOCALAPPDATA%Low\VRChat\VRChat\OSC\usr_*\Avatars\avtr_*.json) からパラメータ一覧を作る。
/// 一覧は ParamGrid (1 つのコントロールで見えている行だけ描く) なので、数百件でも軽い。
/// 設定ファイルの読み込み・解析はバックグラウンドで行う。
/// </summary>
public sealed class ParamPanel : UserControl, IThemeAware
{
    private sealed class AvatarFile
    {
        public string Path, Id, Name;
        public DateTime Time;
        public override string ToString() => $"{Name}   ({Time:MM/dd HH:mm})";
    }

    // VRChat 組み込み (読み取り専用) パラメータ
    private static readonly HashSet<string> BuiltIn = new(StringComparer.Ordinal)
    {
        "IsLocal", "PreviewMode", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight",
        "GestureRightWeight", "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright",
        "Grounded", "Seated", "AFK", "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs",
        "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled", "ScaleModified", "ScaleFactor",
        "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent"
    };

    private const string ParamPrefix = "/avatar/parameters/";
    private static readonly string[] Types = { "Bool", "Int", "Float" };

    private readonly OscClient _osc;
    private readonly ValueLocker _locker;
    private readonly AppSettings _s;
    private readonly ToolTip _tip;

    private readonly ComboBox _cmbAvatar;
    private readonly TextBox _txtFilter;
    private readonly RCheck _chkHideRO, _chkAutoAdd;
    private readonly FastLabel _lblInfo;
    private readonly RoundPanel _host;
    private readonly ParamGrid _grid;
    private readonly ContextMenuStrip _menu = new();

    private readonly List<ParamItem> _items = new();
    private readonly Dictionary<string, ParamItem> _byAddr = new();
    private readonly ConcurrentDictionary<string, object> _incoming = new();  // 受信スレッド → UI
    private readonly Dictionary<string, object> _cache = new();               // 最後に受信した値
    private string _pendingAvatar;
    private string _currentAvatarId;
    private bool _loadingCombo, _initialized, _needRebuild;
    private long _lastRebuild;
    private int _scanVersion, _loadVersion;
    private ParamItem _menuItem;
    private readonly ConcurrentDictionary<string, string> _avatarNames = new();

    /// <summary>アバター ID から名前 (設定ファイルにあれば)。どのスレッドからでも呼べる</summary>
    public string AvatarName(string id) => id != null && _avatarNames.TryGetValue(id, out var n) ? n : null;

    public event Action<string, string, string> SendToCustom;
    public event Action<string, string, string> AddToMacro;
    public event Action<string, string, string, string> AddToCustomPreset;

    public ParamPanel(OscClient osc, ValueLocker locker, AppSettings s, ToolTip tip)
    {
        _osc = osc;
        _locker = locker;
        _s = s;
        _tip = tip;
        Dock = DockStyle.Fill;

        _cmbAvatar = Ui.Combo(240, false);
        _cmbAvatar.DropDownWidth = Ui.W(340);
        var change = Ui.Btn("👤 このアバターに変更");
        var reload = Ui.Btn("再読込");
        var open = Ui.Btn("開く…");
        _txtFilter = Ui.Txt("", 150);
        _txtFilter.PlaceholderText = "パラメータ名で検索";
        _chkHideRO = Ui.Chk("読み取り専用を隠す", s.ParamHideReadOnly);
        _chkAutoAdd = Ui.Chk("受信から自動追加", s.ParamAutoAdd);
        var add = Ui.Btn("追加…");
        var copyAll = Ui.Btn("全コピー");
        var paste = Ui.Btn("貼り付けて適用");
        var unlockAll = Ui.Btn("🔓 固定を全解除");
        _lblInfo = Ui.Info(230);

        var bar = Ui.TopBar();
        bar.Padding = new Padding(0, 0, 0, 3);
        bar.Controls.AddRange(new Control[] { Ui.L("アバター"), _cmbAvatar, change, reload, open });
        bar.SetFlowBreak(open, true);
        bar.Controls.AddRange(new Control[] { _txtFilter, _chkHideRO, _chkAutoAdd });
        bar.SetFlowBreak(_chkAutoAdd, true);
        bar.Controls.AddRange(new Control[] { add, copyAll, paste, unlockAll, _lblInfo });

        _grid = new ParamGrid { Dock = DockStyle.Fill };
        _host = new RoundPanel { Card = true, Dock = DockStyle.Fill, Padding = new Padding(4, 4, 2, 4) };
        _host.Controls.Add(_grid);

        Controls.Add(_host);
        Controls.Add(bar);

        BuildMenu();

        _tip.SetToolTip(_cmbAvatar, "VRChat が作るアバターの OSC 設定ファイルから選択 (アバター変更時は自動で切替)");
        _tip.SetToolTip(change, "選んでいるアバターに VRChat 側で着替える (/avatar/change)\n自分のアバター・お気に入り等、着られるアバターのみ");
        _tip.SetToolTip(reload, "アバター一覧を読み込み直す");
        _tip.SetToolTip(open, "アバターの OSC 設定ファイル (avtr_*.json) を直接開く");
        _tip.SetToolTip(_chkHideRO, "VRChat 組み込みの読み取り専用パラメータ (Velocity, Gesture 等) を隠す");
        _tip.SetToolTip(_chkAutoAdd, "設定ファイルに無いパラメータでも、受信したものは一覧に追加する");
        _tip.SetToolTip(add, "パラメータを手動で追加");
        _tip.SetToolTip(copyAll, "表示中のパラメータを「名前 [Tab] 型 [Tab] 値」でコピー (状態の保存に)");
        _tip.SetToolTip(paste, "クリップボードの「名前 [Tab] 型 [Tab] 値」または「名前=値」を読み込んで送信 (状態の復元に)");
        _tip.SetToolTip(unlockAll, "固定しているパラメータを全て解除");
        _tip.SetToolTip(_grid, "スイッチ: クリック / 数値: − + か数字をクリック / スライダー: ドラッグ\nダブルクリック: 値を入力 / 右クリック: 固定・コピーなど");

        _cmbAvatar.SelectedIndexChanged += (_, _) =>
        {
            if (!_loadingCombo && _cmbAvatar.SelectedItem is AvatarFile f) LoadAvatarFileAsync(f);
        };
        change.Click += (_, _) => ChangeAvatar();
        reload.Click += (_, _) => ScanAvatarsAsync(_currentAvatarId);
        open.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                InitialDirectory = Directory.Exists(OscFolder) ? OscFolder : "",
                Filter = "アバター OSC 設定 (avtr_*.json)|avtr_*.json|JSON (*.json)|*.json"
            };
            if (Ui.ShowCommon(this, dlg) != DialogResult.OK) return;
            var f = ReadAvatarHeader(dlg.FileName);
            if (f != null) LoadAvatarFileAsync(f);
        };
        _txtFilter.TextChanged += (_, _) => RebuildList();
        _chkHideRO.CheckedChanged += (_, _) => { _s.ParamHideReadOnly = _chkHideRO.Checked; RebuildList(); };
        _chkAutoAdd.CheckedChanged += (_, _) => _s.ParamAutoAdd = _chkAutoAdd.Checked;
        add.Click += (_, _) => AddManual();
        copyAll.Click += (_, _) => CopyAll();
        paste.Click += (_, _) => PasteApply();
        unlockAll.Click += (_, _) => _locker.UnlockAll("ボタン");

        _grid.ValueChanging += (it, v) => SendValue(it, v);
        _grid.EditRequested += it => EditValue(it, false);
        _grid.MenuRequested += (it, p) =>
        {
            _menuItem = it;
            _menu.Show(_grid, p);
        };

        _locker.Changed += () =>
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(new Action(RefreshLocks)); } catch (InvalidOperationException) { }
        };
    }

    public static string OscFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", "VRChat", "VRChat", "OSC");

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        ScanAvatarsAsync(null);
    }

    /// <summary>マクロの対象一覧用 (名前, アドレス, 型)</summary>
    public List<(string Name, string Address, string Type)> ParamList() =>
        _items.Where(r => r.Writable).Select(r => (r.Name, r.Address, r.Type)).ToList();

    // =================================================================
    // 受信 (どのスレッドからでも)
    // =================================================================
    public void Feed(OscMessage m)
    {
        if (m.Args.Length == 0 || m.Args[0] == null) return;
        if (m.Address == "/avatar/change")
        {
            if (m.Args[0] is string id) Interlocked.Exchange(ref _pendingAvatar, id);
            return;
        }
        if (m.Address.StartsWith(ParamPrefix, StringComparison.Ordinal))
            _incoming[m.Address] = m.Args[0];
    }

    /// <summary>UI スレッドから定期的に呼ぶ (値を入れて、見えている行だけ描き直す)</summary>
    public void Tick()
    {
        var id = Interlocked.Exchange(ref _pendingAvatar, null);
        if (id != null && id != _currentAvatarId)
        {
            OpLog.Write($"👤 アバター変更: {id}");
            ScanAvatarsAsync(id);
        }

        if (!_incoming.IsEmpty)
        {
            foreach (var key in _incoming.Keys.ToArray())
            {
                if (!_incoming.TryRemove(key, out var v)) continue;
                _cache[key] = v;
                if (_byAddr.TryGetValue(key, out var it))
                {
                    if (it.Locked) continue; // 固定中は固定値を表示したまま
                    it.Value = v;
                    _grid.InvalidateItem(it);
                }
                else if (_chkAutoAdd.Checked)
                {
                    string name = key.Substring(ParamPrefix.Length);
                    AddItem(name, key, ValueLocker.TypeOf(v) == "String" ? "Float" : ValueLocker.TypeOf(v), !BuiltIn.Contains(name), v);
                    _needRebuild = true;
                }
            }
        }
        if (_needRebuild && Environment.TickCount64 - _lastRebuild > 1000) RebuildList();
    }

    // =================================================================
    // アバター設定ファイル (読み込みは全てバックグラウンド)
    // =================================================================
    private void ScanAvatarsAsync(string selectId)
    {
        int version = Interlocked.Increment(ref _scanVersion);
        _lblInfo.Text = "アバター一覧を読み込み中…";
        Task.Run(() =>
        {
            var list = new List<AvatarFile>();
            try
            {
                if (Directory.Exists(OscFolder))
                {
                    foreach (var path in Directory.EnumerateFiles(OscFolder, "avtr_*.json", SearchOption.AllDirectories))
                    {
                        var f = ReadAvatarHeader(path);
                        if (f != null) list.Add(f);
                    }
                }
            }
            catch { }
            foreach (var f in list) _avatarNames[f.Id] = f.Name;
            return list.OrderByDescending(f => f.Time).ToList();
        }).ContinueWith(t => OnUi(() =>
        {
            if (version == _scanVersion) ApplyScan(t.Result, selectId);
        }));
    }

    private void OnUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private void ApplyScan(List<AvatarFile> list, string selectId)
    {
        _loadingCombo = true;
        try
        {
            _cmbAvatar.BeginUpdate();
            _cmbAvatar.Items.Clear();
            foreach (var f in list) _cmbAvatar.Items.Add(f);
            _cmbAvatar.EndUpdate();
        }
        finally { _loadingCombo = false; }

        var target = selectId != null
            ? list.FirstOrDefault(f => string.Equals(f.Id, selectId, StringComparison.OrdinalIgnoreCase))
            : list.FirstOrDefault();

        if (target != null)
        {
            _loadingCombo = true;
            _cmbAvatar.SelectedItem = target;
            _loadingCombo = false;
            LoadAvatarFileAsync(target);
        }
        else if (selectId != null)
        {
            ClearItems();
            _currentAvatarId = selectId;
            RebuildList();
            _lblInfo.Text = "設定ファイルなし (受信から追加)";
        }
        else
        {
            _lblInfo.Text = list.Count == 0 ? "アバター設定が見つかりません (VRChat で OSC を有効に)" : "";
        }
    }

    /// <summary>ファイルの先頭部分だけ読んで id と name を取る (一覧作成用に軽く)</summary>
    private static AvatarFile ReadAvatarHeader(string path)
    {
        try
        {
            string id = null, name = null;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[Math.Min(fs.Length, 4096)];
                int n = fs.Read(buf, 0, buf.Length);
                int start = n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0; // BOM を飛ばす
                var reader = new Utf8JsonReader(buf.AsSpan(start, n - start), isFinalBlock: false, state: default);
                int depth = 0;
                try
                {
                    while ((id == null || name == null) && reader.Read())
                    {
                        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                        else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
                        else if (reader.TokenType == JsonTokenType.PropertyName && depth == 1)
                        {
                            string prop = reader.GetString();
                            if (!reader.Read()) break;
                            if (reader.TokenType == JsonTokenType.String)
                            {
                                if (prop == "id") id = reader.GetString();
                                else if (prop == "name") name = reader.GetString();
                            }
                            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                            {
                                depth++;
                            }
                        }
                    }
                }
                catch (JsonException) { }
            }
            id ??= Path.GetFileNameWithoutExtension(path);
            return new AvatarFile { Path = path, Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name, Time = File.GetLastWriteTime(path) };
        }
        catch { return null; }
    }

    private void LoadAvatarFileAsync(AvatarFile f)
    {
        int version = Interlocked.Increment(ref _loadVersion);
        _lblInfo.Text = "パラメータを読み込み中…";
        Task.Run(() =>
        {
            var list = new List<(string Name, string Addr, string Type, bool Writable)>();
            using var doc = JsonDocument.Parse(File.ReadAllText(f.Path)); // ReadAllText は BOM を除去する
            if (doc.RootElement.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in ps.EnumerateArray())
                {
                    if (!p.TryGetProperty("name", out var nameEl)) continue;
                    string name = nameEl.GetString() ?? "";
                    bool hasIn = p.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object;
                    bool hasOut = p.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object;
                    if (!hasIn && !hasOut) continue;
                    var src = hasIn ? input : output;
                    string addr = src.TryGetProperty("address", out var a) ? a.GetString() : ParamPrefix + name;
                    string type = src.TryGetProperty("type", out var t) ? t.GetString() : "Float";
                    if (!string.IsNullOrEmpty(addr)) list.Add((name, addr, type, hasIn));
                }
            }
            return list;
        }).ContinueWith(t => OnUi(() =>
        {
            if (version != _loadVersion) return; // 途中で別のアバターが選ばれた
            if (t.IsFaulted)
            {
                _lblInfo.Text = "読込エラー: " + t.Exception?.GetBaseException().Message;
                return;
            }
            ClearItems();
            foreach (var (name, addr, type, writable) in t.Result)
            {
                if (_byAddr.ContainsKey(addr)) continue;
                AddItem(name, addr, type, writable, _cache.TryGetValue(addr, out var v) ? v : null);
            }
            _currentAvatarId = f.Id;
            RebuildList();
            OpLog.Write($"👤 パラメータ読込: {f.Name}  ({_items.Count} 件)");
        }));
    }

    private void ChangeAvatar()
    {
        if (_cmbAvatar.SelectedItem is not AvatarFile f) { Ui.Msg(this, "アバターを選んでください"); return; }
        if (!_osc.IsConnected) { Ui.Msg(this, "接続してから実行してください"); return; }
        if (_osc.Send("/avatar/change", f.Id)) OpLog.Write($"👤 アバターを変更: {f.Name} ({f.Id})");
        else Ui.Msg(this, "送信できませんでした (出力 OFF の可能性があります)");
    }

    // =================================================================
    // 項目
    // =================================================================
    private void ClearItems()
    {
        _items.Clear();
        _byAddr.Clear();
        _grid.SetItems(new List<ParamItem>());
    }

    private ParamItem AddItem(string name, string addr, string type, bool writable, object value)
    {
        var it = new ParamItem
        {
            Name = name, Address = addr, Type = Types.Contains(type) ? type : "Float", Writable = writable, Value = value
        };
        if (_locker.TryGet(addr, out var lv)) { it.Locked = true; it.Value = lv; }
        _items.Add(it);
        _byAddr[addr] = it;
        return it;
    }

    private void RebuildList()
    {
        _needRebuild = false;
        _lastRebuild = Environment.TickCount64;
        string filter = _txtFilter.Text.Trim();
        var shown = _items.Where(r =>
            !(_chkHideRO.Checked && !r.Writable) &&
            (filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
        _grid.SetItems(shown);
        int writable = _items.Count(r => r.Writable);
        _lblInfo.Text = $"表示 {shown.Count} / 全 {_items.Count} 件 (操作可 {writable})";
    }

    private void RefreshLocks()
    {
        foreach (var it in _items)
        {
            bool locked = _locker.TryGet(it.Address, out var lv);
            if (locked) it.Value = lv;
            else if (it.Locked && _cache.TryGetValue(it.Address, out var cv)) it.Value = cv;
            it.Locked = locked;
        }
        _grid.Invalidate();
    }

    private void SendValue(ParamItem it, object value)
    {
        if (!it.Writable) return;
        // 固定中なら固定値ごと変更する
        if (_locker.IsLocked(it.Address)) _locker.Lock(it.Address, value, log: false);
        else _osc.Send(it.Address, value);
        it.Value = value;
        _cache[it.Address] = value;
        _grid.InvalidateItem(it);
    }

    private static string Raw(object v) => ValueLocker.RawText(v);

    /// <summary>今の値の文字 (固定値 → 受信値 → 画面の値)</summary>
    private static string ValueText(ParamItem it) => it.Value == null ? "" : Raw(it.Value);

    private static string DisplayValueText(ParamItem it) => it.Value != null ? Raw(it.Value) : it.Type switch
    {
        "Bool" => "false",
        "Int" => "0",
        _ => "0"
    };

    // =================================================================
    // 編集・コピー
    // =================================================================
    private void BuildMenu()
    {
        var edit = new ToolStripMenuItem("値を編集…");
        var editLock = new ToolStripMenuItem("値を編集して固定…");
        var lockToggle = new ToolStripMenuItem("今の値で固定");
        var copyName = new ToolStripMenuItem("名前をコピー");
        var copyAddr = new ToolStripMenuItem("アドレスをコピー");
        var copyValue = new ToolStripMenuItem("値をコピー");
        var copyLine = new ToolStripMenuItem("名前・型・値をコピー");
        var toCustom = new ToolStripMenuItem("カスタムタブへ送る");
        var toPreset = new ToolStripMenuItem("カスタムのプリセットに追加");
        var toMacro = new ToolStripMenuItem("マクロに「値を送信」として追加");
        var remove = new ToolStripMenuItem("一覧から外す");
        _menu.Items.AddRange(new ToolStripItem[]
        {
            edit, editLock, lockToggle, new ToolStripSeparator(), copyName, copyAddr, copyValue, copyLine,
            new ToolStripSeparator(), toCustom, toPreset, toMacro, new ToolStripSeparator(), remove
        });

        _menu.Opening += (_, e) =>
        {
            if (_menuItem == null) { e.Cancel = true; return; }
            edit.Enabled = editLock.Enabled = _menuItem.Writable;
            bool locked = _locker.IsLocked(_menuItem.Address);
            lockToggle.Text = locked ? "🔓 固定を解除" : "🔒 今の値で固定";
            lockToggle.Enabled = locked || _menuItem.Writable;
            ThemedMenu.Apply(_menu);
        };
        edit.Click += (_, _) => EditValue(_menuItem, false);
        editLock.Click += (_, _) => EditValue(_menuItem, true);
        lockToggle.Click += (_, _) =>
        {
            var it = _menuItem;
            if (it == null) return;
            if (_locker.IsLocked(it.Address)) _locker.Unlock(it.Address);
            else if (OscValue.TryParse(it.Type, DisplayValueText(it), out var v)) _locker.Lock(it.Address, v);
        };
        copyName.Click += (_, _) => Copy(_menuItem?.Name);
        copyAddr.Click += (_, _) => Copy(_menuItem?.Address);
        copyValue.Click += (_, _) => { if (_menuItem != null) Copy(DisplayValueText(_menuItem)); };
        copyLine.Click += (_, _) => { if (_menuItem != null) Copy(LineOf(_menuItem)); };
        toCustom.Click += (_, _) =>
        {
            if (_menuItem != null) SendToCustom?.Invoke(_menuItem.Address, _menuItem.Type, DisplayValueText(_menuItem));
        };
        toPreset.Click += (_, _) =>
        {
            if (_menuItem != null) AddToCustomPreset?.Invoke(_menuItem.Name, _menuItem.Address, _menuItem.Type, DisplayValueText(_menuItem));
        };
        toMacro.Click += (_, _) =>
        {
            if (_menuItem != null) AddToMacro?.Invoke(_menuItem.Address, _menuItem.Type, DisplayValueText(_menuItem));
        };
        remove.Click += (_, _) =>
        {
            var it = _menuItem;
            if (it == null) return;
            _items.Remove(it);
            _byAddr.Remove(it.Address);
            RebuildList();
        };
    }

    private static string LineOf(ParamItem it) => $"{it.Name}\t{it.Type}\t{DisplayValueText(it)}";

    private static void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { }
    }

    private void EditValue(ParamItem it, bool lockIt)
    {
        if (it == null || !it.Writable) return;
        string cur = ValueText(it);
        string shown = DisplayValueText(it);
        string original = cur.Length > 0
            ? cur + (it.Locked ? "  (固定中)" : "")
            : shown + "  (まだ受信していないので初期値)";
        var res = InputDialog.Ask(FindForm(), Font, lockIt ? "値を編集して固定" : "値を編集", $"{it.Name}  ({it.Type})",
            shown, original: original);
        if (res == null) return;
        if (!OscValue.TryParse(it.Type, res.Value.Text, out var v))
        {
            Ui.Msg(this, $"「{res.Value.Text}」を {it.Type} に変換できません", "パラメータ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (v is float f && it.Type == "Float") v = Math.Clamp(f, -1f, 1f);
        if (lockIt) _locker.Lock(it.Address, v);
        SendValue(it, v);
        if (!lockIt) OpLog.Write($"🎛 {it.Name} = {Raw(v)}");
    }

    private void AddManual()
    {
        var res = InputDialog.Ask(FindForm(), Font, "パラメータを追加", "パラメータ名 (または /avatar/parameters/... のアドレス)", "",
            Types, "Bool");
        if (res == null || string.IsNullOrWhiteSpace(res.Value.Text)) return;
        string text = res.Value.Text.Trim();
        string addr = text.StartsWith("/") ? text : ParamPrefix + text;
        string name = addr.StartsWith(ParamPrefix) ? addr.Substring(ParamPrefix.Length) : addr;
        if (_byAddr.ContainsKey(addr)) { Ui.Msg(this, "既に一覧にあります", "パラメータ"); return; }
        AddItem(name, addr, res.Value.Choice ?? "Bool", true, _cache.TryGetValue(addr, out var v) ? v : null);
        RebuildList();
    }

    private void CopyAll()
    {
        var sb = new StringBuilder();
        var shown = _items.Where(r => !(_chkHideRO.Checked && !r.Writable)).ToList();
        string filter = _txtFilter.Text.Trim();
        if (filter.Length > 0) shown = shown.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in shown) sb.AppendLine(LineOf(r));
        Copy(sb.ToString());
        OpLog.Write($"📋 パラメータを {shown.Count} 件コピー");
    }

    private void PasteApply()
    {
        string text;
        try { text = Clipboard.GetText(); } catch { return; }
        if (string.IsNullOrWhiteSpace(text)) return;

        int sent = 0, skipped = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            if (line.Length == 0) continue;
            string name, value;
            var tab = line.Split('\t');
            if (tab.Length >= 3) { name = tab[0]; value = tab[2]; }
            else if (tab.Length == 2) { name = tab[0]; value = tab[1]; }
            else if (line.Contains('=')) { name = line[..line.IndexOf('=')].Trim(); value = line[(line.IndexOf('=') + 1)..].Trim(); }
            else { skipped++; continue; }

            string addr = name.StartsWith("/") ? name : ParamPrefix + name.Trim();
            if (!_byAddr.TryGetValue(addr, out var it) || !it.Writable || !OscValue.TryParse(it.Type, value, out var v))
            {
                skipped++;
                continue;
            }
            SendValue(it, v);
            sent++;
        }
        OpLog.Write($"📋 貼り付けて適用: {sent} 件送信" + (skipped > 0 ? $" ({skipped} 件スキップ)" : ""));
        _lblInfo.Text = $"{sent} 件適用" + (skipped > 0 ? $" / {skipped} 件スキップ" : "");
    }

    public void OnThemeChanged() => _grid.Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}
