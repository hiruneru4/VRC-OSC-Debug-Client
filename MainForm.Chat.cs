using VRCOscDebugClient.Core;
using VRCOscDebugClient.UI;

namespace VRCOscDebugClient;

/// <summary>チャット: カテゴリ操作 (停止・ON/OFF・キー) と定型文 (保存・呼び出し・キーで送信)</summary>
public sealed partial class MainForm
{
    public const string ChatCategory = "チャット";
    private const int HK_CHAT = 5, HK_CHATPRESET_BASE = 2000;

    private ListBox _lstChatPresets;
    private TextBox _txtChatPresetName;
    private HotkeyBox _hkChatPreset;
    private readonly List<ChatPreset> _hotkeyChatPresets = new();

    /// <summary>他のタブと同じ「カテゴリ停止 / ON/OFF / キー」</summary>
    private Control BuildChatTools()
    {
        var stop = Ui.Btn("カテゴリ停止");
        stop.Click += (_, _) => StopChat("ボタン");
        _s.CategoryHotkeys.TryGetValue(ChatCategory, out var hkText);
        var hk = new HotkeyBox { Width = Ui.W(130), Anchor = AnchorStyles.Left, Margin = Ui.InputMargin, Hotkey = Hotkey.Parse(hkText) };
        hk.HotkeyChanged += (_, _) => _s.CategoryHotkeys[ChatCategory] = hk.Hotkey.ToString();
        var run = Ui.Btn("ON/OFF");
        run.Click += (_, _) => ToggleChat("ボタン");
        _tip.SetToolTip(stop, "チャットの連打を止め、入力中表示も消す");
        _tip.SetToolTip(run, "ON: 入力欄 (空なら選択中の定型文) を送信・連打開始 / 動作中なら停止");
        _tip.SetToolTip(hk, "チャットの ON/OFF キー\nON: 入力欄 (空なら選択中の定型文) を送信 / OFF: 連打を停止");
        return Ui.Row(stop, run, Ui.L("キー"), hk);
    }

    private void StopChat(string by)
    {
        _chatRunner.Stop();
        if (_chkChatTyping != null && _chkChatTyping.Checked) _chkChatTyping.Checked = false;
        OpLog.Write($"■ カテゴリ停止: {ChatCategory} ({by})");
    }

    private void ToggleChat(string by)
    {
        if (_chatRunner.IsRunning)
        {
            StopChat(by);
            return;
        }
        if (_txtChat.TextLength > 0)
        {
            OpLog.Write($"⌨ {ChatCategory} ON ({by})");
            StartChat(_txtChat.Text, true);
        }
        else if (_lstChatPresets?.SelectedItem is ChatPreset p)
        {
            OpLog.Write($"⌨ {ChatCategory} ON ({by}) 定型文: {DisplayName(p)}");
            StartChat(p.Text, false);
        }
        else OpLog.Write($"⌨ {ChatCategory}: 送信する文字がありません (入力欄か定型文を選んでください)");
    }

    private static string DisplayName(ChatPreset p) =>
        string.IsNullOrWhiteSpace(p.Name) ? (p.Text ?? "").Replace("\n", " ") : p.Name;

    // ---------------- 定型文 ----------------
    private Control BuildChatPresetCard()
    {
        _lstChatPresets = new ListBox { IntegralHeight = false, HorizontalScrollbar = true };
        foreach (var p in _s.ChatPresets) _lstChatPresets.Items.Add(p);

        _txtChatPresetName = Ui.Txt("", 140);
        _txtChatPresetName.PlaceholderText = "名前 (省略可)";
        _hkChatPreset = new HotkeyBox { Width = Ui.W(130), Anchor = AnchorStyles.Left, Margin = Ui.InputMargin };

        var save = Ui.Btn("入力欄を保存");
        var update = Ui.Btn("上書き");
        var load = Ui.Btn("呼び出し");
        var send = Ui.Btn("送信");
        var del = Ui.Btn("削除");
        var up = Ui.Btn("↑", 30);
        var down = Ui.Btn("↓", 30);

        _tip.SetToolTip(save, "入力欄の文字を定型文として保存 (名前・キーも一緒に)");
        _tip.SetToolTip(update, "選択中の定型文を、入力欄の文字・名前・キーで置き換え");
        _tip.SetToolTip(load, "選択中の定型文を入力欄に呼び出す (ダブルクリックでも可)");
        _tip.SetToolTip(send, "選択中の定型文をそのまま送信 (モードが「連打」なら連打)");
        _tip.SetToolTip(_hkChatPreset, "この定型文を送信するキー (Back/Delete で解除)");
        _tip.SetToolTip(_lstChatPresets, "ダブルクリック: 呼び出し / Enter: 送信 / Delete: 削除");

        save.Click += (_, _) =>
        {
            string text = _txtChat.Text.Replace("\r\n", "\n");
            if (text.Length == 0) { Ui.Msg(this, "入力欄に文字を入れてから保存してください", Text); return; }
            var p = new ChatPreset { Name = _txtChatPresetName.Text.Trim(), Text = text, Hotkey = _hkChatPreset.Hotkey.ToString() };
            _lstChatPresets.Items.Add(p);
            _lstChatPresets.SelectedItem = p;
            OpLog.Write($"＋ 定型文を保存: {DisplayName(p)}");
            RegisterHotkeys();
        };
        update.Click += (_, _) =>
        {
            int i = _lstChatPresets.SelectedIndex;
            if (i < 0) return;
            string text = _txtChat.Text.Replace("\r\n", "\n");
            var p = (ChatPreset)_lstChatPresets.Items[i];
            if (text.Length > 0) p.Text = text;
            p.Name = _txtChatPresetName.Text.Trim();
            p.Hotkey = _hkChatPreset.Hotkey.ToString();
            _lstChatPresets.Items[i] = p; // 表示を更新
            RegisterHotkeys();
        };
        load.Click += (_, _) => LoadChatPreset();
        send.Click += (_, _) =>
        {
            if (_lstChatPresets.SelectedItem is ChatPreset p) StartChat(p.Text, false);
        };
        del.Click += (_, _) => RemoveChatPreset();
        up.Click += (_, _) => MoveChatPreset(-1);
        down.Click += (_, _) => MoveChatPreset(1);

        _lstChatPresets.DoubleClick += (_, _) => LoadChatPreset();
        _lstChatPresets.SelectedIndexChanged += (_, _) =>
        {
            if (_lstChatPresets.SelectedItem is not ChatPreset p) return;
            _txtChatPresetName.Text = p.Name;
            _hkChatPreset.Hotkey = Hotkey.Parse(p.Hotkey);
        };
        _lstChatPresets.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { RemoveChatPreset(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && _lstChatPresets.SelectedItem is ChatPreset p) { StartChat(p.Text, false); e.Handled = true; }
        };

        return Ui.WideCard(
            Ui.Sub("定型文 (よく使う文字を保存して呼び出し・キーで送信)"),
            Ui.Row(Ui.L("名前"), _txtChatPresetName, Ui.L("キー"), _hkChatPreset, save, update),
            Ui.Row(load, send, del, up, down),
            Ui.Stretch(Ui.SizedHost(_lstChatPresets, 430, 130)));
    }

    private void LoadChatPreset()
    {
        if (_lstChatPresets.SelectedItem is not ChatPreset p) return;
        _txtChat.Text = (p.Text ?? "").Replace("\n", "\r\n");
        _txtChat.Focus();
        _txtChat.SelectionStart = _txtChat.TextLength;
    }

    private void RemoveChatPreset()
    {
        if (_lstChatPresets.SelectedItem is not ChatPreset p) return;
        int i = _lstChatPresets.SelectedIndex;
        _lstChatPresets.Items.Remove(p);
        if (_lstChatPresets.Items.Count > 0) _lstChatPresets.SelectedIndex = Math.Min(i, _lstChatPresets.Items.Count - 1);
        RegisterHotkeys();
    }

    private void MoveChatPreset(int d)
    {
        int i = _lstChatPresets.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= _lstChatPresets.Items.Count) return;
        var p = _lstChatPresets.Items[i];
        _lstChatPresets.Items.RemoveAt(i);
        _lstChatPresets.Items.Insert(j, p);
        _lstChatPresets.SelectedIndex = j;
    }

    private List<ChatPreset> ChatPresetList() =>
        _lstChatPresets?.Items.Cast<ChatPreset>().ToList() ?? _s.ChatPresets;

    /// <summary>チャットのキー (カテゴリ ON/OFF・定型文) を登録</summary>
    private void RegisterChatHotkeys(List<string> fails)
    {
        if (_s.CategoryHotkeys.TryGetValue(ChatCategory, out var text))
            Reg(HK_CHAT, Hotkey.Parse(text), "カテゴリ:" + ChatCategory, fails);
        _hotkeyChatPresets.Clear();
        foreach (var p in ChatPresetList())
        {
            var hk = Hotkey.Parse(p.Hotkey);
            if (hk.IsEmpty) continue;
            int id = HK_CHATPRESET_BASE + _hotkeyChatPresets.Count;
            _hotkeyChatPresets.Add(p);
            Reg(id, hk, "定型文:" + DisplayName(p), fails);
        }
    }

    /// <summary>チャットのキーなら処理して true</summary>
    private bool HandleChatHotkey(int id)
    {
        if (id == HK_CHAT) { ToggleChat("キー"); return true; }
        int i = id - HK_CHATPRESET_BASE;
        if (i >= 0 && i < _hotkeyChatPresets.Count)
        {
            var p = _hotkeyChatPresets[i];
            OpLog.Write($"⌨ 定型文を送信 (キー): {DisplayName(p)}");
            StartChat(p.Text, false);
            return true;
        }
        return false;
    }
}
