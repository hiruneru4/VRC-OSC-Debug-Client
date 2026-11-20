using VRCOscDebugClient.Osc;

namespace VRCOscDebugClient.Core;

/// <summary>
/// 複数の操作 (行・マクロ・カスタム) が同じ入力を同時に押した時の調停役。
/// 押している持ち主 (owner) を数え、最後の1つが離した時だけ「離す」を送る。
/// 例: マクロA がダッシュを押しっぱなし中に、マクロB がダッシュを押して離しても、A の分は押されたまま。
/// 軸は最後に押した持ち主の値が有効。その持ち主が離すと、残っている持ち主の値に戻る。
/// </summary>
public sealed class InputArbiter
{
    private sealed class Slot
    {
        public object Off;
        public readonly List<(object Owner, object Value)> Holders = new();
    }

    private readonly OscClient _osc;
    private readonly object _lock = new();
    private readonly Dictionary<string, Slot> _slots = new();

    public InputArbiter(OscClient osc) => _osc = osc;

    public void Press(object owner, string address, object on, object off)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(address, out var s)) _slots[address] = s = new Slot();
            s.Off = off;
            s.Holders.Add((owner, on));
        }
        _osc.Send(address, on);
    }

    public void Release(object owner, string address)
    {
        object send = null;
        bool force = false;
        lock (_lock)
        {
            if (!_slots.TryGetValue(address, out var s)) return;
            int idx = s.Holders.FindLastIndex(h => ReferenceEquals(h.Owner, owner));
            if (idx < 0) return;
            var removed = s.Holders[idx].Value;
            s.Holders.RemoveAt(idx);
            if (s.Holders.Count == 0)
            {
                send = s.Off;
                force = true;
                _slots.Remove(address);
            }
            else
            {
                var top = s.Holders[^1].Value;
                if (!Equals(top, removed)) send = top; // 軸: 残っている持ち主の値に戻す
            }
        }
        if (send == null) return;
        if (force) _osc.SendForce(address, send); // 離すは必ず届ける
        else _osc.Send(address, send);
    }

    /// <summary>この持ち主が押しているものを全て離す</summary>
    public void ReleaseOwner(object owner)
    {
        var sends = new List<(string Address, object Value, bool Force)>();
        lock (_lock)
        {
            foreach (var kv in _slots.ToList())
            {
                var s = kv.Value;
                if (s.Holders.RemoveAll(h => ReferenceEquals(h.Owner, owner)) == 0) continue;
                if (s.Holders.Count == 0)
                {
                    sends.Add((kv.Key, s.Off, true));
                    _slots.Remove(kv.Key);
                }
                else sends.Add((kv.Key, s.Holders[^1].Value, false));
            }
        }
        foreach (var (addr, v, force) in sends)
        {
            if (force) _osc.SendForce(addr, v); else _osc.Send(addr, v);
        }
    }

    public int CountOwner(object owner)
    {
        lock (_lock) return _slots.Values.Count(s => s.Holders.Any(h => ReferenceEquals(h.Owner, owner)));
    }

    /// <summary>全停止用: 記録を全て消す (0 の送信は呼び出し側で行う)</summary>
    public void Clear()
    {
        lock (_lock) _slots.Clear();
    }
}
