using System.Globalization;

namespace VRCOscDebugClient.Core;

/// <summary>文字列と型名 (Bool / Int / Float / String) から OSC の値を作る</summary>
public static class OscValue
{
    public static readonly string[] Types = { "Bool", "Int", "Float", "String" };

    public static bool TryParse(string type, string text, out object value)
    {
        text ??= "";
        switch (type)
        {
            case "Bool":
                var t = text.Trim().ToLowerInvariant();
                value = t is "true" or "1" or "on" or "t" or "yes";
                return true;
            case "Int":
                if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { value = i; return true; }
                break;
            case "Float":
                if (float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) { value = f; return true; }
                break;
            default:
                value = text;
                return true;
        }
        value = null;
        return false;
    }

    /// <summary>「離す」時に送る値</summary>
    public static object Zero(string type) => type switch
    {
        "Bool" => false,
        "Int" => (object)0,
        "Float" => 0f,
        _ => ""
    };
}
