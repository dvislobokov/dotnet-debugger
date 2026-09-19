using ClrDebug;

namespace DotnetDebugger.Engine;

public sealed record BreakpointRequest(int Line, int? Column = null, string? Condition = null, string? HitCondition = null, string? LogMessage = null);

public sealed record FunctionBreakpointRequest(string Name, string? Condition = null, string? HitCondition = null);

public sealed partial class DebugEngine
{
    private readonly record struct CodeLocation(ulong Module, uint Token, int Offset);

    /// <summary>
    /// One ICorDebug breakpoint per code location, shared by everything that wants to stop there. ICorDebug reports
    /// each breakpoint object separately, so two objects at one location would stop the debuggee twice.
    /// </summary>
    private sealed class NativeBreakpoint(CorDebugFunctionBreakpoint breakpoint)
    {
        public CorDebugFunctionBreakpoint Breakpoint { get; } = breakpoint;
        public int Users { get; set; }
    }

    private sealed class UserBreakpoint
    {
        public required int Id { get; init; }

        // source breakpoint
        public string? Path { get; init; }
        public int RequestedLine { get; init; }
        public int? RequestedColumn { get; init; }

        // function breakpoint
        public string? FunctionName { get; init; }

        public string? Condition { get; init; }
        public string? HitCondition { get; init; }
        public string? LogMessage { get; init; }
        public int HitCount { get; set; }

        public (int Line, int Column, int EndLine, int EndColumn)? Resolved { get; set; }

        /// <summary>Set when the file the user is looking at is not the one the module was compiled from.</summary>
        public string? Warning { get; set; }
        public List<CodeLocation> Bound { get; } = [];

        public bool Verified => Bound.Count > 0;
        public bool NeedsEvaluation => Condition != null || LogMessage != null;

        public BreakpointInfo ToInfo() => new(Id, Path, Verified, Resolved?.Line ?? RequestedLine,
            Resolved?.Column ?? RequestedColumn, Resolved?.EndLine, Resolved?.EndColumn,
            Verified ? Warning : FunctionName != null
                ? $"No loaded module with symbols contains a function named '{FunctionName}'."
                : "No loaded module with symbols contains code for this location.");
    }

    private readonly Dictionary<string, List<UserBreakpoint>> _breakpoints = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly List<UserBreakpoint> _functionBreakpoints = [];
    private readonly Dictionary<CodeLocation, NativeBreakpoint> _nativeBreakpoints = [];
    private CodeLocation? _entryBreakpoint;
    private int _nextBreakpointId;
    private bool _resolvingBreakpoint;
    private bool _pauseRequested;

    private IEnumerable<UserBreakpoint> AllBreakpoints => _breakpoints.Values.SelectMany(b => b).Concat(_functionBreakpoints);

    // ---------------------------------------------------------------- setting

    /// <summary>Replaces all breakpoints of one source file.</summary>
    public IReadOnlyList<BreakpointInfo> SetBreakpoints(string sourcePath, IReadOnlyList<BreakpointRequest> requests)
    {
        lock (_lock)
        {
            return WhileSynchronized(() =>
            {
                if (_breakpoints.Remove(sourcePath, out List<UserBreakpoint>? old))
                    old.ForEach(Unbind);

                var created = requests.Select(r => new UserBreakpoint
                {
                    Id = ++_nextBreakpointId, Path = sourcePath, RequestedLine = r.Line, RequestedColumn = r.Column,
                    Condition = NullIfBlank(r.Condition), HitCondition = NullIfBlank(r.HitCondition), LogMessage = NullIfBlank(r.LogMessage),
                }).ToList();
                return Install(created, c => _breakpoints[sourcePath] = c);
            });
        }
    }

    /// <summary>Replaces all function breakpoints.</summary>
    public IReadOnlyList<BreakpointInfo> SetFunctionBreakpoints(IReadOnlyList<FunctionBreakpointRequest> requests)
    {
        lock (_lock)
        {
            return WhileSynchronized(() =>
            {
                _functionBreakpoints.ForEach(Unbind);
                _functionBreakpoints.Clear();
                var created = requests.Select(r => new UserBreakpoint
                {
                    Id = ++_nextBreakpointId, FunctionName = r.Name.Trim().TrimEnd('(', ')'),
                    Condition = NullIfBlank(r.Condition), HitCondition = NullIfBlank(r.HitCondition),
                }).ToList();
                return Install(created, _functionBreakpoints.AddRange);
            });
        }
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private List<BreakpointInfo> Install(List<UserBreakpoint> created, Action<List<UserBreakpoint>> store)
    {
        foreach (UserBreakpoint bp in created)
            foreach (LoadedModule module in _modules.Values)
                TryBind(bp, module);
        if (created.Count > 0)
            store(created);
        return created.Select(b => b.ToInfo()).ToList();
    }

    // Breakpoints can only be changed while the debuggee is synchronized.
    private T WhileSynchronized<T>(Func<T> action)
    {
        bool stoppedHere = false;
        if (_process != null && !_stopped)
        {
            _process.Stop(0);
            stoppedHere = true;
        }
        try
        {
            return action();
        }
        finally
        {
            if (stoppedHere)
                _process!.Continue(false);
        }
    }

    private void BindBreakpoints(LoadedModule module)
    {
        foreach (UserBreakpoint bp in AllBreakpoints)
        {
            bool wasVerified = bp.Verified;
            if (TryBind(bp, module) && !wasVerified)
            {
                BreakpointInfo info = bp.ToInfo();
                Post(() => BreakpointChanged?.Invoke(info));
            }
        }
    }

    private bool TryBind(UserBreakpoint bp, LoadedModule module)
    {
        if (module.Metadata is not { HasSymbols: true } metadata)
            return false;
        try
        {
            if (bp.FunctionName != null)
            {
                bool any = false;
                foreach ((int token, int offset) in metadata.FindMethodsByName(bp.FunctionName))
                {
                    bp.Bound.Add(AcquireNativeBreakpoint(module, token, offset));
                    any = true;
                }
                return any;
            }

            string pdbPath = ToPdbPath(bp.Path!);
            if (metadata.ResolveBreakpoint(pdbPath, bp.RequestedLine, bp.RequestedColumn) is not { } resolved)
                return false;
            if (File.Exists(bp.Path) && metadata.MatchesChecksum(pdbPath, bp.Path!) == false)
                bp.Warning = "The source file differs from the one the module was built with; the breakpoint may be on a different line.";
            bp.Bound.Add(AcquireNativeBreakpoint(module, resolved.MethodToken, resolved.ILOffset));
            bp.Resolved = (resolved.Line, resolved.Column, resolved.EndLine, resolved.EndColumn);
            return true;
        }
        catch (Exception e)
        {
            Log?.Invoke($"Failed to bind breakpoint {bp.Path ?? bp.FunctionName}:{bp.RequestedLine}: {e.Message}");
            return false;
        }
    }

    private void Unbind(UserBreakpoint bp)
    {
        foreach (CodeLocation location in bp.Bound)
            ReleaseNativeBreakpoint(location);
        bp.Bound.Clear();
    }

    private CodeLocation AcquireNativeBreakpoint(LoadedModule module, int methodToken, int ilOffset)
    {
        var location = new CodeLocation(module.Module.BaseAddress.Value, (uint)methodToken, ilOffset);
        if (!_nativeBreakpoints.TryGetValue(location, out NativeBreakpoint? native))
        {
            CorDebugFunction function = module.Module.GetFunctionFromToken(new mdMethodDef(methodToken));
            CorDebugFunctionBreakpoint breakpoint = function.ILCode.CreateBreakpoint(ilOffset);
            breakpoint.Activate(true);
            _nativeBreakpoints[location] = native = new NativeBreakpoint(breakpoint);
        }
        native.Users++;
        return location;
    }

    private void ReleaseNativeBreakpoint(CodeLocation location)
    {
        if (!_nativeBreakpoints.TryGetValue(location, out NativeBreakpoint? native) || --native.Users > 0)
            return;
        _nativeBreakpoints.Remove(location);
        try
        {
            native.Breakpoint.Activate(false);
        }
        catch (Exception)
        {
            // the module may already be gone
        }
    }

    private void TrySetEntryBreakpoint(LoadedModule module)
    {
        if (module.Metadata?.GetEntryPoint() is not { } entry)
            return;
        try
        {
            _entryBreakpoint = AcquireNativeBreakpoint(module, entry.MethodToken, entry.ILOffset);
        }
        catch (Exception e)
        {
            Log?.Invoke("Failed to set the entry breakpoint: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- hitting

    private EventAction OnBreakpointHit(BreakpointCorDebugManagedCallbackEventArgs e)
    {
        CorDebugThread thread = e.Thread;
        int threadId = thread.Id;
        if (e.Breakpoint is not CorDebugFunctionBreakpoint hit)
            return EventAction.Continue;
        if (OnReturnSiteBreakpoint(thread, hit))
            return EventAction.Continue;

        CorDebugFunction function = hit.Function;
        var location = new CodeLocation(function.Module.BaseAddress.Value, function.Token.Value, hit.Offset);

        if (OnAsyncStepBreakpoint(thread, location) is { } asyncAction)
            return asyncAction;

        bool isEntry = _entryBreakpoint == location;
        if (isEntry)
        {
            ReleaseNativeBreakpoint(location);
            _entryBreakpoint = null;
            _stopAtEntry = false;
        }

        List<UserBreakpoint> matches = AllBreakpoints.Where(b => b.Bound.Contains(location)).ToList();
        if (matches.Count == 0)
        {
            if (!isEntry)
                return EventAction.Continue;
            Post(() => Stopped?.Invoke(new StopInfo("entry", threadId)));
            return EventAction.Stop;
        }

        if (matches.Any(b => b.NeedsEvaluation))
        {
            // Conditions may need func-evals, which cannot complete while this callback is still running:
            // stay stopped and decide on another thread.
            _resolvingBreakpoint = true;
            Task.Run(() => ResolveConditionalBreakpoints(threadId, matches));
            return EventAction.InternalStop;
        }

        List<UserBreakpoint> stopping = matches.Where(b => PassesHitCondition(b, ++b.HitCount)).ToList();
        if (stopping.Count == 0)
            return EventAction.Continue;
        StopInfo stop = BreakpointStop(threadId, stopping);
        Post(() => Stopped?.Invoke(stop));
        return EventAction.Stop;
    }

    private static StopInfo BreakpointStop(int threadId, List<UserBreakpoint> breakpoints) =>
        new(breakpoints.All(b => b.FunctionName != null) ? "function breakpoint" : "breakpoint", threadId,
            BreakpointIds: breakpoints.Select(b => b.Id).ToArray());

    private void ResolveConditionalBreakpoints(int threadId, List<UserBreakpoint> matches)
    {
        var events = new List<Action>();
        lock (_lock)
        {
            try
            {
                if (_process == null || _processExited || !_stopped)
                    return;

                StartImplicitEvalBudget();
                var stopping = new List<UserBreakpoint>();
                foreach (UserBreakpoint bp in matches)
                {
                    var evaluator = new Evaluator(this, TopFrame(threadId), allowCalls: true);
                    try
                    {
                        if (bp.Condition != null && evaluator.EvaluateToHost(bp.Condition) is not true)
                            continue;
                        if (!PassesHitCondition(bp, ++bp.HitCount))
                            continue;
                        if (bp.LogMessage != null)
                        {
                            string message = FormatLogMessage(bp.LogMessage, evaluator);
                            events.Add(() => Output?.Invoke("console", message + Environment.NewLine));
                            continue;
                        }
                    }
                    catch (DebuggerException e)
                    {
                        // A broken condition must not silently disable the breakpoint.
                        string error = $"Breakpoint condition '{bp.Condition ?? bp.LogMessage}' failed: {e.Message}{Environment.NewLine}";
                        events.Add(() => Output?.Invoke("console", error));
                    }
                    stopping.Add(bp);
                }

                if (_process == null || _processExited)
                    return;
                if (stopping.Count > 0 || _pauseRequested)
                {
                    DeactivateStepper();
                    ClearAsyncStep();
                    StopInfo stop = stopping.Count > 0 ? BreakpointStop(threadId, stopping) : new StopInfo("pause", threadId);
                    events.Add(() => Stopped?.Invoke(stop));
                }
                else
                {
                    ResumeProcess(_process);
                }
            }
            catch (Exception e)
            {
                Log?.Invoke("Failed to resolve a conditional breakpoint: " + e);
                events.Add(() => Stopped?.Invoke(new StopInfo("breakpoint", threadId)));
            }
            finally
            {
                _resolvingBreakpoint = false;
                _pauseRequested = false;
            }
        }

        foreach (Action action in events)
            action();
    }

    // "5" / "==5": exactly the fifth hit;  ">=5", ">5", "<5", "<=5";  "%5": every fifth hit
    private bool PassesHitCondition(UserBreakpoint bp, int hitCount)
    {
        if (bp.HitCondition == null)
            return true;
        string text = bp.HitCondition.Replace(" ", "");
        string op = new(text.TakeWhile(c => !char.IsDigit(c)).ToArray());
        if (!int.TryParse(text[op.Length..], out int n) || n <= 0)
        {
            Log?.Invoke($"Ignoring invalid hit condition '{bp.HitCondition}'.");
            return true;
        }
        return op switch
        {
            "" or "=" or "==" => hitCount == n,
            ">=" => hitCount >= n,
            ">" => hitCount > n,
            "<=" => hitCount <= n,
            "<" => hitCount < n,
            "%" => hitCount % n == 0,
            _ => true,
        };
    }

    // "{expression}" is replaced by its value; "{{" and "}}" are literal braces
    /// <param name="quoteStrings">false for log messages, true for [DebuggerDisplay] (where ",nq" removes the quotes).</param>
    private static string FormatLogMessage(string template, Evaluator evaluator, bool quoteStrings = false)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                sb.Append(c);
                i++;
            }
            else if (c == '{')
            {
                int close = template.IndexOf('}', i);
                if (close < 0)
                {
                    sb.Append(template, i, template.Length - i);
                    break;
                }
                string expression = template[(i + 1)..close];
                try
                {
                    sb.Append(evaluator.EvaluateToText(expression, quoteStrings));
                }
                catch (DebuggerException e)
                {
                    sb.Append("<error: ").Append(e.Message).Append('>');
                }
                i = close;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
