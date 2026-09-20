using System.Diagnostics;
using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>Roadmap wave 2 (2.1, Linux/macOS, is skipped on the owner's request).</summary>
public class Wave2Tests
{
    private static string EmbeddedAppDll => Path.Combine(TestPaths.Root, "tests", "EmbeddedApp", "bin", TestPaths.Configuration, TestPaths.TargetFramework, "EmbeddedApp.dll");
    private static string EmbeddedAppDirectory => Path.Combine(TestPaths.Root, "tests", "EmbeddedApp");

    // ---------------------------------------------------------------- 2.2 DebuggerTypeProxy

    [Fact]
    public void DebuggerTypeProxy()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("wave2", "wave2");
        Variable bag = client.Locals(top.Id)["bag"];

        Dictionary<string, Variable> children = client.Variables(bag.VariablesReference);
        Assert.Equal("2", children["Count"].Value);
        Assert.Equal("\"x\"", children["First"].Value);
        Assert.Equal("\"y\"", children["[1]"].Value); // RootHidden member of the proxy
        Assert.DoesNotContain("Noise", children.Keys);
        Assert.DoesNotContain("bag", children.Keys); // the proxy's own plumbing stays out of sight

        Dictionary<string, Variable> raw = client.Variables(children["Raw View"].VariablesReference);
        Assert.Equal("1", raw["Noise"].Value);
        Assert.Equal("Count = 2", raw["Items"].Value);
    }

    // ---------------------------------------------------------------- 2.3 assignments through the UI

    [Fact]
    public void SetVariableCallsPropertySettersAndAssignsEnums()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("wave2", "wave2");
        int scope = client.Request<ScopesResponseBody>("scopes", new { frameId = top.Id }).Scopes[0].VariablesReference;
        Dictionary<string, Variable> locals = client.Variables(scope);

        SetVariableResponseBody Set(int reference, string name, string value) =>
            client.Request<SetVariableResponseBody>("setVariable", new { variablesReference = reference, name, value });

        Assert.Equal("4", Set(locals["square"].VariablesReference, "Counter", "2").Value); // the setter doubles
        Assert.Equal("Blue", Set(scope, "color", "Color.Blue").Value);
        Assert.Equal("Green", Set(scope, "color", "1").Value);
        Assert.False(client.RequestRaw("setVariable", new { variablesReference = locals["square"].VariablesReference, name = "Area", value = "1" }).Success);

        var capabilities = client.Request<SetExpressionResponseBody>("setExpression", new { expression = "square.Side", value = "7", frameId = top.Id });
        Assert.Equal("7", capabilities.Value);
        Assert.Equal("10", client.Request<SetExpressionResponseBody>("setExpression", new { expression = "numbers[0]", value = "counter + 10", frameId = top.Id }).Value);
        Assert.Equal("5", client.Request<SetExpressionResponseBody>("setExpression", new { expression = "counter", value = "5", frameId = top.Id }).Value);
        Assert.Equal("8", client.Request<SetExpressionResponseBody>("setExpression", new { expression = "pair.A", value = "8", frameId = top.Id }).Value);
        Assert.False(client.RequestRaw("setExpression", new { expression = "counter + 1", value = "5", frameId = top.Id }).Success);

        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.Contains("state2: 7 4 Green 8 5 10", client.Output("stdout"));
    }

    // ---------------------------------------------------------------- 2.4 - 2.6 expressions

    [Theory]
    // 2.4
    [InlineData("typeof(Square).Name", "\"Square\"")]
    [InlineData("typeof(int).FullName", "\"System.Int32\"")]
    [InlineData("typeof(System.Collections.Generic.List<int>).Name", "\"List`1\"")]
    [InlineData("default(int)", "0")]
    [InlineData("default(string)", "null")]
    [InlineData("default(Square) == null", "true")]
    [InlineData("nameof(square.Side)", "\"Side\"")]
    [InlineData("nameof(Square)", "\"Square\"")]
    [InlineData("sizeof(long)", "8")]
    [InlineData("$\"side={square.Side} n={numbers.Length}\"", "\"side=3 n=3\"")]
    [InlineData("$\"{color}/{numbers[1],3}/{square.Area:F1}\"", "\"Red/  2/9.0\"")]
    [InlineData("System.Collections.Generic.Comparer<int>.Default.Compare(1, 2)", "-1")]
    [InlineData("new System.Collections.Generic.List<int>().Count", "0")]
    [InlineData("System.Array.Empty<string>().Length", "0")]
    // 2.5
    [InlineData("new Square(4).Area", "16")]
    [InlineData("new Pair().A", "0")]
    [InlineData("new Pair { A = 5 }.A", "5")]
    [InlineData("new int[3].Length", "3")]
    [InlineData("new[] { 4, 5, 6 }[1]", "5")]
    [InlineData("new System.Text.StringBuilder(\"ab\").Append(\"c\").ToString()", "\"abc\"")]
    [InlineData("new string('x', 3)", "\"xxx\"")]
    // 2.6
    [InlineData("shape.Area", "9")]
    [InlineData("shape.Describe()", "\"square 3\"")]
    [InlineData("((IShape)square).Describe()", "\"square 3\"")]
    [InlineData("square is IShape", "true")]
    [InlineData("color is IShape", "false")]
    [InlineData("(shape as Square).Side", "3")]
    [InlineData("numbers is System.Collections.IEnumerable", "true")]
    public void Expressions(string expression, string expected)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("wave2", "wave2");
        Assert.Equal(expected, client.Evaluate(expression, top.Id).Result);
    }

    [Fact]
    public void AssignmentExpressions()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("wave2", "wave2");

        Assert.Equal("5", client.Evaluate("counter = 5", top.Id, "repl").Result);
        Assert.Equal("5", client.Evaluate("counter++", top.Id, "repl").Result);
        Assert.Equal("7", client.Evaluate("++counter", top.Id, "repl").Result);
        Assert.Equal("9", client.Evaluate("counter += 2", top.Id, "repl").Result);
        Assert.Equal("4", client.Evaluate("counter -= 5", top.Id, "repl").Result);
        Assert.Equal("7", client.Evaluate("square.Side = 7", top.Id, "repl").Result);
        Assert.Equal("4", client.Evaluate("square.Counter = 2", top.Id, "repl").Result);
        Assert.Equal("10", client.Evaluate("numbers[0] = 10", top.Id, "repl").Result);
        Assert.Equal("Green", client.Evaluate("color = Color.Green", top.Id, "repl").Result);
        Assert.Equal("8", client.Evaluate("pair.A = 8", top.Id, "repl").Result);
        Assert.Equal("4", client.Evaluate("counter", top.Id).Result);

        // hovering must never change the program
        Assert.False(client.RequestRaw("evaluate", new { expression = "counter = 1", frameId = top.Id, context = "hover" }).Success);
        Assert.False(client.RequestRaw("evaluate", new { expression = "counter++", frameId = top.Id, context = "hover" }).Success);
        Assert.False(client.RequestRaw("evaluate", new { expression = "new Square(1)", frameId = top.Id, context = "hover" }).Success);

        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.Contains("state2: 7 4 Green 8 4 10", client.Output("stdout"));
    }

    [Fact]
    public void SpansMemoriesConcurrentCollectionsAndResultsView()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("wave2", "wave2");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Assert.Equal("{System.Span<int>[2]}", locals["span"].Value);
        Assert.Equal("Count = 2", locals["memory"].Value);
        Assert.Equal("3", client.Variables(locals["memory"].VariablesReference)["[1]"].Value);
        Assert.Equal("\"ell\"", locals["text"].Value);

        Assert.Equal("Count = 1", locals["concurrent"].Value);
        Assert.Equal("5", client.Variables(locals["concurrent"].VariablesReference)["[\"k\"]"].Value);

        // a LINQ query: its elements only exist once somebody enumerates it
        Dictionary<string, Variable> lazy = client.Variables(locals["lazy"].VariablesReference);
        Variable resultsView = lazy["Results View"];
        Assert.True(resultsView.PresentationHint?.Lazy);
        Dictionary<string, Variable> results = client.Variables(Assert.Single(client.Variables(resultsView.VariablesReference).Values).VariablesReference);
        Assert.Equal("2", results["[0]"].Value);
        Assert.Equal("3", results["[1]"].Value);
    }

    [Fact]
    public void SpanElements()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("wave2", "wave2");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        // primitives are read straight from memory
        Assert.Equal("{System.Span<int>[2]}", locals["span"].Value);
        Assert.Equal(2, locals["span"].IndexedVariables);
        Dictionary<string, Variable> span = client.Variables(locals["span"].VariablesReference);
        Assert.Equal("2", span["[0]"].Value);
        Assert.Equal("3", span["[1]"].Value);
        Assert.Equal("int", span["[0]"].Type);

        // a span of chars reads best as text
        Assert.Equal("\"ell\"", locals["chars"].Value);

        // references and structs
        Dictionary<string, Variable> words = client.Variables(locals["words"].VariablesReference);
        Assert.Equal("\"b\"", words["[0]"].Value);
        Assert.Equal("\"c\"", words["[1]"].Value);
        Dictionary<string, Variable> pairs = client.Variables(locals["pairs"].VariablesReference);
        Assert.Equal("{TestApp.Pair}", pairs["[1]"].Value);
        Assert.Equal("4", client.Variables(pairs["[1]"].VariablesReference)["B"].Value);

        Assert.Equal("{System.Span<double>[0]}", locals["none"].Value);
        Assert.Equal(0, locals["none"].VariablesReference);

        // expressions
        Assert.Equal("3", client.Evaluate("span[1]", top.Id).Result);
        Assert.Equal("5", client.Evaluate("span[0] + span[1]", top.Id).Result);
        Assert.Equal("2", client.Evaluate("span.Length", top.Id).Result);
        Assert.Equal("'l'", client.Evaluate("chars[1]", top.Id).Result);
        Assert.Equal("\"c\"", client.Evaluate("words[1]", top.Id).Result);
        Assert.Equal("3", client.Evaluate("pairs[1].A", top.Id).Result);
        Assert.Contains("IndexOutOfRangeException", client.EvaluateError("span[2]", top.Id));
        Assert.Equal("0x00000002", client.Variables(client.Evaluate("span,h", top.Id).VariablesReference)["[0]"].Value);
    }

    // ---------------------------------------------------------------- 2.7 async stepping

    [Fact]
    public void StepOverAwaitFollowsTheSameInvocation()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("asyncParallel", "asyncWorker");
        Assert.Equal("1", client.Locals(top.Id)["id"].Value);
        client.SetBreakpointsIn(TestPaths.Find("asyncWorker").Path); // the second invocation must not stop at the breakpoint

        (threadId, top) = client.StepAndWait("next", threadId); // on the await
        (threadId, top) = client.StepAndWait("next", threadId); // over it: invocation 2 resumes first (shorter delay)
        Assert.Equal("TestApp.Wave2.Worker()", top.Name);
        Assert.Equal(TestPaths.LineOf("asyncWorker") + 2, top.Line);
        Assert.Equal("1", client.Locals(top.Id)["id"].Value);
    }

    [Fact]
    public void StepOutOfAnAsyncMethodLandsInTheAwaitingCaller()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("asyncNested", "innerAfter");
        Assert.Equal("TestApp.Wave2.Inner()", top.Name);

        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("TestApp.Wave2.Outer()", top.Name);
        Assert.InRange(top.Line, TestPaths.LineOf("outerAwait"), TestPaths.LineOf("outerAfter"));
        if (top.Line == TestPaths.LineOf("outerAwait"))
            (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal("42", client.Locals(top.Id)["result"].Value);
    }

    // ---------------------------------------------------------------- 2.8 set next statement

    [Fact]
    public void GotoMovesTheInstructionPointer()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("loop", "loopEnd");
        Assert.Equal("45", client.Locals(top.Id)["total"].Value);

        var targets = client.Request<GotoTargetsResponseBody>("gotoTargets",
            new { source = new { path = TestPaths.ProgramSource }, line = TestPaths.LineOf("loopBody") });
        GotoTarget target = Assert.Single(targets.Targets);
        Assert.Equal(TestPaths.LineOf("loopBody"), target.Line);

        client.Request("goto", new { threadId, targetId = target.Id });
        StoppedEventBody stop = client.WaitForStop("goto");
        top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.Equal(TestPaths.LineOf("loopBody"), top.Line);

        // the statement really runs again: total += i  with i == 10
        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal("55", client.Locals(top.Id)["total"].Value);

        // jumping into another method is not possible
        var elsewhere = client.Request<GotoTargetsResponseBody>("gotoTargets",
            new { source = new { path = TestPaths.ProgramSource }, line = TestPaths.LineOf("add") });
        Assert.Empty(elsewhere.Targets);
    }

    // ---------------------------------------------------------------- 2.9 exception conditions

    [Theory]
    [InlineData("System.ArgumentException")]
    [InlineData("!System.InvalidOperationException")]
    [InlineData("System.Arg*")]
    [InlineData("System.Text.DecoderFallbackException, System.ArgumentException")] // DecoderFallbackException derives from ArgumentException
    public void ExceptionFilterConditions(string condition)
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("exception");
        client.Request("setExceptionBreakpoints", new { filters = Array.Empty<string>(), filterOptions = new[] { new { filterId = "all", condition } } });
        client.Request("configurationDone");

        // InvalidOperationException ("caught one") is thrown first and must be skipped
        StoppedEventBody stop = client.WaitForStop("exception");
        Assert.Equal("fatal one", stop.Text);
    }

    [Fact]
    public void ExceptionConditionMatchesBaseTypes()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("exception");
        client.Request("setExceptionBreakpoints", new { filters = Array.Empty<string>(), filterOptions = new[] { new { filterId = "all", condition = "System.SystemException" } } });
        client.Request("configurationDone");
        Assert.Equal("caught one", client.WaitForStop("exception").Text); // InvalidOperationException : SystemException
    }

    // ---------------------------------------------------------------- 2.10 - 2.11 sources

    [Fact]
    public void EmbeddedSourcesAreServedWhenTheFileIsNotOnDisk()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = EmbeddedAppDll });
        client.Request("setFunctionBreakpoints", new { breakpoints = new[] { new { name = "EmbeddedApp.Program.Compute" } } });
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("function breakpoint"));
        Assert.Equal("/_/embedded/Program.cs", top.Source!.Path!.Replace('\\', '/'));
        Assert.True(top.Source.SourceReference > 0);

        var source = client.Request<SourceResponseBody>("source", new { sourceReference = top.Source.SourceReference, source = top.Source });
        Assert.Contains("embedded marker", source.Content);
        Assert.Contains("int doubled = input * 2;", source.Content);

        // breakpoints can be set in such a document, by the path the debugger reported
        int line = source.Content.Split('\n').ToList().FindIndex(l => l.Contains("return doubled + 2;")) + 1;
        Breakpoint bp = Assert.Single(client.Request<SetBreakpointsResponseBody>("setBreakpoints",
            new { source = top.Source, breakpoints = new[] { new { line } } }).Breakpoints);
        Assert.True(bp.Verified);
        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal(line, top.Line);
    }

    [Fact]
    public void SourceFileMapTranslatesPathsBothWays()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new
        {
            program = EmbeddedAppDll,
            sourceFileMap = new Dictionary<string, string> { ["/_/embedded"] = EmbeddedAppDirectory },
        });
        string localPath = Path.Combine(EmbeddedAppDirectory, "Program.cs");
        int line = Array.FindIndex(File.ReadAllLines(localPath), l => l.Contains("// bp:embeddedCompute")) + 1;
        client.Request("setBreakpoints", new { source = new { path = localPath }, breakpoints = new[] { new { line } } });
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal(localPath, top.Source!.Path, ignoreCase: true);
        Assert.True(top.Source.SourceReference is null or 0); // the local file is the real thing
        Assert.Equal(line, top.Line);
    }

    [Fact]
    public void BreakpointInAChangedSourceFileIsFlagged()
    {
        string copy = Path.Combine(Path.GetTempPath(), "dotnet-debugger-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        try
        {
            string original = File.ReadAllText(Path.Combine(EmbeddedAppDirectory, "Program.cs"));
            File.WriteAllText(Path.Combine(copy, "Program.cs"), "// edited after the build\n" + original);

            using var second = new DapClient();
            second.Initialize();
            second.Request("launch", new { program = EmbeddedAppDll, stopAtEntry = true, sourceFileMap = new Dictionary<string, string> { ["/_/embedded"] = copy } });
            second.Request("configurationDone");
            second.WaitForStop("entry");
            Breakpoint bp = Assert.Single(second.Request<SetBreakpointsResponseBody>("setBreakpoints",
                new { source = new { path = Path.Combine(copy, "Program.cs") }, breakpoints = new[] { new { line = 8 } } }).Breakpoints);
            Assert.True(bp.Verified);
            Assert.Contains("differs", bp.Message);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    // ---------------------------------------------------------------- 2.12 threads

    [Fact]
    public void FrozenThreadDoesNotRun()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("workers");
        client.Request("configurationDone");
        client.WaitForOutput("stdout", "workers started");

        client.Request("pause", new { threadId = 0 });
        StoppedEventBody stop = client.WaitForStop("pause");
        Protocol.Thread[] threads = client.Request<ThreadsResponseBody>("threads").Threads;
        int threadA = threads.Single(t => t.Name == "counter-A").Id;
        StackFrame frame = client.StackTrace(stop.ThreadId!.Value).First(f => f.Source != null);

        client.Request("dotnet/freezeThread", new { threadId = threadA });
        Assert.Contains(client.Request<ThreadsResponseBody>("threads").Threads, t => t.Id == threadA && t.Name == "counter-A (frozen)");
        int a1 = int.Parse(client.Evaluate("Wave2.CounterA", frame.Id).Result);
        int b1 = int.Parse(client.Evaluate("Wave2.CounterB", frame.Id).Result);

        client.Request("continue", new { threadId = stop.ThreadId });
        System.Threading.Thread.Sleep(600);
        client.Request("pause", new { threadId = 0 });
        stop = client.WaitForStop("pause");
        frame = client.StackTrace(stop.ThreadId!.Value).First(f => f.Source != null);
        Assert.InRange(int.Parse(client.Evaluate("Wave2.CounterA", frame.Id).Result), a1, a1 + 1);
        Assert.True(int.Parse(client.Evaluate("Wave2.CounterB", frame.Id).Result) > b1 + 10);

        client.Request("dotnet/thawThread", new { threadId = threadA });
        client.Evaluate("Wave2.StopWorkers = true", frame.Id, "repl");
        client.Request("continue", new { threadId = stop.ThreadId });
        client.WaitForEvent("exited");
    }

    // ---------------------------------------------------------------- 2.13 - 2.14 session

    [Fact]
    public void RestartKeepsBreakpointsAndStartsAFreshProcess()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("basic");
        client.SetBreakpoints("add");
        client.Request("setExceptionBreakpoints", new { filters = new[] { "unhandled" } });
        client.Request("configurationDone");
        int firstPid = client.WaitForEvent("process").GetBody<ProcessEventBody>()!.SystemProcessId!.Value;
        client.WaitForStop("breakpoint");

        client.Request("restart", new { });
        int secondPid = client.WaitForEvent("process").GetBody<ProcessEventBody>()!.SystemProcessId!.Value;
        Assert.NotEqual(firstPid, secondPid);
        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestApp.Program.Add()", top.Name);
        Assert.False(client.HasPendingEvent("terminated"), "restarting must not end the session");
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(firstPid));

        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        client.WaitForEvent("terminated");
    }

    [Fact]
    public void AdapterReportsItsVersion()
    {
        using var client = new DapClient();
        client.Initialize();
        var info = client.Request<Dictionary<string, System.Text.Json.JsonElement>>("dotnet/info");
        Assert.Matches(@"^\d+\.\d+", info["version"].GetString());
        Assert.StartsWith(".NET", info["runtime"].GetString());
    }

    // ---------------------------------------------------------------- 2.16 return values when stepping over

    [Fact]
    public void StepOverReportsWhatTheCallsOfTheLineReturned()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("basic", "locals");
        (threadId, top) = client.StepAndWait("next", threadId);

        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("52", locals["TestApp.Program.Add() returned"].Value);
        Assert.Equal("52", locals["sum"].Value);

        // two calls on one line: Console.WriteLine(multiply(3)) reports the delegate's result
        using var second = new DapClient();
        (threadId, top) = second.RunTo("stepFilters", "stepEnd");
        (threadId, top) = second.StepAndWait("next", threadId);
        Assert.Equal("\"xxx\"", second.Locals(top.Id)["TestApp.StepFilters.MakeText() returned"].Value);
    }
}

/// <summary>Heavy scenarios; run with DOTNET_DEBUGGER_STRESS=1.</summary>
public class StressTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("DOTNET_DEBUGGER_STRESS") == "1";

    [Fact]
    public void ManyThreadsHittingAConditionalBreakpoint()
    {
        if (!Enabled)
            return;
        using var client = new DapClient(TimeSpan.FromMinutes(3));
        client.Initialize();
        client.Launch("stress");
        // 400 hits from 8 threads, each needing an evaluation; none of them may stop or get lost
        client.SetBreakpoints(new BreakpointSpec("stressHit", Condition: "i < 0"), new BreakpointSpec("stressDone"));
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal(TestPaths.LineOf("stressDone"), top.Line);
        Assert.Equal("400", client.Locals(top.Id)["hits"].Value);
    }

    [Fact]
    public void DeepStacksAndLargeArraysStayResponsive()
    {
        if (!Enabled)
            return;
        using var client = new DapClient(TimeSpan.FromMinutes(3));
        var (threadId, _) = client.RunTo("stress", "stressBottom");

        var watch = Stopwatch.StartNew();
        var page = client.Request<StackTraceResponseBody>("stackTrace", new { threadId, startFrame = 0, levels = 20 });
        Assert.True(page.TotalFrames > 2000);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"stackTrace took {watch.Elapsed}");

        StackFrame stress = client.Request<StackTraceResponseBody>("stackTrace", new { threadId, startFrame = page.TotalFrames - 2, levels = 1 }).StackFrames[0];
        Assert.Equal("TestApp.Wave2.Stress()", stress.Name);
        Variable big = client.Locals(stress.Id)["big"];
        Assert.Equal(1_000_000, big.IndexedVariables);

        watch.Restart();
        Variable[] slice = client.Request<VariablesResponseBody>("variables",
            new { variablesReference = big.VariablesReference, filter = "indexed", start = 999_900, count = 100 }).Variables;
        Assert.Equal(100, slice.Length);
        Assert.Equal("999999", slice[^1].Value);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"variables took {watch.Elapsed}");
    }
}
