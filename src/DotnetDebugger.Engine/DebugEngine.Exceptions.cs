using ClrDebug;
using DotnetDebugger.Engine.Symbols;
using DotnetDebugger.Engine.Values;

namespace DotnetDebugger.Engine;

public sealed partial class DebugEngine
{
    public const string FilterAll = "all";
    public const string FilterUnhandled = "unhandled";
    public const string FilterUserUnhandled = "user-unhandled";

    // thread id -> break mode of the exception the thread is stopped at
    private readonly Dictionary<int, string> _exceptionStops = [];
    private readonly HashSet<int> _firstChanceReported = [];
    // threads whose exception in flight was thrown in, or has propagated through, user code
    private readonly HashSet<int> _passedUserCode = [];
    private bool _breakOnThrown;
    private bool _breakOnUserUnhandled;
    private bool _breakOnUnhandled = true;

    public void SetExceptionFilters(IReadOnlyCollection<string> filters)
    {
        lock (_lock)
        {
            _breakOnThrown = filters.Contains(FilterAll);
            _breakOnUnhandled = filters.Contains(FilterUnhandled);
            _breakOnUserUnhandled = filters.Contains(FilterUserUnhandled);
        }
    }

    private EventAction OnException(Exception2CorDebugManagedCallbackEventArgs e)
    {
        CorDebugThread thread = e.Thread;
        int threadId = thread.Id;
        switch (e.EventType)
        {
            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE:
            {
                _firstChanceReported.Remove(threadId);
                _passedUserCode.Remove(threadId);
                bool inUserCode = IsUserFrame(e.Frame);
                if (inUserCode)
                    _passedUserCode.Add(threadId);
                if (!_breakOnThrown || (_justMyCode && !inUserCode) || !MatchesExceptionCondition(FilterAll, thread))
                    return EventAction.Continue;
                _firstChanceReported.Add(threadId);
                return StopAtException(thread, "always");
            }

            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_USER_FIRST_CHANCE:
                _passedUserCode.Add(threadId);
                if (!_breakOnThrown || !MatchesExceptionCondition(FilterAll, thread) || !_firstChanceReported.Add(threadId))
                    return EventAction.Continue;
                return StopAtException(thread, "always");

            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_CATCH_HANDLER_FOUND:
            {
                // The stack is still intact here. Code the user cannot see is about to swallow an exception that came
                // out of the user's code: for the user that is as good as unhandled.
                bool userUnhandled = _breakOnUserUnhandled && _justMyCode && _passedUserCode.Contains(threadId)
                    && !_firstChanceReported.Contains(threadId) && !IsUserFrame(e.Frame)
                    && MatchesExceptionCondition(FilterUserUnhandled, thread);
                _firstChanceReported.Remove(threadId);
                _passedUserCode.Remove(threadId);
                return userUnhandled ? StopAtException(thread, "userUnhandled") : EventAction.Continue;
            }

            case CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED:
                _firstChanceReported.Remove(threadId);
                _passedUserCode.Remove(threadId);
                return _breakOnUnhandled && MatchesExceptionCondition(FilterUnhandled, thread)
                    ? StopAtException(thread, "unhandled")
                    : EventAction.Continue;

            default:
                return EventAction.Continue;
        }
    }

    private bool IsUserFrame(CorDebugFrame? frame)
    {
        try
        {
            return frame != null && IsUserCode(frame.Function);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private EventAction StopAtException(CorDebugThread thread, string breakMode)
    {
        int threadId = thread.Id;
        _exceptionStops[threadId] = breakMode;
        ExceptionData? details = ReadException(() => thread.CurrentException, breakMode, 0, null);
        string kind = breakMode switch { "unhandled" => "unhandled", "userUnhandled" => "user-unhandled", _ => "thrown" };
        string description = details == null ? "Exception" : $"Exception {kind}: {details.TypeName}";
        Post(() => Stopped?.Invoke(new StopInfo("exception", threadId, description, details?.Message)));
        return EventAction.Stop;
    }

    /// <param name="evalThreadId">Thread to run the StackTrace getter on; null to read fields only.</param>
    private ExceptionData? ReadException(Func<CorDebugValue?> getter, string breakMode, int depth, int? evalThreadId)
    {
        try
        {
            CorDebugValue? exception = getter();
            if (exception == null || ValueInspector.Unwrap(exception, out bool isNull) == null || isNull)
                return null;

            Func<CorDebugValue?> stable = Stabilize(getter);
            string typeName = _values.GetTypeName(exception);
            string? message = _values.ReadString(_values.GetFieldByName(exception, "_message"));
            ExceptionData? inner = depth < 8
                ? ReadException(() => _values.GetFieldByName(stable()!, "_innerException"), breakMode, depth + 1, evalThreadId)
                : null;

            string? stackTrace = _values.ReadString(_values.GetFieldByName(stable()!, "_stackTraceString"));
            if (stackTrace == null && evalThreadId is { } threadId)
                stackTrace = ReadStackTraceProperty(stable, threadId);
            return new ExceptionData(typeName, message, breakMode, stackTrace, inner);
        }
        catch (Exception e)
        {
            Log?.Invoke("Failed to read the current exception: " + e.Message);
            return null;
        }
    }

    private string? ReadStackTraceProperty(Func<CorDebugValue?> exception, int threadId)
    {
        try
        {
            CorDebugValue self = exception()!;
            foreach (ValueInspector.TypeLevel level in _values.GetTypeLevels(ValueInspector.Unwrap(self, out _)!))
            {
                if (level.Metadata.GetTypeName(level.Token) != "System.Exception")
                    continue;
                PropertyDescription? property = level.Metadata.GetProperties(level.Token).FirstOrDefault(p => p.Name == "StackTrace");
                if (property == null)
                    return null;
                CorDebugFunction getter = level.Type.Class.Module.GetFunctionFromToken(new mdMethodDef(property.GetterToken));
                return _values.ReadString(RunEval(threadId, eval => eval.CallFunction(getter.Raw, 1, [self.Raw])));
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("Failed to evaluate Exception.StackTrace: " + e.Message);
        }
        return null;
    }

    /// <summary>Details of the exception <paramref name="threadId"/> is currently stopped at.</summary>
    public ExceptionData? GetExceptionDetails(int threadId)
    {
        lock (_lock)
        {
            RequireStopped();
            string breakMode = _exceptionStops.GetValueOrDefault(threadId, "always");
            return ReadException(() => RequireProcess().GetThread(threadId).CurrentException, breakMode, 0, threadId);
        }
    }
}
