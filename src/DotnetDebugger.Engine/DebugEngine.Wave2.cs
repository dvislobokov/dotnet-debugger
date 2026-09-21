using System.Text.RegularExpressions;
using ClrDebug;
using DotnetDebugger.Engine.Symbols;
using DotnetDebugger.Engine.Values;

namespace DotnetDebugger.Engine;

public sealed record GotoTargetInfo(int Id, string Label, int Line, int Column, int EndLine, int EndColumn);

/// <param name="Condition">Comma separated exception type names; "!" excludes, "*" is a wildcard; base types match too.</param>
public sealed record ExceptionFilterRequest(string Filter, string? Condition = null);

public sealed partial class DebugEngine
{
    // ---------------------------------------------------------------- expressions that change things

    public (VariableInfo Variable, int Handle) SetExpression(string expression, string value, int? frameId)
    {
        lock (_lock)
        {
            RequireStopped();
            if (frameId == null)
                throw new DebuggerException("Expressions can only be evaluated in the context of a stack frame.");
            StartImplicitEvalBudget();
            var evaluator = new Evaluator(this, ResolveFrame(frameId.Value), allowCalls: true);
            evaluator.EvaluateToVariable($"({expression}) = ({value})");
            return WithHandle(evaluator.EvaluateToVariable(expression));
        }
    }

    // ---------------------------------------------------------------- helpers for the value inspector

    Func<CorDebugValue?>? IEvalHost.CreateTypeProxy(string proxyTypeName, Func<CorDebugValue?> target, InspectionContext context)
    {
        if (FindType(proxyTypeName) is not { } found)
            return null;
        MethodDescription? constructor = found.Module.Metadata!.GetMethods(found.Token, ".ctor").FirstOrDefault(m => !m.IsStatic && m.ParameterTypes.Length == 1);
        CorDebugValue? self = target();
        CorDebugValue? unwrapped = self == null ? null : ValueInspector.Unwrap(self, out _);
        if (constructor == null || self == null || unwrapped == null)
            return null;

        // a generic proxy (ICollectionDebugView<T>) is instantiated over the type arguments of the object it shows
        ICorDebugType[] typeArguments = found.Module.Metadata.GetTypeGenericParameters(found.Token).Length == 0
            ? []
            : ValueInspector.TypeArgumentsOf(unwrapped.ExactType).Select(t => t.Raw).ToArray();
        CorDebugFunction function = found.Module.Module.GetFunctionFromToken(new mdMethodDef(constructor.Token));
        ICorDebugValue argument = (unwrapped.Type == CorElementType.ValueType ? unwrapped : self).Raw;
        CorDebugValue? proxy = RunEval(context.ThreadId, eval => eval.NewParameterizedObject(function.Raw, typeArguments.Length, typeArguments, 1, [argument]));
        return proxy == null ? null : Stabilize(() => proxy);
    }

    Func<CorDebugValue?> IEvalHost.EnumerateToArray(Func<CorDebugValue?> sequence, InspectionContext context)
    {
        // Enumerable.ToArray(Enumerable.Cast<object>(sequence)): works for any IEnumerable without knowing its element type
        var enumerable = FindType("System.Linq.Enumerable") ?? throw new DebuggerException("System.Linq is not loaded in the debuggee.");
        var objectType = FindType("System.Object") ?? throw new DebuggerException("System.Object not found.");
        ICorDebugType[] ofObject = [objectType.Module.Module.GetClassFromToken(new mdTypeDef(objectType.Token)).GetParameterizedType(CorElementType.Class, 0, []).Raw];

        CorDebugFunction Method(string name) => enumerable.Module.Module.GetFunctionFromToken(new mdMethodDef(
            enumerable.Module.Metadata!.GetMethods(enumerable.Token, name).First(m => m.IsStatic && m.ParameterTypes.Length == 1).Token));

        CorDebugFunction cast = Method("Cast"), toArray = Method("ToArray");
        ICorDebugValue source = sequence()?.Raw ?? throw new DebuggerException("The value is not available.");
        CorDebugValue? castResult = RunEval(context.ThreadId, eval => eval.CallParameterizedFunction(cast.Raw, 1, ofObject, 1, [source]));
        Func<CorDebugValue?> casted = Stabilize(() => castResult);
        CorDebugValue? array = RunEval(context.ThreadId, eval => eval.CallParameterizedFunction(toArray.Raw, 1, ofObject, 1, [casted()!.Raw]));
        return Stabilize(() => array);
    }

    byte[] IEvalHost.ReadMemory(ulong address, int size) => RequireProcess().ReadMemory(new CORDB_ADDRESS(address), size);

    CorDebugValue? IEvalHost.GetObjectAt(ulong address) => RequireProcess().GetObject(new CORDB_ADDRESS(address));

    // ---------------------------------------------------------------- set next statement

    private readonly Dictionary<int, (LoadedModule Module, ResolvedBreakpoint Location)> _gotoTargets = [];

    /// <summary>Places in the given line the instruction pointer of a stopped thread could be moved to.</summary>
    public IReadOnlyList<GotoTargetInfo> GetGotoTargets(string sourcePath, int line, int? column)
    {
        lock (_lock)
        {
            CorDebugProcess process = RequireStopped();
            string pdbPath = ToPdbPath(sourcePath);
            var result = new List<GotoTargetInfo>();
            foreach (LoadedModule module in _modules.Values)
            {
                if (module.Metadata?.ResolveBreakpoint(pdbPath, line, column) is not { } location || location.Line != line)
                    continue;
                // only within the method some thread is currently executing
                bool reachable = process.Threads.Any(t => TryGet(() => t.ActiveFrame) is CorDebugILFrame frame
                    && frame.Function.Token.Value == (uint)location.MethodToken
                    && frame.Function.Module.BaseAddress.Value == module.Module.BaseAddress.Value);
                if (!reachable)
                    continue;
                int id = ++_nextHandle;
                _gotoTargets[id] = (module, location);
                result.Add(new GotoTargetInfo(id, $"Line {location.Line}", location.Line, location.Column, location.EndLine, location.EndColumn));
            }
            return result;
        }
    }

    public void Goto(int threadId, int targetId)
    {
        lock (_lock)
        {
            CorDebugProcess process = RequireStopped();
            if (!_gotoTargets.TryGetValue(targetId, out var target))
                throw new DebuggerException("Unknown goto target.");
            if (RequireThread(threadId).ActiveFrame is not CorDebugILFrame frame
                || frame.Function.Token.Value != (uint)target.Location.MethodToken
                || frame.Function.Module.BaseAddress.Value != target.Module.Module.BaseAddress.Value)
            {
                throw new DebuggerException("The next statement can only be set within the method at the top of the stack.");
            }

            try
            {
                frame.CanSetIP(target.Location.ILOffset);
                frame.SetIP(target.Location.ILOffset);
            }
            catch (Exception e)
            {
                throw new DebuggerException("The next statement cannot be set to this location: " + e.Message);
            }

            // everything known about the thread's frames is stale now
            _threadFrames.Clear();
            _returnValue = null;
        }
        Stopped?.Invoke(new StopInfo("goto", threadId));
    }

    // ---------------------------------------------------------------- exception filter conditions

    private readonly Dictionary<string, List<(bool Exclude, Regex Pattern)>> _exceptionConditions = [];

    public void SetExceptionFilters(IReadOnlyCollection<ExceptionFilterRequest> filters)
    {
        lock (_lock)
        {
            SetExceptionFilters(filters.Select(f => f.Filter).ToList());
            _exceptionConditions.Clear();
            foreach (ExceptionFilterRequest filter in filters.Where(f => !string.IsNullOrWhiteSpace(f.Condition)))
            {
                _exceptionConditions[filter.Filter] = filter.Condition!
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(item => (Exclude: item.StartsWith('!'), Name: item.TrimStart('!').Trim()))
                    .Select(item => (item.Exclude, new Regex("^" + Regex.Escape(item.Name).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant)))
                    .ToList();
            }
        }
    }

    /// <summary>Whether the exception in flight passes the type condition of a filter ("all", "unhandled", ...).</summary>
    private bool MatchesExceptionCondition(string filter, CorDebugThread thread)
    {
        if (!_exceptionConditions.TryGetValue(filter, out var condition))
            return true;
        try
        {
            CorDebugValue? exception = thread.CurrentException;
            CorDebugValue? unwrapped = exception == null ? null : ValueInspector.Unwrap(exception, out _);
            if (unwrapped == null)
                return true;
            // the exception's type and all of its base types
            List<string> names = _values.GetTypeLevels(unwrapped).Select(l => l.Metadata.GetTypeName(l.Token)).ToList();
            if (condition.Any(c => c.Exclude && names.Any(c.Pattern.IsMatch)))
                return false;
            return condition.All(c => c.Exclude) || condition.Any(c => !c.Exclude && names.Any(c.Pattern.IsMatch));
        }
        catch (Exception)
        {
            return true;
        }
    }

    // ---------------------------------------------------------------- source paths

    private readonly List<(string PdbPrefix, string LocalPrefix)> _sourceFileMap = [];
    private readonly Dictionary<int, (LoadedModule Module, string Document)> _sourceReferences = [];
    private readonly Dictionary<string, int> _sourceReferenceIds = new(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Path recorded in the PDB -> path on this machine (sourceFileMap), or unchanged.</summary>
    private string ToLocalPath(string pdbPath)
    {
        string normalized = NormalizeSeparators(pdbPath);
        foreach ((string pdbPrefix, string localPrefix) in _sourceFileMap)
        {
            if (normalized.StartsWith(pdbPrefix + "/", PathComparison) || normalized.Equals(pdbPrefix, PathComparison))
                return Path.GetFullPath(localPrefix + normalized[pdbPrefix.Length..]);
        }
        return pdbPath;
    }

    private string ToPdbPath(string localPath)
    {
        string normalized = NormalizeSeparators(localPath);
        foreach ((string pdbPrefix, string localPrefix) in _sourceFileMap)
        {
            string local = NormalizeSeparators(localPrefix);
            if (normalized.StartsWith(local + "/", PathComparison) || normalized.Equals(local, PathComparison))
                return pdbPrefix + normalized[local.Length..];
        }
        return localPath;
    }

    /// <summary>
    /// What to tell the client about a document of a module: the local path, plus a source reference when the file
    /// does not exist here but its text is embedded in the PDB.
    /// </summary>
    private (string Path, int SourceReference) DescribeSource(LoadedModule? module, string pdbPath)
    {
        string local = ToLocalPath(pdbPath);
        if (File.Exists(local) || module?.Metadata?.HasEmbeddedSource(pdbPath) != true)
            return (local, 0);

        string key = module.Id + "|" + pdbPath;
        if (!_sourceReferenceIds.TryGetValue(key, out int id))
        {
            id = _sourceReferenceIds[key] = _sourceReferenceIds.Count + 1;
            _sourceReferences[id] = (module, pdbPath);
        }
        return (local, id);
    }

    public string GetSource(int sourceReference)
    {
        lock (_lock)
        {
            if (!_sourceReferences.TryGetValue(sourceReference, out var source))
                throw new DebuggerException($"Unknown source reference {sourceReference}.");
            return source.Module.Metadata?.GetEmbeddedSource(source.Document)
                ?? throw new DebuggerException("The source is not available.");
        }
    }

    // ---------------------------------------------------------------- threads

    private readonly HashSet<int> _frozenThreads = [];

    /// <summary>A frozen thread stays suspended while the rest of the process runs.</summary>
    public void SetThreadFrozen(int threadId, bool frozen)
    {
        lock (_lock)
        {
            CorDebugProcess process = RequireStopped();
            RequireThread(threadId).DebugState = frozen ? CorDebugThreadState.THREAD_SUSPEND : CorDebugThreadState.THREAD_RUN;
            if (frozen)
                _frozenThreads.Add(threadId);
            else
                _frozenThreads.Remove(threadId);
        }
    }

    // A func-eval suspends and then resumes all threads; the user's frozen ones have to be frozen again.
    private void RestoreFrozenThreads(CorDebugProcess process)
    {
        foreach (int threadId in _frozenThreads.ToList())
        {
            try
            {
                RequireThread(threadId).DebugState = CorDebugThreadState.THREAD_SUSPEND;
            }
            catch (Exception)
            {
                _frozenThreads.Remove(threadId); // the thread is gone
            }
        }
    }
}
