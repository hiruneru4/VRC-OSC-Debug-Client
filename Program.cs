using VRCOscDebugClient.Core;
using VRCOscDebugClient.UI;

namespace VRCOscDebugClient;

internal static class Program
{
    /// <summary>設定の「適用して再起動」の時に付ける印 (起動時の確認を出さない)</summary>
    public const string RestartFlag = "VRC_OSC_DEBUG_CLIENT_RESTART";

    private const string StartupNoticeJa =
        "このツールはあくまでもVRChatのOSCを利用してのデバッグ等を行なうために作成されました。\n" +
        "全て公式のOSCを使用しての機能となっていますので非公式な仕組み等は利用していません。\n" +
        "何かトラブル等が発生しても自己責任でお願い致します。";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        AppSettings.MigrateOldFolders(); // 以前の名前 (VRCOscTool) の保存フォルダを引き継ぐ
        var s = AppSettings.Load();
        L10n.Init(s.Language);
        Theme.Dark = s.Dark;

        // 起動時の確認: はい → 起動 / いいえ → 終了
        bool restarted = Environment.GetEnvironmentVariable(RestartFlag) == "1";
        Environment.SetEnvironmentVariable(RestartFlag, null);
        if (!restarted)
        {
            float zoom = Math.Clamp(s.UiZoom, 70, 130) / 100f;
            Font font;
            try { font = new Font(s.FontName, Math.Clamp(s.FontSize, 7f, 16f) * zoom); }
            catch { font = SystemFonts.MessageBoxFont; }
            bool ok = ConfirmDialog.Ask(null, MainForm.AppName, L10n.Auto(StartupNoticeJa),
                L10n.T("はい", "Yes"), L10n.T("いいえ", "No"), warning: false, font: font);
            if (!ok) return;
        }

        Application.Run(new MainForm());
    }
}
