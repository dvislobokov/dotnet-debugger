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

    // Evaluations nobody asked for (ToString(), [DebuggerDisplay], the getters of an expanded object) get less patience.
    private static readonly TimeSpan ImplicitEvalTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AbortTimeout = TimeSpan.FromSeconds(1);

    // Time one request may spend on evaluations nobody asked for explicitly (ToString(), [DebuggerDisplay], property
    // getters of an expanded object). Once it is used up values fall back to type names and properties become lazy.
    private static readonly TimeSpan ImplicitEvalBudget = TimeSpan.FromSeconds(1);

    private DateTime _implicitEvalDeadline = DateTime.MaxValue;

    private void StartImplicitEvalBudget() => _implicitEvalDeadline = DateTime.UtcNow + ImplicitEvalBudget;

    /// <summary>
    /// Whether values are described by running code of the debuggee: ToString(), [DebuggerDisplay] expressions, property
    /// getters, type proxies. Without it objects show their type and fields; properties are evaluated when asked for.
    /// </summary>
    public bool AllowImplicitFuncEval { get; set; } = true;

    // > 0 while the inspector runs code on its own account
    private int _implicitScope;

    // an implicit evaluation timed out: whatever hangs there would hang again on every refresh of the view
    private bool _implicitEvalTimedOut;

    // the user asked for this very value (expanded a lazy property): not an implicit evaluation, whoever runs it
    private bool _explicitExpansion;

    // threads an evaluation was abandoned on: they are still inside it, nothing else can run there
    private readonly List<(CorDebugEval Eval, int ThreadId)> _abandonedEvals = [];

    bool IEvalHost.BudgetExceeded =>
        !_explicitExpansion && (!AllowImplicitFuncEval || _implicitEvalTimedOut || DateTime.UtcNow > _implicitEvalDeadline);

    string IEvalHost.FormatDebuggerDisplay(string template, Func<CorDebugValue?> self, InspectionContext context)
    {
        _implicitScope++;
        try
        {
            return FormatLogMessage(template, new Evaluator(this, context.Frame!, allowCalls: true, self), quoteStrings: true);
        }
        finally
        {
            _implicitScope--;
        }
    }

    // Set without the engine lock, which the request being cancelled holds: long enumerations look at these.
    private volatile bool _requestCancelled;
    private volatile bool _shuttingDown;

    /// <summary>
    /// The client no longer wants the answer of the request that is running. Never blocks: a request that is busy
    /// reading the debuggee notices by itself, a running evaluation is aborted in the background.
    /// </summary>
    /// <param name="sessionEnds">Nothing that comes later is wanted either (disconnect).</param>
    public void CancelCurrentRequest(bool sessionEnds = false)
    {
        _requestCancelled = true;
        _shuttingDown |= sessionEnds;
        Task.Run(CancelEvaluation);
    }

    /// <summary>A new request begins: a cancellation that came for the previous one is not meant for it.</summary>
    public void ResetCancellation() => _requestCancelled = false;

    void IEvalHost.ThrowIfCancelled()
    {
        if (_requestCancelled || _shuttingDown)
            throw new OperationCanceledException();
    }

    /// <summary>Aborts the evaluation that is currently running, if any.</summary>
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
        if (_abandonedEvals.Any(a => a.ThreadId == threadId))
            throw new EvalFailedException(AbandonedEvaluationMessage);
        bool isImplicit = _implicitScope > 0 && !_explicitExpansion;

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
            throw new EvalFailedException("Evaluation not possible: " + ErrorText.Describe(e));
        }

        var pending = new PendingEval(eval);
        _pendingEval = pending;
        try
        {
            // Only the evaluating thread runs, so the evaluation cannot be blocked on (or disturb) other threads' progress.
            process.SetAllThreadsDebugState(CorDebugThreadState.THREAD_SUSPEND, thread.Raw);
            _stopped = false;
            process.Continue(false);

            if (!WaitForEval(pending, isImplicit ? ImplicitEvalTimeout : EvalTimeout))
            {
                // Timed out or cancelled. Aborting only works once the thread is back in managed code: an evaluation
                // stuck in a wait cannot be stopped at all. It is then left behind; it completes (unnoticed) whenever
                // the debuggee gets to run long enough.
                string reason = pending.Cancelled ? "Evaluation cancelled." : "Evaluation timed out.";
                Log?.Invoke(reason + " Aborting.");
                if (isImplicit && !pending.Cancelled && !_implicitEvalTimedOut)
                {
                    _implicitEvalTimedOut = true;
                    Output?.Invoke("console", "Describing a value took too long (ToString(), a property getter or a [DebuggerDisplay] expression does not return). " +
                        "Until the program continues values are shown without running its code; expressions are still evaluated." + Environment.NewLine);
                }
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
                        _abandonedEvals.Add((eval, threadId));
                        Output?.Invoke("important", "An evaluation could not be interrupted: the evaluated code is blocked inside the runtime (a lock, a wait, a sleep). " +
                            $"Thread {threadId} stays inside that evaluation: no further expressions can be evaluated on it, and when the program continues " +
                            "the thread will not get back to its own code before the evaluated code returns. If it never does, restart the session." + Environment.NewLine);
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

            // The client believes the debuggee is stopped. It is not if stopping it again failed (Synchronize threw).
            if (!_stopped && !_processExited && _process != null)
            {
                ClearStopState();
                Task.Run(() => Continued?.Invoke(threadId));
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
        // an evaluation that was given up on earlier may complete at any later time: its thread is usable again
        _abandonedEvals.RemoveAll(a => a.Eval.Equals(eval));
        if (_pendingEval == null || !_pendingEval.Eval.Equals(eval))
            return false;
        _pendingEval.Completed = true;
        _pendingEval.ThrewException = threwException;
        return true;
    }

    private const string AbandonedEvaluationMessage =
        "Evaluation is not possible on this thread: an earlier evaluation could not be interrupted and still occupies it.";

    CorDebugValue? IEvalHost.CallFunction(int threadId, CorDebugFunction function, CorDebugType[] typeArguments, CorDebugValue[] arguments, bool isImplicit)
    {
        _implicitScope += isImplicit ? 1 : 0;
        try
        {
            return RunEval(threadId, eval => eval.CallParameterizedFunction(
                function.Raw, typeArguments.Length, typeArguments.Select(t => t.Raw).ToArray(),
                arguments.Length, arguments.Select(a => a.Raw).ToArray()));
        }
        finally
        {
            _implicitScope -= isImplicit ? 1 : 0;
        }
    }

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
