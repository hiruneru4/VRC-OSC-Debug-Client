using System.Net;
using System.Net.Sockets;

namespace VRCOscDebugClient.Osc;

/// <summary>VRChat への送信 (既定 9000) と VRChat からの受信 (既定 9001) を扱う</summary>
public sealed class OscClient : IDisposable
{
    private readonly object _lock = new();
    private UdpClient _sender;
    private UdpClient _receiver;
    private IPEndPoint _target;
    private CancellationTokenSource _cts;
    private long _sent, _recv;

    public bool IsConnected { get; private set; }
    public bool IsListening => _receiver != null;

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

    public void Connect(string host, int sendPort, int recvPort, bool listen)
    {
        Disconnect();

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
                var rc = new UdpClient(new IPEndPoint(IPAddress.Any, recvPort));
                lock (_lock) _receiver = rc;
                _ = Task.Run(() => ReceiveLoop(rc, cts.Token));
            }
            catch (SocketException ex)
            {
                Error?.Invoke($"受信ポート {recvPort} を開けません (他アプリ使用中?): {ex.Message}");
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
            try { _sender?.Dispose(); } catch { }
            _receiver = null;
            _sender = null;
            _cts = null;
        }
    }

    private async Task ReceiveLoop(UdpClient rc, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await rc.ReceiveAsync(ct).ConfigureAwait(false);
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
