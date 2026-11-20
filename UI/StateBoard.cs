using System.Drawing.Drawing2D;
using VRCOscDebugClient.Core;

namespace VRCOscDebugClient.UI;

/// <summary>
/// 右上の現在の状態表示 (2 行 × 3 列、値はそれぞれ 2 行)。
/// 移動 (向き・速さ) / 状態 / マイク / 身長 / ジェスチャー (右・左) / AFK
/// 表示内容が変わった時だけ描き直す。
/// 1 つのコントロールで描くので軽い。
/// </summary>
public sealed class StateBoard : Control
{
    private VrcSnapshot _state = new();
    private bool _connected;
    private (string Label, string Value, Color Accent, int Span)[] _chips;
    private string _lastKey = "";
    private Font _small;

    public StateBoard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Margin = new Padding(6, 4, 6, 2);
    }

    private float D => DeviceDpi / 96f;
    private int LineH => TextRenderer.MeasureText("あAg", Font).Height;
    private int ChipH => LineH * 2 + (int)(8 * D); // 値は 2 行

    public override Size GetPreferredSize(Size proposedSize) =>
        new((int)(Ui.W(430) * D), ChipH * 2 + (int)(5 * D));

    /// <summary>目の高さ (m)。NaN = 不明</summary>
    private float _height = float.NaN, _heightOrig = float.NaN;

    /// <summary>表示する内容が変わった時だけ描き直す (毎回は描かない)</summary>
    public void SetState(VrcSnapshot s, bool connected, float height = float.NaN, float heightOrig = float.NaN)
    {
        _state = s;
        _connected = connected;
        _height = height;
        _heightOrig = heightOrig;
        var chips = Chips();
        var key = connected + "|" + string.Join("|", chips.Select(c => c.Value + "#" + c.Accent.ToArgb()));
        if (key == _lastKey) return;
        _lastKey = key;
        _chips = chips;
        Invalidate();
    }

    private Font SmallFont => _small ??= new Font(Font.FontFamily, Math.Max(7f, Font.Size * 0.82f));

    protected override void OnFontChanged(EventArgs e)
    {
        _small?.Dispose();
        _small = null;
        base.OnFontChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _small?.Dispose();
        base.Dispose(disposing);
    }

    // 並び: 1 行目 = 移動・状態・マイク / 2 行目 = 身長・ジェスチャー・AFK (値はそれぞれ 2 行)
    private (string Label, string Value, Color Accent, int Span)[] Chips()
    {
        var s = _state;
        Color good = Theme.Active, bad = Theme.Danger, info = Theme.Accent, none = Theme.Off;

        string dir = VrcMonitor.Direction(s.VelX, s.VelZ);
        string move = s.Motion == "?" ? "?" : s.Motion == "停止" ? $"停止\n{s.Speed:0.0} m/s" : $"{s.Motion}　{dir}\n{s.Speed:0.0} m/s";
        Color moveC = s.Motion is "歩き" or "走り" ? good : s.Motion == "停止" ? info : none;

        string pose = s.InStation == 1 ? "ステーション" : s.Seated == 1 ? "座り" :
            s.Grounded == 0 ? (s.VelY > 0.3f ? "ジャンプ中" : "空中") : s.Grounded == 1 ? "立ち" : "?";
        string mode = s.Vr == 1 ? "VR" : s.Vr == 0 ? "デスクトップ" : "";
        Color poseC = pose == "?" ? none : info;
        pose += "\n" + mode;

        string mic = s.Mute == 1 ? "ミュート" : s.Mute == 0 ? "ON" : "?";
        if (s.Mute == 0) mic += "\n" + (s.Talking ? "話し中" : "");
        Color micC = s.Mute == 1 ? bad : s.Mute == 0 ? good : none;

        string height = float.IsNaN(_height) ? "?" : $"{_height:0.00} m";
        if (!float.IsNaN(_height) && !float.IsNaN(_heightOrig) && Math.Abs(_height - _heightOrig) > 0.005f)
            height += $"\n元 {_heightOrig:0.00} m";
        else if (!float.IsNaN(_height)) height += "\n目の高さ";
        Color heightC = float.IsNaN(_height) ? none : info;

        // 上に右手、下に左手
        string gest = s.GestureL < 0 && s.GestureR < 0 ? "?"
            : $"右 {GestureName(s.GestureR)}\n左 {GestureName(s.GestureL)}";
        Color gestC = gest == "?" ? none : info;

        string afk = s.Afk == 1 ? $"AFK 中\n{(int)(DateTime.Now - s.AfkSince).TotalMinutes}分" : s.Afk == 0 ? "AFKしていない" : "?";
        Color afkC = s.Afk == 1 ? bad : s.Afk == 0 ? good : none;

        return new[]
        {
            ("移動", move, moveC, 1), ("状態", pose, poseC, 1), ("マイク", mic, micC, 1),
            ("身長", height, heightC, 1), ("ジェスチャー", gest, gestC, 1), ("AFK", afk, afkC, 1),
        };
    }

    private static string GestureName(int g) =>
        g >= 0 && g < VrcMonitor.GestureNames.Length ? VrcMonitor.GestureNames[g] : "?";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var chips = _chips ?? Chips();
        const int cols = 3, rows = 2;
        float gap = 5 * D;
        float cw = (Width - gap * (cols - 1)) / cols;
        float ch = (Height - gap * (rows - 1)) / rows;
        var small = SmallFont;
        const TextFormatFlags tf = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                   TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left;

        int col = 0, row = 0;
        foreach (var (label, value, accent, span) in chips)
        {
            if (col + span > cols) { col = 0; row++; }
            float x = col * (cw + gap), y = row * (ch + gap);
            float w = cw * span + gap * (span - 1);
            col += span;

            var r = new RectangleF(x + 0.5f, y + 0.5f, w - 1, ch - 1);
            using (var path = Rounded.Path(r, 7 * D))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(Rounded.Blend(Theme.Border, Theme.Card, 0.3f))) g.DrawPath(p, path);
            }
            using (var ab = new SolidBrush(_connected ? accent : Theme.Off))
                g.FillRectangle(ab, x + 4 * D, y + ch * 0.25f, 3 * D, ch * 0.5f);

            int lx = (int)(x + 11 * D);
            string lab = L10n.Auto(label);
            int labelW = TextRenderer.MeasureText(lab, small).Width;
            TextRenderer.DrawText(g, lab, small, new Rectangle(lx, (int)y, labelW + 2, (int)ch), Theme.SubFore, tf);

            // 値は 2 行 (1 行目を太く、2 行目は補助色)
            int vx = lx + labelW + (int)(5 * D);
            int vw = (int)(x + w - vx - 4 * D);
            var lines = (_connected ? L10n.Auto(value) : "-").Split('\n');
            int lh = LineH;
            bool two = lines.Length > 1 && lines[1].Trim().Length > 0;
            if (!two)
            {
                // 2 行目が無い時は 1 行を上下中央に
                TextRenderer.DrawText(g, lines[0], Font, new Rectangle(vx, (int)y, vw, (int)ch), Theme.Fore, tf);
                continue;
            }
            int top = (int)(y + (ch - lh * 2) / 2);
            // ジェスチャーは右手・左手とも同じ扱い (どちらもメインの文字)
            bool same = label == "ジェスチャー";
            TextRenderer.DrawText(g, lines[0], Font, new Rectangle(vx, top, vw, lh), Theme.Fore, tf);
            TextRenderer.DrawText(g, lines[1], same ? Font : small, new Rectangle(vx, top + lh, vw, lh),
                same ? Theme.Fore : Theme.SubFore, tf);
        }
    }
}
