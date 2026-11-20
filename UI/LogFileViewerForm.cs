using VRCOscDebugClient.Core;

namespace VRCOscDebugClient.UI;

/// <summary>保存したログ (記録した txt など) を開いて見るビューアー</summary>
public sealed class LogFileViewerForm : Form
{
    private readonly ToolTip _tip = new() { InitialDelay = 400, AutoPopDelay = 20000 };
    private readonly LogViewer _viewer;

    public LogFileViewerForm(AppSettings s, Font font, string path)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = font;
        Text = "ログビューアー";
        ClientSize = new Size(Ui.W(820), Ui.W(560));
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(6);

        _viewer = new LogViewer(s, _tip, live: false);
        Controls.Add(_viewer);
        ResumeLayout(false);
        PerformLayout();

        Theme.Apply(this);
        Shown += (_, _) =>
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _viewer.LoadFile(path);
                Text = "ログビューアー - " + Path.GetFileName(path);
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int v = Theme.Dark ? 1 : 0;
            NativeDwm.SetAttribute(Handle, 20, v); // ダークのタイトルバー
            NativeDwm.SetAttribute(Handle, 33, 2); // 角丸 (Win11)
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>DWM (タイトルバーの色・角丸) の設定</summary>
public static class NativeDwm
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void SetAttribute(IntPtr hwnd, int attr, int value) =>
        DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int));
}
