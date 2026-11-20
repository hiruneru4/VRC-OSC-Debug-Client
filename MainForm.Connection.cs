using System.Net;
using VRCOscDebugClient.Core;
using VRCOscDebugClient.Osc;
using VRCOscDebugClient.UI;

namespace VRCOscDebugClient;

/// <summary>「接続」タブ: 接続先・ポート・自動接続と OSCQuery (VRChat の自動検出・パラメータの取得)</summary>
public sealed partial class MainForm
{
    public const string ConnectionPage = "接続";

    private RCheck _chkOscQuery, _chkQueryAutoFetch;
    private FastLabel _lblQueryVrc, _lblQuerySelf, _lblQueryRecv, _lblConnState;
    private RButton _btnQueryFetch;
    private OscQueryService _oscQuery;
    private System.Threading.Timer _queryFetchTimer;
    private int _queryVer;
    private volatile bool _queryFeeding;
    private readonly System.Windows.Forms.Timer _queryPollTimer = new() { Interval = 1000 };
    private bool _queryPolling;

    private void BuildConnectionPage()
    {
        var page = NewPage(ConnectionPage);
        var list = Ui.PageList(page);

        // ---- 接続先 ----
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
        _lblConnState = Ui.Info(380, false);
        _btnConnect.MinimumSize = new Size(Ui.W(90), 0);
        AddRow("IP", _txtHost);
        AddRow("送信ポート", _nudSend);
        AddRow("受信ポート", Ui.Row(_nudRecv, _chkListen));
        AddRow("", Ui.Row(_btnConnect, _lamp, _lblConnState));
        AddRow("", _chkAutoConnect);

        list.Controls.Add(Ui.WideCard(
            Ui.Sub("接続先"),
            grid,
            Ui.Sub("※ 同じ PC の VRChat なら IP 127.0.0.1 / 送信 9000 / 受信 9001 のままで接続できます"),
            Ui.Sub("※ VRChat のアクションメニュー → Options → OSC → Enabled を ON にしてください")));

        // ---- OSCQuery ----
        _chkOscQuery = Ui.Chk("OSCQuery を使う (VRChat を自動で見つけ、受信ポートを自動で決める)", _s.UseOscQuery);
        _chkQueryAutoFetch = Ui.Chk("接続した時・アバターを変えた時にパラメータを自動で取得", _s.OscQueryAutoFetch);
        _lblQueryVrc = Ui.Info(460, false);
        _lblQuerySelf = Ui.Info(520);
        _lblQueryRecv = Ui.Info(520);
        _btnQueryFetch = Ui.Btn("🔎 パラメータを取得");
        _btnQueryFetch.Click += (_, _) => FetchQueryParams("ボタン");
        _chkOscQuery.CheckedChanged += (_, _) => UpdateConnectionUi();
        _tip.SetToolTip(_chkOscQuery,
            "ON: 受信ポートを空いているものから自動で選び、OSCQuery (mDNS) で VRChat に知らせます。\n" +
            "受信ポート 9001 を他の OSC アプリと取り合わずに同時に使えます。切り替えは切断中のみ");
        _tip.SetToolTip(_btnQueryFetch, "VRChat に今のアバターのパラメータ一覧と値を問い合わせ、パラメータタブ・受信タブに反映");

        list.Controls.Add(Ui.WideCard(
            Ui.Sub("OSCQuery (VRChat 公式対応の自動接続)"),
            _chkOscQuery,
            _chkQueryAutoFetch,
            _lblQueryVrc,
            _lblQuerySelf,
            _lblQueryRecv,
            Ui.Row(_btnQueryFetch),
            Ui.Sub("※ VRChat がこのツールを見つけると、VRChat の画面にツール名の通知が出ます"),
            Ui.Sub("※ Windows 版の VRChat は、同じ PC のアプリにだけパラメータの一覧と値を公開します"),
            Ui.Sub("※ アバターを読み込んでから一度も変わっていない値は 0 / false で返ることがあります (VRChat 側の既知の不具合)。届いた値で上書きされます"),
            Ui.Sub("※ 見つからない時は、Windows のファイアウォールでこのツールを許可するか、OSCQuery を OFF にしてポート指定で接続してください")));

        // VRChat から OSC が届かない時は、OSCQuery で値を定期的に問い合わせて補う
        _queryPollTimer.Tick += (_, _) => PollQuery();
        _queryPollTimer.Start();
        FormClosed += (_, _) => _queryPollTimer.Stop();

        UpdateOscQueryUi();
    }

    private async void PollQuery()
    {
        var q = _oscQuery;
        if (q?.Vrc == null || _queryPolling || !_osc.IsConnected) return;
        long since = _osc.MsSinceReceive;
        if (since >= 0 && since < 3000) return; // OSC が届いていれば問い合わせない
        _queryPolling = true;
        try
        {
            var values = await q.FetchAvatarAsync();
            await Task.Run(() =>
            {
                _queryFeeding = true;
                try
                {
                    foreach (var v in values)
                        if (v.Address != "/avatar/change") OnOscReceived(new OscMessage(v.Address, v.Value));
                }
                finally { _queryFeeding = false; }
            });
            Interlocked.Increment(ref _queryVer);
        }
        catch { /* 次の回で再試行 */ }
        finally { _queryPolling = false; }
    }

    // =====================================================================
    // OSCQuery
    // =====================================================================
    private void StartOscQuery()
    {
        StopOscQuery();
        int port = _osc.LocalRecvPort;
        if (port == 0)
        {
            _lastError = "OSCQuery: 受信ポートを開けませんでした";
            return;
        }
        var q = new OscQueryService(port);
        q.Log += msg => OpLog.Write("🔎 " + msg);
        q.VrcFound += v => BeginInvokeSafe(() => OnVrcFound(q, v));
        try
        {
            q.Start();
            _oscQuery = q;
        }
        catch (Exception ex)
        {
            q.Dispose();
            _lastError = "OSCQuery を開始できません: " + ex.Message;
            OpLog.Write("⚠ " + _lastError);
        }
        Interlocked.Increment(ref _queryVer);
    }

    private void StopOscQuery()
    {
        var q = _oscQuery;
        _oscQuery = null;
        _queryFetchTimer?.Dispose();
        _queryFetchTimer = null;
        q?.Dispose();
        Interlocked.Increment(ref _queryVer);
    }

    private void OnVrcFound(OscQueryService q, OscQueryService.VrcEndpoint v)
    {
        if (q != _oscQuery || !_osc.IsConnected) return;
        // VRChat の OSC 受信ポートへ送るように切り替える
        _osc.SetTarget(v.Ip, v.OscPort);
        OpLog.Write($"🔎 VRChat を検出: {v.Name}  (送信先 {v.Ip}:{v.OscPort} / 問い合わせ {v.QueryPort})");
        Interlocked.Increment(ref _queryVer);
        if (_chkQueryAutoFetch.Checked) FetchQueryParams("自動");
    }

    /// <summary>アバターが変わったら少し待ってから取り直す (受信スレッドから呼ばれる)</summary>
    private void OnQueryAvatarChanged()
    {
        if (_oscQuery?.Vrc == null || !_s.OscQueryAutoFetch) return;
        _queryFetchTimer?.Dispose();
        _queryFetchTimer = new System.Threading.Timer(_ => BeginInvokeSafe(() => FetchQueryParams("アバター変更")),
            null, 1500, Timeout.Infinite);
    }

    private async void FetchQueryParams(string by)
    {
        var q = _oscQuery;
        if (q?.Vrc == null)
        {
            if (by == "ボタン")
                Ui.Msg(this, q == null
                        ? "OSCQuery が動いていません。\n「OSCQuery を使う」を ON にして接続してください。"
                        : "VRChat がまだ見つかっていません。\nVRChat を起動して OSC を有効にしてください。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _btnQueryFetch.Enabled = false;
        try
        {
            var values = await q.FetchAvatarAsync();
            // /avatar/change (アバター ID) を先に入れてから、パラメータの値を流す
            var change = values.FirstOrDefault(v => v.Address == "/avatar/change");
            await Task.Run(() =>
            {
                _queryFeeding = true; // 流している /avatar/change で取り直さないように
                try
                {
                if (change != null) OnOscReceived(new OscMessage(change.Address, change.Value));
                foreach (var v in values)
                    if (v.Address != "/avatar/change") OnOscReceived(new OscMessage(v.Address, v.Value));
                }
                finally { _queryFeeding = false; }
            });
            int count = values.Count(v => v.Address.StartsWith("/avatar/parameters/", StringComparison.Ordinal));
            OpLog.Write($"🔎 OSCQuery: パラメータ {count} 件を取得 ({by})");
        }
        catch (Exception ex)
        {
            _lastError = "OSCQuery の問い合わせに失敗: " + ex.Message;
            OpLog.Write("⚠ " + _lastError);
        }
        finally
        {
            _btnQueryFetch.Enabled = true;
        }
    }

    /// <summary>接続タブの表示 (UiTick から呼ぶ。変わった時だけ書き換える)</summary>
    private void UpdateOscQueryUi()
    {
        if (_lblQueryVrc == null) return;
        static void Set(FastLabel l, string text)
        {
            if (l != null && l.Text != L10n.Auto(text)) l.Text = text;
        }

        var t = _osc.Target;
        Set(_lblConnState, !_osc.IsConnected || t == null ? "未接続"
            : $"接続中  送信 {t.Address}:{t.Port}" + (_osc.IsListening ? $" / 受信 {_osc.LocalRecvPort}" : " (送信のみ)") +
              (_osc.ExtraRecvPort > 0 ? $" + {_osc.ExtraRecvPort}" : ""));

        var q = _oscQuery;
        if (q == null)
        {
            Set(_lblQueryVrc, _chkOscQuery.Checked ? "VRChat: 接続すると探します" : "VRChat: (OSCQuery を使っていません)");
            Set(_lblQuerySelf, "");
            Set(_lblQueryRecv, "");
        }
        else
        {
            var v = q.Vrc;
            Set(_lblQueryVrc, v == null
                ? "VRChat: 探しています…"
                : $"VRChat: {v.Name}  送信先 {v.Ip}:{v.OscPort} / 問い合わせ {v.QueryPort}");
            Set(_lblQuerySelf, $"このツール: 受信 {q.OscPort}" + (_osc.ExtraRecvPort > 0 ? $" (予備 {_osc.ExtraRecvPort})" : "") +
                               $" / 問い合わせ {q.HttpPort}   " + (q.SeenByVrc ? "VRChat 確認済み" : "VRChat からの確認待ち"));
            long since = _osc.MsSinceReceive;
            Set(_lblQueryRecv, since < 0 ? "VRChat からの受信: まだありません"
                : since < 3000 ? (_osc.PrimaryReceiving ? "VRChat からの受信: あり (OSCQuery のポート)" : $"VRChat からの受信: あり (予備のポート {_osc.ExtraRecvPort})")
                : "VRChat からの受信: 止まっています (OSCQuery で値を問い合わせて補っています)");
        }
        var c = q?.Vrc != null ? Theme.Ok : Theme.Fore;
        if (_lblQueryVrc.ForeColor != c) _lblQueryVrc.ForeColor = c;
        bool en = q?.Vrc != null;
        if (_btnQueryFetch.Enabled != en) _btnQueryFetch.Enabled = en;
    }
}
