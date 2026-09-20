using ClrDebug;
using DotnetDebugger.Engine.Values;

namespace DotnetDebugger.Engine;

// Func-eval: running code inside the debuggee on behalf of the debugger.
public sealed partial class DebugEngine : IEvalHost
{
    private sealed class PendingEval(CorDebugEval eval)
    {
        public CorDebugEval Eval { get; } = eval;
        public bool Completed { get; set; }
        public bool ThrewException { get; set; }
        public bool Cancelled { get; set; }
    }

    private static readonly TimeSpan EvalTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AbortTimeout = TimeSpan.FromSeconds(1);

    // Time one request may spend on evaluations nobody asked for explicitly (ToString(), [DebuggerDisplay], property
    // getters of an expanded object). Once it is used up values fall back to type names and properties become lazy.
    private static readonly TimeSpan ImplicitEvalBudget = TimeSpan.FromSeconds(1);

    private DateTime _implicitEvalDeadline = DateTime.MaxValue;

    private void StartImplicitEvalBudget() => _implicitEvalDeadline = DateTime.UtcNow + ImplicitEvalBudget;

    bool IEvalHost.BudgetExceeded => DateTime.UtcNow > _implicitEvalDeadline;

    string IEvalHost.FormatDebuggerDisplay(string template, Func<CorDebugValue?> self, InspectionContext context) =>
        FormatLogMessage(template, new Evaluator(this, context.Frame!, allowCalls: true, self), quoteStrings: true);

    /// <summary>Aborts the evaluation that is currently running, if any. Safe to call from any thread.</summary>
    public void CancelEvaluation()
    {
        lock (_lock)
        {
            if (_pendingEval is not { Completed: false } pending || _process == null)
                return;
            pending.Cancelled = true;
            Monitor.PulseAll(_lock);
            try
            {
                Synchronize(_process);
                pending.Eval.Abort();
                _process.Continue(false);
            }
            catch (Exception e)
            {
                Log?.Invoke("Failed to abort the evaluation: " + e.Message);
            }
        }
    }

    private readonly List<CorDebugHandleValue> _strongHandles = [];
    private PendingEval? _pendingEval;

    /// <summary>
    /// Runs an evaluation on <paramref name="threadId"/> and waits for it. Must be called with the engine lock held;
    /// the lock is released while waiting so the completion callback can get in. Everything obtained from ICorDebug
    /// before this call (frames, values) is stale afterwards, except strong handles.
    /// </summary>
    private CorDebugValue? RunEval(int threadId, Action<CorDebugEval> start)
    {
        CorDebugProcess process = RequireStopped();
        if (_pendingEval != null)
            throw new EvalFailedException("Evaluation not possible: another evaluation is in progress.");

        CorDebugThread thread;
        CorDebugEval eval;
        try
        {
            thread = process.GetThread(threadId);
            eval = thread.CreateEval();
            start(eval);
        }
        catch (Exception e)
        {
            throw new EvalFailedException("Evaluation not possible: " + e.Message);
        }

        var pending = new PendingEval(eval);
        _pendingEval = pending;
        try
        {
            // Only the evaluating thread runs, so the evaluation cannot be blocked on (or disturb) other threads' progress.
            process.SetAllThreadsDebugState(CorDebugThreadState.THREAD_SUSPEND, thread.Raw);
            _stopped = false;
            process.Continue(false);

            if (!WaitForEval(pending, EvalTimeout))
            {
                // Timed out or cancelled. Aborting only works once the thread is back in managed code: an evaluation
                // stuck in a wait cannot be stopped at all. It is then left behind; it completes (unnoticed) whenever
                // the debuggee gets to run long enough.
                string reason = pending.Cancelled ? "Evaluation cancelled." : "Evaluation timed out.";
                Log?.Invoke(reason + " Aborting.");
                if (!pending.Cancelled)
                {
                    Synchronize(process);
                    eval.Abort();
                    process.Continue(false);
                }
                if (!WaitForEval(pending, AbortTimeout, stopOnCancel: false))
                {
                    Synchronize(process);
                    eval.RudeAbort();
                    process.Continue(false);
                    if (!WaitForEval(pending, AbortTimeout, stopOnCancel: false))
                    {
                        Synchronize(process);
                        _stopped = true;
                        reason += " The evaluated code could not be interrupted and is still running on its thread.";
                    }
                }
                throw new EvalFailedException(reason);
            }

            if (_processExited || _process == null)
                throw new EvalFailedException("The debuggee exited during the evaluation.");

            CorDebugValue? result = null;
            try
            {
                result = eval.Result;
            }
            catch (Exception)
            {
                // void method
            }

            if (pending.ThrewException)
            {
                string description = "exception";
                if (result != null)
                {
                    string typeName = _values.GetTypeName(result);
                    string? message = _values.ReadString(_values.GetFieldByName(result, "_message"));
                    description = message == null ? typeName : typeName + ": " + message;
                }
                throw new EvalFailedException(description);
            }
            return result;
        }
        finally
        {
            _pendingEval = null;
            try
            {
                // once Stop has hung, every other call into the debugging interface blocks behind it
                if (!_processExited && _process != null && !_cannotSynchronize)
                {
                    _process.SetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN, null!);
                    RestoreFrozenThreads(_process);
                }
            }
            catch (Exception e)
            {
                Log?.Invoke("Failed to resume threads after an evaluation: " + e.Message);
            }
        }
    }

    /// <returns>false if the evaluation is still running (timeout, or cancellation was requested).</returns>
    private bool WaitForEval(PendingEval pending, TimeSpan timeout, bool stopOnCancel = true)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!pending.Completed && !_processExited)
        {
            if (stopOnCancel && pending.Cancelled)
                return false;
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !Monitor.Wait(_lock, remaining))
                return pending.Completed || _processExited;
        }
        return true;
    }

    /// <returns>true if the event belonged to the evaluation in progress.</returns>
    private bool OnEvalFinished(CorDebugEval eval, bool threwException)
    {
        // an evaluation that was given up on earlier may complete at any later time
        if (_pendingEval == null || !_pendingEval.Eval.Equals(eval))
            return false;
        _pendingEval.Completed = true;
        _pendingEval.ThrewException = threwException;
        return true;
    }

    CorDebugValue? IEvalHost.CallFunction(int threadId, CorDebugFunction function, CorDebugType[] typeArguments, CorDebugValue[] arguments) =>
        RunEval(threadId, eval => eval.CallParameterizedFunction(
            function.Raw, typeArguments.Length, typeArguments.Select(t => t.Raw).ToArray(),
            arguments.Length, arguments.Select(a => a.Raw).ToArray()));

    Func<CorDebugValue?> IEvalHost.Stabilize(Func<CorDebugValue?> getter) => Stabilize(getter);

    CorDebugFrame? IEvalHost.GetFrame(FrameRef frame) => GetILFrame(frame);

    private Func<CorDebugValue?> Stabilize(Func<CorDebugValue?> getter)
    {
        try
        {
            CorDebugValue? value = getter();
            if (value == null)
                return getter;
            if (value is CorDebugHandleValue)
                return () => value;

            CorDebugValue heapCandidate = value;
            if (value is CorDebugReferenceValue reference)
            {
                if (reference.IsNull || value.Type is CorElementType.Ptr or CorElementType.FnPtr or CorElementType.ByRef)
                    return getter;
                heapCandidate = reference.Dereference();
            }

            CorDebugHandleValue handle = heapCandidate.As<CorDebugHeapValue>().CreateHandle(CorDebugHandleType.HANDLE_STRONG);
            _strongHandles.Add(handle);
            return () => handle;
        }
        catch (Exception)
        {
            // value types living in frames or inside other objects cannot be pinned down; re-read them via the getter
            return getter;
        }
    }

    private void ReleaseStrongHandles()
    {
        foreach (CorDebugHandleValue handle in _strongHandles)
        {
            try
            {
                handle.Dispose();
            }
            catch (Exception)
            {
            }
        }
        _strongHandles.Clear();
    }
}
