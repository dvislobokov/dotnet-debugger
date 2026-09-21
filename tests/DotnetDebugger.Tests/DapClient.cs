using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>Starts the adapter as a child process and talks DAP to it over stdio.</summary>
internal sealed class DapClient : IDisposable
{
    private readonly TimeSpan Timeout;

    private readonly Process _adapter;
    private readonly DapConnection _connection;
    private readonly List<DapMessage> _responses = [];
    private bool _closed;
    private readonly List<DapMessage> _events = [];
    private readonly StringBuilder _stderr = new();
    private readonly BlockingCollection<TerminalSession> _terminals = [];

    public DapClient(TimeSpan? timeout = null)
    {
        Timeout = timeout ?? TimeSpan.FromSeconds(30);
        // DOTNET_DEBUGGER_ADAPTER points the suite at a published adapter (an executable or a dll)
        string adapter = Environment.GetEnvironmentVariable("DOTNET_DEBUGGER_ADAPTER") is { Length: > 0 } custom ? custom : TestPaths.AdapterDll;
        bool isDll = adapter.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo(isDll ? "dotnet" : adapter)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (isDll)
            startInfo.ArgumentList.Add(adapter);
        // DOTNET_DEBUGGER_TEST_LOG: a file (the last session wins) or a directory (one trace per session, named after the test)
        if (Environment.GetEnvironmentVariable("DOTNET_DEBUGGER_TEST_LOG") is { Length: > 0 } log)
        {
            if (Directory.Exists(log) || log.EndsWith('/') || log.EndsWith('\\'))
            {
                Directory.CreateDirectory(log);
                log = Path.Combine(log, $"{CallingTest()}-{Interlocked.Increment(ref _logNumber):D4}.log");
            }
            startInfo.ArgumentList.Add("--log=" + log);
        }

        _adapter = Process.Start(startInfo)!;
        _adapter.ErrorDataReceived += (_, e) =>
        {
            lock (_stderr)
                _stderr.AppendLine(e.Data);
        };
        _adapter.BeginErrorReadLine();
        _connection = new DapConnection(_adapter.StandardOutput.BaseStream, _adapter.StandardInput.BaseStream);

        new System.Threading.Thread(ReadLoop) { IsBackground = true, Name = "DAP reader" }.Start();
    }

    private static int _logNumber;

    /// <summary>"Class.Method" of the test that creates the client (async tests run inside a state machine "&lt;Method&gt;d__N").</summary>
    private static string CallingTest()
    {
        foreach (var frame in new StackTrace().GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            if (type == null || type == typeof(DapClient) || type.Namespace != typeof(DapClient).Namespace && type.DeclaringType?.Namespace != typeof(DapClient).Namespace)
                continue;
            string method = frame.GetMethod()!.Name;
            if (type.Name.StartsWith('<') && type.DeclaringType != null)
            {
                method = type.Name[1..type.Name.IndexOf('>')];
                type = type.DeclaringType;
            }
            if (type == typeof(DapClient))
                continue;
            return string.Concat($"{type.Name}.{method}".Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '_'));
        }
        return "client";
    }

    private void ReadLoop()
    {
        try
        {
            while (_connection.Read() is { } message)
            {
                if (message.Type == "response")
                {
                    lock (_responses)
                    {
                        _responses.Add(message);
                        Monitor.PulseAll(_responses);
                    }
                }
                else if (message.Type == "request")
                {
                    HandleReverseRequest(message);
                }
                else if (message.Type == "event")
                {
                    lock (_events)
                    {
                        _events.Add(message);
                        Monitor.PulseAll(_events);
                    }
                }
            }
        }
        catch (Exception)
        {
            // adapter went away
        }
        lock (_responses)
        {
            _closed = true;
            Monitor.PulseAll(_responses);
        }
    }

    /// <summary>Writes bytes to the adapter as they are: for frames no well-behaved client would send.</summary>
    public void SendRaw(string text)
    {
        Stream stdin = _adapter.StandardInput.BaseStream;
        stdin.Write(Encoding.UTF8.GetBytes(text));
        stdin.Flush();
    }

    /// <summary>Sends a request without waiting; pair with <see cref="WaitForResponse"/>.</summary>
    public int Send(string command, object? arguments = null) => _connection.SendRequest(command, arguments);

    /// <summary>Responses may arrive out of order once requests overlap (cancel), so they are matched by sequence number.</summary>
    public DapMessage WaitForResponse(int seq, string command = "")
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        lock (_responses)
        {
            while (true)
            {
                int index = _responses.FindIndex(r => r.RequestSeq == seq);
                if (index >= 0)
                {
                    DapMessage found = _responses[index];
                    _responses.RemoveAt(index);
                    return found;
                }
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (_closed || remaining <= TimeSpan.Zero || !Monitor.Wait(_responses, remaining))
                    throw new TimeoutException($"No response to '{command}' ({seq}). Adapter stderr: {_stderr}");
            }
        }
    }

    // The only request an adapter sends to its client: "please run this command line in a terminal".
    private void HandleReverseRequest(DapMessage request)
    {
        if (request.Command != "runInTerminal")
        {
            _connection.SendErrorResponse(request, "unsupported");
            return;
        }
        try
        {
            var arguments = request.GetArguments<RunInTerminalArguments>()!;
            var session = new TerminalSession(arguments);
            _terminals.Add(session);
            _connection.SendResponse(request, new RunInTerminalResponseBody { ProcessId = session.Process.Id });
        }
        catch (Exception e)
        {
            _connection.SendErrorResponse(request, e.Message);
        }
    }

    public TerminalSession WaitForTerminal() =>
        _terminals.TryTake(out TerminalSession? session, Timeout) ? session : throw new TimeoutException($"No runInTerminal request arrived. Adapter stderr: {_stderr}");

    public DapMessage RequestRaw(string command, object? arguments = null)
    {
        return WaitForResponse(_connection.SendRequest(command, arguments), command);
    }

    public DapMessage Request(string command, object? arguments = null)
    {
        DapMessage response = RequestRaw(command, arguments);
        Assert.True(response.Success, $"'{command}' failed: {response.Message}");
        return response;
    }

    public T Request<T>(string command, object? arguments = null) => Request(command, arguments).GetBody<T>()!;

    /// <summary>Waits for (and consumes) the first matching event, including ones that already arrived.</summary>
    public DapMessage WaitForEvent(string name, Func<DapMessage, bool>? predicate = null)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        lock (_events)
        {
            while (true)
            {
                int index = _events.FindIndex(e => e.Event == name && (predicate == null || predicate(e)));
                if (index >= 0)
                {
                    DapMessage found = _events[index];
                    _events.RemoveAt(index);
                    return found;
                }
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_events, remaining))
                {
                    throw new TimeoutException(
                        $"Event '{name}' did not arrive. Pending: [{string.Join(", ", _events.Select(e => e.Event))}]. Adapter stderr: {_stderr}");
                }
            }
        }
    }

    public bool HasPendingEvent(string name)
    {
        lock (_events)
            return _events.Any(e => e.Event == name);
    }

    public StoppedEventBody WaitForStop(string? reason = null)
    {
        StoppedEventBody body = WaitForEvent("stopped").GetBody<StoppedEventBody>()!;
        if (reason != null)
            Assert.Equal(reason, body.Reason);
        return body;
    }

    /// <summary>All output received so far for a category, concatenated.</summary>
    public string Output(string category)
    {
        lock (_events)
        {
            return string.Concat(_events
                .Where(e => e.Event == "output")
                .Select(e => e.GetBody<OutputEventBody>()!)
                .Where(o => o.Category == category)
                .Select(o => o.Output));
        }
    }

    // ---------------------------------------------------------------- scenario helpers

    public void Initialize(bool supportsRunInTerminal = false)
    {
        var capabilities = Request<Capabilities>("initialize", new
        {
            clientID = "tests", adapterID = "dotnet-debugger", linesStartAt1 = true, columnsStartAt1 = true,
            supportsRunInTerminalRequest = supportsRunInTerminal,
        });
        Assert.True(capabilities.SupportsConfigurationDoneRequest);
        WaitForEvent("initialized");
    }

    public void Launch(string mode, bool stopAtEntry = false, bool justMyCode = true) =>
        Request("launch", new { program = TestPaths.TestAppDll, args = new[] { mode }, stopAtEntry, justMyCode });

    /// <summary>Sets line breakpoints at the given markers (all markers must live in the same file).</summary>
    public Breakpoint[] SetBreakpoints(params string[] markers) =>
        SetBreakpoints(markers.Select(m => new BreakpointSpec(m)).ToArray());

    /// <summary>Sets breakpoints, one request per source file; results are in the order of <paramref name="specs"/>.</summary>
    public Breakpoint[] SetBreakpoints(params BreakpointSpec[] specs)
    {
        if (specs.Length == 0)
            return SetBreakpointsIn(TestPaths.ProgramSource);
        var results = new Dictionary<BreakpointSpec, Breakpoint>();
        foreach (var file in specs.GroupBy(s => TestPaths.Find(s.Marker).Path))
        {
            BreakpointSpec[] inFile = file.ToArray();
            Breakpoint[] set = SetBreakpointsIn(file.Key, inFile);
            for (int i = 0; i < inFile.Length; i++)
                results[inFile[i]] = set[i];
        }
        return specs.Select(s => results[s]).ToArray();
    }

    public Breakpoint[] SetBreakpointsIn(string path, params BreakpointSpec[] specs) =>
        Request<SetBreakpointsResponseBody>("setBreakpoints", new
        {
            source = new { path },
            breakpoints = specs.Select(s => new
            {
                line = TestPaths.Find(s.Marker).Line,
                column = s.ColumnOf == null ? (int?)null : TestPaths.ColumnOf(s.Marker, s.ColumnOf),
                condition = s.Condition,
                hitCondition = s.HitCondition,
                logMessage = s.LogMessage,
            }).ToArray(),
        }).Breakpoints;

    /// <summary>Launches a scenario, sets breakpoints and runs to the first stop.</summary>
    public (int ThreadId, StackFrame Top) RunTo(string mode, params string[] markers)
    {
        Initialize();
        Launch(mode);
        SetBreakpoints(markers);
        Request("configurationDone");
        return Top(WaitForStop("breakpoint"));
    }

    public (int ThreadId, StackFrame Top) Top(StoppedEventBody stop)
    {
        int threadId = stop.ThreadId!.Value;
        return (threadId, StackTrace(threadId)[0]);
    }

    public (int ThreadId, StackFrame Top) ContinueToStop(int threadId, string reason = "breakpoint")
    {
        Request("continue", new { threadId });
        return Top(WaitForStop(reason));
    }

    public (int ThreadId, StackFrame Top) StepAndWait(string command, int threadId)
    {
        Request(command, new { threadId });
        return Top(WaitForStop("step"));
    }

    public EvaluateResponseBody Evaluate(string expression, int frameId, string context = "watch") =>
        Request<EvaluateResponseBody>("evaluate", new { expression, frameId, context });

    public string EvaluateError(string expression, int frameId)
    {
        DapMessage response = RequestRaw("evaluate", new { expression, frameId, context = "watch" });
        Assert.False(response.Success, $"'{expression}' unexpectedly evaluated successfully.");
        return response.Message ?? "";
    }

    public void WaitForOutput(string category, string text)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!Output(category).Contains(text))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Output '{text}' did not arrive. Got: {Output(category)}");
            System.Threading.Thread.Sleep(20);
        }
    }

    public StackFrame[] StackTrace(int threadId) =>
        Request<StackTraceResponseBody>("stackTrace", new { threadId }).StackFrames;

    public Dictionary<string, Variable> Locals(int frameId)
    {
        Scope scope = Assert.Single(Request<ScopesResponseBody>("scopes", new { frameId }).Scopes);
        return Variables(scope.VariablesReference);
    }

    public Dictionary<string, Variable> Variables(int variablesReference, bool hex = false) =>
        Request<VariablesResponseBody>("variables", new { variablesReference, format = new { hex } }).Variables.ToDictionary(v => v.Name);

    public bool AdapterExited(TimeSpan timeout) => _adapter.WaitForExit(timeout);

    /// <summary>The adapter dies without a chance to clean up (SIGKILL, OOM killer, a crashed IDE).</summary>
    public void KillAdapter() => _adapter.Kill();

    public void Dispose()
    {
        try
        {
            if (!_adapter.HasExited)
            {
                _connection.SendRequest("disconnect", new { terminateDebuggee = true });
                if (!_adapter.WaitForExit(10000))
                    _adapter.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            try
            {
                _adapter.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }
        _adapter.Dispose();
        foreach (TerminalSession terminal in _terminals)
            terminal.Dispose();
    }
}

/// <summary>Stands in for the client's terminal: runs the requested command line with pipes the test controls.</summary>
internal sealed class TerminalSession : IDisposable
{
    private readonly StringBuilder _output = new();

    public RunInTerminalArguments Request { get; }
    public Process Process { get; }

    public TerminalSession(RunInTerminalArguments request)
    {
        Request = request;
        var startInfo = new ProcessStartInfo(request.Args[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = request.Cwd ?? "",
        };
        foreach (string argument in request.Args.Skip(1))
            startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in request.Env ?? [])
        {
            if (value == null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }

        Process = Process.Start(startInfo)!;
        Process.OutputDataReceived += (_, e) => Append(e.Data);
        Process.ErrorDataReceived += (_, e) => Append(e.Data);
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
    }

    private void Append(string? line)
    {
        lock (_output)
            _output.AppendLine(line);
    }

    public void WaitForOutput(string text)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            lock (_output)
            {
                if (_output.ToString().Contains(text))
                    return;
            }
            Assert.True(DateTime.UtcNow < deadline, $"Terminal output '{text}' did not arrive. Got: {_output}");
            System.Threading.Thread.Sleep(20);
        }
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
        Process.Dispose();
    }
}

internal sealed record BreakpointSpec(string Marker, string? Condition = null, string? HitCondition = null, string? LogMessage = null, string? ColumnOf = null);

internal static class TestPaths
{
    private static readonly string s_root = FindRoot();
    private static readonly string s_configuration =
        AppContext.BaseDirectory.Replace('\\', '/').Contains("/Release/") ? "Release" : "Debug";

    public static string Root => s_root;
    public static string Configuration => s_configuration;

    /// <summary>What the adapter and the debuggees are built for (DebuggerTargetFramework in Directory.Build.props).</summary>
    public const string TargetFramework = "net8.0";

    public static string AdapterDll =>
        Path.Combine(s_root, "src", "DotnetDebugger.Adapter", "bin", s_configuration, TargetFramework, "dotnet-debugger.dll");

    public static string TestAppDll =>
        Path.Combine(s_root, "tests", "TestApp", "bin", s_configuration, TargetFramework, "TestApp.dll");

    public static string TestAppDirectory => Path.Combine(s_root, "tests", "TestApp");

    public static string ProgramSource => Path.Combine(TestAppDirectory, "Program.cs");

    /// <summary>File and 1-based line of the statement tagged with "// bp:&lt;marker&gt;".</summary>
    public static (string Path, int Line) Find(string marker)
    {
        // the main debuggee first, then the small helper applications
        IEnumerable<string> files = Directory.GetFiles(TestAppDirectory, "*.cs")
            .Concat(new[] { "SymbolLib", "SymbolApp", "EmbeddedApp", "AsyncMainApp" }.SelectMany(d => Directory.GetFiles(Path.Combine(s_root, "tests", d), "*.cs")));
        foreach (string path in files)
        {
            string[] lines = File.ReadAllLines(path);
            int index = Array.FindIndex(lines, l => l.TrimEnd().EndsWith("// bp:" + marker, StringComparison.Ordinal));
            if (index >= 0)
                return (path, index + 1);
        }
        throw new InvalidOperationException($"Marker '{marker}' not found in {TestAppDirectory}");
    }

    public static int LineOf(string marker) => Find(marker).Line;

    /// <summary>1-based column of <paramref name="text"/> on the marker's line.</summary>
    public static int ColumnOf(string marker, string text)
    {
        (string path, int line) = Find(marker);
        int index = File.ReadAllLines(path)[line - 1].IndexOf(text, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{text}' not found on the line of marker '{marker}'");
        return index + 1;
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotnetDebugger.sln")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
