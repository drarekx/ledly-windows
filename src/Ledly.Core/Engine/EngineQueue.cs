using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ledly;

/// <summary>
/// A dedicated thread that runs posted work in order plus a periodic tick, like a serial dispatch queue
/// with a timer. Everything owned by the output loop is only touched here.
/// </summary>
public sealed class EngineQueue : IDisposable
{
    readonly BlockingCollection<Action> _work = new();
    readonly Thread _thread;
    readonly double _interval;
    Action? _tick;
    volatile bool _running = true;

    public EngineQueue(string name, double rateHz)
    {
        _interval = 1000.0 / rateHz;
        _thread = new Thread(Run) { Name = name, IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public bool IsCurrent => Thread.CurrentThread == _thread;

    public void SetTick(Action tick) => Post(() => _tick = tick);

    public void Post(Action action)
    {
        if (_running) _work.Add(action);
    }

    /// <summary>Runs <paramref name="func"/> on the queue and waits for its result.</summary>
    public T Invoke<T>(Func<T> func)
    {
        if (IsCurrent) return func();
        using var done = new ManualResetEventSlim();
        T result = default!;
        Exception? error = null;
        Post(() =>
        {
            try { result = func(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("engine queue busy");
        if (error is not null) throw error;
        return result;
    }

    public void Invoke(Action action) => Invoke(() => { action(); return 0; });

    void Run()
    {
        var clock = Stopwatch.StartNew();
        var next = 0.0;
        while (_running)
        {
            var wait = next - clock.Elapsed.TotalMilliseconds;
            if (wait > 0 && _work.TryTake(out var action, (int)Math.Ceiling(Math.Min(wait, 50))))
            {
                Safe(action);
                continue;
            }
            // Drain anything pending, then tick when due.
            while (_work.TryTake(out var pending)) Safe(pending);
            if (clock.Elapsed.TotalMilliseconds >= next)
            {
                if (_tick is { } tick) Safe(tick);
                next += _interval;
                // Fell far behind (sleep, debugger): don't try to catch up.
                if (clock.Elapsed.TotalMilliseconds - next > _interval * 5) next = clock.Elapsed.TotalMilliseconds + _interval;
            }
        }
    }

    static void Safe(Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Write($"engine: {e}"); }
    }

    public void Dispose()
    {
        _running = false;
        _work.CompleteAdding();
    }
}
