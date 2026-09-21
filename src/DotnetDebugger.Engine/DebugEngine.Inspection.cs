using ClrDebug;
using DotnetDebugger.Engine.Symbols;
using DotnetDebugger.Engine.Values;

namespace DotnetDebugger.Engine;

public sealed partial class DebugEngine
{
    private const string ExternalCode = "[External Code]";

    private sealed class VariableContainer(Func<int, int, List<VariableInfo>> provider, InspectionContext? context)
    {
        /// <summary>The value of a lazy variable: asking for it is the user's decision to run its getter.</summary>
        public bool IsLazy { get; init; }
        public Func<int, int, List<VariableInfo>> Provider { get; } = provider;
        public InspectionContext? Context { get; } = context;
        public List<VariableInfo>? LastChildren { get; set; }
    }

    // Handles are only valid while the process is stopped; ClearStopState drops them. Frames are remembered by
    // position rather than by ICorDebug object because a func-eval invalidates those objects.
    private readonly Dictionary<int, FrameRef?> _frames = [];
    private readonly Dictionary<int, List<FrameInfo>> _threadFrames = [];
    private readonly Dictionary<int, FrameRef> _externalFrames = [];
    private readonly Dictionary<int, VariableContainer> _variableHandles = [];
    private int _nextHandle;

    /// <summary>What the method the user just stepped out of returned; valid until the debuggee runs again.</summary>
    private sealed record ReturnedValue(int ThreadId, string Method, Func<CorDebugValue?>? Getter, object? HostValue, CorDebugClass? EnumClass);

    // the most recent one is "$ReturnValue"; a step over a line with several calls collects them all
    private readonly List<ReturnedValue> _returnValues = [];

    private ReturnedValue? _returnValue
    {
        get => _returnValues.LastOrDefault();
        set
        {
            _returnValues.Clear();
            if (value != null)
                _returnValues.Add(value);
        }
    }

    // ---------------------------------------------------------------- stack

    public IReadOnlyList<FrameInfo> GetStackTrace(int threadId)
    {
        lock (_lock)
        {
            RequireStopped();
            if (_threadFrames.TryGetValue(threadId, out List<FrameInfo>? cached))
                return cached;

            var result = new List<FrameInfo>();
            List<CorDebugILFrame> frames = EnumerateILFrames(threadId).ToList();
            for (int i = 0; i < frames.Count; i++)
                AddFrame(result, frames[i], new FrameRef(threadId, frames.Count - 1 - i));
            try
            {
                AppendAsyncCallStack(threadId, frames, result);
            }
            catch (Exception e)
            {
                Log?.Invoke("Async call stack not available: " + e.Message);
            }
            return _threadFrames[threadId] = result;
        }
    }

    private IEnumerable<CorDebugILFrame> EnumerateILFrames(int threadId)
    {
        CorDebugThread thread = RequireThread(threadId);
        foreach (CorDebugChain chain in thread.Chains)
        {
            if (!chain.IsManaged)
                continue;
            foreach (CorDebugFrame frame in chain.Frames)
            {
                if (frame is CorDebugILFrame ilFrame)
                    yield return ilFrame;
            }
        }
    }

    // frames of the async call stack: there is no real frame, the state lives in a state machine object
    private readonly Dictionary<int, (int ThreadId, Func<CorDebugValue?> StateMachine)> _logicalFrames = [];

    /// <summary>
    /// A continuation runs on a bare thread pool stack: what called it is history. The logical callers are still
    /// known though: every async method's task remembers the state machine that awaits it.
    /// </summary>
    private void AppendAsyncCallStack(int threadId, List<CorDebugILFrame> frames, List<FrameInfo> result)
    {
        // start from the outermost async method that is physically on the stack; its awaiters are not
        CorDebugILFrame? outermost = frames.LastOrDefault(f =>
            GetMetadata(f.Function.Module)?.GetAsyncSteppingInfo((int)f.Function.Token.Value) != null);
        CorDebugValue? stateMachine = outermost?.GetArgument(0);

        bool labelled = false;
        for (int depth = 0; depth < 64 && stateMachine != null; depth++)
        {
            CorDebugValue? awaiting = GetAwaitingStateMachine(stateMachine);
            CorDebugValue? unwrapped = awaiting == null ? null : ValueInspector.Unwrap(awaiting, out _);
            if (awaiting == null || unwrapped == null)
                break;
            stateMachine = awaiting;

            CorDebugClass cls = unwrapped.ExactType.Class;
            ModuleMetadata? metadata = GetMetadata(cls.Module);
            MethodDescription? moveNext = metadata?.GetMethods((int)cls.Token.Value, "MoveNext").FirstOrDefault();
            if (metadata == null || moveNext == null || (_justMyCode && !metadata.HasSymbols))
                continue;

            // <>1__state is the index of the await the method is suspended at
            SourceLocation? location = null;
            if (metadata.GetAsyncSteppingInfo(moveNext.Token) is { } info
                && _values.GetFieldByName(awaiting, "<>1__state") is { } stateField
                && ValueInspector.ReadPrimitive(stateField) is int state && state >= 0 && state < info.Awaits.Count)
            {
                location = metadata.GetNearestSourceLocation(moveNext.Token, info.Awaits[state].YieldOffset);
            }

            if (!labelled)
            {
                int labelId = ++_nextHandle;
                _frames[labelId] = null;
                result.Add(new FrameInfo(labelId, "[Async Call Stack]", null, 0, 0, 0, 0, PresentationHint: "label"));
                labelled = true;
            }

            int id = ++_nextHandle;
            _frames[id] = null;
            CorDebugValue captured = awaiting;
            _logicalFrames[id] = (threadId, Stabilize(() => captured));
            string name = metadata.GetMethodName(moveNext.Token) + "()";
            if (location is { } l)
            {
                _modules.TryGetValue(cls.Module.BaseAddress.Value, out LoadedModule? module);
                (string path, int sourceReference) = DescribeSource(module, l.Path);
                result.Add(new FrameInfo(id, name, path, l.Line, l.Column, l.EndLine, l.EndColumn, sourceReference));
            }
            else
            {
                result.Add(new FrameInfo(id, name, null, 0, 0, 0, 0));
            }
        }
    }

    private CorDebugILFrame? GetILFrame(FrameRef frame)
    {
        List<CorDebugILFrame> frames = EnumerateILFrames(frame.ThreadId).ToList();
        int index = frames.Count - 1 - frame.Depth;
        return index >= 0 && index < frames.Count ? frames[index] : null;
    }

    private FrameRef TopFrame(int threadId) => new(threadId, Math.Max(0, EnumerateILFrames(threadId).Count() - 1));

    private CorDebugILFrame RequireILFrame(FrameRef frame) =>
        GetILFrame(frame) ?? throw new DebuggerException("The stack frame is no longer available.");

    private void AddFrame(List<FrameInfo> result, CorDebugILFrame frame, FrameRef frameRef)
    {
        CorDebugFunction function = frame.Function;
        int token = (int)function.Token.Value;
        ModuleMetadata? metadata = GetMetadata(function.Module);
        SourceLocation? location = metadata?.GetSourceLocation(token, frame.IP.pnOffset, out _);

        if (_justMyCode && (location == null || !IsUserCode(function)))
        {
            // Collapse runs of non-user frames into a single placeholder.
            if (result.Count > 0 && result[^1].Name == ExternalCode)
                return;
            int externalId = ++_nextHandle;
            _frames[externalId] = null;
            _externalFrames[externalId] = frameRef;
            result.Add(new FrameInfo(externalId, ExternalCode, null, 0, 0, 0, 0));
            return;
        }

        string name = metadata?.GetMethodName(token, GetTypeArgumentNames(frame)) ?? $"<method 0x{token:X8}>";
        int id = ++_nextHandle;
        _frames[id] = frameRef;
        if (location is { } l)
        {
            _modules.TryGetValue(function.Module.BaseAddress.Value, out LoadedModule? module);
            (string path, int sourceReference) = DescribeSource(module, l.Path);
            result.Add(new FrameInfo(id, name + "()", path, l.Line, l.Column, l.EndLine, l.EndColumn, sourceReference));
        }
        else
        {
            result.Add(new FrameInfo(id, name + "()", null, 0, 0, 0, 0));
        }
    }

    // Exact type arguments of the frame; null entries where the runtime shares code between reference types.
    private List<string?>? GetTypeArgumentNames(CorDebugILFrame frame)
    {
        try
        {
            CorDebugType[] typeParameters = frame.TypeParameters;
            if (typeParameters.Length == 0)
                return null;
            return typeParameters.Select(t =>
            {
                string name = _values.TypeName(t);
                return name == "System.__Canon" ? null : name;
            }).ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Code with symbols that is not marked [DebuggerHidden]/[DebuggerStepThrough]/[DebuggerNonUserCode].</summary>
    private bool IsUserCode(CorDebugFunction function)
    {
        try
        {
            return GetMetadata(function.Module)?.HasSymbols == true && function.JMCStatus;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private FrameRef ResolveFrame(int frameId)
    {
        if (!_frames.TryGetValue(frameId, out FrameRef? frame))
            throw new DebuggerException($"Unknown frame {frameId}.");
        if (_logicalFrames.ContainsKey(frameId))
            throw new DebuggerException("Expressions cannot be evaluated in frames of the async call stack: the method is suspended.");
        // Behind an [External Code] placeholder there is a real frame. It has no names to offer, but what belongs to
        // the thread ($exception) or to nobody (statics, literals) can be evaluated there as anywhere else.
        return frame ?? _externalFrames.GetValueOrDefault(frameId)
            ?? throw new DebuggerException("No information is available for external code.");
    }

    // ---------------------------------------------------------------- variables

    /// <summary>Returns the variables handle of the frame's locals, or 0 if the frame has none.</summary>
    public int GetLocalsHandle(int frameId)
    {
        lock (_lock)
        {
            RequireStopped();
            if (!_frames.TryGetValue(frameId, out FrameRef? frame))
                throw new DebuggerException($"Unknown frame {frameId}.");
            if (_logicalFrames.TryGetValue(frameId, out var logical))
            {
                // the variables of a suspended async method are the fields of its state machine
                var logicalContext = new InspectionContext(logical.ThreadId, TopFrame(logical.ThreadId));
                return RegisterContainer((_, _) =>
                {
                    var variables = new List<(string Name, Func<CorDebugValue?> Getter)>();
                    AddHoistedVariables(variables, logical.StateMachine, 0);
                    return variables.Select(v => _values.Describe(v.Name, v.Getter, null, logicalContext, _requestOptions)).ToList();
                }, logicalContext);
            }
            if (frame == null)
                return 0;
            var context = new InspectionContext(frame.ThreadId, frame);
            return RegisterContainer((_, _) => GetLocals(frame, context, _requestOptions), context);
        }
    }

    // Only the locals scope consults this: everything below it inherits the options it was described with.
    private DisplayOptions? _requestOptions;

    private int RegisterContainer(Func<int, int, List<VariableInfo>> provider, InspectionContext? context, bool isLazy = false)
    {
        int handle = ++_nextHandle;
        _variableHandles[handle] = new VariableContainer(provider, context) { IsLazy = isLazy };
        return handle;
    }

    private (VariableInfo Variable, int Handle) WithHandle(VariableInfo variable) =>
        (variable, variable.ChildrenProvider is { } provider ? RegisterContainer(provider, variable.Context, variable.IsLazy) : 0);

    /// <param name="hex">Show integers in hexadecimal (the client's "value format").</param>
    public IReadOnlyList<(VariableInfo Variable, int Handle)> GetVariables(int handle, int start = 0, int count = 0, bool hex = false)
    {
        lock (_lock)
        {
            RequireStopped();
            if (!_variableHandles.TryGetValue(handle, out VariableContainer? container))
                throw new DebuggerException($"Unknown variables reference {handle}.");

            StartImplicitEvalBudget();
            _requestOptions = hex ? new DisplayOptions(Hex: true) : null;
            _explicitExpansion = container.IsLazy;
            try
            {
                List<VariableInfo> children = container.Provider(start, count);
                container.LastChildren = children;
                return children.Select(WithHandle).ToList();
            }
            finally
            {
                _explicitExpansion = false;
            }
        }
    }

    private List<VariableInfo> GetLocals(FrameRef frame, InspectionContext context, DisplayOptions? options = null)
    {
        var result = new List<VariableInfo>();

        // pseudo variables first: what was just returned, and the exception in flight on this thread
        bool isTopFrame = _returnValues.Count > 0 && frame == TopFrame(frame.ThreadId);
        foreach (ReturnedValue returned in _returnValues.Where(r => isTopFrame && r.ThreadId == frame.ThreadId))
        {
            string name = returned.Method + " returned";
            VariableInfo value = returned.Getter != null
                ? _values.Describe(name, returned.Getter, "$ReturnValue", context, options)
                : _values.DescribeHost(name, returned.HostValue, returned.EnumClass == null ? null : _values.FormatEnum(returned.EnumClass, ToBits(returned.HostValue!)), options);
            // not a storage location
            result.Add(new VariableInfo
            {
                Name = name, Value = value.Value, Type = value.Type, EvaluateName = "$ReturnValue", Context = context,
                IndexedChildren = value.IndexedChildren, ChildrenProvider = value.ChildrenProvider,
            });
        }

        Func<CorDebugValue?> exception = () => RequireThread(frame.ThreadId).CurrentException;
        if (TryGet(exception) is { } current && ValueInspector.Unwrap(current, out bool isNull) != null && !isNull)
        {
            VariableInfo value = _values.Describe("$exception", Stabilize(exception), "$exception", context, options);
            result.Add(new VariableInfo
            {
                Name = value.Name, Value = value.Value, Type = value.Type, EvaluateName = value.EvaluateName, Context = context,
                ChildrenProvider = value.ChildrenProvider,
            });
        }

        result.AddRange(GetLocalRefs(frame).Select(l => _values.Describe(l.Name, l.Getter, l.Name, context, options)));
        return result;
    }

    private static ulong ToBits(object value) => value is ulong u ? u : unchecked((ulong)Convert.ToInt64(value));

    private static T? TryGet<T>(Func<T?> func) where T : class
    {
        try
        {
            return func();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Arguments and locals of a frame as the user declared them. Variables the compiler moved into closure or state
    /// machine objects are presented under their original names.
    /// </summary>
    private List<(string Name, Func<CorDebugValue?> Getter)> GetLocalRefs(FrameRef frameRef)
    {
        var plain = new List<(string Name, Func<CorDebugValue?> Getter)>();
        var hoisted = new List<(string Name, Func<CorDebugValue?> Getter)>();

        CorDebugILFrame frame = RequireILFrame(frameRef);
        CorDebugFunction function = frame.Function;
        int token = (int)function.Token.Value;
        ModuleMetadata? metadata = GetMetadata(function.Module);

        int argumentCount = 0;
        try
        {
            argumentCount = frame.Arguments.Length;
        }
        catch (Exception)
        {
        }

        bool isStatic = true;
        string[] parameterNames = [];
        bool compilerGeneratedType = false;
        if (metadata != null)
        {
            (isStatic, parameterNames) = metadata.GetMethodParameters(token);
            compilerGeneratedType = metadata.IsDeclaredInCompilerGeneratedType(token);
        }

        for (int i = 0; i < argumentCount; i++)
        {
            int argumentIndex = i;
            Func<CorDebugValue?> getter = () => GetILFrame(frameRef)?.GetArgument(argumentIndex);
            if (i == 0 && !isStatic)
            {
                // State machines and closures: show what the user wrote instead of the generated object.
                if (compilerGeneratedType)
                    AddHoistedVariables(hoisted, getter, 0);
                else
                    plain.Add(("this", getter));
                continue;
            }
            int nameIndex = isStatic ? i : i - 1;
            plain.Add((nameIndex < parameterNames.Length ? parameterNames[nameIndex] : $"arg{i}", getter));
        }

        if (metadata != null)
        {
            foreach ((int index, string name) in metadata.GetLocals(token, frame.IP.pnOffset))
            {
                Func<CorDebugValue?> getter = () => GetILFrame(frameRef)?.GetLocalVariable(index);
                if (name.StartsWith("CS$<>8__locals", StringComparison.Ordinal))
                    AddHoistedVariables(hoisted, getter, 0);
                else if (!name.StartsWith('<') && !name.StartsWith("CS$", StringComparison.Ordinal))
                    plain.Add((name, getter));
            }
        }

        // A captured parameter exists twice: the stale original and the closure field the code really uses.
        var hoistedNames = hoisted.Select(v => v.Name).ToHashSet();
        plain.RemoveAll(v => hoistedNames.Contains(v.Name));
        int thisIndex = hoisted.FindIndex(v => v.Name == "this");
        if (thisIndex >= 0)
        {
            plain.Insert(0, hoisted[thisIndex]);
            hoisted.RemoveAt(thisIndex);
        }
        plain.AddRange(hoisted);
        return plain;
    }

    // Fields of compiler generated classes:  <>4__this -> this,  <x>5__1 -> x,  x -> x (captured),
    // <>8__1 -> nested closure to flatten, everything else (<>1__state, <>t__builder, ...) is noise.
    private void AddHoistedVariables(List<(string Name, Func<CorDebugValue?> Getter)> result, Func<CorDebugValue?> containerGetter, int depth)
    {
        CorDebugValue? container;
        try
        {
            container = containerGetter();
        }
        catch (Exception)
        {
            return;
        }
        CorDebugValue? target = container == null ? null : ValueInspector.Unwrap(container, out _);
        if (target == null)
            return;

        foreach (ValueInspector.TypeLevel level in _values.GetTypeLevels(target))
        {
            CorDebugClass cls = level.Type.Class;
            foreach (FieldDescription field in level.Metadata.GetFields(level.Token))
            {
                if (field.IsStatic || field.IsLiteral)
                    continue;
                int fieldToken = field.Token;
                Func<CorDebugValue?> getter = () =>
                {
                    CorDebugValue? owner = containerGetter() is { } v ? ValueInspector.Unwrap(v, out _) : null;
                    return owner?.As<CorDebugObjectValue>().GetFieldValue(cls.Raw, new mdFieldDef(fieldToken));
                };

                string fieldName = field.Name;
                if (fieldName == "<>4__this")
                {
                    result.Add(("this", getter));
                }
                else if (fieldName.StartsWith("<>8__", StringComparison.Ordinal) || fieldName.StartsWith("CS$<>8__locals", StringComparison.Ordinal))
                {
                    if (depth < 4)
                        AddHoistedVariables(result, getter, depth + 1);
                }
                else if (fieldName.StartsWith('<'))
                {
                    int close = fieldName.IndexOf('>');
                    if (close > 1 && fieldName.AsSpan(close + 1).StartsWith("5__"))
                        result.Add((fieldName[1..close], getter));
                }
                else
                {
                    result.Add((fieldName, getter));
                }
            }
        }
    }

    private Dictionary<string, string?[]> GetLocalTupleElementNames(FrameRef frameRef)
    {
        try
        {
            CorDebugILFrame frame = RequireILFrame(frameRef);
            CorDebugFunction function = frame.Function;
            return GetMetadata(function.Module)?.GetLocalTupleElementNames((int)function.Token.Value, frame.IP.pnOffset) ?? [];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }

    // ---------------------------------------------------------------- evaluate / set

    /// <param name="allowCalls">false for side-effect free contexts such as mouse hovers.</param>
    /// <param name="fullStrings">Strings are returned whole, however long (the "clipboard" context).</param>
    public (VariableInfo Variable, int Handle) Evaluate(string expression, int? frameId, bool allowCalls = true, bool hex = false, bool fullStrings = false)
    {
        lock (_lock)
        {
            RequireStopped();
            StartImplicitEvalBudget();
            if (frameId == null)
                throw new DebuggerException("Expressions can only be evaluated in the context of a stack frame.");
            FrameRef frame = ResolveFrame(frameId.Value);
            var evaluator = new Evaluator(this, frame, allowCalls);
            return WithHandle(evaluator.EvaluateToVariable(expression, new DisplayOptions(Hex: hex, FullStrings: fullStrings)));
        }
    }

    public (VariableInfo Variable, int Handle) SetVariable(int containerHandle, string name, string expression)
    {
        lock (_lock)
        {
            RequireStopped();
            if (!_variableHandles.TryGetValue(containerHandle, out VariableContainer? container))
                throw new DebuggerException($"Unknown variables reference {containerHandle}.");

            VariableInfo variable = (container.LastChildren ?? container.Provider(0, 0)).FirstOrDefault(v => v.Name == name)
                ?? throw new DebuggerException($"Variable '{name}' not found.");
            FrameRef frame = container.Context?.Frame ?? throw new DebuggerException("No frame to evaluate the new value in.");
            StartImplicitEvalBudget();
            var evaluator = new Evaluator(this, frame, allowCalls: true);
            container.LastChildren = null;

            if (variable.Location != null)
            {
                evaluator.Assign(variable.Location, expression);
                return WithHandle(_values.Describe(name, variable.Location, variable.EvaluateName, container.Context));
            }

            // not a storage location: a property, which is set by running its setter
            if (variable.EvaluateName == null || variable.EvaluateName.StartsWith('$'))
                throw new DebuggerException($"'{name}' cannot be assigned to.");
            evaluator.EvaluateToVariable($"{variable.EvaluateName} = ({expression})");
            VariableInfo updated = evaluator.EvaluateToVariable(variable.EvaluateName);
            return WithHandle(new VariableInfo
            {
                Name = name, Value = updated.Value, Type = updated.Type, EvaluateName = variable.EvaluateName, Context = updated.Context,
                IndexedChildren = updated.IndexedChildren, ChildrenProvider = updated.ChildrenProvider,
            });
        }
    }
}
