using ClrDebug;
using DotnetDebugger.Engine.Symbols;
using DotnetDebugger.Engine.Values;

namespace DotnetDebugger.Engine;

public sealed partial class DebugEngine
{
    /// <summary>
    /// A step over an "await" that really suspends. The ICorDebug stepper would follow the thread out of the method,
    /// so instead breakpoints watch the await's yield point and then the place where the method continues.
    /// </summary>
    private sealed class AsyncStep
    {
        public required int ThreadId { get; init; }
        public required LoadedModule Module { get; init; }
        public required int MethodToken { get; init; }
        public Dictionary<CodeLocation, int> YieldToResumeOffset { get; } = [];
        public HashSet<CodeLocation> Resume { get; } = [];

        /// <summary>
        /// Strong handle to the state machine object the step belongs to: the same async method may be running many
        /// times at once, and only this invocation's continuation is the one being stepped. Null when unknown.
        /// </summary>
        public CorDebugHandleValue? StateMachine { get; set; }
    }

    private CorDebugStepper? _activeStepper;
    private StepKind _activeStepKind;
    private bool _stepFiltering = true;
    private bool _leavingFilteredMethod;
    private string? _stepOriginMethod;
    private AsyncStep? _asyncStep;

    public void Step(int threadId, StepKind kind)
    {
        lock (_lock)
        {
            CorDebugProcess process = RequireStopped();
            CorDebugThread thread = process.GetThread(threadId);
            ClearAsyncStep();
            _leavingFilteredMethod = false;
            _stepOriginMethod = GetMethodName(thread.ActiveFrame);
            ClearReturnSites();

            // Stepping out of an async method means waiting for whoever awaits it, not for the (framework) caller.
            if (kind == StepKind.Out && ArmAsyncStepOut(thread))
            {
                ResumeProcess(process);
                return;
            }

            StartStep(thread, kind);
            if (kind != StepKind.Out)
            {
                ArmAsyncStep(thread);
                ArmReturnSites(thread);
            }
            ResumeProcess(process);
        }
    }

    private void StartStep(CorDebugThread thread, StepKind kind)
    {
        CorDebugFrame? frame = thread.ActiveFrame;
        CorDebugStepper stepper = frame != null ? frame.CreateStepper() : thread.CreateStepper();
        stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
        stepper.SetJMC(_justMyCode);

        (int Start, int End)? range = null;
        if (kind != StepKind.Out && frame is CorDebugILFrame ilFrame)
        {
            CorDebugFunction function = ilFrame.Function;
            range = GetMetadata(function.Module)?.GetStepRange((int)function.Token.Value, ilFrame.IP.pnOffset, function.ILCode.Size);
        }

        if (kind == StepKind.Out)
        {
            stepper.StepOut();
        }
        else if (range is { } r)
        {
            stepper.SetRangeIL(true);
            var ranges = new[] { new COR_DEBUG_STEP_RANGE { startOffset = r.Start, endOffset = r.End } };
            stepper.StepRange(kind == StepKind.In, ranges, ranges.Length);
        }
        else
        {
            stepper.Step(kind == StepKind.In);
        }

        _activeStepper = stepper;
        _activeStepKind = kind;
    }

    private void DeactivateStepper()
    {
        if (_activeStepper == null)
            return;
        try
        {
            if (_activeStepper.IsActive)
                _activeStepper.Deactivate();
        }
        catch (Exception)
        {
        }
        _activeStepper = null;
    }

    private EventAction OnStepComplete(StepCompleteCorDebugManagedCallbackEventArgs e)
    {
        _activeStepper = null;
        CorDebugThread thread = e.Thread;
        int threadId = thread.Id;

        if (thread.ActiveFrame is CorDebugILFrame frame)
        {
            CorDebugFunction function = frame.Function;
            ModuleMetadata? metadata = GetMetadata(function.Module);

            try
            {
                // Step filtering: "step in" does not stop in property accessors and operators. Entering one means
                // leaving it again and carrying on with the step-in from where it was called.
                if (_leavingFilteredMethod)
                {
                    _leavingFilteredMethod = false;
                    StartStep(thread, StepKind.In);
                    return EventAction.Continue;
                }
                if (_stepFiltering && _activeStepKind == StepKind.In && e.Reason == CorDebugStepReason.STEP_CALL
                    && metadata?.IsPropertyOrOperator((int)function.Token.Value) == true)
                {
                    StartStep(thread, StepKind.Out);
                    _leavingFilteredMethod = true;
                    return EventAction.Continue;
                }
            }
            catch (Exception ex)
            {
                _leavingFilteredMethod = false;
                Log?.Invoke("Step filtering failed: " + ex.Message);
            }

            // Landed somewhere without source lines: keep going until user-visible code is reached.
            bool hidden = false;
            SourceLocation? location = metadata?.GetSourceLocation((int)function.Token.Value, frame.IP.pnOffset, out hidden);
            if (location == null && (_justMyCode || hidden))
            {
                try
                {
                    StepKind next = !hidden ? StepKind.Out : _activeStepKind == StepKind.Out ? StepKind.Over : _activeStepKind;
                    StartStep(thread, next);
                    return EventAction.Continue;
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Re-step failed: " + ex.Message);
                }
            }
        }

        if (e.Reason == CorDebugStepReason.STEP_RETURN)
            CaptureReturnValue(thread);
        Post(() => Stopped?.Invoke(new StopInfo("step", threadId)));
        return EventAction.Stop;
    }

    private string? GetMethodName(CorDebugFrame? frame)
    {
        try
        {
            if (frame is not CorDebugILFrame ilFrame)
                return null;
            CorDebugFunction function = ilFrame.Function;
            return GetMetadata(function.Module)?.GetMethodName((int)function.Token.Value, GetTypeArgumentNames(ilFrame)) + "()";
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Right after a return the result still sits in the return register. ICorDebug hands it out for the call site
    // whose "return value live" native offset is the current instruction pointer.
    private void CaptureReturnValue(CorDebugThread thread)
    {
        try
        {
            if (_stepOriginMethod == null || thread.ActiveFrame is not CorDebugILFrame frame)
                return;
            CorDebugFunction function = frame.Function;
            ModuleMetadata? metadata = GetMetadata(function.Module);
            if (metadata == null)
                return;

            int nativeIp = new CorDebugNativeFrame((ICorDebugNativeFrame)frame.Raw).IP;
            CorDebugCode nativeCode = function.NativeCode;
            foreach (int callSite in metadata.GetCallSites((int)function.Token.Value))
            {
                int[] liveOffsets;
                try
                {
                    liveOffsets = nativeCode.GetReturnValueLiveOffset(callSite);
                }
                catch (Exception)
                {
                    continue;
                }
                if (!liveOffsets.Contains(nativeIp))
                    continue;

                RecordReturnValue(thread.Id, _stepOriginMethod, frame.GetReturnValueForILOffset(callSite));
                return;
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("No return value available: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- async

    private void ArmAsyncStep(CorDebugThread thread)
    {
        try
        {
            if (thread.ActiveFrame is not CorDebugILFrame frame)
                return;
            CorDebugFunction function = frame.Function;
            if (!_modules.TryGetValue(function.Module.BaseAddress.Value, out LoadedModule? module))
                return;
            int token = (int)function.Token.Value;
            if (module.Metadata?.GetAsyncSteppingInfo(token) is not { } info)
                return;

            var step = new AsyncStep { ThreadId = thread.Id, Module = module, MethodToken = token, StateMachine = TryCreateHandle(frame.GetArgument(0)) };
            foreach ((int yieldOffset, int resumeOffset) in info.Awaits)
                step.YieldToResumeOffset[AcquireNativeBreakpoint(module, token, yieldOffset)] = resumeOffset;
            _asyncStep = step;
        }
        catch (Exception e)
        {
            Log?.Invoke("Async stepping is not available: " + e.Message);
        }
    }

    /// <summary>
    /// For a step out of an async method: finds the async method awaiting it (the continuation registered on its task)
    /// and watches the places where that one resumes. False if there is no such caller, e.g. a blocking Wait().
    /// </summary>
    private bool ArmAsyncStepOut(CorDebugThread thread)
    {
        try
        {
            if (thread.ActiveFrame is not CorDebugILFrame frame)
                return false;
            CorDebugFunction function = frame.Function;
            if (GetMetadata(function.Module)?.GetAsyncSteppingInfo((int)function.Token.Value) == null)
                return false;

            CorDebugValue? callerStateMachine = GetAwaitingStateMachine(frame.GetArgument(0));
            CorDebugValue? unwrapped = callerStateMachine == null ? null : ValueInspector.Unwrap(callerStateMachine, out _);
            if (unwrapped == null)
                return false;

            CorDebugClass callerClass = unwrapped.ExactType.Class;
            if (!_modules.TryGetValue(callerClass.Module.BaseAddress.Value, out LoadedModule? callerModule) || callerModule.Metadata == null)
                return false;
            MethodDescription? moveNext = callerModule.Metadata.GetMethods((int)callerClass.Token.Value, "MoveNext").FirstOrDefault();
            if (moveNext == null || callerModule.Metadata.GetAsyncSteppingInfo(moveNext.Token) is not { } info)
                return false;

            var step = new AsyncStep { ThreadId = thread.Id, Module = callerModule, MethodToken = moveNext.Token, StateMachine = TryCreateHandle(callerStateMachine) };
            foreach ((_, int resumeOffset) in info.Awaits)
                step.Resume.Add(AcquireNativeBreakpoint(callerModule, moveNext.Token, resumeOffset));
            _asyncStep = step;
            _activeStepKind = StepKind.Out;
            return true;
        }
        catch (Exception e)
        {
            Log?.Invoke("Async step out is not available: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// The state machine of the async method that awaits the one owning <paramref name="stateMachine"/>:
    /// this.&lt;&gt;t__builder.m_task.m_continuationObject is the awaiter's AsyncStateMachineBox, which holds its state machine.
    /// Null when nobody awaits it that way (blocking waits, ContinueWith, several continuations).
    /// </summary>
    private CorDebugValue? GetAwaitingStateMachine(CorDebugValue? stateMachine)
    {
        CorDebugValue? builder = stateMachine == null ? null : _values.GetFieldByName(stateMachine, "<>t__builder");
        CorDebugValue? task = builder == null ? null : _values.GetFieldByName(builder, "m_task");
        CorDebugValue? continuation = task == null ? null : _values.GetFieldByName(task, "m_continuationObject");
        CorDebugValue? awaiting = continuation == null ? null : _values.GetFieldByName(continuation, "StateMachine");
        return awaiting != null && ValueInspector.Unwrap(awaiting, out bool isNull) != null && !isNull ? awaiting : null;
    }

    // Not tracked with the per-stop handles: this one has to survive while the debuggee runs.
    private static CorDebugHandleValue? TryCreateHandle(CorDebugValue? value)
    {
        try
        {
            if (value is not CorDebugReferenceValue { IsNull: false } reference)
                return null; // struct state machines (optimized builds) have no identity before they are boxed
            return reference.Dereference().As<CorDebugHeapValue>().CreateHandle(CorDebugHandleType.HANDLE_STRONG);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ClearAsyncStep()
    {
        if (_asyncStep == null)
            return;
        foreach (CodeLocation location in _asyncStep.YieldToResumeOffset.Keys)
            ReleaseNativeBreakpoint(location);
        foreach (CodeLocation resume in _asyncStep.Resume)
            ReleaseNativeBreakpoint(resume);
        try
        {
            _asyncStep.StateMachine?.Dispose();
        }
        catch (Exception)
        {
        }
        _asyncStep = null;
    }

    /// <returns>null if the breakpoint has nothing to do with async stepping.</returns>
    private EventAction? OnAsyncStepBreakpoint(CorDebugThread thread, CodeLocation location)
    {
        if (_asyncStep is not { } step)
            return null;

        if (step.Resume.Contains(location))
        {
            // another invocation of the same async method resuming: not the one being stepped
            if (step.StateMachine != null && !IsSameObject(step.StateMachine, TryGet(() => (thread.ActiveFrame as CorDebugILFrame)?.GetArgument(0))))
                return AllBreakpoints.Any(b => b.Bound.Contains(location)) ? null : EventAction.Continue;

            // The method continues (possibly on another thread): finish the step from here to the next line.
            ClearAsyncStep();
            StartStep(thread, StepKind.Over);
            return EventAction.Continue;
        }

        if (thread.Id == step.ThreadId && step.YieldToResumeOffset.TryGetValue(location, out int resumeOffset))
        {
            // The awaited operation is not finished, the method is about to return to its caller.
            DeactivateStepper();
            foreach (CodeLocation yield in step.YieldToResumeOffset.Keys)
                ReleaseNativeBreakpoint(yield);
            step.YieldToResumeOffset.Clear();
            step.Resume.Add(AcquireNativeBreakpoint(step.Module, step.MethodToken, resumeOffset));
            return AllBreakpoints.Any(b => b.Bound.Contains(location)) ? null : EventAction.Continue;
        }
        return null;
    }

    private static bool IsSameObject(CorDebugHandleValue expected, CorDebugValue? actual)
    {
        try
        {
            return actual is CorDebugReferenceValue { IsNull: false } reference && reference.Value.Value == expected.Value.Value;
        }
        catch (Exception)
        {
            return true; // when in doubt, stop
        }
    }

    // ---------------------------------------------------------------- return values of stepped-over calls

    private sealed record ReturnSite(CorDebugFunctionBreakpoint Breakpoint, int CallSite, string Method);

    private readonly List<ReturnSite> _returnSites = [];

    // Native breakpoints right behind every call of the statement being stepped over: that is the only moment at
    // which ICorDebug can still produce the value a call returned.
    private void ArmReturnSites(CorDebugThread thread)
    {
        try
        {
            if (thread.ActiveFrame is not CorDebugILFrame frame)
                return;
            CorDebugFunction function = frame.Function;
            ModuleMetadata? metadata = GetMetadata(function.Module);
            int token = (int)function.Token.Value;
            if (metadata?.GetStepRange(token, frame.IP.pnOffset, function.ILCode.Size) is not { } range)
                return;

            CorDebugCode nativeCode = function.NativeCode;
            foreach (int callSite in metadata.GetCallSites(token).Where(c => c >= range.Start && c < range.End))
            {
                if (metadata.GetCalledMethodName(token, callSite) is not { } method)
                    continue;
                foreach (int nativeOffset in nativeCode.GetReturnValueLiveOffset(callSite))
                {
                    CorDebugFunctionBreakpoint breakpoint = nativeCode.CreateBreakpoint(nativeOffset);
                    breakpoint.Activate(true);
                    _returnSites.Add(new ReturnSite(breakpoint, callSite, method + "()"));
                }
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("Return values are not available for this step: " + e.Message);
        }
    }

    private void ClearReturnSites()
    {
        foreach (ReturnSite site in _returnSites)
        {
            try
            {
                site.Breakpoint.Activate(false);
            }
            catch (Exception)
            {
            }
        }
        _returnSites.Clear();
    }

    /// <returns>true if the breakpoint was one of the return sites (never a reason to stop).</returns>
    private bool OnReturnSiteBreakpoint(CorDebugThread thread, CorDebugFunctionBreakpoint hit)
    {
        ReturnSite? site = _returnSites.FirstOrDefault(s => s.Breakpoint.Equals(hit));
        if (site == null)
            return false;
        try
        {
            if (thread.ActiveFrame is CorDebugILFrame frame)
                RecordReturnValue(thread.Id, site.Method, frame.GetReturnValueForILOffset(site.CallSite));
        }
        catch (Exception e)
        {
            Log?.Invoke($"No return value for {site.Method}: {e.Message}"); // void methods end up here
        }
        return true;
    }

    private void RecordReturnValue(int threadId, string method, CorDebugValue value)
    {
        ReturnedValue returned;
        if (_values.TryReadHostValue(value, out object? host))
        {
            returned = host is ValueInspector.EnumBits bits
                ? new ReturnedValue(threadId, method, null, bits.Underlying, bits.Class)
                : new ReturnedValue(threadId, method, null, host, null);
        }
        else
        {
            returned = new ReturnedValue(threadId, method, Stabilize(() => value), null, null);
        }
        _returnValues.RemoveAll(r => r.Method == method);
        _returnValues.Add(returned);
    }
}
