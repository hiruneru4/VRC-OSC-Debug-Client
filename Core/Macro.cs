namespace VRCOscDebugClient.Core;

public enum MacroStepKind
{
    Press = 0,          // 押す (押しっぱなし)
    Release = 1,        // 離す
    Tap = 2,            // 1回押す
    Hold = 3,           // 長押し (ms 後に離す)
    Rapid = 4,          // 連打
    Wait = 5,           // 待機
    Chat = 6,           // チャット送信
    Send = 7,           // 値を送信 (離さない)
    ReleaseAll = 8,     // このマクロで押したものを全て離す
    WaitParallel = 9,   // 並行実行中のステップが全て終わるまで待つ
    MacroRun = 10,      // 別のマクロを実行
    MacroStop = 11,     // 別のマクロを停止
    MacroPause = 12,    // 別のマクロを一時停止
    MacroResume = 13,   // 別のマクロを再開
    Interrupt = 14,     // 割込み実行: 他のマクロを一時停止 → 対象を実行 → 再開
    Category = 15,      // カテゴリの ON / OFF / 切替
}

public sealed class MacroStep
{
    public const string AllMacros = "*";
    public const string CustomPrefix = "custom:";

    public MacroStepKind Kind { get; set; }
    /// <summary>
    /// 対象: 操作ID (MoveForward 等) / OSC アドレス (/avatar/parameters/...) /
    /// カスタムプリセット ("custom:名前") / マクロ名 / カテゴリ名
    /// </summary>
    public string Target { get; set; } = "";
    public string Type { get; set; } = "Bool";
    public string Value { get; set; } = "";
    public int Ms { get; set; } = 100;
    public int Count { get; set; } = 1;
    /// <summary>true = 終わるのを待たずに次のステップへ進む (並行実行)</summary>
    public bool Parallel { get; set; }

    public MacroStep Clone() => (MacroStep)MemberwiseClone();

    public static readonly string[] KindNames =
    {
        "押す", "離す", "1回押す", "長押し", "連打", "待機", "チャット", "値を送信", "全て離す", "並行待ち",
        "マクロ実行", "マクロ停止", "マクロ一時停止", "マクロ再開", "割込み実行", "カテゴリ操作"
    };

    public static bool UsesTarget(MacroStepKind k) =>
        k is MacroStepKind.Press or MacroStepKind.Release or MacroStepKind.Tap or MacroStepKind.Hold
          or MacroStepKind.Rapid or MacroStepKind.Send or MacroStepKind.Category || IsMacroKind(k);

    public static bool IsMacroKind(MacroStepKind k) =>
        k is MacroStepKind.MacroRun or MacroStepKind.MacroStop or MacroStepKind.MacroPause
          or MacroStepKind.MacroResume or MacroStepKind.Interrupt;

    public static bool IsInputKind(MacroStepKind k) =>
        k is MacroStepKind.Press or MacroStepKind.Release or MacroStepKind.Tap or MacroStepKind.Hold
          or MacroStepKind.Rapid or MacroStepKind.Send;

    /// <summary>並行実行を選べる種類 (時間のかかるもの)</summary>
    public static bool CanParallel(MacroStepKind k) =>
        k is MacroStepKind.Tap or MacroStepKind.Hold or MacroStepKind.Rapid or MacroStepKind.Wait
          or MacroStepKind.MacroRun or MacroStepKind.Interrupt;

    public string Describe()
    {
        string name = TargetName(Target);
        string val = string.IsNullOrWhiteSpace(Value) ? "" : $" = {Value}";
        string kind = KindNames[(int)Kind];
        string macro = Target == AllMacros ? "全マクロ (自分以外)" : $"「{Target}」";
        string text = Kind switch
        {
            MacroStepKind.Press => $"{kind}　{name}{val}",
            MacroStepKind.Release => $"{kind}　{name}",
            MacroStepKind.Tap => $"{kind}　{name}{val}　({Ms}ms)",
            MacroStepKind.Hold => $"{kind}　{name}{val}　{Ms}ms",
            MacroStepKind.Rapid => $"{kind}　{name}{val}　×{(Count <= 0 ? "∞" : Count.ToString())}　間隔{Ms}ms",
            MacroStepKind.Wait => $"{kind}　{Ms}ms",
            MacroStepKind.Chat => $"{kind}　「{Value}」",
            MacroStepKind.Send => $"{kind}　{name}{val}" + (Target.StartsWith("/") ? $" ({Type})" : ""),
            MacroStepKind.ReleaseAll => kind,
            MacroStepKind.WaitParallel => $"{kind}　(並行中のステップの終了を待つ)",
            MacroStepKind.MacroRun => $"{kind}　{macro}" + (Parallel ? "" : "　(終わるまで待つ)"),
            MacroStepKind.MacroStop => $"{kind}　{macro}",
            MacroStepKind.MacroPause => $"{kind}　{macro}",
            MacroStepKind.MacroResume => $"{kind}　{macro}",
            MacroStepKind.Interrupt => $"{kind}　{macro}　(他を一時停止 → 実行 → 再開)",
            MacroStepKind.Category => $"{kind}　{Target}　{(string.IsNullOrWhiteSpace(Value) ? "切替" : Value)}",
            _ => kind
        };
        return Parallel && CanParallel(Kind) ? "⇉ " + text + "　[並行]" : text;
    }

    public static string TargetName(string target)
    {
        if (string.IsNullOrEmpty(target)) return "";
        var def = ActionCatalog.Find(target);
        if (def != null) return def.Name;
        if (target.StartsWith(CustomPrefix)) return "[C] " + target.Substring(CustomPrefix.Length);
        if (target.StartsWith("/avatar/parameters/")) return "[P] " + target.Substring("/avatar/parameters/".Length);
        if (target.StartsWith(CameraCatalog.Cam)) return "[Cam] " + target.Substring(CameraCatalog.Cam.Length);
        if (target.StartsWith("/dolly/")) return "[Cam] " + target.Substring(1);
        return target;
    }
}

public sealed class Macro
{
    public string Name { get; set; } = "新しいマクロ";
    /// <summary>繰り返し回数 (0 = 停止するまで無限)</summary>
    public int Repeat { get; set; } = 1;
    public string Hotkey { get; set; } = "";
    public List<MacroStep> Steps { get; set; } = new();

    public Macro Clone() => new()
    {
        Name = Name, Repeat = Repeat, Hotkey = Hotkey,
        Steps = Steps.Select(s => s.Clone()).ToList()
    };

    public override string ToString() =>
        string.IsNullOrEmpty(Hotkey) ? Name : $"{Name}  [{Hotkey}]";
}

/// <summary>マクロから他のマクロ・カテゴリ・プリセットを操作するための窓口 (どのスレッドからでも呼べる)</summary>
public interface IMacroHost
{
    Task RunMacroAsync(string name, bool wait, MacroRunner caller, CancellationToken ct);
    void StopMacro(string name, MacroRunner caller);
    void PauseMacro(string name, bool pause, MacroRunner caller);
    Task InterruptAsync(string name, MacroRunner caller, CancellationToken ct);
    void CategoryCommand(string category, string command);
    CustomPreset FindCustom(string name);
}
