using System.Text.Encodings.Web;
using System.Text.Json;

namespace VRCOscDebugClient.Core;

/// <summary>
/// マクロ・コンボを「1 件 = 1 ファイル (*.json)」でフォルダに保存・読込する。
/// ・Load: フォルダ内の *.json をすべて読み込む
/// ・Save: 変更があったものだけ書き込み、一覧から消したもののファイルは削除
/// ・フォルダに新しいファイルが置かれたら FilesAdded を通知 (LoadNew で取り込む)
/// UI スレッドから呼ぶこと (FilesAdded は別スレッドから来る)
/// </summary>
public sealed class PresetStore<T> : IDisposable where T : class
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をそのまま書く (手で編集しやすい)
    };

    private readonly Func<T, string> _name;
    private readonly Dictionary<T, string> _files = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<T, string> _written = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<T, string> _nameAt = new(ReferenceEqualityComparer.Instance); // 読込/保存した時の名前
    private FileSystemWatcher _watcher;
    private System.Threading.Timer _debounce;

    /// <summary>フォルダに知らないファイルが増えた (別スレッドから通知)</summary>
    public event Action FilesAdded;

    public string Folder { get; private set; } = "";
    public List<string> Errors { get; } = new();

    public PresetStore(Func<T, string> name) => _name = name;

    public static bool HasFiles(string folder)
    {
        try { return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.json").Any(); }
        catch { return false; }
    }

    /// <summary>フォルダを切り替えて中身をすべて読み込む</summary>
    public List<T> Load(string folder)
    {
        _files.Clear();
        _written.Clear();
        _nameAt.Clear();
        Errors.Clear();
        Folder = folder;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { Errors.Add($"フォルダを作れません: {ex.Message}"); }
        var list = ReadFiles(folder, _ => true);
        Watch();
        return list;
    }

    /// <summary>まだ読み込んでいないファイルだけを読み込む</summary>
    public List<T> LoadNew()
    {
        Errors.Clear();
        var known = new HashSet<string>(_files.Values, StringComparer.OrdinalIgnoreCase);
        return ReadFiles(Folder, f => !known.Contains(f));
    }

    private List<T> ReadFiles(string folder, Func<string, bool> filter)
    {
        var list = new List<T>();
        string[] files;
        try { files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : Array.Empty<string>(); }
        catch (Exception ex) { Errors.Add(ex.Message); return list; }
        Array.Sort(files, StringComparer.CurrentCultureIgnoreCase);
        foreach (var f in files.Where(filter))
        {
            try
            {
                string text = File.ReadAllText(f);
                var item = JsonSerializer.Deserialize<T>(text);
                if (item == null || string.IsNullOrWhiteSpace(_name(item)))
                {
                    Errors.Add($"{Path.GetFileName(f)}: 内容がありません");
                    continue;
                }
                _files[item] = f;
                _written[item] = JsonSerializer.Serialize(item, Json);
                _nameAt[item] = _name(item);
                list.Add(item);
            }
            catch (Exception ex)
            {
                Errors.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>一覧をフォルダへ保存。変更のあったものだけ書き込み、消えたもののファイルは削除</summary>
    public int Save(IEnumerable<T> items)
    {
        if (string.IsNullOrEmpty(Folder)) return 0;
        Errors.Clear();
        var list = items.ToList();
        var set = new HashSet<T>(list, ReferenceEqualityComparer.Instance);
        int written = 0;

        try { Directory.CreateDirectory(Folder); }
        catch (Exception ex) { Errors.Add(ex.Message); return 0; }

        // 一覧から消えたもの → ファイルも消す
        foreach (var kv in _files.ToList())
        {
            if (set.Contains(kv.Key)) continue;
            TryDelete(kv.Value);
            _files.Remove(kv.Key);
            _written.Remove(kv.Key);
            _nameAt.Remove(kv.Key);
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            string json = JsonSerializer.Serialize(item, Json);
            string path = PathFor(item, used);
            used.Add(path);

            bool moved = _files.TryGetValue(item, out var old) && !string.Equals(old, path, StringComparison.OrdinalIgnoreCase);
            bool changed = !_written.TryGetValue(item, out var prev) || prev != json;
            if (!moved && !changed && File.Exists(path)) continue;

            try
            {
                _files[item] = path; // 先に登録 (フォルダ監視で「新しいファイル」扱いしないように)
                File.WriteAllText(path, json);
                _written[item] = json;
                _nameAt[item] = _name(item);
                written++;
                if (moved) TryDelete(old);
            }
            catch (Exception ex)
            {
                if (moved) _files[item] = old;
                Errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return written;
    }

    /// <summary>名前からファイルのパスを決める (今のファイル名が名前どおりならそのまま)</summary>
    private string PathFor(T item, HashSet<string> used)
    {
        string baseName = SafeFileName(_name(item));
        _files.TryGetValue(item, out var current);
        // 名前を変えていなければ今のファイル名のまま (手で付けたファイル名を勝手に変えない)
        if (current != null && !used.Contains(current) && _nameAt.TryGetValue(item, out var was) && was == _name(item))
            return current;

        string Candidate(int n) => Path.Combine(Folder, n == 1 ? baseName + ".json" : $"{baseName} ({n}).json");
        for (int n = 1; n < 1000; n++)
        {
            string p = Candidate(n);
            if (used.Contains(p)) continue;
            if (current != null && string.Equals(current, p, StringComparison.OrdinalIgnoreCase)) return p;
            bool takenByOther = _files.Any(kv => !ReferenceEquals(kv.Key, item) &&
                                                 string.Equals(kv.Value, p, StringComparison.OrdinalIgnoreCase));
            if (takenByOther) continue;
            if (File.Exists(p)) continue; // まだ読み込んでいない他のファイル
            return p;
        }
        return Path.Combine(Folder, baseName + "_" + Guid.NewGuid().ToString("N")[..8] + ".json");
    }

    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var chars = (name ?? "").Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray();
        string s = new string(chars).Trim().TrimEnd('.');
        if (s.Length == 0) s = "無題";
        return s.Length > 80 ? s[..80] : s;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------- フォルダ監視 ----------------
    private void Watch()
    {
        StopWatch();
        if (!Directory.Exists(Folder)) return;
        try
        {
            _watcher = new FileSystemWatcher(Folder, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
            };
            _watcher.Created += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch { StopWatch(); }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        // コピー中のファイルを読まないよう、少し待ってからまとめて通知
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => FilesAdded?.Invoke(), null, 700, Timeout.Infinite);
    }

    private void StopWatch()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        StopWatch();
        _debounce?.Dispose();
    }
}
