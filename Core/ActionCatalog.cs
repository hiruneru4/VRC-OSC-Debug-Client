namespace VRCOscDebugClient.Core;

public enum ActionKind { Button, Axis }

public sealed record OscActionDef(
    string Id, string Name, string Address, ActionKind Kind, string Category, string Tip, float DefaultValue);

/// <summary>VRChat OSC /input/ で操作できる全項目</summary>
public static class ActionCatalog
{
    public const string CatMove = "移動";
    public const string CatLook = "視点";
    public const string CatHand = "手・アイテム";
    public const string CatSystem = "システム";

    public static readonly string[] Categories = { CatMove, CatLook, CatHand, CatSystem };

    public static readonly Dictionary<string, string> Hints = new()
    {
        [CatMove] = "軸は -1〜1 (前後/左右)。ボタンと同時実行可",
        [CatLook] = "Comfort はVRのスナップターン",
        [CatHand] = "保持系の軸は物を掴んでいる時に有効",
        [CatSystem] = "マイクはVRChat側のトグル/PTT設定に依存",
    };

    public static readonly IReadOnlyList<OscActionDef> All = new List<OscActionDef>
    {
        // ---- 移動 ----
        B(CatMove, "MoveForward",  "前進",        "W キー相当"),
        B(CatMove, "MoveBackward", "後退",        "S キー相当"),
        B(CatMove, "MoveLeft",     "左移動",      "A キー相当"),
        B(CatMove, "MoveRight",    "右移動",      "D キー相当"),
        B(CatMove, "Run",          "ダッシュ",    "Shift 相当 (移動と同時に使用)"),
        B(CatMove, "Jump",         "ジャンプ",    "Space 相当"),
        A(CatMove, "Vertical",     "前後軸",      "1=前 / -1=後 (速度調整可)", 1f),
        A(CatMove, "Horizontal",   "左右軸",      "1=右 / -1=左 (速度調整可)", 1f),

        // ---- 視点 ----
        B(CatLook, "LookLeft",     "左を向く",    "左回転"),
        B(CatLook, "LookRight",    "右を向く",    "右回転"),
        B(CatLook, "ComfortLeft",  "スナップ左",  "VR時のスナップターン左"),
        B(CatLook, "ComfortRight", "スナップ右",  "VR時のスナップターン右"),
        A(CatLook, "LookHorizontal","視点左右軸", "1=右回転 / -1=左回転", 0.5f),

        // ---- 手・アイテム ----
        B(CatHand, "UseRight",     "右手 使う",   "Use (トリガー)"),
        B(CatHand, "GrabRight",    "右手 掴む",   "Grab"),
        B(CatHand, "DropRight",    "右手 離す",   "Drop"),
        B(CatHand, "UseLeft",      "左手 使う",   "Use (トリガー)"),
        B(CatHand, "GrabLeft",     "左手 掴む",   "Grab"),
        B(CatHand, "DropLeft",     "左手 離す",   "Drop"),
        A(CatHand, "UseAxisRight", "右 使う軸",   "トリガーのアナログ値", 1f),
        A(CatHand, "GrabAxisRight","右 掴む軸",   "グリップのアナログ値", 1f),
        A(CatHand, "MoveHoldFB",   "保持物 前後", "掴んだ物を前後に移動", 1f),
        A(CatHand, "SpinHoldCwCcw","保持物 回転", "時計/反時計回り", 1f),
        A(CatHand, "SpinHoldUD",   "保持物 上下", "上下に回転", 1f),
        A(CatHand, "SpinHoldLR",   "保持物 左右", "左右に回転", 1f),

        // ---- システム ----
        B(CatSystem, "Voice",               "マイク",       "トグル設定: 0→1でミュート切替 / PTT: 押している間ON"),
        B(CatSystem, "QuickMenuToggleLeft", "QM開閉 (左)",  "クイックメニュー開閉"),
        B(CatSystem, "QuickMenuToggleRight","QM開閉 (右)",  "クイックメニュー開閉"),
        B(CatSystem, "PanicButton",         "セーフモード", "⚠ 周囲のアバターを全て非表示化 (パニック)"),
    };

    private static readonly Dictionary<string, OscActionDef> _byId =
        All.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

    public static OscActionDef Find(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : _byId.TryGetValue(id.Trim(), out var d) ? d : null;

    private static OscActionDef B(string cat, string id, string name, string tip) =>
        new(id, name, "/input/" + id, ActionKind.Button, cat, tip, 1f);

    private static OscActionDef A(string cat, string id, string name, string tip, float def) =>
        new(id, name, "/input/" + id, ActionKind.Axis, cat, tip, def);
}
