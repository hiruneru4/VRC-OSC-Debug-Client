namespace VRCOscDebugClient.Core;

/// <summary>操作ログ (どのスレッドからでも書き込み可。表示側は ActivityPanel)</summary>
public static class OpLog
{
    public static event Action<string> Written;

    public static void Write(string text) => Written?.Invoke(Translate(text));

    /// <summary>
    /// 英語表示の時はログも訳す。チャットの文字 (💬) は「: 」より後ろを訳さない (ユーザーの文字なので)
    /// </summary>
    private static string Translate(string text)
    {
        if (!L10n.En || string.IsNullOrEmpty(text)) return text;
        int i = text.IndexOf(": ", StringComparison.Ordinal);
        if ((text.StartsWith("💬") || text[..Math.Max(0, i)].Contains("チャット")) && i > 0) return L10n.Auto(text[..(i + 2)]) + text[(i + 2)..];
        return L10n.Auto(text);
    }
}
