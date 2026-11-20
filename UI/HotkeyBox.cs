using VRCOscDebugClient.Core;

namespace VRCOscDebugClient.UI;

/// <summary>クリックしてキーを押すとショートカットを登録する入力欄 (Back / Delete / Esc でクリア)</summary>
public sealed class HotkeyBox : TextBox
{
    /// <summary>入力中は全体ホットキーを一時解除するための通知</summary>
    public static event Action<bool> CapturingChanged;
    public static bool Capturing { get; private set; }

    private Hotkey _hotkey;
    public event EventHandler HotkeyChanged;

    public Hotkey Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; Text = Display(value); }
    }

    public HotkeyBox()
    {
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        Text = Display(default);
    }

    private static string Display(Hotkey h) => h.IsEmpty ? "(なし)" : h.ToString();

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        Capturing = true;
        Text = "キーを押してください";
        CapturingChanged?.Invoke(true);
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        Text = Display(_hotkey);
        Capturing = false;
        CapturingChanged?.Invoke(false);
    }

    protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
    {
        e.IsInputKey = true;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        var k = e.KeyCode;

        if (k is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
            or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu)
        {
            Text = Hotkey.ModifierText(e.Modifiers) + "+ …";
            return;
        }

        if (e.Modifiers == Keys.None && (k is Keys.Back or Keys.Delete or Keys.Escape))
            Hotkey = default;
        else
            Hotkey = new Hotkey(k, e.Modifiers);

        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Modifiers == Keys.None) Text = Display(_hotkey);
    }

    protected override void OnKeyPress(KeyPressEventArgs e) => e.Handled = true;
}
