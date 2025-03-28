﻿using System.Threading;
using System.Threading.Tasks;

namespace Satisfy;

// base class for automation tasks
// all tasks are cancellable, and all continuations are executed on the main thread (in framework update)
// tasks also support progress reporting
// note: it's assumed that any created task will be executed (either by calling Run directly or by passing to Automation.Start)
public abstract class AutoTask
{
    // debug context scope
    protected readonly struct DebugContext : IDisposable
    {
        private readonly AutoTask _ctx;
        private readonly int _depth;

        public DebugContext(AutoTask ctx, string name)
        {
            _ctx = ctx;
            _depth = _ctx._debugContext.Count;
            _ctx._debugContext.Add(name);
            _ctx.Log("Scope enter");
        }

        public void Dispose()
        {
            _ctx.Log($"Scope exit (depth={_depth}, cur={_ctx._debugContext.Count - 1})");
            if (_depth < _ctx._debugContext.Count)
                _ctx._debugContext.RemoveRange(_depth, _ctx._debugContext.Count - _depth);
        }

        public void Rename(string newName)
        {
            _ctx.Log($"Transition to {newName} @ {_depth}");
            if (_depth < _ctx._debugContext.Count)
                _ctx._debugContext[_depth] = newName;
        }
    }

    public string Status { get; protected set; } = ""; // user-facing status string
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _debugContext = [];

    public void Cancel() => _cts.Cancel();

    public void Run(Action completed)
    {
        Service.Framework.Run(async () =>
        {
            var task = Execute();
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // we don't really care about cancelation...
            if (task.IsFaulted)
                Service.Log.Warning($"Task ended with error: {task.Exception}");
            completed();
            _cts.Dispose();
        }, _cts.Token);
    }

    // implementations are typically expected to be async (coroutines)
    protected abstract Task Execute();

    protected CancellationToken CancelToken => _cts.Token;

    // wait for a few frames
    protected Task NextFrame(int numFramesToWait = 1) => Service.Framework.DelayTicks(numFramesToWait, _cts.Token);

    // wait until condition function returns false, checking once every N frames
    protected async Task WaitWhile(Func<bool> condition, string scopeName, int checkFrequency = 1)
    {
        using var scope = BeginScope(scopeName);
        while (condition())
        {
            Log("waiting...");
            await NextFrame(checkFrequency);
        }
    }

    protected void Log(string message) => Service.Log.Debug($"[{GetType().Name}] [{string.Join(" > ", _debugContext)}] {message}");

    // start a new debug context; should be disposed, so usually should be assigned to RAII variable
    protected DebugContext BeginScope(string name) => new(this, name);

    // abort a task unconditionally
    protected void Error(string message)
    {
        Log($"Error: {message}");
        throw new Exception($"[{GetType().Name}] [{string.Join(" > ", _debugContext)}] {message}");
    }

    // abort a task if condition is true
    protected void ErrorIf(bool condition, string message)
    {
        if (condition)
            Error(message);
    }
}

// utility that allows concurrently executing only one task; starting a new task if one is already in progress automatically cancels olds one
public sealed class Automation : IDisposable
{
    public AutoTask? CurrentTask { get; private set; }

    public bool Running => CurrentTask != null;

    public void Dispose() => Stop();

    // stop executing any running task
    // this requires tasks to cooperate by checking the token
    public void Stop()
    {
        CurrentTask?.Cancel();
        CurrentTask = null;
    }

    // if any other task is running, it's cancelled
    public void Start(AutoTask task)
    {
        Stop();
        CurrentTask = task;
        task.Run(() =>
        {
            if (CurrentTask == task)
                CurrentTask = null;
            // else: some other task is now executing
        });
    }
}
