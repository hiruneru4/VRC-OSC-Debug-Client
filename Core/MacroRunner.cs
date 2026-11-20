using System.Globalization;
using VRCOscDebugClient.Osc;

namespace VRCOscDebugClient.Core;

/// <summary>
/// マクロ1つを実行する。
/// ・「押す」で押したもの (ダッシュ等) は「離す」「全て離す」か、マクロ終了まで押しっぱなし
/// ・[並行] のステップは終わるのを待たずに次へ進み、複数の操作を同時に動かせる
/// ・他のマクロの実行 / 停止 / 一時停止 / 再開 / 割込み実行ができる
/// ・同じ入力を他の操作と同時に押しても InputArbiter が調停するので、勝手に離されない
/// ・停止・終了時はこのマクロで押したものを必ず離す
/// </summary>
public sealed class MacroRunner
{
    // 実行中のマクロの呼び出し経路 (循環呼び出しの防止)
    private static readonly AsyncLocal<string> _chain = new();

    private readonly OscClient _osc;
    private readonly InputArbiter _arb;
    private readonly IMacroHost _host;
    private readonly object _gate = new();
    private CancellationTokenSource _cts;
    private Task _task = Task.CompletedTask;
    private TaskCompletionSource _resume; // null 以外 = 一時停止中
    private volatile int _currentStep = -1;
    private volatile int _repeatIndex;
    private int _parallelRunning;
    private volatile int _completed;

    /// <summary>終わった周回の数 (繰り返しのカウント)</summary>
    public int CompletedRepeats => _completed;

    /// <summary>周回の開始 (何回目, 全体 (0 = 無限))</summary>
    public event Action<int, int> RepeatStarted;

    public string Name { get; }
    public int CurrentStep => _currentStep;
    public int RepeatIndex => _repeatIndex;
    public int RepeatTotal { get; private set; }
    public int StepCount { get; private set; }
    public int ParallelRunning => Volatile.Read(ref _parallelRunning);
    public bool IsPaused => Volatile.Read(ref _resume) != null;
    public bool IsRunning { get { lock (_gate) return _cts != null; } }

    public event Action<bool> StateChanged;
    public event Action StepChanged;
    /// <summary>ステップ開始 (index, step) — 操作ログ用</summary>
    public event Action<int, MacroStep> StepStarted;

    public MacroRunner(string name, OscClient osc, InputArbiter arb, IMacroHost host)
    {
        Name = name;
        _osc = osc;
        _arb = arb;
        _host = host;
    }

    /// <summary>このマクロを呼び出し中のマクロ (自分を含む経路) に name があるか</summary>
    public static bool InCallChain(string name) =>
        (_chain.Value ?? "").Contains("\u0001" + name + "\u0001", StringComparison.Ordinal);

    /// <summary>開始して、その実行タスクを返す (終わるまで待つ用)</summary>
    public Task Start(Macro macro)
    {
        var snapshot = macro.Clone(); // 実行中に編集されても影響しない
        var cts = new CancellationTokenSource();
        Task task;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = cts;
            RepeatTotal = snapshot.Repeat;
            StepCount = snapshot.Steps.Count;
            _repeatIndex = 0;
            _completed = 0;
            Resume();
            var prev = _task;
            task = _task = Task.Run(() => RunAsync(prev, snapshot, cts));
        }
        StateChanged?.Invoke(true);
        return task;
    }

    public void Stop()
    {
        CancellationTokenSource c;
        lock (_gate) { c = _cts; _cts = null; }
        if (c != null)
        {
            c.Cancel();
            Resume();
            StateChanged?.Invoke(false);
        }
    }

    public void Pause()
    {
        if (!IsRunning) return;
        Interlocked.CompareExchange(ref _resume, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);
        StepChanged?.Invoke();
    }

    public void Resume()
    {
        var t = Interlocked.Exchange(ref _resume, null);
        if (t == null) return;
        t.TrySetResult();
        StepChanged?.Invoke();
    }

    private Task WaitIfPaused(CancellationToken ct)
    {
        var t = Volatile.Read(ref _resume);
        return t == null ? Task.CompletedTask : t.Task.WaitAsync(ct);
    }

    /// <summary>動作状況 (例: "3/7  周回 2/∞  並行 1  押下 2")</summary>
    public string Describe()
    {
        int step = _currentStep;
        string rep = RepeatTotal <= 0 ? "∞" : RepeatTotal.ToString();
        string s = $"{_repeatIndex + 1}/{rep} 回目  ステップ {Math.Max(step, 0) + 1}/{StepCount}";
        int par = ParallelRunning;
        if (par > 0) s += $"  並行 {par}";
        int held = _arb.CountOwner(this);
        if (held > 0) s += $"  押下 {held}";
        if (IsPaused) s = "⏸ " + s;
        return s;
    }

    private async Task RunAsync(Task prev, Macro m, CancellationTokenSource cts)
    {
        try { await prev.ConfigureAwait(false); } catch { }
        _chain.Value = (_chain.Value ?? "\u0001") + m.Name + "\u0001";

        var ct = cts.Token;
        var background = new List<Task>();
        try
        {
            for (int r = 0; m.Repeat <= 0 || r < m.Repeat; r++)
            {
                _repeatIndex = r;
                RepeatStarted?.Invoke(r + 1, m.Repeat);
                long t0 = Environment.TickCount64;
                for (int i = 0; i < m.Steps.Count; i++)
                {
                    await WaitIfPaused(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var step = m.Steps[i];
                    _currentStep = i;
                    StepChanged?.Invoke();
                    StepStarted?.Invoke(i, step);

                    if (step.Kind == MacroStepKind.WaitParallel)
                    {
                        await WaitBackground(background).ConfigureAwait(false);
                        continue;
                    }

                    if (step.Parallel && MacroStep.CanParallel(step.Kind))
                    {
                        background.RemoveAll(t => t.IsCompleted);
                        background.Add(RunInBackground(step, ct));
                    }
                    else
                    {
                        await ExecAsync(step, ct).ConfigureAwait(false);
                    }
                }

                // 1周の終わりは並行中のステップが終わるまで待つ
                await WaitBackground(background).ConfigureAwait(false);
                _completed = r + 1;

                // 待機の無いマクロを無限ループしても CPU を使い切らないように
                if (Environment.TickCount64 - t0 < 20) await Task.Delay(20, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OpLog.Write($"⚠ マクロ「{m.Name}」エラー: {ex.Message}"); }
        finally
        {
            // 停止時: 並行タスクもキャンセル済みなので、終わるのを待ってから全て離す
            try { await Task.WhenAll(background).ConfigureAwait(false); } catch { }
            _arb.ReleaseOwner(this);
            _currentStep = -1;
            StepChanged?.Invoke();
            bool raise = false;
            lock (_gate)
            {
                if (_cts == cts) { _cts = null; raise = true; }
            }
            if (raise) StateChanged?.Invoke(false);
        }
    }

    private static async Task WaitBackground(List<Task> background)
    {
        if (background.Count == 0) return;
        var all = background.ToArray();
        background.Clear();
        await Task.WhenAll(all).ConfigureAwait(false); // キャンセル時は例外で抜ける
    }

    private async Task RunInBackground(MacroStep step, CancellationToken ct)
    {
        Interlocked.Increment(ref _parallelRunning);
        try { await ExecAsync(step, ct).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _parallelRunning); }
    }

    private async Task ExecAsync(MacroStep s, CancellationToken ct)
    {
        switch (s.Kind)
        {
            case MacroStepKind.Press:
                Press(s);
                break;
            case MacroStepKind.Release:
                Release(s);
                break;
            case MacroStepKind.Tap:
                if (Press(s))
                {
                    try { await Task.Delay(Math.Max(s.Ms, 16), ct).ConfigureAwait(false); }
                    finally { Release(s); }
                }
                break;
            case MacroStepKind.Hold:
                if (Press(s))
                {
                    try { await Task.Delay(Math.Max(s.Ms, 0), ct).ConfigureAwait(false); }
                    finally { Release(s); }
                }
                break;
            case MacroStepKind.Rapid:
                int interval = Math.Max(s.Ms, 40);
                int press = Math.Max(interval / 2, 20);
                for (int i = 0; s.Count <= 0 || i < s.Count; i++)
                {
                    await WaitIfPaused(ct).ConfigureAwait(false);
                    if (!Press(s)) break;
                    try { await Task.Delay(press, ct).ConfigureAwait(false); }
                    finally { Release(s); }
                    await Task.Delay(Math.Max(interval - press, 10), ct).ConfigureAwait(false);
                }
                break;
            case MacroStepKind.Wait:
                await Task.Delay(Math.Max(s.Ms, 0), ct).ConfigureAwait(false);
                break;
            case MacroStepKind.Chat:
                var text = (s.Value ?? "").Replace("\\n", "\n");
                if (text.Length > 144) text = text.Substring(0, 144);
                _osc.Send("/chatbox/input", text, true, false);
                break;
            case MacroStepKind.Send:
                if (Resolve(s, out var addr, out var on, out _)) _osc.Send(addr, on);
                break;
            case MacroStepKind.ReleaseAll:
                _arb.ReleaseOwner(this);
                break;
            case MacroStepKind.MacroRun:
                if (_host != null) await _host.RunMacroAsync(s.Target, true, this, ct).ConfigureAwait(false);
                break;
            case MacroStepKind.MacroStop:
                _host?.StopMacro(s.Target, this);
                break;
            case MacroStepKind.MacroPause:
                _host?.PauseMacro(s.Target, true, this);
                break;
            case MacroStepKind.MacroResume:
                _host?.PauseMacro(s.Target, false, this);
                break;
            case MacroStepKind.Interrupt:
                if (_host != null) await _host.InterruptAsync(s.Target, this, ct).ConfigureAwait(false);
                break;
            case MacroStepKind.Category:
                _host?.CategoryCommand(s.Target, s.Value);
                break;
        }
    }

    /// <summary>対象から送信先と 押す/離す の値を決める</summary>
    private bool Resolve(MacroStep s, out string address, out object on, out object off)
    {
        address = null; on = null; off = null;
        var target = (s.Target ?? "").Trim();

        // 操作 (/input/...)
        var def = ActionCatalog.Find(target);
        if (def != null)
        {
            address = def.Address;
            if (def.Kind == ActionKind.Axis)
            {
                float f = float.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                    ? Math.Clamp(p, -1f, 1f) : def.DefaultValue;
                on = f; off = 0f;
            }
            else { on = 1; off = 0; }
            return true;
        }

        // カスタムプリセット
        if (target.StartsWith(MacroStep.CustomPrefix))
        {
            var preset = _host?.FindCustom(target.Substring(MacroStep.CustomPrefix.Length));
            if (preset == null || string.IsNullOrWhiteSpace(preset.Address)) return false;
            string value = string.IsNullOrWhiteSpace(s.Value) ? preset.Value : s.Value;
            if (!OscValue.TryParse(preset.Type, value, out on)) return false;
            if (!OscValue.TryParse(preset.Type, preset.ResetValue, out off)) off = OscValue.Zero(preset.Type);
            address = preset.Address.Trim();
            return true;
        }

        // アドレス直接 (アバターパラメータ等)
        if (target.StartsWith("/") && OscValue.TryParse(s.Type, s.Value, out on))
        {
            address = target;
            off = OscValue.Zero(s.Type);
            return true;
        }
        return false;
    }

    private bool Press(MacroStep s)
    {
        if (!Resolve(s, out var addr, out var on, out var off)) return false;
        _arb.Press(this, addr, on, off);
        return true;
    }

    private void Release(MacroStep s)
    {
        if (Resolve(s, out var addr, out _, out _)) _arb.Release(this, addr);
    }
}
