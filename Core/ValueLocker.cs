using System.Collections.Concurrent;
using VRCOscDebugClient.Osc;

namespace VRCOscDebugClient.Core;

/// <summary>
/// 値の固定化。固定したアドレスは、
/// ・VRChat から違う値が届いたら、すぐに固定値を送り直す (同じアドレスは 0.1 秒に 1 回まで)
/// ・1 秒ごとに固定値を送り直す (アバター変更などで戻されても復帰する)
/// 全停止すると全て解除される。
/// </summary>
public sealed class ValueLocker
{
    private readonly OscClient _osc;
    private readonly ConcurrentDictionary<string, object> _locks = new();
    private readonly ConcurrentDictionary<string, long> _lastSend = new();
    private long _nextRefresh;

    /// <summary>固定の追加・変更・解除 (呼び出し元のスレッドで発生)</summary>
    public event Action Changed;

    public ValueLocker(OscClient osc) => _osc = osc;

    public int Count => _locks.Count;
    public bool IsLocked(string address) => address != null && _locks.ContainsKey(address);
    public bool TryGet(string address, out object value) => _locks.TryGetValue(address, out value);
    public KeyValuePair<string, object>[] Snapshot() => _locks.ToArray();

    /// <summary>固定 (既に固定中なら値を変更)</summary>
    public void Lock(string address, object value, bool log = true)
    {
        if (string.IsNullOrWhiteSpace(address) || value == null) return;
        bool was = _locks.TryGetValue(address, out var old);
        _locks[address] = value;
        _lastSend[address] = Environment.TickCount64;
        _osc.Send(address, value);
        if (log) OpLog.Write($"🔒 固定{(was ? "値を変更" : "")}: {Short(address)} = {OscMessage.FormatArg(value)}");
        if (!was || !Same(old, value)) Changed?.Invoke();
    }

    public void Unlock(string address)
    {
        if (address == null || !_locks.TryRemove(address, out _)) return;
        _lastSend.TryRemove(address, out _);
        OpLog.Write($"🔓 固定解除: {Short(address)}");
        Changed?.Invoke();
    }

    public void UnlockAll(string by)
    {
        if (_locks.IsEmpty) return;
        int n = _locks.Count;
        _locks.Clear();
        _lastSend.Clear();
        OpLog.Write($"🔓 固定を全て解除 ({n} 件, {by})");
        Changed?.Invoke();
    }

    /// <summary>受信スレッドから: 固定と違う値が届いたら送り直す</summary>
    public void OnReceived(OscMessage m)
    {
        if (_locks.IsEmpty || m.Args.Length == 0) return;
        if (!_locks.TryGetValue(m.Address, out var v) || Same(m.Args[0], v)) return;
        long now = Environment.TickCount64;
        if (_lastSend.TryGetValue(m.Address, out var t) && now - t < 100) return;
        _lastSend[m.Address] = now;
        _osc.Send(m.Address, v);
    }

    /// <summary>定期的に呼ぶ: 1 秒ごとに全ての固定値を送り直す</summary>
    public void Tick()
    {
        if (_locks.IsEmpty || !_osc.IsConnected) return;
        long now = Environment.TickCount64;
        if (now < _nextRefresh) return;
        _nextRefresh = now + 1000;
        foreach (var kv in _locks) _osc.Send(kv.Key, kv.Value);
    }

    private static bool Same(object a, object b) => a switch
    {
        float fa when b is float fb => Math.Abs(fa - fb) < 0.002f,
        _ => Equals(a, b)
    };

    public static string Short(string address) =>
        address != null && address.StartsWith("/avatar/parameters/") ? address.Substring("/avatar/parameters/".Length) : address;

    /// <summary>値の文字から型を推定 (true/false → Bool, 整数 → Int, 小数 → Float, それ以外 → String)。空なら null</summary>
    public static string InferType(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t.Equals("false", StringComparison.OrdinalIgnoreCase)) return "Bool";
        if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)) return "Int";
        if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return "Float";
        return "String";
    }

    /// <summary>受信した値の型名</summary>
    public static string TypeOf(object v) => v switch
    {
        bool => "Bool",
        int => "Int",
        float => "Float",
        _ => "String"
    };

    /// <summary>値を編集欄に出す文字 (true/false・小数は . 区切り)</summary>
    public static string RawText(object v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)
    };
}
