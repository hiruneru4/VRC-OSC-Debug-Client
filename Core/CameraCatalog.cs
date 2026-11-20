namespace VRCOscDebugClient.Core;

public sealed record CamToggle(string Id, string Name);
public sealed record CamSlider(string Id, string Name, float Min, float Max, float Default, int Decimals);

/// <summary>
/// VRChat 公式カメラ (/usercamera/...) とカメラドリー (/dolly/...) の OSC。
/// 出典: VRChat 2025.3.3 パッチノート (docs.vrchat.com) / VRChat Wiki (Dolly)。
/// カメラは VRChat 側で開いている時だけ操作できる (OSC ではカメラを開けない)。
/// </summary>
public static class CameraCatalog
{
    public const string Cam = "/usercamera/";
    public const string Mode = Cam + "Mode";
    public const string Pose = Cam + "Pose";
    public const string Close = Cam + "Close";
    public const string Capture = Cam + "Capture";
    public const string CaptureDelayed = Cam + "CaptureDelayed";

    public const string DollyPlay = "/dolly/Play";
    public const string DollyPlayDelayed = "/dolly/PlayDelayed";
    public const string DollyExport = "/dolly/Export";
    public const string DollyImport = "/dolly/Import";

    public static readonly string[] Modes = { "オフ", "写真", "ストリーム", "エモート", "マルチレイヤー", "プリント", "ドローン" };

    public static readonly CamToggle[] Toggles =
    {
        new("ShowUIInCamera", "UI を写す"),
        new("Lock", "カメラをロック"),
        new("LocalPlayer", "自分を写す"),
        new("RemotePlayer", "他の人を写す"),
        new("Environment", "背景を写す"),
        new("GreenScreen", "グリーンスクリーン"),
        new("SmoothMovement", "スムーズ移動"),
        new("LookAtMe", "ルックアットミー"),
        new("AutoLevelRoll", "自動水平 (ロール)"),
        new("AutoLevelPitch", "自動水平 (ピッチ)"),
        new("Flying", "飛行"),
        new("TriggerTakesPhotos", "トリガーで撮影"),
        new("DollyPathsStayVisible", "ドリー経路を表示"),
        new("CameraEars", "カメラ位置の音を聞く"),
        new("ShowFocus", "フォーカス表示"),
        new("Streaming", "Spout 配信"),
        new("RollWhileFlying", "飛行中にロール"),
        new("OrientationIsLandscape", "横向き"),
    };

    public static readonly CamSlider[] Sliders =
    {
        new("Zoom", "ズーム", 20f, 150f, 45f, 0),
        new("Exposure", "露出", -10f, 4f, 0f, 1),
        new("FocalDistance", "焦点距離", 0f, 10f, 1.5f, 2),
        new("Aperture", "絞り", 1.4f, 32f, 15f, 1),
        new("Hue", "GS 色相", 0f, 360f, 120f, 0),
        new("Saturation", "GS 彩度", 0f, 100f, 100f, 0),
        new("Lightness", "GS 明度", 0f, 100f, 60f, 0),
        new("LookAtMeXOffset", "ルックアットミー 横", -25f, 25f, 0f, 1),
        new("LookAtMeYOffset", "ルックアットミー 縦", -25f, 25f, 0f, 1),
        new("FlySpeed", "飛行速度", 0.1f, 15f, 3f, 1),
        new("TurnSpeed", "回転速度", 0.1f, 5f, 1f, 1),
        new("SmoothingStrength", "スムーズの強さ", 0.1f, 10f, 5f, 1),
        new("PhotoRate", "ドリー撮影間隔", 0.1f, 2f, 1f, 2),
        new("Duration", "ドリー時間 (秒)", 0.1f, 60f, 2f, 1),
    };

    /// <summary>マクロの対象一覧用 (名前, アドレス, 型)</summary>
    public static List<(string Name, string Address, string Type)> MacroTargets()
    {
        var list = new List<(string, string, string)>
        {
            ("撮影", Capture, "Bool"),
            ("タイマー撮影", CaptureDelayed, "Bool"),
            ("カメラを閉じる", Close, "Bool"),
            ("モード (0〜6)", Mode, "Int"),
            ("ドリー 再生/停止", DollyPlay, "Bool"),
            ("ドリー 遅延再生 (秒)", DollyPlayDelayed, "Float"),
        };
        foreach (var t in Toggles) list.Add((t.Name, Cam + t.Id, "Bool"));
        foreach (var sl in Sliders) list.Add(($"{sl.Name} ({sl.Min:0.#}〜{sl.Max:0.#})", Cam + sl.Id, "Float"));
        return list;
    }
}
