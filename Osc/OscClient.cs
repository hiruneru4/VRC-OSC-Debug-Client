using System.Net;
using System.Net.Sockets;

namespace VRCOscDebugClient.Osc;

/// <summary>VRChat への送信 (既定 9000) と VRChat からの受信 (既定 9001) を扱う</summary>
public sealed class OscClient : IDisposable
{
    private readonly object _lock = new();
    private UdpClient _sender;
    private UdpClient _receiver;
    private UdpClient _extra;          // OSCQuery 時の予備の受信 (通常の受信ポート 9001 など)
    private long _primaryTick;         // メインの受信ポートに最後に届いた時刻
    private long _lastRecvTick;

    /// <summary>予備の受信ポート (0 = 使っていない)</summary>
    public int ExtraRecvPort { get; private set; }

    /// <summary>最後に何か受信してからの時間 (ms)。まだ無ければ -1</summary>
    public long MsSinceReceive
    {
        get { long t = Interlocked.Read(ref _lastRecvTick); return t == 0 ? -1 : Environment.TickCount64 - t; }
    }

    /// <summary>メインの受信ポート (OSCQuery で知らせたポート) に届いているか</summary>
    public bool PrimaryReceiving => Environment.TickCount64 - Interlocked.Read(ref _primaryTick) < 3000;
    private IPEndPoint _target;
    private CancellationTokenSource _cts;
    private long _sent, _recv;

    public bool IsConnected { get; private set; }
    public bool IsListening => _receiver != null;

    /// <summary>実際に受信しているポート (受信ポート 0 = 空いているポートを自動で使う時の確認用)</summary>
    public int LocalRecvPort
    {
        get
        {
            lock (_lock)
            {
                try { return (_receiver?.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0; }
                catch { return 0; }
            }
        }
    }

    /// <summary>今の送信先</summary>
    public IPEndPoint Target { get { lock (_lock) return _target; } }

    /// <summary>送信先だけを変える (OSCQuery で VRChat の受信ポートが分かった時)</summary>
    public void SetTarget(IPAddress ip, int port)
    {
        lock (_lock) _target = new IPEndPoint(ip, port);
    }

    /// <summary>false の間は通常送信をすべて破棄する (オフ機能)</summary>
    public volatile bool OutputEnabled = true;

    private long _suppressUntil;

    /// <summary>指定時間、通常送信 (押す側) を遮断する。全停止直後に暴走中の処理が再び押すのを防ぐ</summary>
    public void SuppressFor(int ms) =>
        Interlocked.Exchange(ref _suppressUntil, Environment.TickCount64 + ms);

    public bool IsSuppressed => Environment.TickCount64 < Interlocked.Read(ref _suppressUntil);

    public long SentCount => Interlocked.Read(ref _sent);
    public long RecvCount => Interlocked.Read(ref _recv);

    public event Action<OscMessage> MessageReceived;
    public event Action<OscMessage> MessageSent;
    public event Action<string> Error;

    public void Connect(string host, int sendPort, int recvPort, bool listen, int extraRecvPort = 0)
    {
        Disconnect();
        Interlocked.Exchange(ref _lastRecvTick, 0);
        Interlocked.Exchange(ref _primaryTick, 0);

        IPAddress ip;
        if (!IPAddress.TryParse(host, out ip))
        {
            ip = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        }

        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _target = new IPEndPoint(ip, sendPort);
            _sender = new UdpClient(AddressFamily.InterNetwork);
            _cts = cts;
        }

        if (listen)
        {
            try
            {
                // recvPort = 0 なら空いているポート (OSCQuery で VRChat に知らせる)
                var rc = new UdpClient(new IPEndPoint(recvPort == 0 ? IPAddress.Loopback : IPAddress.Any, recvPort));
                lock (_lock) _receiver = rc;
                _ = Task.Run(() => ReceiveLoop(rc, true, cts.Token));
            }
            catch (SocketException ex)
            {
                Error?.Invoke($"受信ポート {recvPort} を開けません (他アプリ使用中?): {ex.Message}");
            }

            // 予備: VRChat が通常の受信ポートへ送っている分も受け取る (使えなければ黙って諦める)
            if (extraRecvPort > 0)
            {
                try
                {
                    var ex2 = new UdpClient(new IPEndPoint(IPAddress.Any, extraRecvPort));
                    lock (_lock) _extra = ex2;
                    ExtraRecvPort = extraRecvPort;
                    _ = Task.Run(() => ReceiveLoop(ex2, false, cts.Token));
                }
                catch (SocketException) { ExtraRecvPort = 0; }
            }
        }

        IsConnected = true;
    }

    public void Disconnect()
    {
        IsConnected = false;
        lock (_lock)
        {
            try { _cts?.Cancel(); } catch { }
            try { _receiver?.Dispose(); } catch { }
            try { _extra?.Dispose(); } catch { }
            _extra = null;
            ExtraRecvPort = 0;
            try { _sender?.Dispose(); } catch { }
            _receiver = null;
            _sender = null;
            _cts = null;
        }
    }

    private async Task ReceiveLoop(UdpClient rc, bool primary, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await rc.ReceiveAsync(ct).ConfigureAwait(false);
                long now = Environment.TickCount64;
                if (primary) Interlocked.Exchange(ref _primaryTick, now);
                // メインのポートに届いている間は、予備のポートの分は重複になるので捨てる
                else if (now - Interlocked.Read(ref _primaryTick) < 3000) continue;
                Interlocked.Exchange(ref _lastRecvTick, now);
                Interlocked.Increment(ref _recv);
                var handler = MessageReceived;
                if (handler == null) continue;
                foreach (var m in OscMessage.Parse(r.Buffer, r.Buffer.Length))
                    handler(m);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (ct.IsCancellationRequested) break; }
            catch (Exception ex) { Error?.Invoke("受信エラー: " + ex.Message); }
        }
    }

    /// <summary>通常送信 (出力OFF中は送らない)</summary>
    public bool Send(string address, params object[] args) => SendCore(new OscMessage(address, args), false);

    /// <summary>出力OFFでも送る (リセット用)</summary>
    public bool SendForce(string address, params object[] args) => SendCore(new OscMessage(address, args), true);

    private bool SendCore(OscMessage msg, bool force)
    {
        if (!IsConnected) return false;
        if (!force && (!OutputEnabled || IsSuppressed)) return false;

        byte[] bytes;
        try { bytes = msg.ToBytes(); }
        catch (Exception ex) { Error?.Invoke(ex.Message); return false; }

        lock (_lock)
        {
            if (_sender == null) return false;
            try { _sender.Send(bytes, bytes.Length, _target); }
            catch (Exception ex) { Error?.Invoke("送信エラー: " + ex.Message); return false; }
        }
        Interlocked.Increment(ref _sent);
        MessageSent?.Invoke(msg);
        return true;
    }

    public void Dispose() => Disconnect();
}
