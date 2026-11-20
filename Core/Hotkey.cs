namespace VRCOscDebugClient.Core;

/// <summary>ショートカットキー (修飾キー + キー)。文字列 "Ctrl+Alt+F12" 形式で保存</summary>
public readonly struct Hotkey
{
    public Keys Key { get; }
    public Keys Modifiers { get; }

    public Hotkey(Keys key, Keys modifiers)
    {
        Key = key;
        Modifiers = modifiers & (Keys.Control | Keys.Alt | Keys.Shift);
    }

    public bool IsEmpty => Key == Keys.None;
    public bool Ctrl => (Modifiers & Keys.Control) != 0;
    public bool Alt => (Modifiers & Keys.Alt) != 0;
    public bool Shift => (Modifiers & Keys.Shift) != 0;

    /// <summary>RegisterHotKey 用の修飾フラグ</summary>
    public uint Win32Modifiers => (Alt ? 1u : 0u) | (Ctrl ? 2u : 0u) | (Shift ? 4u : 0u);

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static string ModifierText(Keys mods)
    {
        var parts = new List<string>();
        if ((mods & Keys.Control) != 0) parts.Add("Ctrl");
        if ((mods & Keys.Alt) != 0) parts.Add("Alt");
        if ((mods & Keys.Shift) != 0) parts.Add("Shift");
        return string.Join("+", parts);
    }

    private static string KeyName(Keys k)
    {
        if (k >= Keys.D0 && k <= Keys.D9) return ((int)k - (int)Keys.D0).ToString();
        return k.ToString();
    }

    public static Hotkey Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        Keys mods = Keys.None, key = Keys.None;
        foreach (var raw in text.Split('+'))
        {
            var p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= Keys.Control; break;
                case "alt": mods |= Keys.Alt; break;
                case "shift": mods |= Keys.Shift; break;
                default:
                    if (p.Length == 1 && char.IsDigit(p[0])) key = (Keys)((int)Keys.D0 + (p[0] - '0'));
                    else if (Enum.TryParse<Keys>(p, true, out var k)) key = k;
                    break;
            }
        }
        return new Hotkey(key, mods);
    }
}
