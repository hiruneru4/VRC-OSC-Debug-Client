using System.Text;

namespace VRCOscDebugClient.Core;

/// <summary>
/// 表示言語 (日本語 / 英語)。
/// 画面の文字は日本語で書いてあり、英語の時だけ Lang\en.txt (日本語 [Tab] 英語) で置き換える。
/// 1) 文字列が丸ごと一致すれば、その訳
/// 2) 一致しなければ、含まれている語句を長い順に置き換える (数値などが入った文字用)
/// </summary>
public static class L10n
{
    public const string Japanese = "ja", English = "en";

    public static bool En { get; private set; }
    public static string Language => En ? English : Japanese;

    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static (string Ja, string En)[] _frags = Array.Empty<(string, string)>();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Lang", "en.txt");

    public static void Init(string lang)
    {
        En = string.Equals(lang, English, StringComparison.OrdinalIgnoreCase);
        if (!En) return;
        try
        {
            if (!File.Exists(FilePath)) { En = false; return; }
            foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw.StartsWith("#")) continue;
                int tab = raw.IndexOf('\t');
                if (tab <= 0) continue;
                string ja = Unescape(raw[..tab]), en = Unescape(raw[(tab + 1)..]);
                if (ja.Length == 0) continue;
                Exact[ja] = en;
            }
            _frags = Exact.Where(kv => HasJapanese(kv.Key))
                          .OrderByDescending(kv => kv.Key.Length)
                          .Select(kv => (kv.Key, kv.Value)).ToArray();
        }
        catch { En = false; }
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");

    public static bool HasJapanese(string s)
    {
        foreach (char c in s)
            if (c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or >= '！' and <= '｠' or '　' or '「' or '」' or '、' or '。')
                return true;
        return false;
    }

    /// <summary>表示用の文字を今の言語にする (日本語の時はそのまま)</summary>
    public static string Auto(string s)
    {
        if (!En || string.IsNullOrEmpty(s) || !HasJapanese(s)) return s;
        lock (Gate)
        {
            if (Cache.TryGetValue(s, out var hit)) return hit;
            string r = Translate(s);
            if (Cache.Count > 6000) Cache.Clear();
            Cache[s] = r;
            return r;
        }
    }

    /// <summary>日本語と英語で書き分ける (コード中で直接使う時)</summary>
    public static string T(string ja, string en) => En ? en : ja;

    private static string Translate(string s)
    {
        if (Exact.TryGetValue(s, out var en)) return en;

        // 前後の空白・記号だけ違う場合
        string trimmed = s.Trim();
        if (trimmed.Length != s.Length && Exact.TryGetValue(trimmed, out en))
            return s[..s.IndexOf(trimmed, StringComparison.Ordinal)] + en + s[(s.IndexOf(trimmed, StringComparison.Ordinal) + trimmed.Length)..];

        // 語句ごとに置き換え (境目は \u0001 で印を付け、最後に空白を整える)
        var sb = new StringBuilder(s);
        foreach (var (ja, e) in _frags)
        {
            if (sb.Length < ja.Length) continue;
            if (sb.ToString().IndexOf(ja, StringComparison.Ordinal) < 0) continue;
            sb.Replace(ja, "\u0001" + e + "\u0001");
        }
        return Tidy(sb.ToString());
    }

    private static string Tidy(string s)
    {
        if (s.IndexOf('\u0001') < 0) return s.Replace('　', ' ');
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\u0001') { sb.Append(c == '　' ? ' ' : c); continue; }
            char prev = sb.Length > 0 ? sb[^1] : ' ';
            int j = i + 1;
            while (j < s.Length && s[j] == '\u0001') j++;
            char next = j < s.Length ? s[j] : ' ';
            if (NeedsSpace(prev) && NeedsSpaceAfter(next)) sb.Append(' ');
            i = j - 1;
        }
        return sb.ToString();
    }

    private static bool NeedsSpace(char c) => char.IsLetterOrDigit(c) || c is ')' or ']' or '%' or '»' or '"' or '.' or ',' or ':';
    private static bool NeedsSpaceAfter(char c) => char.IsLetterOrDigit(c) || c is '(' or '[' or '«' or '"';
}
