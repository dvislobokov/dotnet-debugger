using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>
/// Regression tests for the findings of the black-box DAP probe (P1 and P2). The numbers are those of its FINDINGS.md.
/// The debuggee side lives in tests/TestApp/Findings.cs and tests/AsyncMainApp.
/// </summary>
public class FindingsTests
{
    private static string AsyncMainAppDll => Path.Combine(TestPaths.Root, "tests", "AsyncMainApp", "bin", TestPaths.Configuration, TestPaths.TargetFramework, "AsyncMainApp.dll");

    // ---------------------------------------------------------------- 1. unhandled exceptions

    /// <summary>A client that does not know our "unhandled" filter (any generic DAP client) must still get the stop.</summary>
    [Theory]
    [InlineData("exception", "System.ArgumentException", false)]
    [InlineData("exception", "System.ArgumentException", true)]
    [InlineData("unhandledAsync", "System.InvalidOperationException", false)]
    [InlineData("unhandledThread", "System.InvalidOperationException", false)]
    public void UnhandledExceptionStopsWhateverTheFilters(string mode, string exceptionType, bool userUnhandledFilter)
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch(mode);
        client.Request("setExceptionBreakpoints", new { filters = userUnhandledFilter ? new[] { "user-unhandled" } : Array.Empty<string>() });
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("exception");
        var info = client.Request<ExceptionInfoResponseBody>("exceptionInfo", new { threadId = stop.ThreadId });
        Assert.Equal(exceptionType, info.ExceptionId);
        Assert.Equal("unhandled", info.BreakMode);

        client.Request("continue", new { threadId = stop.ThreadId });
        int exitCode = client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode;
        Assert.NotEqual(0, exitCode);
        if (OperatingSystem.IsWindows())
            Assert.Equal(unchecked((int)0xE0434352), exitCode);
    }

    // ---------------------------------------------------------------- 2. output that is not ASCII

    [Fact]
    public void NonAsciiOutputAndArgumentsArriveIntact()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = TestPaths.TestAppDll, args = new[] { "unicodeOutput", "ünï Жук" } });
        client.Request("configurationDone");
        client.WaitForEvent("exited");

        string stdout = client.Output("stdout");
        Assert.Contains("Кириллица и emoji 🙂 €", stdout);
        Assert.Contains("arg: ünï Жук", stdout);
    }

    // ---------------------------------------------------------------- 3. async stepping

    [Fact]
    public void StepInEntersAnAsyncMethod()
    {
        using var client = new DapClient();
        var (threadId, _) = client.RunTo("asyncSteps", "asyncSecond");

        var (_, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestApp.Findings.Compute()", top.Name);
        // the opening brace is a sequence point of a debug build: a step in lands there, as it does in a synchronous method
        Assert.InRange(top.Line, TestPaths.LineOf("computeFirst") - 1, TestPaths.LineOf("computeFirst"));
    }

    [Fact]
    public void NextPastTheEndOfAnAsyncMethodStopsInTheAwaitingCaller()
    {
        using var client = new DapClient(TimeSpan.FromSeconds(10));
        var (threadId, top) = client.RunTo("asyncSteps", "computeEnd");
        Assert.Equal(TestPaths.LineOf("computeEnd"), top.Line);
        // the second call of Compute must not be what stops the program
        client.SetBreakpointsIn(TestPaths.Find("computeEnd").Path);

        (_, top) = client.StepAndWait("next", threadId);
        Assert.Equal("TestApp.Findings.AsyncSteps()", top.Name);
    }

    // ---------------------------------------------------------------- 4. long strings

    [Fact]
    public void LongStringsAreTruncatedForDisplayAndWholeForTheClipboard()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("findings", "findings");

        Variable longText = client.Locals(top.Id)["longText"];
        Assert.Equal("string", longText.Type);
        Assert.StartsWith("\"aaaa", longText.Value);
        Assert.Contains("...", longText.Value);
        Assert.True(longText.Value.Length < 5000, $"the value has {longText.Value.Length} characters");

        foreach (string context in new[] { "watch", "hover", "repl" })
        {
            EvaluateResponseBody shown = client.Evaluate("longText", top.Id, context);
            Assert.Equal("string", shown.Type);
            Assert.True(shown.Result.Length < 5000, $"{context}: {shown.Result.Length} characters");
        }
        Assert.Contains(new string('a', 5000), client.Evaluate("longText", top.Id, "clipboard").Result);
        Assert.Equal("5000", client.Evaluate("longText.Length", top.Id).Result);

        // a string created by the evaluation takes another path: it needs the same limit
        Assert.True(client.Evaluate("new string('q', 5000)", top.Id).Result.Length < 5000);
        Assert.Contains(new string('q', 5000), client.Evaluate("new string('q', 5000)", top.Id, "clipboard").Result);
    }

    // ---------------------------------------------------------------- 5, 14. attach to the wrong process

    [Fact]
    public void SecondDebuggerIsRefusedAndTheProcessSurvives()
    {
        using Process debuggee = StartWaitingDebuggee();
        try
        {
            using var first = new DapClient();
            first.Initialize();
            first.Request("attach", new { processId = debuggee.Id });
            first.Request("configurationDone");

            using (var second = new DapClient())
            {
                second.Initialize();
                DapMessage response = second.RequestRaw("attach", new { processId = debuggee.Id });
                Assert.False(response.Success, "the second attach reported success");
                Assert.Contains("already", response.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("HRESULT", response.Message);
            }

            Assert.False(debuggee.WaitForExit(1500), "the second attach killed the debuggee");
            // the first session is still in charge
            Assert.NotEmpty(first.Request<ThreadsResponseBody>("threads").Threads);
            first.Request("disconnect", new { terminateDebuggee = false });
        }
        finally
        {
            debuggee.Kill();
        }
    }

    [Fact]
    public void AttachToAProcessWithoutDotnetFails()
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping") { ArgumentList = { "-n", "60", "127.0.0.1" } }
            : new ProcessStartInfo("sleep") { ArgumentList = { "60" } };
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        using Process native = Process.Start(startInfo)!;
        try
        {
            using var client = new DapClient(TimeSpan.FromSeconds(20));
            client.Initialize();
            DapMessage response = client.RequestRaw("attach", new { processId = native.Id });
            Assert.False(response.Success, "attaching to a native process reported success");
            Assert.Contains(".NET", response.Message);
            Assert.DoesNotContain("HRESULT", response.Message);
            Assert.False(native.HasExited);
        }
        finally
        {
            native.Kill();
        }
    }

    [Fact]
    public void AttachToAMissingProcessIsExplainedInPlainWords()
    {
        using var client = new DapClient();
        client.Initialize();
        DapMessage response = client.RequestRaw("attach", new { processId = 0x7FFFFFF0 });
        Assert.False(response.Success);
        Assert.DoesNotContain("HRESULT", response.Message);
        Assert.Contains(0x7FFFFFF0.ToString(), response.Message);
    }

    // ---------------------------------------------------------------- 6. huge collections

    /// <summary>The client was told indexedVariables, paging is its job: an unpaged request gets a capped answer.</summary>
    [Theory]
    [InlineData("million")]
    [InlineData("bytes")]
    public void UnpagedRequestForAHugeCollectionIsCapped(string name)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("huge", "huge");
        Variable huge = client.Locals(top.Id)[name];
        Assert.True(huge.IndexedVariables >= 1_000_000);

        var watch = Stopwatch.StartNew();
        Variable[] children = client.Request<VariablesResponseBody>("variables", new { variablesReference = huge.VariablesReference }).Variables;
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.InRange(children.Length, 1, 10_100); // 10 000 elements and whatever goes with them (Raw View, a note)
        Assert.Contains(children, c => c.Name == "[0]");
    }

    [Fact]
    public void DisconnectIsNotBlockedByASlowVariablesRequest()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("huge", "huge");
        Variable million = client.Locals(top.Id)["million"];
        client.Send("variables", new { variablesReference = million.VariablesReference });
        client.Send("variables", new { variablesReference = million.VariablesReference, filter = "indexed", start = 0, count = 1_000_000 });

        var watch = Stopwatch.StartNew();
        client.Send("disconnect", new { terminateDebuggee = true });
        Assert.True(client.AdapterExited(TimeSpan.FromSeconds(10)), "the adapter did not exit");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"disconnect took {watch.Elapsed}");
    }

    [Fact]
    public void SlowVariablesRequestCanBeCancelled()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("huge", "huge");
        Variable million = client.Locals(top.Id)["million"];

        int slow = client.Send("variables", new { variablesReference = million.VariablesReference, filter = "indexed", start = 0, count = 1_000_000 });
        System.Threading.Thread.Sleep(300);
        var watch = Stopwatch.StartNew();
        client.Request("cancel", new { requestId = slow });
        client.WaitForResponse(slow, "variables"); // cancelled or capped: either way it must not take a minute
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"cancelling took {watch.Elapsed}");

        // and the session goes on
        Assert.Equal("1000000", client.Evaluate("million.Count", top.Id).Result);
    }

    [Fact]
    public void PagingDeepIntoADictionaryIsNotLinearInFuncEvals()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("huge", "huge");
        Variable lookup = client.Locals(top.Id)["lookup"];

        Variable[] Page(int start, out TimeSpan elapsed)
        {
            var watch = Stopwatch.StartNew();
            Variable[] page = client.Request<VariablesResponseBody>("variables",
                new { variablesReference = lookup.VariablesReference, filter = "indexed", start, count = 100 }).Variables;
            elapsed = watch.Elapsed;
            return page;
        }

        Assert.Equal(100, Page(99_900, out TimeSpan last).Length);
        Assert.True(last < TimeSpan.FromSeconds(5), $"the last page took {last}");

        Assert.Empty(Page(999_900, out TimeSpan beyond));
        Assert.True(beyond < TimeSpan.FromSeconds(1), $"a page beyond the end took {beyond}");
    }

    // ---------------------------------------------------------------- 7. malformed input

    [Theory]
    [InlineData("this is not JSON")]
    [InlineData("{\"seq\": 5, \"type\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("42")]
    [InlineData("{\"seq\": \"7\", \"type\": \"request\", \"command\": \"threads\"}")]
    public void MalformedBodyDoesNotEndTheSession(string body)
    {
        using var client = new DapClient(TimeSpan.FromSeconds(10));
        client.Initialize();

        client.SendRaw($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");

        // answered, successfully or not: the adapter is still there and still in step with the stream
        client.RequestRaw("threads");
        Assert.False(client.AdapterExited(TimeSpan.FromMilliseconds(200)));
        client.Launch("basic");
        client.Request("configurationDone");
        client.WaitForEvent("exited");
    }

    [Theory]
    [InlineData("Content-Length: abc\r\n\r\n")]
    [InlineData("Content-Length: 99999999999999999999\r\n\r\n")]
    public void MalformedHeaderDoesNotEndTheSession(string header)
    {
        using var client = new DapClient(TimeSpan.FromSeconds(10));
        client.Initialize();

        client.SendRaw(header);

        client.RequestRaw("threads");
        Assert.False(client.AdapterExited(TimeSpan.FromMilliseconds(200)));
    }

    // ---------------------------------------------------------------- 8. an evaluation that cannot be aborted

    [Fact]
    public void AfterAnAbandonedEvaluationTheDebuggerSpeaksPlainlyAndCanEndTheSession()
    {
        using var client = new DapClient(TimeSpan.FromSeconds(40));
        var (threadId, top) = client.RunTo("evil", "evil");

        string timeout = client.EvaluateError("evil.Deadlocks", top.Id);
        Assert.Contains("timed out", timeout, StringComparison.OrdinalIgnoreCase);

        // the thread is lost to the abandoned evaluation: say that, not CORDBG_E_FUNC_EVAL_BAD_START_POINT
        DapMessage later = client.RequestRaw("evaluate", new { expression = "evil.Fine", frameId = top.Id, context = "watch" });
        if (later.Success != true)
        {
            Assert.DoesNotContain("HRESULT", later.Message);
            Assert.DoesNotContain("CORDBG_", later.Message);
            Assert.Contains("evaluation", later.Message, StringComparison.OrdinalIgnoreCase);
        }
        // the user is told once, where it cannot be missed
        client.WaitForEvent("output", e => e.GetBody<OutputEventBody>() is { Category: "important" } o && o.Output.Contains("evaluation", StringComparison.OrdinalIgnoreCase));

        // what needs no code to run still works, and the session can be ended
        Assert.Equal("2", client.Evaluate("1 + 1", top.Id).Result);
        Assert.NotEmpty(client.StackTrace(threadId));
        client.Request("terminate");
        client.WaitForEvent("exited");
    }

    // ---------------------------------------------------------------- 9. implicit evaluation

    [Fact]
    public void ImplicitToStringGetsAShortTimeoutAndIsNotRetriedDuringTheSameStop()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("implicitHang", "implicitHang");

        var watch = Stopwatch.StartNew();
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        TimeSpan first = watch.Elapsed;
        Assert.Equal("{TestApp.SpinningToString}", locals["spinning"].Value);
        Assert.Equal("5", locals["plain"].Value);
        Assert.True(first < TimeSpan.FromSeconds(3), $"the first listing took {first}");

        watch.Restart();
        Assert.Equal("{TestApp.SpinningToString}", client.Locals(top.Id)["spinning"].Value);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"the second listing took {watch.Elapsed}");

        // an explicit evaluation is the user's own decision: it still runs
        Assert.Equal("6", client.Evaluate("plain + 1", top.Id).Result);
    }

    [Fact]
    public void ImplicitEvaluationCanBeTurnedOff()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = TestPaths.TestAppDll, args = new[] { "display" }, allowImplicitFuncEval = false });
        client.SetBreakpoints("display");
        client.Request("configurationDone");
        var (_, top) = client.Top(client.WaitForStop("breakpoint"));

        // no ToString(), no DebuggerDisplay expressions, no property getters
        Variable temperature = client.Locals(top.Id)["temperature"];
        Assert.Equal("{TestApp.Temperature}", temperature.Value);
        Assert.DoesNotContain("24.5", client.Variables(temperature.VariablesReference).GetValueOrDefault("Text")?.Value ?? "");

        // asked for explicitly, it runs
        Assert.Equal("\"24.5 C\"", client.Evaluate("temperature.ToString()", top.Id).Result);
    }

    // ---------------------------------------------------------------- 10. enums of assemblies without symbols

    [Fact]
    public void FrameworkEnumsAreShownByName()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("findings", "findings");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Assert.Equal("Friday", locals["day"].Value);
        Assert.Equal("System.DayOfWeek", locals["day"].Type);
        Assert.Equal("Friday", client.Evaluate("day", top.Id).Result);
        Assert.Equal("Friday", client.Evaluate("date.DayOfWeek", top.Id).Result);
        Assert.Equal("Utc", client.Evaluate("date.Kind", top.Id).Result);
        Assert.Equal("Friday", client.Variables(locals["date"].VariablesReference)["DayOfWeek"].Value);
        // inside a DebuggerDisplay string: "Id = 1, Status = RanToCompletion, Method = ..."
        Assert.Contains("Status = RanToCompletion", locals["done"].Value);
    }

    // ---------------------------------------------------------------- 11. DebuggerDisplay with escaped braces

    [Fact]
    public void AnonymousTypesAreDisplayed()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("findings", "findings");

        // the compiler generates [DebuggerDisplay("\{ A = {A}, B = {B} }")]
        Assert.Equal("{ A = 1, B = \"two\" }", client.Locals(top.Id)["anon"].Value);
    }

    // ---------------------------------------------------------------- 12. stopAtEntry with an async Main

    [Fact]
    public void StopAtEntryOfAnAsyncMainStopsInTheUsersCode()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = AsyncMainAppDll, stopAtEntry = true });
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("entry");
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        (string path, int line) = TestPaths.Find("asyncMainEntry");
        Assert.Equal(path, top.Source?.Path, ignoreCase: true);
        Assert.Equal(line, top.Line);

        client.Request("continue", new { threadId = stop.ThreadId });
        Assert.Equal(0, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
    }

    // ---------------------------------------------------------------- 13. stepping into code without symbols

    [Fact]
    public void StepInWithoutJustMyCodeNeverStopsWhereThereIsNoSource()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = TestPaths.TestAppDll, args = new[] { "linqStep" }, justMyCode = false, enableStepFiltering = false });
        client.SetBreakpoints("linqCall");
        client.Request("configurationDone");
        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));

        // Where(), the lambda, Max(): wherever the steps lead, every stop has a line to show
        for (int i = 0; i < 4 && top.Line != TestPaths.LineOf("linqAfter"); i++)
        {
            (_, top) = client.StepAndWait("stepIn", threadId);
            Assert.True(top.Source?.Path != null && top.Line > 0, $"step {i + 1} stopped in '{top.Name}' without source");
        }
    }

    // ---------------------------------------------------------------- 15. first chance exception in external code

    [Fact]
    public void ExceptionVariableIsAvailableInAnExternalTopFrame()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("parseFail");
        client.Request("setExceptionBreakpoints", new { filters = new[] { "all" } });
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("exception");
        StackFrame[] frames = client.StackTrace(stop.ThreadId!.Value);
        Assert.Contains(frames, f => f.Line == TestPaths.LineOf("parseFail"));

        // $exception belongs to the thread, not to a frame: any frame of it will do, the external one on top included
        foreach (StackFrame frame in frames.Take(2))
            Assert.Contains("System.FormatException", client.Evaluate("$exception", frame.Id).Result);
        Assert.Contains("not a number", client.Evaluate("$exception.Message", frames[0].Id).Result);
    }

    // ---------------------------------------------------------------- 16. --server

    [Fact]
    public void ServerModeDoesNotLingerAfterItsClientLeft()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using Process adapter = StartAdapter($"--server={port}");
        try
        {
            adapter.StandardError.ReadLine(); // "Waiting for a DAP client on ..."
            using (var tcp = new TcpClient())
            {
                tcp.Connect(IPAddress.Loopback, port);
                var connection = new DapConnection(tcp.GetStream(), tcp.GetStream());
                connection.SendRequest("initialize", new { clientID = "tests", adapterID = "dotnet-debugger" });
                Assert.Equal("initialize", connection.Read()!.Command);
            }

            // either is fine: the process ends with its only session, or it accepts the next client. Not: alive and deaf.
            if (adapter.WaitForExit(5000))
                return;
            using var again = new TcpClient();
            again.Connect(IPAddress.Loopback, port);
            var second = new DapConnection(again.GetStream(), again.GetStream());
            second.SendRequest("initialize", new { clientID = "tests", adapterID = "dotnet-debugger" });
            Assert.Equal("initialize", second.Read()!.Command);
        }
        finally
        {
            if (!adapter.HasExited)
                adapter.Kill(entireProcessTree: true);
        }
    }

    // ---------------------------------------------------------------- 17. breakpoints that make no sense

    [Theory]
    [InlineData("abc")]
    [InlineData(">=")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("%0")]
    public void InvalidHitConditionIsRejected(string hitCondition)
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("loop");
        Breakpoint bp = Assert.Single(client.SetBreakpoints(new BreakpointSpec("loopBody", HitCondition: hitCondition)));
        Assert.False(bp.Verified);
        Assert.Contains(hitCondition, bp.Message); // pending breakpoints are unverified too: the message is what tells

        // and it does not act as an unconditional breakpoint
        client.Request("configurationDone");
        client.WaitForEvent("exited");
        Assert.False(client.HasPendingEvent("stopped"));
    }

    [Fact]
    public void ConditionThatIsNotBooleanIsReported()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("loop");
        client.SetBreakpoints(new BreakpointSpec("loopBody", Condition: "i + 1"));
        client.Request("configurationDone");

        // the same way a failing condition is: stop, and say why
        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal(TestPaths.LineOf("loopBody"), top.Line);
        client.WaitForOutput("console", "'i + 1'");
        Assert.Contains("bool", client.Output("console"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void BreakpointOnANonPositiveLineIsRejected(int line)
    {
        using var client = new DapClient(TimeSpan.FromSeconds(10));
        client.Initialize();
        client.Launch("loop");
        Breakpoint bp = Assert.Single(client.Request<SetBreakpointsResponseBody>("setBreakpoints",
            new { source = new { path = TestPaths.ProgramSource }, breakpoints = new[] { new { line } } }).Breakpoints);
        Assert.False(bp.Verified);
        Assert.Contains("line", bp.Message, StringComparison.OrdinalIgnoreCase); // not the "module is not loaded yet" of a pending breakpoint

        client.Request("configurationDone");
        client.WaitForEvent("exited");
        Assert.False(client.HasPendingEvent("stopped"));
    }

    // ---------------------------------------------------------------- 18. gaps of the expression evaluator

    [Theory]
    // optional parameters are filled in
    [InlineData("greeter.Greet(\"bob\")", "\"hi bob!\"")]
    [InlineData("greeter.Greet(\"bob\", 2)", "\"hi bobhi bob!\"")]
    [InlineData("greeter.Greet(\"bob\", 1, \"?\")", "\"hi bob?\"")]
    [InlineData("Greeter.Scale(4)", "41")]
    [InlineData("Greeter.Scale(4, 100)", "401")]
    // an enum argument for a System.Enum parameter
    [InlineData("perm.HasFlag(Perm.Write)", "true")]
    [InlineData("perm.HasFlag(Perm.Exec)", "false")]
    // delegates are invoked like methods
    [InlineData("twice(4)", "8")]
    [InlineData("shifted(4)", "7")]
    [InlineData("twice.Invoke(5) + 1", "11")]
    // tuple element names come from the symbols
    [InlineData("tuple.Name", "\"t\"")]
    [InlineData("tuple.Id + tuple.Item1", "2")]
    // checked / unchecked
    [InlineData("checked(offset + 1)", "4")]
    [InlineData("unchecked(offset * 2)", "6")]
    // pointers
    [InlineData("*pointer", "42")]
    [InlineData("*pointer + 1", "43")]
    [InlineData("*realPointer", "2.5")]
    public void EvaluatorGaps(string expression, string expected)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("evaluator", "evaluator");
        Assert.Equal(expected, client.Evaluate(expression, top.Id).Result);
    }

    [Fact]
    public void CheckedArithmeticOverflows()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("evaluator", "evaluator");
        Assert.Contains("Overflow", client.EvaluateError("checked(int.MaxValue + offset)", top.Id));
    }

    [Theory]
    [InlineData("numbers,3", 3)]
    [InlineData("numbers,100", 7)]
    [InlineData("numbers, 2", 2)]
    public void ElementCountFormatSpecifier(string expression, int expectedElements)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("evaluator", "evaluator");

        EvaluateResponseBody shown = client.Evaluate(expression, top.Id);
        Assert.Equal(expectedElements, shown.IndexedVariables);
        Dictionary<string, Variable> children = client.Variables(shown.VariablesReference);
        Assert.Equal(expectedElements, children.Count);
        Assert.Equal("10", children["[0]"].Value);
    }

    [Fact]
    public void TypeParametersOfAGenericMethodCanBeNamed()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("evaluator", "genericMethod");
        Assert.Equal("\"Int32\"", client.Evaluate("typeof(T).Name", top.Id).Result);
        Assert.Equal("0", client.Evaluate("default(T)", top.Id).Result);

        // the second instantiation: shared code, where the exact type is only known to the runtime
        (_, top) = client.ContinueToStop(threadId);
        Assert.Equal("\"String\"", client.Evaluate("typeof(T).Name", top.Id).Result);
    }

    [Fact]
    public void NullableAndStructVariablesCanBeAssigned()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("evaluator", "evaluator", "evaluatorAfter");
        int locals = client.Request<ScopesResponseBody>("scopes", new { frameId = top.Id }).Scopes[0].VariablesReference;

        Assert.Equal("null", client.Evaluate("maybe = null", top.Id).Result);
        Assert.Equal("null", client.Locals(top.Id)["maybe"].Value);
        Assert.Equal("7", client.Evaluate("maybe = 7", top.Id).Result);
        var set = client.Request<SetVariableResponseBody>("setVariable", new { variablesReference = locals, name = "maybe", value = "null" });
        Assert.Equal("null", set.Value);

        client.Request("setVariable", new { variablesReference = locals, name = "point", value = "new GridPoint(9, 8)" });
        Assert.Equal("9", client.Evaluate("point.X", top.Id).Result);
        Assert.Equal("8", client.Evaluate("point.Y", top.Id).Result);

        (_, top) = client.ContinueToStop(threadId);
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.Contains("evaluator after null 9,8", client.Output("stdout"));
    }

    // ---------------------------------------------------------------- L1 - L5: what the Linux run of the probe added
    // (L1 is UnpagedRequestForAHugeCollectionIsCapped, L3 is AttachToAMissingProcessIsExplainedInPlainWords)

    [Fact]
    public void PropertyWithAnEndlessLoopDoesNotCostTheSession()
    {
        using var client = new DapClient(TimeSpan.FromSeconds(40));
        var (threadId, top) = client.RunTo("implicitLoop", "implicitLoop");
        bool interruptible = RuntimeCanInterruptTightLoops(client, top.Id);

        Dictionary<string, Variable> members = client.Variables(client.Locals(top.Id)["looping"].VariablesReference);
        Assert.Equal("1", members["Before"].Value);
        if (!interruptible)
        {
            AssertTheSessionCanStillBeEnded(client, members["Hangs"].Value);
            return;
        }
        Assert.DoesNotContain("cannot be stopped", members["Hangs"].Value);

        // the explicit evaluation of the same getter recovers on every platform: the implicit one has to as well
        Assert.Equal("4", client.Evaluate("looping.After", top.Id).Result);
        Assert.NotEmpty(client.StackTrace(threadId));
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
    }

    /// <summary>The explicit counterpart of the test above: the path that was reported to recover.</summary>
    [Fact]
    public void ExplicitEvaluationOfAnEndlessLoopTimesOutAndTheSessionGoesOn()
    {
        using var client = new DapClient(TimeSpan.FromSeconds(40));
        var (threadId, top) = client.RunTo("implicitLoop", "implicitLoop");
        bool interruptible = RuntimeCanInterruptTightLoops(client, top.Id);

        string error = client.EvaluateError("looping.Hangs", top.Id);
        if (!interruptible)
        {
            AssertTheSessionCanStillBeEnded(client, error);
            return;
        }
        Assert.Contains("timed out", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("4", client.Evaluate("looping.After", top.Id).Result);
        Assert.NotEmpty(client.StackTrace(threadId));
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
    }

    [Fact]
    public void DebuggeeDoesNotOutliveAKilledAdapter()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("wait");
        client.Request("configurationDone");
        int pid = client.WaitForEvent("process").GetBody<ProcessEventBody>()!.SystemProcessId!.Value;
        using Process debuggee = Process.GetProcessById(pid);
        client.WaitForOutput("stdout", "mode: wait");

        client.KillAdapter();
        try
        {
            Assert.True(debuggee.WaitForExit(5000), "the debuggee is still running");
        }
        finally
        {
            if (!debuggee.HasExited)
                debuggee.Kill();
        }
    }

    [Fact]
    public void MissingWorkingDirectoryFailsTheLaunch()
    {
        using var client = new DapClient();
        client.Initialize();
        string cwd = Path.Combine(Path.GetTempPath(), "no-such-directory-" + Guid.NewGuid().ToString("N"));
        DapMessage response = client.RequestRaw("launch", new { program = TestPaths.TestAppDll, args = new[] { "basic" }, cwd });
        if (response.Success == true)
        {
            // the launch may be answered before the process is started: then the failure has to follow
            client.Send("configurationDone");
            client.WaitForEvent("terminated");
            Assert.DoesNotContain("mode: basic", client.Output("stdout"));
            Assert.Contains(cwd, client.Output("console") + client.Output("stderr") + client.Output("important"));
            return;
        }
        Assert.Contains(cwd, response.Message);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// On Linux and macOS the runtimes before .NET 9 cannot suspend a thread that is in a loop without calls or
    /// allocations: ICorDebugProcess.Stop never returns, for an explicit evaluation as for an implicit one (measured in
    /// Docker: .NET 8 loses the session either way, .NET 10 recovers either way). Nothing a debugger can do about it.
    /// </summary>
    private static bool RuntimeCanInterruptTightLoops(DapClient client, int frameId) =>
        OperatingSystem.IsWindows() || int.Parse(client.Evaluate("System.Environment.Version.Major", frameId).Result) >= 9;

    private static void AssertTheSessionCanStillBeEnded(DapClient client, string message)
    {
        Assert.Contains("cannot be stopped", message);
        Assert.DoesNotContain("HRESULT", message);
        client.Request("terminate");
        client.WaitForEvent("exited");
    }

    private static Process StartWaitingDebuggee()
    {
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add(TestPaths.TestAppDll);
        startInfo.ArgumentList.Add("wait");
        Process debuggee = Process.Start(startInfo)!;
        Assert.Equal("mode: wait", debuggee.StandardOutput.ReadLine());
        return debuggee;
    }

    private static Process StartAdapter(params string[] arguments)
    {
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
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo)!;
    }
}
