namespace VRCOscDebugClient.Core;

public enum PressMode { Tap = 0, Hold = 1, Rapid = 2, Momentary = 3 }

/// <summary>
/// 1つの操作を「1回押し / 長押し / 連打」で実行する。操作ごとに独立して動くので複数同時実行可能。
/// apply(true)=押す / apply(false)=離す
/// </summary>
public sealed class ActionRunner
{
    private readonly object _gate = new();
    private CancellationTokenSource _cts;
    private Task _task = Task.CompletedTask;
    private int _done;

    public string Name { get; }
    public bool IsRunning { get { lock (_gate) return _cts != null; } }

    // ---- 動作状況 (動作パネル表示用) ----
    public PressMode CurrentMode { get; private set; }
    public int TimeMs { get; private set; }
    public int Total { get; private set; }
    public int Done => Volatile.Read(ref _done);
    public long StartedAt { get; private set; }

    /// <summary>実行状態の変化 (スレッドプールから呼ばれる場合あり)</summary>
    public event Action<bool> StateChanged;

    public ActionRunner(string name) => Name = name;

    public void Start(PressMode mode, int timeMs, int count, Action<bool> apply)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = cts;
            CurrentMode = mode;
            TimeMs = timeMs;
            Total = count;
            Volatile.Write(ref _done, 0);
            StartedAt = Environment.TickCount64;
            var prev = _task;
            // 前回の実行 (離す処理) が終わってから開始し、押下/解放の順序が逆転しないようにする
            _task = Task.Run(() => RunAsync(prev, mode, timeMs, count, apply, cts));
        }
        StateChanged?.Invoke(true);
    }

    public void Stop()
    {
        CancellationTokenSource c;
        lock (_gate)
        {
            c = _cts;
            _cts = null;
        }
        if (c != null)
        {
            c.Cancel();
            StateChanged?.Invoke(false);
        }
    }

    /// <summary>動作状況の文字列 (例: "長押し 2.3s" / "連打 5/20")</summary>
    public string Describe()
    {
        double sec = (Environment.TickCount64 - StartedAt) / 1000.0;
        return CurrentMode switch
        {
            PressMode.Tap => "1回",
            PressMode.Rapid => $"連打 {Done}/{(Total <= 0 ? "∞" : Total.ToString())}",
            _ => TimeMs > 0 ? $"長押し {sec:0.0}/{TimeMs / 1000.0:0.0}s" : $"長押し {sec:0.0}s"
        };
    }

    private async Task RunAsync(Task prev, PressMode mode, int timeMs, int count,
        Action<bool> apply, CancellationTokenSource cts)
    {
        try { await prev.ConfigureAwait(false); } catch { }

        var ct = cts.Token;
        bool pressed = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            switch (mode)
            {
                case PressMode.Tap:
                    apply(true); pressed = true;
                    await Task.Delay(Math.Max(timeMs, 16), ct).ConfigureAwait(false);
                    break;

                case PressMode.Hold:
                case PressMode.Momentary:
                    apply(true); pressed = true;
                    await Task.Delay(timeMs > 0 ? timeMs : Timeout.Infinite, ct).ConfigureAwait(false);
                    break;

                case PressMode.Rapid:
                    int interval = Math.Max(timeMs, 40);
                    int press = Math.Max(interval / 2, 20);
                    for (int i = 0; count <= 0 || i < count; i++)
                    {
                        apply(true); pressed = true;
                        Interlocked.Increment(ref _done);
                        await Task.Delay(press, ct).ConfigureAwait(false);
                        apply(false); pressed = false;
                        await Task.Delay(Math.Max(interval - press, 10), ct).ConfigureAwait(false);
                    }
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            if (pressed)
            {
                try { apply(false); } catch { }
            }
            bool raise = false;
            lock (_gate)
            {
                if (_cts == cts) { _cts = null; raise = true; }
            }
            if (raise) StateChanged?.Invoke(false);
        }
    }
}
