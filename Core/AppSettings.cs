using System.Text.Json;

namespace VRCOscDebugClient.Core;

public sealed class RowSetting
{
    public int Mode { get; set; }
    public decimal Value { get; set; }
    public decimal Time { get; set; }
    public decimal Count { get; set; }
    public bool Marked { get; set; }
}

public sealed class CustomPreset
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Type { get; set; } = "Bool";
    public string Value { get; set; } = "true";
    public string ResetValue { get; set; } = "false";
    public override string ToString() => $"{Name}  ({Address})";
}

/// <summary>チャットの送信履歴 1 件</summary>
public sealed class ChatEntry
{
    public DateTime Time { get; set; }
    public string Text { get; set; } = "";

    public override string ToString() =>
        (Time == default ? "--/-- --:--:--" : Time.ToString("MM/dd HH:mm:ss")) + "   " + (Text ?? "").Replace("\n", " ⏎ ");
}

/// <summary>チャットの定型文 (保存して呼び出す・キーで送信)</summary>
public sealed class ChatPreset
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public string Hotkey { get; set; } = "";

    public override string ToString()
    {
        string key = string.IsNullOrEmpty(Hotkey) ? "" : $"  [{Hotkey}]";
        string body = (Text ?? "").Replace("\n", " ⏎ ");
        return string.IsNullOrWhiteSpace(Name) ? body + key : $"{Name}{key}   {body}";
    }
}

public sealed class ComboPreset
{
    public string Name { get; set; } = "";
    public List<string> Ids { get; set; } = new();
    public override string ToString() => Name;
}

public sealed class AppSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int SendPort { get; set; } = 9000;
    public int RecvPort { get; set; } = 9001;
    public bool Listen { get; set; } = true;
    public bool AutoConnect { get; set; } = false;

    public bool Dark { get; set; } = true;
    public bool TopMost { get; set; } = false;
    public int Opacity { get; set; } = 100;
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public string LastPage { get; set; } = "移動";
    public int LayoutVersion { get; set; }

    public string LogFolder { get; set; } = "";
    public bool LogAutoStart { get; set; } = true;

    public bool ChatImmediate { get; set; } = true;
    public bool ChatSound { get; set; } = false;
    public int ChatInterval { get; set; } = 3000;
    public int ComboDelay { get; set; } = 200;

    public string FontName { get; set; } = "Meiryo UI";
    public float FontSize { get; set; } = 9f;

    public string StopHotkey { get; set; } = "Ctrl+Alt+F12";
    public string OutputHotkey { get; set; } = "Ctrl+Alt+F11";
    public bool StopDisablesOutput { get; set; } = false;
    public bool PollStopKey { get; set; } = true;

    public List<Macro> Macros { get; set; } = new();   // ファイル保存へ移行後は settings.json には書かない

    // ---- マクロ・コンボ (ファイル保存と有効化) ----
    /// <summary>マクロのファイル保存先 (空 = 既定: %APPDATA%\VRC OSC Debug Client\Macros)</summary>
    public string MacroFolder { get; set; } = "";
    /// <summary>コンボのファイル保存先 (空 = 既定: %APPDATA%\VRC OSC Debug Client\Combos)</summary>
    public string ComboFolder { get; set; } = "";
    /// <summary>settings.json 内のマクロ・コンボをフォルダへ移した</summary>
    public bool PresetFilesMigrated { get; set; } = false;
    /// <summary>マクロ・コンボ機能の有効化 (自己責任の同意済み)</summary>
    public bool MacroComboEnabled { get; set; } = false;
    public bool MacroActive { get; set; } = true;
    public bool ComboActive { get; set; } = true;
    public string MacroToggleHotkey { get; set; } = "";
    public string ComboToggleHotkey { get; set; } = "";
    public decimal ScaleStep { get; set; } = 0.1M;
    public Dictionary<string, string> CategoryHotkeys { get; set; } = new();

    public bool ShowSidePanel { get; set; } = true;
    public int SidePanelWidth { get; set; } = 250;
    public bool LogRawOsc { get; set; } = true;
    public bool LogRecvOsc { get; set; } = false;

    public List<string> HistHidden { get; set; } = new();
    public bool ParamHideReadOnly { get; set; } = true;
    public bool ParamAutoAdd { get; set; } = true;
    public bool MacroSampleAdded { get; set; } = false;
    public Dictionary<string, RowSetting> Rows { get; set; } = new();
    public List<CustomPreset> Customs { get; set; } = new();
    public List<ComboPreset> Combos { get; set; } = new();
    public List<string> ChatHistory { get; set; } = new();   // 旧形式 (読み込み時に ChatLog へ移す)
    public List<ChatEntry> ChatLog { get; set; } = new();
    public List<ChatPreset> ChatPresets { get; set; } = new();
    public string Language { get; set; } = "ja";
    /// <summary>UI の大きさ (%)。文字サイズと各部の大きさにかかる</summary>
    public int UiZoom { get; set; } = 90;
    public Dictionary<string, float[]> CameraPoses { get; set; } = new();

    /// <summary>保存フォルダの名前 (%APPDATA% / ドキュメント の下)</summary>
    public const string AppFolderName = "VRC OSC Debug Client";
    private const string OldFolderName = "VRCOscTool"; // 以前の名前 (初回起動時に引き継ぐ)

    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    /// <summary>以前の名前のフォルダがあれば、新しいフォルダへ中身をコピーする (新しい方が無い時だけ)</summary>
    public static void MigrateOldFolders()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.MyDocuments })
        {
            try
            {
                string baseDir = Environment.GetFolderPath(root);
                string oldDir = Path.Combine(baseDir, OldFolderName), newDir = Path.Combine(baseDir, AppFolderName);
                if (Directory.Exists(oldDir) && !Directory.Exists(newDir)) CopyDir(oldDir, newDir);
            }
            catch { }
        }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    public string MacroFolderPath => string.IsNullOrWhiteSpace(MacroFolder) ? Path.Combine(DataFolder, "Macros") : MacroFolder;
    public string ComboFolderPath => string.IsNullOrWhiteSpace(ComboFolder) ? Path.Combine(DataFolder, "Combos") : ComboFolder;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                s.Macros ??= new();
                s.Combos ??= new();
                return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // マクロ・コンボはフォルダのファイルに保存しているので、ここには書かない
            var data = this;
            if (PresetFilesMigrated)
            {
                data = (AppSettings)MemberwiseClone(); // 実行中のマクロが読む本体には触らない
                data.Macros = null;
                data.Combos = null;
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
