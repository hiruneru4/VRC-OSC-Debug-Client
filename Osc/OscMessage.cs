using System.Buffers.Binary;
using System.Text;

namespace VRCOscDebugClient.Osc;

/// <summary>最小限の OSC 1.0 メッセージ (i / f / s / T / F 対応、バンドル受信対応)</summary>
public sealed class OscMessage
{
    public string Address { get; }
    public object[] Args { get; }

    public OscMessage(string address, params object[] args)
    {
        Address = address;
        Args = args ?? Array.Empty<object>();
    }

    // ---------- エンコード ----------
    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        WritePaddedString(ms, Address);

        var tags = new StringBuilder(",");
        foreach (var a in Args)
        {
            switch (a)
            {
                case int _: tags.Append('i'); break;
                case float _: tags.Append('f'); break;
                case double _: tags.Append('f'); break;
                case string _: tags.Append('s'); break;
                case bool b: tags.Append(b ? 'T' : 'F'); break;
                default: throw new NotSupportedException($"未対応の型: {a?.GetType().Name ?? "null"}");
            }
        }
        WritePaddedString(ms, tags.ToString());

        var buf = new byte[4];
        foreach (var a in Args)
        {
            switch (a)
            {
                case int i:
                    BinaryPrimitives.WriteInt32BigEndian(buf, i);
                    ms.Write(buf, 0, 4);
                    break;
                case float f:
                    BinaryPrimitives.WriteSingleBigEndian(buf, f);
                    ms.Write(buf, 0, 4);
                    break;
                case double d:
                    BinaryPrimitives.WriteSingleBigEndian(buf, (float)d);
                    ms.Write(buf, 0, 4);
                    break;
                case string s:
                    WritePaddedString(ms, s);
                    break;
                    // bool はタグのみ
            }
        }
        return ms.ToArray();
    }

    private static void WritePaddedString(Stream s, string str)
    {
        var b = Encoding.UTF8.GetBytes(str ?? "");
        s.Write(b, 0, b.Length);
        int pad = 4 - (b.Length % 4); // 最低 1 つの NULL
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }

    // ---------- デコード ----------
    public static List<OscMessage> Parse(byte[] data, int length)
    {
        var list = new List<OscMessage>();
        try { ParseInto(data, 0, length, list); }
        catch { /* 壊れたパケットは無視 */ }
        return list;
    }

    private static void ParseInto(byte[] d, int start, int end, List<OscMessage> list)
    {
        if (end - start < 4) return;

        if (d[start] == (byte)'#')
        {
            // "#bundle\0" + timetag(8)
            int p = start + 16;
            while (p + 4 <= end)
            {
                int size = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p, 4));
                p += 4;
                if (size <= 0 || p + size > end) break;
                ParseInto(d, p, p + size, list);
                p += size;
            }
            return;
        }

        int pos = start;
        string address = ReadString(d, ref pos, end);
        if (pos >= end)
        {
            list.Add(new OscMessage(address));
            return;
        }

        string tags = ReadString(d, ref pos, end);
        var args = new List<object>();
        bool stop = false;
        for (int i = 1; i < tags.Length && !stop; i++)
        {
            switch (tags[i])
            {
                case 'i':
                    if (pos + 4 > end) { stop = true; break; }
                    args.Add(BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos, 4)));
                    pos += 4;
                    break;
                case 'f':
                    if (pos + 4 > end) { stop = true; break; }
                    args.Add(BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(pos, 4)));
                    pos += 4;
                    break;
                case 'h':
                    if (pos + 8 > end) { stop = true; break; }
                    args.Add(BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(pos, 8)));
                    pos += 8;
                    break;
                case 'd':
                    if (pos + 8 > end) { stop = true; break; }
                    args.Add((float)BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(pos, 8)));
                    pos += 8;
                    break;
                case 's':
                    args.Add(ReadString(d, ref pos, end));
                    break;
                case 'T': args.Add(true); break;
                case 'F': args.Add(false); break;
                case 'N': args.Add(null); break;
                default: stop = true; break;
            }
        }
        list.Add(new OscMessage(address, args.ToArray()));
    }

    private static string ReadString(byte[] d, ref int pos, int end)
    {
        int s = pos;
        while (pos < end && d[pos] != 0) pos++;
        string str = Encoding.UTF8.GetString(d, s, pos - s);
        int len = pos - s + 1;
        pos = s + ((len + 3) & ~3);
        return str;
    }

    public override string ToString() =>
        Args.Length == 0 ? Address : $"{Address} {string.Join(", ", Args.Select(FormatArg))}";

    public static string FormatArg(object a) => a switch
    {
        null => "nil",
        float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        string s => $"\"{s}\"",
        _ => a.ToString()
    };
}
