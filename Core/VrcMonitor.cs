using VRCOscDebugClient.Osc;

namespace VRCOscDebugClient.Core;

/// <summary>VRC 動作履歴の種類</summary>
public enum HistKind
{
    Move, Jump, Gesture, Voice, Mute, Pose, Afk, Mode, Avatar, Param, Camera, World, Player, Video, Error
}

public static class HistKinds
{
    public static readonly (HistKind Kind, string Name)[] All =
    {
        (HistKind.Move, "移動 (歩き・走り・停止)"),
        (HistKind.Jump, "ジャンプ・着地・落下"),
        (HistKind.Gesture, "ジェスチャー"),
        (HistKind.Voice, "声 (話し始め・終わり)"),
        (HistKind.Mute, "ミュート"),
        (HistKind.Pose, "状態 (座る・ステーション)"),
        (HistKind.Afk, "AFK"),
        (HistKind.Mode, "VR / デスクトップ・トラッキング"),
        (HistKind.Avatar, "アバター変更"),
        (HistKind.Param, "アバターの切り替え (表情・衣装など)"),
        (HistKind.Camera, "カメラ"),
        (HistKind.World, "ワールド移動"),
        (HistKind.Player, "プレイヤーの参加・退出"),
        (HistKind.Video, "動画プレイヤー"),
        (HistKind.Error, "エラー"),
    };
}

/// <summary>現在の状態 (右上の状態表示用)。-1 = 不明</summary>
public sealed class VrcSnapshot
{
    public int Afk = -1, Mute = -1, Seated = -1, InStation = -1, Grounded = -1, Vr = -1, Tracking = -1, GestureL = -1, GestureR = -1;
    public float Speed, VelX, VelY, VelZ;
    public bool Talking;
    public string Motion = "?";
    public DateTime AfkSince;
}

/// <summary>
/// VRChat から受信した OSC (アバターの状態・カメラ) と VRChat のログから、
/// 「歩き始めた」「ジャンプ」「右手: ピース」「話し始めた」「○○ さんが参加」などの動作履歴を作る。
/// ※ VRChat は押したキーそのものは送ってこないので、アバターの状態から推定している。
/// </summary>
public sealed class VrcMonitor
{
    private const string P = "/avatar/parameters/";

    // VRChat 組み込みパラメータ (アバター独自の切り替えと区別する)
    public static readonly HashSet<string> BuiltIn = new(StringComparer.Ordinal)
    {
        "IsLocal", "PreviewMode", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight",
        "GestureRightWeight", "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright",
        "Grounded", "Seated", "AFK", "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs",
        "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled", "ScaleModified", "ScaleFactor",
        "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent"
    };

    public static readonly string[] GestureNames = { "ニュートラル", "グー", "パー", "指差し", "ピース", "ロックンロール", "ハンドガン", "サムズアップ" };
    public static readonly string[] GestureIcons = { "✋", "✊", "🖐", "☝", "✌", "🤘", "👉", "👍" };

    /// <summary>TrackingType の目安 (VRChat の値)</summary>
    public static string TrackingName(int t) => t switch
    {
        0 => "初期化中",
        1 => "汎用",
        2 => "手のみ",
        3 => "頭と手 (3点)",
        4 => "頭・手・腰 (4点)",
        5 => "フルトラ",
        6 => "フルトラ (多点)",
        _ => t < 0 ? "?" : $"種類 {t}"
    };

    public static string GestureText(int g) =>
        g >= 0 && g < GestureNames.Length ? GestureNames[g] : "?";

    private readonly object _lock = new();
    private readonly VrcSnapshot _s = new();
    private float _voice;
    private long _talkStart, _quietSince, _airSince, _pendingSince, _lastError;
    private bool _jumpPending, _fallEmitted;
    private string _pendingMotion;
    private int _errorsSuppressed;
    private readonly Dictionary<string, object> _paramLast = new();
    private readonly Dictionary<string, long> _paramTime = new();
    private readonly Dictionary<string, object> _camLast = new();
    private readonly Dictionary<string, (object Value, long Time)> _camPending = new();

    /// <summary>アバター ID → 名前 (分かれば)</summary>
    public Func<string, string> AvatarName;

    /// <summary>動作履歴 (種類, 文) — どのスレッドからでも発生</summary>
    public event Action<HistKind, string> Event;

    private void Emit(HistKind k, string text) => Event?.Invoke(k, L10n.Auto(text));
    private static long Now => Environment.TickCount64;

    public VrcSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new VrcSnapshot
            {
                Afk = _s.Afk, Mute = _s.Mute, Seated = _s.Seated, InStation = _s.InStation, Grounded = _s.Grounded,
                Vr = _s.Vr, Tracking = _s.Tracking, GestureL = _s.GestureL, GestureR = _s.GestureR,
                Speed = _s.Speed, VelX = _s.VelX, VelY = _s.VelY, VelZ = _s.VelZ, Talking = _s.Talking,
                Motion = _s.Motion, AfkSince = _s.AfkSince
            };
        }
    }

    private static int I(object v) => v switch { bool b => b ? 1 : 0, int i => i, float f => (int)Math.Round(f), _ => -1 };
    private static float F(object v) => v switch { bool b => b ? 1f : 0f, int i => i, float f => f, _ => 0f };

    /// <summary>受信スレッドから: OSC を状態と履歴に反映</summary>
    public void Feed(OscMessage m)
    {
        if (m.Args.Length == 0 || m.Args[0] == null) return;
        var a = m.Args[0];
        string addr = m.Address;

        if (addr == "/avatar/change")
        {
            string id = a as string ?? "";
            string name = AvatarName?.Invoke(id);
            Emit(HistKind.Avatar, $"👤 アバター変更: {(string.IsNullOrEmpty(name) ? id : $"{name} ({id})")}");
            return;
        }
        if (addr.StartsWith(CameraCatalog.Cam, StringComparison.Ordinal) || addr.StartsWith("/dolly/", StringComparison.Ordinal))
        {
            FeedCamera(addr, a);
            return;
        }
        if (!addr.StartsWith(P, StringComparison.Ordinal)) return;
        string pname = addr.Substring(P.Length);

        lock (_lock)
        {
            switch (pname)
            {
                case "AFK":
                {
                    int v = I(a);
                    if (v == _s.Afk) break;
                    if (v == 1)
                    {
                        _s.AfkSince = DateTime.Now;
                        Emit(HistKind.Afk, "💤 AFK になった");
                    }
                    else if (_s.Afk == 1)
                    {
                        var dur = DateTime.Now - _s.AfkSince;
                        Emit(HistKind.Afk, $"👀 AFK から戻った ({(int)dur.TotalMinutes}分{dur.Seconds}秒)");
                    }
                    _s.Afk = v;
                    break;
                }
                case "MuteSelf":
                {
                    int v = I(a);
                    if (v != _s.Mute && _s.Mute != -1) Emit(HistKind.Mute, v == 1 ? "🔇 ミュート ON" : "🎙 ミュート OFF (マイク ON)");
                    _s.Mute = v;
                    break;
                }
                case "Seated":
                {
                    int v = I(a);
                    if (v != _s.Seated && _s.Seated != -1) Emit(HistKind.Pose, v == 1 ? "🪑 座った" : "🧍 立ち上がった");
                    _s.Seated = v;
                    break;
                }
                case "InStation":
                {
                    int v = I(a);
                    if (v != _s.InStation && _s.InStation != -1) Emit(HistKind.Pose, v == 1 ? "🚗 ステーション (椅子・乗り物) に入った" : "🚪 ステーションから出た");
                    _s.InStation = v;
                    break;
                }
                case "Grounded":
                {
                    int v = I(a);
                    if (v == _s.Grounded) break;
                    if (v == 0 && _s.Grounded == 1)
                    {
                        _airSince = Now;
                        _fallEmitted = false;
                        if (_s.VelY > 0.5f) Emit(HistKind.Jump, "🦘 ジャンプ");
                        else _jumpPending = true; // 上向きの速度が後から届くことがある
                    }
                    else if (v == 1 && _s.Grounded == 0)
                    {
                        _jumpPending = false;
                        long air = Now - _airSince;
                        if (air > 250) Emit(HistKind.Jump, $"🛬 着地 (滞空 {air / 1000.0:0.0} 秒)");
                    }
                    _s.Grounded = v;
                    break;
                }
                case "VelocityY":
                    _s.VelY = F(a);
                    if (_jumpPending && _s.VelY > 0.5f)
                    {
                        _jumpPending = false;
                        Emit(HistKind.Jump, "🦘 ジャンプ");
                    }
                    if (_s.Grounded == 0 && _s.VelY < -4f && !_fallEmitted)
                    {
                        _fallEmitted = true;
                        Emit(HistKind.Jump, "⬇ 落下中");
                    }
                    break;
                case "VelocityX": _s.VelX = F(a); _s.Speed = MathF.Sqrt(_s.VelX * _s.VelX + _s.VelZ * _s.VelZ); break;
                case "VelocityZ": _s.VelZ = F(a); _s.Speed = MathF.Sqrt(_s.VelX * _s.VelX + _s.VelZ * _s.VelZ); break;
                case "GestureLeft":
                {
                    int v = I(a);
                    if (v != _s.GestureL && _s.GestureL != -1) Emit(HistKind.Gesture, $"左手: {GestureText(v)}");
                    _s.GestureL = v;
                    break;
                }
                case "GestureRight":
                {
                    int v = I(a);
                    if (v != _s.GestureR && _s.GestureR != -1) Emit(HistKind.Gesture, $"右手: {GestureText(v)}");
                    _s.GestureR = v;
                    break;
                }
                case "Voice":
                    _voice = F(a);
                    if (_voice > 0.05f)
                    {
                        if (!_s.Talking)
                        {
                            _s.Talking = true;
                            _talkStart = Now;
                            Emit(HistKind.Voice, "🎤 話し始めた");
                        }
                        _quietSince = 0;
                    }
                    else if (_s.Talking && _quietSince == 0) _quietSince = Now;
                    break;
                case "VRMode":
                {
                    int v = I(a);
                    if (v != _s.Vr) Emit(HistKind.Mode, v == 1 ? "🥽 VR モード" : "🖥 デスクトップ モード");
                    _s.Vr = v;
                    break;
                }
                case "TrackingType":
                {
                    int v = I(a);
                    if (v != _s.Tracking) Emit(HistKind.Mode, $"📡 トラッキング: {TrackingName(v)}");
                    _s.Tracking = v;
                    break;
                }
                default:
                    if (BuiltIn.Contains(pname) || a is float) break; // 細かく変わる小数は履歴にしない
                    long now = Now;
                    bool had = _paramLast.TryGetValue(pname, out var old);
                    _paramLast[pname] = a;
                    if (!had || Equals(old, a)) break; // 初回 (アバター読み込み時) は出さない
                    if (_paramTime.TryGetValue(pname, out var t) && now - t < 250) break;
                    _paramTime[pname] = now;
                    Emit(HistKind.Param, a is bool b ? $"🎛 {pname}: {(b ? "ON" : "OFF")}" : $"🎛 {pname} = {a}");
                    break;
            }
        }
    }

    private void FeedCamera(string addr, object a)
    {
        lock (_lock)
        {
            if (addr == CameraCatalog.Pose) return;
            if (addr == CameraCatalog.Mode)
            {
                int mode = I(a);
                if (_camLast.TryGetValue(addr, out var o) && I(o) == mode) return;
                _camLast[addr] = a;
                string name = mode >= 0 && mode < CameraCatalog.Modes.Length ? CameraCatalog.Modes[mode] : mode.ToString();
                Emit(HistKind.Camera, mode == 0 ? "📷 カメラを閉じた" : $"📷 カメラ: {name} モード");
                return;
            }
            if (addr == CameraCatalog.DollyPlay)
            {
                Emit(HistKind.Camera, I(a) == 1 ? "🎞 ドリー再生" : "🎞 ドリー停止");
                return;
            }
            if (addr == CameraCatalog.DollyExport && a is string path)
            {
                Emit(HistKind.Camera, "📤 ドリー経路を書き出し: " + path);
                return;
            }
            string id = addr.Substring(addr.LastIndexOf('/') + 1);
            var toggle = CameraCatalog.Toggles.FirstOrDefault(t => t.Id == id);
            if (toggle != null)
            {
                bool had = _camLast.TryGetValue(addr, out var o);
                _camLast[addr] = a;
                if (had && I(o) != I(a)) Emit(HistKind.Camera, $"📷 {toggle.Name}: {(I(a) == 1 ? "ON" : "OFF")}");
                return;
            }
            // スライダーは動かし終わってから 1 回だけ出す (Tick で確定)
            if (_camLast.ContainsKey(addr)) _camPending[addr] = (a, Now);
            else _camLast[addr] = a;
        }
    }

    /// <summary>UI スレッドから定期的に呼ぶ: 時間で決まる履歴を確定する</summary>
    public void Tick()
    {
        lock (_lock)
        {
            long now = Now;

            // 話し終わり (0.6 秒静か)
            if (_s.Talking && _quietSince > 0 && now - _quietSince > 600)
            {
                _s.Talking = false;
                Emit(HistKind.Voice, $"🤐 話し終えた ({(_quietSince - _talkStart) / 1000.0:0.0} 秒)");
                _quietSince = 0;
            }

            // ジャンプ判定の保留 (上向きの速度が来なければ落下扱い)
            if (_jumpPending && now - _airSince > 300)
            {
                _jumpPending = false;
                if (_s.VelY < -1f && !_fallEmitted) { _fallEmitted = true; Emit(HistKind.Jump, "⬇ 落下"); }
            }

            // 移動 (0.35 秒続いたら確定)
            string state = _s.Speed < 0.3f ? "停止" : _s.Speed < 3.0f ? "歩き" : "走り";
            if (state == _s.Motion || (_s.Motion == "?" && state == "停止"))
            {
                if (_s.Motion == "?") _s.Motion = state;
                _pendingMotion = null;
            }
            else if (_pendingMotion != state)
            {
                _pendingMotion = state;
                _pendingSince = now;
            }
            else if (now - _pendingSince > 350)
            {
                _s.Motion = state;
                _pendingMotion = null;
                Emit(HistKind.Move, state switch
                {
                    "歩き" => $"🚶 歩き始めた ({_s.Speed:0.0} m/s {Direction(_s.VelX, _s.VelZ)})",
                    "走り" => $"🏃 走り始めた ({_s.Speed:0.0} m/s {Direction(_s.VelX, _s.VelZ)})",
                    _ => "🧍 止まった"
                });
            }

            // カメラのスライダー (0.7 秒動かなければ確定)
            foreach (var kv in _camPending.ToArray())
            {
                if (now - kv.Value.Time < 700) continue;
                _camPending.Remove(kv.Key);
                if (_camLast.TryGetValue(kv.Key, out var old) && Math.Abs(F(old) - F(kv.Value.Value)) < 0.001f) continue;
                _camLast[kv.Key] = kv.Value.Value;
                string id = kv.Key.Substring(kv.Key.LastIndexOf('/') + 1);
                var sl = CameraCatalog.Sliders.FirstOrDefault(x => x.Id == id);
                Emit(HistKind.Camera, $"📷 {(sl != null ? sl.Name : id)}: {F(kv.Value.Value):0.##}");
            }

            // まとめたエラーの件数
            if (_errorsSuppressed > 0 && now - _lastError > 3000)
            {
                Emit(HistKind.Error, $"⚠ ほかにエラー {_errorsSuppressed} 件");
                _errorsSuppressed = 0;
                _lastError = now;
            }
        }
    }

    /// <summary>移動の向き (アバター基準: Z = 前後, X = 左右)</summary>
    public static string Direction(float x, float z)
    {
        if (MathF.Sqrt(x * x + z * z) < 0.3f) return "";
        double deg = Math.Atan2(x, z) * 180 / Math.PI; // 0 = 前
        string[] dirs = { "前", "右前", "右", "右後", "後", "左後", "左", "左前" };
        int idx = (int)Math.Round(((deg % 360) + 360) % 360 / 45.0) % 8;
        return dirs[idx];
    }

    // =================================================================
    // VRChat のログ (output_log) から
    // =================================================================
    public void FeedLogLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            int i;
            if ((i = line.IndexOf("[Behaviour] Entering Room: ", StringComparison.Ordinal)) >= 0)
                Emit(HistKind.World, "🌍 ワールド移動: " + line.Substring(i + 27).Trim());
            else if ((i = line.IndexOf("[Behaviour] Joining wrld_", StringComparison.Ordinal)) >= 0)
                Emit(HistKind.World, "🚪 インスタンスに参加: " + Shorten(line.Substring(i + 20).Trim(), 60));
            else if ((i = line.IndexOf("[Behaviour] OnPlayerJoined ", StringComparison.Ordinal)) >= 0)
                Emit(HistKind.Player, $"👋 {PlayerName(line.Substring(i + 27))} さんが参加");
            else if ((i = line.IndexOf("[Behaviour] OnPlayerLeft ", StringComparison.Ordinal)) >= 0)
                Emit(HistKind.Player, $"🚶 {PlayerName(line.Substring(i + 25))} さんが退出");
            else if ((i = line.IndexOf("[Video Playback] Attempting to resolve URL '", StringComparison.Ordinal)) >= 0)
            {
                string url = line.Substring(i + 44);
                int q = url.IndexOf('\'');
                Emit(HistKind.Video, "🎬 動画: " + (q > 0 ? url.Substring(0, q) : url));
            }
            else if (IsErrorLine(line))
            {
                lock (_lock)
                {
                    long now = Now;
                    if (now - _lastError < 3000) { _errorsSuppressed++; continue; } // 連続するエラーはまとめる
                    _lastError = now;
                }
                int dash = line.IndexOf(" -  ", StringComparison.Ordinal);
                Emit(HistKind.Error, "⚠ エラー: " + Shorten(dash > 0 ? line.Substring(dash + 4).Trim() : line, 120));
            }
        }
    }

    private static bool IsErrorLine(string line)
    {
        int dash = line.IndexOf(" -  ", StringComparison.Ordinal);
        return dash > 19 && line.IndexOf(" Error", 19, dash - 19, StringComparison.Ordinal) >= 0;
    }

    private static string PlayerName(string s)
    {
        s = s.Trim();
        int p = s.LastIndexOf(" (usr_", StringComparison.Ordinal);
        return p > 0 ? s.Substring(0, p) : s;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
