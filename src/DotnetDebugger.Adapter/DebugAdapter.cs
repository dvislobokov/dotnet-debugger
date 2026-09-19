using System.Collections.Concurrent;
using DotnetDebugger.Engine;
using DotnetDebugger.Engine.Values;
using DotnetDebugger.Protocol;
using ExceptionDetails = DotnetDebugger.Protocol.ExceptionDetails;

namespace DotnetDebugger.Adapter;

/// <summary>Translates DAP requests into <see cref="DebugEngine"/> calls and engine events into DAP events.</summary>
internal sealed class DebugAdapter : IDisposable
{
    private readonly DapConnection _connection;
    private readonly Action<string> _log;
    private DebugEngine _engine;

    // what a restart has to bring back
    private LaunchArguments? _launchArguments;
    private readonly Dictionary<string, BreakpointRequest[]> _breakpointRequests = new(StringComparer.OrdinalIgnoreCase);
    private FunctionBreakpointRequest[] _functionBreakpointRequests = [];
    private ExceptionFilterRequest[]? _exceptionFilterRequests;
    private bool _clientLinesStartAt1 = true;
    private bool _clientColumnsStartAt1 = true;
    private bool _isAttach;
    private bool _clientSupportsRunInTerminal;
    private int _terminatedSent;
    private Action? _afterResponse;

    // Requests are executed one at a time by a worker, so that the reader stays free for the few requests that
    // must get through while another one is still running: cancel, pause, terminate and disconnect.
    private readonly BlockingCollection<DapMessage> _queue = [];
    private readonly HashSet<int> _cancelled = [];
    private int _runningRequest;

    public DebugAdapter(DapConnection connection, Action<string> log)
    {
        _connection = connection;
        _log = log;
        _engine = CreateEngine();
    }

    private DebugEngine CreateEngine()
    {
        var engine = new DebugEngine { Log = _log };
        engine.Stopped += stop => _connection.SendEvent("stopped", new StoppedEventBody
        {
            Reason = stop.Reason,
            Description = stop.Description,
            Text = stop.Text,
            ThreadId = stop.ThreadId,
            HitBreakpointIds = stop.BreakpointIds,
        });
        engine.Output += (category, text) => _connection.SendEvent("output", new OutputEventBody { Category = category, Output = text });
        engine.ThreadChanged += (id, started) =>
            _connection.SendEvent("thread", new ThreadEventBody { Reason = started ? "started" : "exited", ThreadId = id });
        engine.ModuleLoaded += module => _connection.SendEvent("module", new ModuleEventBody { Module = ToModule(module) });
        engine.ModuleChanged += module => _connection.SendEvent("module", new ModuleEventBody { Reason = "changed", Module = ToModule(module) });
        engine.BreakpointChanged += bp => _connection.SendEvent("breakpoint", new BreakpointEventBody { Breakpoint = ToBreakpoint(bp) });
        engine.Exited += exitCode =>
        {
            // the process of an engine that was replaced by a restart is none of the client's business anymore
            if (!ReferenceEquals(engine, _engine))
                return;
            _connection.SendEvent("exited", new ExitedEventBody { ExitCode = exitCode });
            SendTerminated();
        };
        return engine;
    }

    public void Run()
    {
        var worker = new System.Threading.Thread(ProcessQueue) { IsBackground = true, Name = "DAP requests" };
        worker.Start();

        while (_connection.Read() is { } message)
        {
            if (message.Type == "response")
            {
                _connection.CompleteRequest(message);
                continue;
            }
            if (message.Type != "request")
                continue;

            switch (message.Command)
            {
                case "cancel":
                    Cancel(message);
                    break;

                case "pause" or "terminate":
                    Handle(message);
                    break;

                case "disconnect":
                    // whatever is running must not delay the end of the session
                    _engine.CancelEvaluation();
                    Handle(message);
                    _queue.CompleteAdding();
                    return;

                // Launching may have to wait for the client (runInTerminal) and may take long (build); breakpoints
                // and configurationDone are expected to be accepted meanwhile.
                case "launch" or "restart":
                    Task.Run(() => Handle(message));
                    break;

                default:
                    _queue.Add(message);
                    break;
            }
        }
        _queue.CompleteAdding();
    }

    private void ProcessQueue()
    {
        foreach (DapMessage message in _queue.GetConsumingEnumerable())
        {
            bool cancelled;
            lock (_cancelled)
            {
                cancelled = _cancelled.Remove(message.Seq);
                _runningRequest = cancelled ? 0 : message.Seq;
            }

            if (cancelled)
                _connection.SendErrorResponse(message, "cancelled");
            else
                Handle(message);

            lock (_cancelled)
                _runningRequest = 0;
        }
    }

    private void Cancel(DapMessage message)
    {
        int? requestId = message.GetArguments<CancelArguments>()?.RequestId;
        if (requestId != null)
        {
            bool running;
            lock (_cancelled)
            {
                running = _runningRequest == requestId;
                if (!running)
                    _cancelled.Add(requestId.Value);
            }
            if (running)
                _engine.CancelEvaluation();
        }
        _connection.SendResponse(message);
    }

    private void Handle(DapMessage message)
    {
        try
        {
            object? body = Dispatch(message);
            _connection.SendResponse(message, body);
            Interlocked.Exchange(ref _afterResponse, null)?.Invoke();
        }
        catch (Exception e)
        {
            if (e is not DebuggerException)
                _log($"Request '{message.Command}' failed: {e}");
            // requests racing with the end of the process (or a restart) are normal; raw HRESULTs help nobody
            bool processGone = e.Message.Contains("CORDBG_E_PROCESS_TERMINATED", StringComparison.Ordinal)
                || e.Message.Contains("CORDBG_E_PROCESS_DETACHED", StringComparison.Ordinal);
            _connection.SendErrorResponse(message, processGone ? "The debuggee is not running." : e.Message);
        }
    }

    private object? Dispatch(DapMessage request)
    {
        switch (request.Command)
        {
            case "initialize":
            {
                var args = request.GetArguments<InitializeArguments>() ?? new InitializeArguments();
                _clientLinesStartAt1 = args.LinesStartAt1;
                _clientColumnsStartAt1 = args.ColumnsStartAt1;
                _clientSupportsRunInTerminal = args.SupportsRunInTerminalRequest;
                _afterResponse = () => _connection.SendEvent("initialized");
                return new Capabilities
                {
                    SupportsConfigurationDoneRequest = true,
                    SupportsTerminateRequest = true,
                    SupportsExceptionInfoRequest = true,
                    SupportTerminateDebuggee = true,
                    SupportsConditionalBreakpoints = true,
                    SupportsHitConditionalBreakpoints = true,
                    SupportsLogPoints = true,
                    SupportsFunctionBreakpoints = true,
                    SupportsSetVariable = true,
                    SupportsEvaluateForHovers = true,
                    SupportsModulesRequest = true,
                    SupportsLoadedSourcesRequest = true,
                    SupportsCancelRequest = true,
                    SupportsValueFormattingOptions = true,
                    SupportsSetExpression = true,
                    SupportsGotoTargetsRequest = true,
                    SupportsRestartRequest = true,
                    SupportsExceptionFilterOptions = true,
                    ExceptionBreakpointFilters =
                    [
                        Filter(DebugEngine.FilterAll, "All Exceptions", "Break when an exception is thrown.", false),
                        Filter(DebugEngine.FilterUserUnhandled, "User-Unhandled Exceptions", "Break when an exception leaves your code and is caught by code without symbols.", true),
                        Filter(DebugEngine.FilterUnhandled, "Unhandled Exceptions", "Break when an exception is not handled.", true),
                    ],
                };

                static ExceptionBreakpointsFilter Filter(string id, string label, string description, bool enabled) => new()
                {
                    Filter = id, Label = label, Description = description, Default = enabled, SupportsCondition = true,
                    ConditionDescription = "Exception types, e.g. System.IO.*, !System.OperationCanceledException",
                };
            }

            case "launch":
            {
                _launchArguments = request.GetArguments<LaunchArguments>() ?? throw new DebuggerException("Missing launch arguments.");
                Launch(_launchArguments);
                return null;
            }

            case "restart":
            {
                if (_launchArguments == null)
                    throw new DebuggerException("Only launched sessions can be restarted.");
                // the client may send an updated configuration
                if (request.GetArguments<RestartArguments>()?.Arguments is { ValueKind: System.Text.Json.JsonValueKind.Object } updated)
                    _launchArguments = System.Text.Json.JsonSerializer.Deserialize<LaunchArguments>(updated, DapJson.Options) ?? _launchArguments;

                DebugEngine old = _engine;
                _engine = CreateEngine();
                old.Dispose();

                foreach (var (path, requests) in _breakpointRequests)
                    _engine.SetBreakpoints(path, requests);
                _engine.SetFunctionBreakpoints(_functionBreakpointRequests);
                if (_exceptionFilterRequests != null)
                    _engine.SetExceptionFilters(_exceptionFilterRequests);
                Launch(_launchArguments);
                _engine.ConfigurationDone();
                return null;
            }

            case "attach":
            {
                var args = request.GetArguments<AttachArguments>() ?? throw new DebuggerException("Missing attach arguments.");
                _isAttach = true;
                _engine.Attach(args.GetProcessId(), args.JustMyCode ?? true, args.SourceFileMap);
                SendProcessEvent(args.GetProcessId().ToString(), "attach");
                return null;
            }

            case "configurationDone":
                _engine.ConfigurationDone();
                return null;

            case "setBreakpoints":
            {
                var args = request.GetArguments<SetBreakpointsArguments>()!;
                string path = args.Source.Path ?? throw new DebuggerException("Only breakpoints in files with a path are supported.");
                BreakpointRequest[] requests = (args.Breakpoints ?? [])
                    .Select(b => new BreakpointRequest(LineFromClient(b.Line), b.Column is { } c ? ColumnFromClient(c) : null,
                        b.Condition, b.HitCondition, b.LogMessage))
                    .ToArray();
                _breakpointRequests[path] = requests;
                return new SetBreakpointsResponseBody
                {
                    Breakpoints = _engine.SetBreakpoints(path, requests).Select(ToBreakpoint).ToArray(),
                };
            }

            case "setFunctionBreakpoints":
            {
                var args = request.GetArguments<SetFunctionBreakpointsArguments>()!;
                FunctionBreakpointRequest[] requests = args.Breakpoints
                    .Select(b => new FunctionBreakpointRequest(b.Name, b.Condition, b.HitCondition)).ToArray();
                _functionBreakpointRequests = requests;
                return new SetBreakpointsResponseBody
                {
                    Breakpoints = _engine.SetFunctionBreakpoints(requests).Select(ToBreakpoint).ToArray(),
                };
            }

            case "setExceptionBreakpoints":
            {
                var args = request.GetArguments<SetExceptionBreakpointsArguments>() ?? new SetExceptionBreakpointsArguments();
                _exceptionFilterRequests = args.Filters.Select(f => new ExceptionFilterRequest(f))
                    .Concat((args.FilterOptions ?? []).Select(o => new ExceptionFilterRequest(o.FilterId, o.Condition)))
                    .ToArray();
                _engine.SetExceptionFilters(_exceptionFilterRequests);
                return null;
            }

            case "setExpression":
            {
                var args = request.GetArguments<SetExpressionArguments>()!;
                (VariableInfo variable, int handle) = _engine.SetExpression(args.Expression, args.Value, args.FrameId);
                return new SetExpressionResponseBody
                {
                    Value = variable.Value,
                    Type = variable.Type,
                    VariablesReference = handle,
                    IndexedVariables = handle != 0 && variable.IndexedChildren > 0 ? variable.IndexedChildren : null,
                };
            }

            case "gotoTargets":
            {
                var args = request.GetArguments<GotoTargetsArguments>()!;
                string path = args.Source.Path ?? throw new DebuggerException("A source path is required.");
                return new GotoTargetsResponseBody
                {
                    Targets = _engine.GetGotoTargets(path, LineFromClient(args.Line), args.Column is { } c ? ColumnFromClient(c) : null)
                        .Select(t => new GotoTarget
                        {
                            Id = t.Id, Label = t.Label, Line = LineToClient(t.Line), Column = ColumnToClient(t.Column),
                            EndLine = LineToClient(t.EndLine), EndColumn = ColumnToClient(t.EndColumn),
                        }).ToArray(),
                };
            }

            case "goto":
            {
                var args = request.GetArguments<GotoArguments>()!;
                // the "stopped" event has to follow the response
                _afterResponse = () => _engine.Goto(args.ThreadId, args.TargetId);
                return null;
            }

            case "source":
            {
                var args = request.GetArguments<SourceArguments>()!;
                int reference = args.Source?.SourceReference is > 0 ? args.Source.SourceReference.Value : args.SourceReference;
                return new SourceResponseBody { Content = _engine.GetSource(reference), MimeType = "text/x-csharp" };
            }

            case "dotnet/freezeThread":
                _engine.SetThreadFrozen(request.GetArguments<ThreadArguments>()!.ThreadId, frozen: true);
                return null;

            case "dotnet/thawThread":
                _engine.SetThreadFrozen(request.GetArguments<ThreadArguments>()!.ThreadId, frozen: false);
                return null;

            case "dotnet/loadSymbols":
            {
                int moduleId = request.Arguments is { } a && a.TryGetProperty("moduleId", out var id) ? id.GetInt32()
                    : throw new DebuggerException("'moduleId' is required.");
                return new Dictionary<string, object?> { ["loaded"] = _engine.LoadSymbols(moduleId) };
            }

            case "dotnet/info":
                return new Dictionary<string, object?>
                {
                    ["version"] = typeof(DebugAdapter).Assembly.GetName().Version?.ToString(),
                    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    ["os"] = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                    ["debuggeeProcessId"] = _engine.ProcessId,
                };

            case "threads":
                return new ThreadsResponseBody
                {
                    Threads = _engine.GetThreads().Select(t => new Protocol.Thread { Id = t.Id, Name = t.Name }).ToArray(),
                };

            case "stackTrace":
            {
                var args = request.GetArguments<StackTraceArguments>()!;
                IReadOnlyList<FrameInfo> frames = _engine.GetStackTrace(args.ThreadId);
                IEnumerable<FrameInfo> page = frames.Skip(args.StartFrame ?? 0);
                if (args.Levels is > 0)
                    page = page.Take(args.Levels.Value);
                return new StackTraceResponseBody { TotalFrames = frames.Count, StackFrames = page.Select(ToStackFrame).ToArray() };
            }

            case "scopes":
            {
                int handle = _engine.GetLocalsHandle(request.GetArguments<ScopesArguments>()!.FrameId);
                return new ScopesResponseBody
                {
                    Scopes = handle == 0 ? [] : [new Scope { Name = "Locals", PresentationHint = "locals", VariablesReference = handle }],
                };
            }

            case "variables":
            {
                var args = request.GetArguments<VariablesArguments>()!;
                bool indexedOnly = args.Filter == "indexed";
                return new VariablesResponseBody
                {
                    Variables = _engine.GetVariables(args.VariablesReference, args.Start ?? 0, args.Count ?? 0, args.Format?.Hex == true)
                        .Where(v => !indexedOnly || v.Variable.Name.StartsWith('['))
                        .Select(v => new Variable
                        {
                            Name = v.Variable.Name,
                            Value = v.Variable.Value,
                            Type = v.Variable.Type,
                            EvaluateName = v.Variable.EvaluateName,
                            PresentationHint = v.Variable.IsLazy ? new VariablePresentationHint { Lazy = true } : null,
                            VariablesReference = v.Handle,
                            IndexedVariables = v.Handle != 0 && v.Variable.IndexedChildren > 0 ? v.Variable.IndexedChildren : null,
                        })
                        .ToArray(),
                };
            }

            case "evaluate":
            {
                var args = request.GetArguments<EvaluateArguments>()!;
                (VariableInfo variable, int handle) = _engine.Evaluate(args.Expression, args.FrameId, allowCalls: args.Context != "hover", hex: args.Format?.Hex == true);
                return new EvaluateResponseBody
                {
                    Result = variable.Value,
                    Type = variable.Type,
                    VariablesReference = handle,
                    IndexedVariables = handle != 0 && variable.IndexedChildren > 0 ? variable.IndexedChildren : null,
                };
            }

            case "setVariable":
            {
                var args = request.GetArguments<SetVariableArguments>()!;
                (VariableInfo variable, int handle) = _engine.SetVariable(args.VariablesReference, args.Name, args.Value);
                return new SetVariableResponseBody
                {
                    Value = variable.Value,
                    Type = variable.Type,
                    VariablesReference = handle,
                    IndexedVariables = handle != 0 && variable.IndexedChildren > 0 ? variable.IndexedChildren : null,
                };
            }

            case "modules":
            {
                IReadOnlyList<ModuleLoadInfo> modules = _engine.GetModules();
                int start = request.Arguments is { } a && a.TryGetProperty("startModule", out var s0) ? s0.GetInt32() : 0;
                int count = request.Arguments is { } b && b.TryGetProperty("moduleCount", out var c0) ? c0.GetInt32() : 0;
                IEnumerable<ModuleLoadInfo> page = modules.Skip(start);
                if (count > 0)
                    page = page.Take(count);
                return new ModulesResponseBody { TotalModules = modules.Count, Modules = page.Select(ToModule).ToArray() };
            }

            case "loadedSources":
                return new LoadedSourcesResponseBody
                {
                    Sources = _engine.GetLoadedSources().Select(f => new Source { Name = Path.GetFileName(f.Path), Path = f.Path }).ToArray(),
                };

            case "continue":
                _engine.Continue();
                return new ContinueResponseBody();

            case "next":
                _engine.Step(request.GetArguments<ThreadArguments>()!.ThreadId, StepKind.Over);
                return null;

            case "stepIn":
                _engine.Step(request.GetArguments<ThreadArguments>()!.ThreadId, StepKind.In);
                return null;

            case "stepOut":
                _engine.Step(request.GetArguments<ThreadArguments>()!.ThreadId, StepKind.Out);
                return null;

            case "pause":
                _engine.Pause();
                return null;

            case "exceptionInfo":
            {
                ExceptionData data = _engine.GetExceptionDetails(request.GetArguments<ThreadArguments>()!.ThreadId)
                    ?? throw new DebuggerException("No exception is being processed on this thread.");
                return new ExceptionInfoResponseBody
                {
                    ExceptionId = data.TypeName,
                    Description = data.Message,
                    BreakMode = data.BreakMode,
                    Details = ToExceptionDetails(data),
                };
            }

            case "terminate":
                _engine.Terminate();
                return null;

            case "disconnect":
            {
                var args = request.GetArguments<DisconnectArguments>();
                if (args?.TerminateDebuggee ?? !_isAttach)
                    _engine.Terminate();
                else
                    _engine.Detach();
                SendTerminated();
                return null;
            }

            default:
                throw new DebuggerException($"Unsupported request '{request.Command}'.");
        }
    }

    private void Launch(LaunchArguments args)
    {
        ResolvedLaunch launch = LaunchResolver.Resolve(args, text => _connection.SendEvent("output", new OutputEventBody { Category = "console", Output = text }));

        string? terminalKind = args.Console switch
        {
            "integratedTerminal" => "integrated",
            "externalTerminal" => "external",
            null or "" or "internalConsole" => null,
            _ => throw new DebuggerException($"Unknown console '{args.Console}'."),
        };
        if (!_clientSupportsRunInTerminal)
            terminalKind = null;

        _engine.Launch(new LaunchOptions
        {
            Program = launch.Program,
            Args = launch.Args,
            WorkingDirectory = launch.WorkingDirectory,
            Environment = launch.Environment,
            StopAtEntry = args.StopAtEntry,
            JustMyCode = args.JustMyCode ?? true,
            StepFiltering = args.EnableStepFiltering ?? true,
            SourceFileMap = args.SourceFileMap,
            SymbolOptions = args.SymbolOptions == null ? null : new DotnetDebugger.Engine.Symbols.SymbolOptions
            {
                SearchPaths = args.SymbolOptions.SearchPaths ?? [],
                CachePath = args.SymbolOptions.CachePath,
                SearchMicrosoftSymbolServer = args.SymbolOptions.SearchMicrosoftSymbolServer,
                SearchNuGetOrgSymbolServer = args.SymbolOptions.SearchNuGetOrgSymbolServer,
            },
            ExternalLauncher = terminalKind == null ? null : () => TerminalLauncher.Launch(_connection, launch, terminalKind),
        });
        SendProcessEvent(Path.GetFileName(launch.Program), "launch");
    }

    private void SendTerminated()
    {
        if (Interlocked.Exchange(ref _terminatedSent, 1) == 0)
            _connection.SendEvent("terminated");
    }

    private static Module ToModule(ModuleLoadInfo module) => new()
    {
        Id = module.Id,
        Name = module.Name,
        Path = module.Path,
        SymbolStatus = module.HasSymbols ? "Symbols loaded." : "Skipped loading symbols.",
    };

    private void SendProcessEvent(string name, string startMethod) => _connection.SendEvent("process", new ProcessEventBody
    {
        Name = name,
        SystemProcessId = _engine.ProcessId,
        StartMethod = startMethod,
    });

    private static ExceptionDetails ToExceptionDetails(ExceptionData data) => new()
    {
        Message = data.Message,
        FullTypeName = data.TypeName,
        TypeName = data.TypeName[(data.TypeName.LastIndexOf('.') + 1)..],
        StackTrace = data.StackTrace,
        InnerException = data.Inner == null ? null : [ToExceptionDetails(data.Inner)],
    };

    private int ColumnFromClient(int column) => _clientColumnsStartAt1 ? column : column + 1;
    private int LineFromClient(int line) => _clientLinesStartAt1 ? line : line + 1;
    private int LineToClient(int line) => _clientLinesStartAt1 ? line : line - 1;
    private int ColumnToClient(int column) => _clientColumnsStartAt1 ? column : column - 1;

    private Breakpoint ToBreakpoint(BreakpointInfo bp) => new()
    {
        Id = bp.Id,
        Verified = bp.Verified,
        Message = bp.Message,
        Line = bp.Line > 0 ? LineToClient(bp.Line) : null,
        Column = bp.Column is { } column ? ColumnToClient(column) : null,
        EndLine = bp.EndLine is { } end ? LineToClient(end) : null,
        EndColumn = bp.EndColumn is { } endColumn ? ColumnToClient(endColumn) : null,
        Source = bp.Path == null ? null : new Source { Name = Path.GetFileName(bp.Path), Path = bp.Path },
    };

    private StackFrame ToStackFrame(FrameInfo frame)
    {
        if (frame.SourcePath == null)
            return new StackFrame { Id = frame.Id, Name = frame.Name, PresentationHint = frame.PresentationHint ?? "subtle" };
        return new StackFrame
        {
            Id = frame.Id,
            Name = frame.Name,
            Source = new Source
            {
                Name = Path.GetFileName(frame.SourcePath),
                Path = frame.SourcePath,
                SourceReference = frame.SourceReference > 0 ? frame.SourceReference : null,
            },
            Line = LineToClient(frame.Line),
            Column = ColumnToClient(frame.Column),
            EndLine = LineToClient(frame.EndLine),
            EndColumn = ColumnToClient(frame.EndColumn),
        };
    }

    public void Dispose() => _engine.Dispose();
}
