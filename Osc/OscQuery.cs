using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VRCOscDebugClient.Osc;

/// <summary>OSCQuery で VRChat から取得した 1 つの値</summary>
public sealed record OscQueryValue(string Address, string Type, object Value);

/// <summary>
/// OSCQuery (VRChat 2023.3.1 以降が対応している公式の仕組み)。外部ライブラリなしで実装。
/// ・mDNS で自分のサービス (_oscjson._tcp / _osc._udp) を告知 → VRChat がこのツールを見つけて OSC を送ってくる
/// ・mDNS で VRChat (VRChat-Client-xxxxxx) を探す → VRChat の OSC 受信ポートと問い合わせ用ポートが分かる
/// ・HTTP で VRChat にアバターのパラメータ一覧と今の値を問い合わせる
/// ・このツールのアドレス一覧 (/avatar を受け取る) を HTTP で公開する
/// </summary>
public sealed class OscQueryService : IDisposable
{
    public const string DisplayName = "VRC OSC Debug Client";
    private const string OscJson = "_oscjson._tcp.local";
    private const string OscUdp = "_osc._udp.local";
    private const string VrcPrefix = "VRChat-Client-";
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    private readonly string _instance;   // 例: VRC-OSC-Debug-Client-123456
    private readonly string _host;       // 例: VRC-OSC-Debug-Client-123456.local
    private readonly int _oscPort;       // このツールの OSC 受信ポート
    private Socket _mdns;
    private TcpListener _http;
    private CancellationTokenSource _cts;
    private readonly object _lock = new();

    // 見つけた VRChat
    private readonly Dictionary<string, int> _vrcQueryPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _vrcOscPorts = new(StringComparer.OrdinalIgnoreCase);

    public int HttpPort { get; private set; }
    public int OscPort => _oscPort;
    public bool Running => _cts != null;

    /// <summary>VRChat を見つけた / 情報が変わった (別スレッドから)</summary>
    public event Action<VrcEndpoint> VrcFound;
    /// <summary>状態・エラーの通知 (別スレッドから)</summary>
    public event Action<string> Log;

    public VrcEndpoint Vrc { get; private set; }

    public sealed record VrcEndpoint(string Name, IPAddress Ip, int OscPort, int QueryPort);

    public OscQueryService(int oscPort)
    {
        _oscPort = oscPort;
        _instance = "VRC-OSC-Debug-Client-" + Random.Shared.Next(100000, 999999);
        _host = _instance + ".local";
    }

    // =================================================================
    // 開始・停止
    // =================================================================
    public void Start()
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;

        // HTTP (このツールのアドレス一覧)
        _http = new TcpListener(IPAddress.Loopback, 0);
        _http.Start();
        HttpPort = ((IPEndPoint)_http.LocalEndpoint).Port;
        _ = Task.Run(() => HttpLoop(_http, cts.Token));

        // mDNS (告知と検索)
        try
        {
            _mdns = OpenMdnsSocket();
            _ = Task.Run(() => MdnsLoop(_mdns, cts.Token));
        }
        catch (Exception ex)
        {
            Log?.Invoke("mDNS を開始できません: " + ex.Message);
        }

        // 告知を数回 + VRChat を定期的に探す
        _ = Task.Run(async () =>
        {
            for (int i = 0; !cts.IsCancellationRequested; i++)
            {
                try
                {
                    if (i < 3) Announce();
                    if (Vrc == null || i % 6 == 0) Query();
                }
                catch { }
                try { await Task.Delay(i < 3 ? 1000 : 5000, cts.Token); } catch { break; }
            }
        });
        Log?.Invoke($"OSCQuery 開始: 受信 {_oscPort} / 問い合わせ {HttpPort}");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        try { if (_mdns != null) SendGoodbye(); } catch { }
        try { _mdns?.Dispose(); } catch { }
        try { _http?.Stop(); } catch { }
        _mdns = null;
        _http = null;
        Vrc = null;
        lock (_lock) { _vrcQueryPorts.Clear(); _vrcOscPorts.Clear(); }
    }

    public void Dispose() => Stop();

    // =================================================================
    // VRChat への問い合わせ (HTTP)
    // =================================================================
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>VRChat のアバターのパラメータ一覧と今の値 (/avatar 以下)</summary>
    public async Task<List<OscQueryValue>> FetchAvatarAsync()
    {
        var v = Vrc ?? throw new InvalidOperationException("VRChat が見つかっていません");
        string json = await Http.GetStringAsync($"http://{v.Ip}:{v.QueryPort}/avatar").ConfigureAwait(false);
        var list = new List<OscQueryValue>();
        using var doc = JsonDocument.Parse(json);
        Walk(doc.RootElement, list);
        return list;
    }

    private static void Walk(JsonElement node, List<OscQueryValue> list)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        if (node.TryGetProperty("FULL_PATH", out var pathEl) && node.TryGetProperty("TYPE", out var typeEl) &&
            node.TryGetProperty("VALUE", out var valEl) && valEl.ValueKind == JsonValueKind.Array && valEl.GetArrayLength() > 0)
        {
            string path = pathEl.GetString() ?? "";
            string type = typeEl.GetString() ?? "";
            object value = ToValue(type, valEl[0]);
            if (value != null && path.Length > 0) list.Add(new OscQueryValue(path, type, value));
        }
        if (node.TryGetProperty("CONTENTS", out var contents) && contents.ValueKind == JsonValueKind.Object)
            foreach (var child in contents.EnumerateObject()) Walk(child.Value, list);
    }

    private static object ToValue(string type, JsonElement v)
    {
        try
        {
            switch (type)
            {
                case "f":
                    return v.ValueKind == JsonValueKind.Number ? (object)v.GetSingle() : null;
                case "i":
                    return v.ValueKind == JsonValueKind.Number ? (object)(int)v.GetDouble() : null;
                case "T":
                case "F":
                    return v.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => v.GetDouble() != 0,
                        _ => null
                    };
                case "s":
                    return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                default:
                    return null;
            }
        }
        catch { return null; }
    }

    private async Task<int> FetchVrcOscPortAsync(IPAddress ip, int queryPort)
    {
        try
        {
            string json = await Http.GetStringAsync($"http://{ip}:{queryPort}/?HOST_INFO").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("OSC_PORT", out var p) && p.TryGetInt32(out int port)) return port;
        }
        catch { }
        return 0;
    }

    // =================================================================
    // HTTP サーバー (このツールのアドレス一覧)
    // =================================================================
    private string HostInfoJson() =>
        "{\"NAME\":\"" + DisplayName + "\"," +
        "\"EXTENSIONS\":{\"ACCESS\":true,\"CLIPMODE\":false,\"RANGE\":true,\"TYPE\":true,\"VALUE\":true}," +
        "\"OSC_IP\":\"127.0.0.1\",\"OSC_PORT\":" + _oscPort + ",\"OSC_TRANSPORT\":\"UDP\"}";

    // VRChat はこの一覧に /avatar があるアプリへ、アバターの OSC (/avatar/change, /avatar/parameters/*) を送る
    private const string AvatarNode =
        "{\"DESCRIPTION\":\"avatar\",\"FULL_PATH\":\"/avatar\",\"ACCESS\":2,\"CONTENTS\":{" +
        "\"change\":{\"DESCRIPTION\":\"\",\"FULL_PATH\":\"/avatar/change\",\"ACCESS\":2,\"TYPE\":\"s\"}}}";

    private static string RootJson() =>
        "{\"DESCRIPTION\":\"root node\",\"FULL_PATH\":\"/\",\"ACCESS\":0,\"CONTENTS\":{\"avatar\":" + AvatarNode + "}}";

    private async Task HttpLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => Serve(client), ct);
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                var stream = client.GetStream();
                var buf = new byte[4096];
                var sb = new StringBuilder();
                while (!sb.ToString().Contains("\r\n\r\n") && sb.Length < 16384)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                }
                string first = sb.ToString().Split("\r\n")[0];
                var parts = first.Split(' ');
                string target = parts.Length > 1 ? parts[1] : "/";
                string path = target, query = "";
                int q = target.IndexOf('?');
                if (q >= 0) { path = target[..q]; query = target[(q + 1)..]; }
                path = Uri.UnescapeDataString(path);

                string body;
                int status = 200;
                if (query.Contains("HOST_INFO", StringComparison.OrdinalIgnoreCase)) body = HostInfoJson();
                else if (path is "/" or "") body = RootJson();
                else if (path.TrimEnd('/') == "/avatar") body = AvatarNode;
                else if (path == "/avatar/change")
                    body = "{\"DESCRIPTION\":\"\",\"FULL_PATH\":\"/avatar/change\",\"ACCESS\":2,\"TYPE\":\"s\"}";
                else { status = 404; body = "{}"; }

                byte[] bytes = Encoding.UTF8.GetBytes(body);
                string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                              "Content-Type: application/json; charset=utf-8\r\n" +
                              $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(head);
                stream.Write(hb, 0, hb.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }
    }

    // =================================================================
    // mDNS
    // =================================================================
    private static Socket OpenMdnsSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.ExclusiveAddressUse = false;
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.Bind(new IPEndPoint(IPAddress.Any, MdnsPort)); // Windows 自身の mDNS と共有
        foreach (var ip in LocalAddresses(true))
        {
            try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, ip)); }
            catch { }
        }
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        return s;
    }

    private static List<IPAddress> LocalAddresses(bool includeLoopback)
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                bool loop = ni.NetworkInterfaceType == NetworkInterfaceType.Loopback;
                if (loop && !includeLoopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address);
            }
        }
        catch { }
        if (includeLoopback && !list.Contains(IPAddress.Loopback)) list.Add(IPAddress.Loopback);
        return list;
    }

    private static bool IsLocal(IPAddress ip) =>
        IPAddress.IsLoopback(ip) || LocalAddresses(false).Any(a => a.Equals(ip));

    /// <summary>全てのネットワークへ mDNS を送る (同じ PC の VRChat にも届くようにループバック有効)</summary>
    private void SendMulticast(byte[] packet)
    {
        var s = _mdns;
        if (s == null) return;
        var to = new IPEndPoint(Group, MdnsPort);
        bool any = false;
        foreach (var ip in LocalAddresses(false))
        {
            try
            {
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                s.SendTo(packet, to);
                any = true;
            }
            catch { }
        }
        if (!any) { try { s.SendTo(packet, to); } catch { } }
    }

    private async Task MdnsLoop(Socket s, CancellationToken ct)
    {
        var buf = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await s.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (ct.IsCancellationRequested) break; continue; }
            try { Handle(buf.AsSpan(0, r.ReceivedBytes).ToArray(), (IPEndPoint)r.RemoteEndPoint); }
            catch { /* 壊れたパケットは無視 */ }
        }
    }

    private void Handle(byte[] p, IPEndPoint from)
    {
        var msg = Dns.Parse(p);
        if (!msg.IsResponse)
        {
            // 問い合わせ: 自分のサービスを聞かれたら答える
            bool json = false, udp = false;
            foreach (var q in msg.Questions)
            {
                if (q.Type is not (Dns.PTR or Dns.SRV or Dns.TXT or Dns.A or Dns.ANY)) continue;
                if (Same(q.Name, OscJson) || Same(q.Name, $"{_instance}.{OscJson}")) json = true;
                if (Same(q.Name, OscUdp) || Same(q.Name, $"{_instance}.{OscUdp}")) udp = true;
                if (Same(q.Name, _host)) { json = true; }
            }
            if (json || udp)
            {
                var packet = BuildResponse(json, udp, msg.Id, 120);
                SendMulticast(packet);
                if (from.Port != MdnsPort) { try { _mdns?.SendTo(packet, from); } catch { } } // 一般の DNS 問い合わせへの返答
            }
            return;
        }

        // 返答: VRChat を探す
        var ptrs = msg.Records.Where(r => r.Type == Dns.PTR).Select(r => r.Data as string).Where(n => n != null).ToList();
        var srvs = msg.Records.Where(r => r.Type == Dns.SRV).ToList();
        var aRecs = msg.Records.Where(r => r.Type == Dns.A).ToList();
        bool changed = false;
        foreach (var srv in srvs)
        {
            string inst = srv.Name;
            if (!inst.StartsWith(VrcPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var (port, target) = ((int, string))srv.Data;
            string name = inst.Split('.')[0];
            lock (_lock)
            {
                if (inst.EndsWith(OscJson, StringComparison.OrdinalIgnoreCase))
                {
                    if (!_vrcQueryPorts.TryGetValue(name, out var old) || old != port) { _vrcQueryPorts[name] = port; changed = true; }
                }
                else if (inst.EndsWith(OscUdp, StringComparison.OrdinalIgnoreCase))
                {
                    if (!_vrcOscPorts.TryGetValue(name, out var old) || old != port) { _vrcOscPorts[name] = port; changed = true; }
                }
            }
            if (changed)
            {
                // IP: A レコード → 無ければ送ってきた相手。同じ PC なら 127.0.0.1
                var a = aRecs.FirstOrDefault(r => Same(r.Name, target));
                var ip = a?.Data as IPAddress ?? from.Address;
                if (IsLocal(ip)) ip = IPAddress.Loopback;
                _ = Task.Run(() => UpdateVrc(name, ip));
            }
        }
        if (!changed && Vrc == null && ptrs.Any(n => n.StartsWith(VrcPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            // PTR だけ届いた → 詳しく聞き直す
            foreach (var n in ptrs.Where(n => n.StartsWith(VrcPrefix, StringComparison.OrdinalIgnoreCase)))
                SendMulticast(Dns.BuildQuery((n, Dns.SRV)));
        }
    }

    private async Task UpdateVrc(string name, IPAddress ip)
    {
        int queryPort, oscPort;
        lock (_lock)
        {
            _vrcQueryPorts.TryGetValue(name, out queryPort);
            _vrcOscPorts.TryGetValue(name, out oscPort);
        }
        if (queryPort == 0) return;
        if (oscPort == 0) oscPort = await FetchVrcOscPortAsync(ip, queryPort).ConfigureAwait(false);
        if (oscPort == 0) oscPort = 9000;
        var v = new VrcEndpoint(name, ip, oscPort, queryPort);
        if (Vrc != null && Vrc == v) return;
        Vrc = v;
        VrcFound?.Invoke(v);
    }

    private void Query() =>
        SendMulticast(Dns.BuildQuery((OscJson, Dns.PTR), (OscUdp, Dns.PTR)));

    private void Announce() => SendMulticast(BuildResponse(true, true, 0, 120));
    private void SendGoodbye() => SendMulticast(BuildResponse(true, true, 0, 0)); // TTL 0 = 終了の知らせ

    private byte[] BuildResponse(bool json, bool udp, ushort id, uint ttl)
    {
        var w = new Dns.Writer(id, response: true);
        string jsonInst = $"{_instance}.{OscJson}", udpInst = $"{_instance}.{OscUdp}";
        if (json) w.Ptr(OscJson, jsonInst, ttl == 0 ? 0u : 4500u);
        if (udp) w.Ptr(OscUdp, udpInst, ttl == 0 ? 0u : 4500u);
        if (json) { w.Srv(jsonInst, HttpPort, _host, ttl); w.Txt(jsonInst, "txtvers=1", ttl); }
        if (udp) { w.Srv(udpInst, _oscPort, _host, ttl); w.Txt(udpInst, "txtvers=1", ttl); }
        w.A(_host, IPAddress.Loopback, ttl);
        return w.ToArray();
    }

    private static bool Same(string a, string b) =>
        string.Equals(a?.TrimEnd('.'), b?.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    // =================================================================
    // DNS メッセージ (mDNS で使う分だけ)
    // =================================================================
    private static class Dns
    {
        public const ushort A = 1, PTR = 12, TXT = 16, SRV = 33, ANY = 255;

        public sealed class Question { public string Name; public ushort Type; }
        public sealed class Record { public string Name; public ushort Type; public object Data; }
        public sealed class Message
        {
            public ushort Id;
            public bool IsResponse;
            public List<Question> Questions = new();
            public List<Record> Records = new();
        }

        public static Message Parse(byte[] p)
        {
            var m = new Message { Id = (ushort)(p[0] << 8 | p[1]), IsResponse = (p[2] & 0x80) != 0 };
            int qd = U16(p, 4), an = U16(p, 6), ns = U16(p, 8), ar = U16(p, 10);
            int o = 12;
            for (int i = 0; i < qd; i++)
            {
                string name = ReadName(p, ref o);
                m.Questions.Add(new Question { Name = name, Type = (ushort)U16(p, o) });
                o += 4;
            }
            for (int i = 0; i < an + ns + ar; i++)
            {
                string name = ReadName(p, ref o);
                ushort type = (ushort)U16(p, o);
                int len = U16(p, o + 8);
                int d = o + 10;
                object data = null;
                switch (type)
                {
                    case PTR: { int t = d; data = ReadName(p, ref t); break; }
                    case SRV: { int t = d + 6; data = (U16(p, d + 4), ReadName(p, ref t)); break; }
                    case A: if (len == 4) data = new IPAddress(new[] { p[d], p[d + 1], p[d + 2], p[d + 3] }); break;
                }
                m.Records.Add(new Record { Name = name, Type = type, Data = data });
                o = d + len;
            }
            return m;
        }

        private static int U16(byte[] p, int o) => p[o] << 8 | p[o + 1];

        private static string ReadName(byte[] p, ref int o)
        {
            var labels = new List<string>();
            int pos = o, jumps = 0;
            bool jumped = false;
            while (true)
            {
                int len = p[pos];
                if (len == 0) { pos++; break; }
                if ((len & 0xC0) == 0xC0)
                {
                    int ptr = (len & 0x3F) << 8 | p[pos + 1];
                    if (!jumped) o = pos + 2;
                    jumped = true;
                    pos = ptr;
                    if (++jumps > 20) break;
                    continue;
                }
                labels.Add(Encoding.UTF8.GetString(p, pos + 1, len));
                pos += 1 + len;
            }
            if (!jumped) o = pos;
            return string.Join(".", labels);
        }

        public static byte[] BuildQuery(params (string Name, ushort Type)[] qs)
        {
            var w = new Writer(0, response: false);
            foreach (var (n, t) in qs) w.Question(n, t);
            return w.ToArray();
        }

        public sealed class Writer
        {
            private readonly ushort _id;
            private readonly bool _response;
            private readonly List<byte[]> _q = new(), _an = new();

            public Writer(ushort id, bool response) { _id = id; _response = response; }

            public void Question(string name, ushort type)
            {
                var b = new List<byte>(Name(name));
                b.AddRange(new byte[] { (byte)(type >> 8), (byte)type, 0x00, 0x01 });
                _q.Add(b.ToArray());
            }

            private void Rec(string name, ushort type, bool unique, uint ttl, byte[] data)
            {
                var b = new List<byte>(Name(name));
                ushort cls = (ushort)(unique ? 0x8001 : 0x0001); // 0x8000 = キャッシュを上書き
                b.AddRange(new byte[]
                {
                    (byte)(type >> 8), (byte)type, (byte)(cls >> 8), (byte)cls,
                    (byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl,
                    (byte)(data.Length >> 8), (byte)data.Length
                });
                b.AddRange(data);
                _an.Add(b.ToArray());
            }

            public void Ptr(string name, string target, uint ttl) => Rec(name, PTR, false, ttl, Name(target));

            public void Srv(string name, int port, string target, uint ttl)
            {
                var d = new List<byte> { 0, 0, 0, 0, (byte)(port >> 8), (byte)port };
                d.AddRange(Name(target));
                Rec(name, SRV, true, ttl, d.ToArray());
            }

            public void Txt(string name, string text, uint ttl)
            {
                var t = Encoding.UTF8.GetBytes(text);
                var d = new byte[t.Length + 1];
                d[0] = (byte)t.Length;
                t.CopyTo(d, 1);
                Rec(name, TXT, true, ttl, d);
            }

            public void A(string name, IPAddress ip, uint ttl) => Rec(name, A, true, ttl, ip.GetAddressBytes());

            private static byte[] Name(string name)
            {
                var b = new List<byte>();
                foreach (var label in name.TrimEnd('.').Split('.'))
                {
                    var l = Encoding.UTF8.GetBytes(label);
                    b.Add((byte)Math.Min(63, l.Length));
                    b.AddRange(l.Take(63));
                }
                b.Add(0);
                return b.ToArray();
            }

            public byte[] ToArray()
            {
                var b = new List<byte>
                {
                    (byte)(_id >> 8), (byte)_id,
                    (byte)(_response ? 0x84 : 0x00), 0x00,
                    0, (byte)_q.Count, 0, (byte)_an.Count, 0, 0, 0, 0
                };
                foreach (var q in _q) b.AddRange(q);
                foreach (var a in _an) b.AddRange(a);
                return b.ToArray();
            }
        }
    }
}
