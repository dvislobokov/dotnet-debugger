using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>
/// .NET Framework 4.x debugging (Windows only): the core scenarios of the CoreCLR suite, replayed against
/// tests/TestAppFx (net48, x64, C# 7.3). The debugger must present a Framework process exactly the way it presents a
/// .NET (Core) one: same frame names, same value and type display, same events.
/// </summary>
public class FrameworkTests
{
    private const string ProgramMain = "TestAppFx.Program.Main()";

    // ---------------------------------------------------------------- launch, exit code, output

    [WindowsFact]
    public void RunsToCompletionAndReportsOutputAndExitCode()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("none");
        client.Request("configurationDone");

        var exited = client.WaitForEvent("exited").GetBody<ExitedEventBody>()!;
        client.WaitForEvent("terminated");

        Assert.Equal(3, exited.ExitCode); // Environment.ExitCode set by a void Main
        Assert.Contains("mode: none", client.Output("stdout"));
        Assert.Contains("done", client.Output("stderr"));
    }

    [WindowsFact]
    public void EnvironmentExitCodeIsReported()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("exit");
        client.Request("configurationDone");

        var exited = client.WaitForEvent("exited").GetBody<ExitedEventBody>()!;
        client.WaitForEvent("terminated");
        Assert.Equal(5, exited.ExitCode);
        Assert.Contains("exiting with 5", client.Output("stdout"));
        Assert.DoesNotContain("done", client.Output("stderr"));
    }

    [WindowsFact]
    public void NonAsciiOutputArrivesIntact()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("output");
        client.Request("configurationDone");
        client.WaitForEvent("exited");

        string stdout = client.Output("stdout");
        Assert.Contains("plain ascii line", stdout);
        Assert.Contains("Привет, мир! Grüße €", stdout);
        Assert.Contains("error line: Ошибка", client.Output("stderr"));
    }

    [WindowsFact]
    public void StopsAtEntry()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("none", stopAtEntry: true);
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("entry");
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.Equal(ProgramMain, top.Name);
        Assert.Equal(TestPaths.TestAppFxProgramSource, top.Source!.Path, ignoreCase: true);

        client.Request("continue", new { threadId = stop.ThreadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
    }

    // ---------------------------------------------------------------- breakpoints, stack, locals

    [WindowsFact]
    public void BreakpointHitShowsStackAndLocals()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("basic");
        Breakpoint[] breakpoints = client.SetBreakpoints("fx_locals", "fx_add");
        client.Request("configurationDone");

        // set before the module was loaded, so they get verified through events
        foreach (Breakpoint bp in breakpoints)
        {
            if (!bp.Verified)
                client.WaitForEvent("breakpoint", e => e.GetBody<BreakpointEventBody>()!.Breakpoint is { Verified: true } b && b.Id == bp.Id);
        }

        StoppedEventBody stop = client.WaitForStop("breakpoint");
        Assert.Equal([breakpoints[0].Id!.Value], stop.HitBreakpointIds!);

        StackFrame[] frames = client.StackTrace(stop.ThreadId!.Value);
        Assert.Equal("TestAppFx.Program.Basic()", frames[0].Name);
        Assert.Equal(TestPaths.LineOf("fx_locals"), frames[0].Line);
        Assert.Equal(TestPaths.TestAppFxProgramSource, frames[0].Source!.Path, ignoreCase: true);
        Assert.Equal(ProgramMain, frames[1].Name);

        Dictionary<string, Variable> locals = client.Locals(frames[0].Id);
        Assert.Equal("42", locals["number"].Value);
        Assert.Equal("int", locals["number"].Type);
        Assert.Equal("\"hello \\\"world\\\"\"", locals["text"].Value);
        Assert.Equal("string", locals["text"].Type);
        Assert.Equal("1.5", locals["ratio"].Value);
        Assert.Equal("double", locals["ratio"].Type);
        Assert.Equal("true", locals["flag"].Value);
        Assert.Equal("bool", locals["flag"].Type);
        Assert.Equal("'x'", locals["letter"].Value);
        Assert.Equal("12.34", locals["money"].Value);
        Assert.Equal("decimal", locals["money"].Type);
        Assert.Equal("1234567890123", locals["big"].Value);
        Assert.Equal("long", locals["big"].Type);
        Assert.Equal("7", locals["maybe"].Value);
        Assert.Equal("null", locals["nothing"].Value);
        Assert.Equal("Green", locals["color"].Value);
        Assert.Equal("TestAppFx.Color", locals["color"].Type);
        Assert.Equal("99", locals["boxed"].Value);
        Assert.Equal("null", locals["nobody"].Value);
        Assert.Equal("{int[3]}", locals["numbers"].Value);
        Assert.Equal("int[]", locals["numbers"].Type);
        Assert.Equal("{Person:Ann}", locals["person"].Value);
        Assert.Equal("TestAppFx.Person", locals["person"].Type);
        Assert.Equal("Count = 2", locals["list"].Value);
        Assert.Equal("System.Collections.Generic.List<string>", locals["list"].Type);
        Assert.Equal("Count = 1", locals["map"].Value);
        Assert.Equal("System.Collections.Generic.Dictionary<string, int>", locals["map"].Type);
        Assert.Equal("\"Привет\"", locals["greeting"].Value);

        Dictionary<string, Variable> point = client.Variables(locals["point"].VariablesReference);
        Assert.Equal("1", point["X"].Value);
        Assert.Equal("2", point["Y"].Value);

        Dictionary<string, Variable> person = client.Variables(locals["person"].VariablesReference);
        Assert.Equal("\"Ann\"", person["Name"].Value);
        Assert.Equal("30", person["Age"].Value);
        Dictionary<string, Variable> friend = client.Variables(person["Friend"].VariablesReference);
        Assert.Equal("\"Bob\"", friend["Name"].Value);

        Dictionary<string, Variable> numbers = client.Variables(locals["numbers"].VariablesReference);
        Assert.Equal(["10", "20", "30"], new[] { numbers["[0]"].Value, numbers["[1]"].Value, numbers["[2]"].Value });

        // second breakpoint, inside the callee: arguments
        client.Request("continue", new { threadId = stop.ThreadId });
        stop = client.WaitForStop("breakpoint");
        frames = client.StackTrace(stop.ThreadId!.Value);
        Assert.Equal("TestAppFx.Program.Add()", frames[0].Name);
        Assert.Equal(TestPaths.LineOf("fx_add"), frames[0].Line);
        Assert.Equal("TestAppFx.Program.Basic()", frames[1].Name);
        Assert.Equal(ProgramMain, frames[2].Name);
        locals = client.Locals(frames[0].Id);
        Assert.Equal("42", locals["a"].Value);
        Assert.Equal("10", locals["b"].Value);

        client.Request("continue", new { threadId = stop.ThreadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        Assert.Contains("52", client.Output("stdout"));
    }

    [WindowsFact]
    public void ObjectExpansionShowsPropertiesBaseMembersAndStatics()
    {
        using var client = new DapClient();
        var (_, top) = client.RunToFx("basic", "fx_locals");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Dictionary<string, Variable> person = client.Variables(locals["person"].VariablesReference);
        Assert.Equal("\"Ann\"", person["Name"].Value);
        Assert.Equal("30", person["Age"].Value);
        Assert.Equal("\"Ann (30)\"", person["Summary"].Value); // computed property => func-eval
        Assert.Equal("{Person:Bob}", person["Friend"].Value);
        Assert.DoesNotContain(person.Keys, k => k.Contains("k__BackingField"));

        Dictionary<string, Variable> statics = client.Variables(person["Static members"].VariablesReference);
        Assert.Equal("3", statics["Instances"].Value);
        Assert.Equal("\"human\"", statics["Species"].Value);

        // declared as Person, shown as the runtime type with derived + inherited members
        Assert.Equal("{Person:Eve}", locals["employee"].Value);
        Assert.Equal("TestAppFx.Employee", locals["employee"].Type);
        Dictionary<string, Variable> employee = client.Variables(locals["employee"].VariablesReference);
        Assert.Equal("\"Initech\"", employee["Company"].Value);
        Assert.Equal("\"Eve\"", employee["Name"].Value);
        Assert.Equal("\"Eve (40)\"", employee["Summary"].Value);

        // struct with a computed property
        Assert.Equal("TestAppFx.Point", locals["point"].Type);
        Dictionary<string, Variable> point = client.Variables(locals["point"].VariablesReference);
        Assert.Equal("3", point["Sum"].Value);
    }

    [WindowsFact]
    public void CollectionsAndSpecialValues()
    {
        using var client = new DapClient();
        var (_, top) = client.RunToFx("basic", "fx_locals");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Assert.Equal("Read | Write", locals["access"].Value);
        Assert.Equal("{int[2, 2]}", locals["grid"].Value);
        Dictionary<string, Variable> grid = client.Variables(locals["grid"].VariablesReference);
        Assert.Equal("3", grid["[1, 0]"].Value);

        // mscorlib's List<T> and Dictionary<K,V> (field layout differs from System.Private.CoreLib)
        Dictionary<string, Variable> list = client.Variables(locals["list"].VariablesReference);
        Assert.Equal("\"a\"", list["[0]"].Value);
        Assert.Equal("\"b\"", list["[1]"].Value);
        Dictionary<string, Variable> map = client.Variables(locals["map"].VariablesReference);
        Assert.Equal("1", map["[\"one\"]"].Value);

        // System.ValueTuple (in mscorlib since 4.7)
        Assert.Equal("(5, \"five\")", locals["pair"].Value);
        Assert.Equal("5", client.Variables(locals["pair"].VariablesReference)["Item1"].Value);

        Assert.Equal(3, locals["numbers"].IndexedVariables);
    }

    [WindowsFact]
    public void ConditionalBreakpointInLoop()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("loop");
        client.SetBreakpoints(new BreakpointSpec("fx_loopBody", Condition: "i == 5"));
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestAppFx.Program.Loop()", top.Name);
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("5", locals["i"].Value);
        Assert.Equal("10", locals["total"].Value);

        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.False(client.HasPendingEvent("stopped"));
        Assert.Contains("total: 45", client.Output("stdout"));
    }

    [WindowsFact]
    public void SetVariableIsSeenByTheDebuggee()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunToFx("basic", "fx_locals");
        int scope = Assert.Single(client.Request<ScopesResponseBody>("scopes", new { frameId = top.Id }).Scopes).VariablesReference;

        Assert.Equal("100", client.Request<SetVariableResponseBody>("setVariable", new { variablesReference = scope, name = "number", value = "100" }).Value);
        Assert.Equal("\"changed\"", client.Request<SetVariableResponseBody>("setVariable", new { variablesReference = scope, name = "text", value = "\"changed\"" }).Value);
        Assert.Equal("100", client.Evaluate("number", top.Id).Result);

        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.Contains("state: 100 changed 1.5 True", client.Output("stdout"));
    }

    // ---------------------------------------------------------------- stepping

    [WindowsFact]
    public void SteppingInOverAndOut()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunToFx("basic", "fx_locals");

        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestAppFx.Program.Add()", top.Name);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_add"), top.Line);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_add") + 1, top.Line);
        Assert.Equal("52", client.Locals(top.Id)["result"].Value);

        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("TestAppFx.Program.Basic()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_locals"), top.Line);

        // "next" must step over framework calls (Console.WriteLine in mscorlib) without stopping inside them
        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_afterAdd"), top.Line);
        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal("TestAppFx.Program.Basic()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_state"), top.Line);
        Assert.Equal("52", client.Locals(top.Id)["sum"].Value);
    }

    [WindowsFact]
    public void StepOverACallWithABreakpointStopsAtTheBreakpoint()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunToFx("basic", "fx_locals", "fx_add");

        client.Request("next", new { threadId });
        (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestAppFx.Program.Add()", top.Name);

        // the interrupted step must not fire later
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.False(client.HasPendingEvent("stopped"));
    }

    [WindowsFact]
    public void StepOutOfMainRunsToTheEnd()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunToFx("loop", "fx_loopEnd");
        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal(ProgramMain, top.Name);
        client.Request("stepOut", new { threadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
    }

    // ---------------------------------------------------------------- async

    [WindowsFact]
    public void AsyncMethodShowsHoistedLocalsAfterAwait()
    {
        using var client = new DapClient();
        var (_, top) = client.RunToFx("async", "fx_async");
        Assert.Equal("TestAppFx.Program.AsyncWork()", top.Name);
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("5", locals["input"].Value);
        Assert.Equal("10", locals["doubled"].Value);
        Assert.Equal("15", client.Evaluate("doubled + input", top.Id).Result);
    }

    [WindowsFact]
    public void StepOverAwaitStaysInTheAsyncMethod()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunToFx("async", "fx_asyncStart");
        Assert.Equal("TestAppFx.Program.AsyncWork()", top.Name);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_asyncStart") + 1, top.Line);

        // the continuation resumes on a thread pool thread
        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal("TestAppFx.Program.AsyncWork()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_async"), top.Line);
        Assert.Equal("10", client.Locals(top.Id)["doubled"].Value);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_async") + 1, top.Line);
        Assert.Equal("11", client.Locals(top.Id)["final"].Value);
    }

    // ---------------------------------------------------------------- exceptions

    [WindowsFact]
    public void BreaksOnThrownExceptionThatIsCaught()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("exception");
        client.Request("setExceptionBreakpoints", new { filters = new[] { "all", "unhandled" } });
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("exception");
        var info = client.Request<ExceptionInfoResponseBody>("exceptionInfo", new { threadId = stop.ThreadId });
        Assert.Equal("System.InvalidOperationException", info.ExceptionId);
        Assert.Equal("caught one", info.Description);
        Assert.Equal("always", info.BreakMode);
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.Equal("TestAppFx.Program.CaughtException()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_throwCaught"), top.Line);

        client.Request("continue", new { threadId = stop.ThreadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        Assert.Contains("handled: caught one", client.Output("stdout"));
    }

    [WindowsFact]
    public void CaughtExceptionDoesNotStopWithoutTheAllFilter()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("exception");
        client.Request("setExceptionBreakpoints", new { filters = new[] { "unhandled" } });
        client.SetBreakpoints("fx_catch");
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal(TestPaths.LineOf("fx_catch"), top.Line);
        Assert.Equal("\"caught one\"", client.Evaluate("e.Message", top.Id).Result);
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
    }

    [WindowsFact]
    public void UnhandledExceptionStopsWithDetailsAndEndsTheProcess()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("unhandled");
        client.Request("setExceptionBreakpoints", new { filters = Array.Empty<string>() }); // unhandled ones stop anyway
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("exception");
        Assert.Contains("System.ArgumentException", stop.Description);
        Assert.Equal("fatal one", stop.Text);

        var info = client.Request<ExceptionInfoResponseBody>("exceptionInfo", new { threadId = stop.ThreadId });
        Assert.Equal("System.ArgumentException", info.ExceptionId);
        Assert.Equal("unhandled", info.BreakMode);
        Assert.Equal("System.ArgumentException", info.Details!.FullTypeName);
        Assert.Equal("ArgumentException", info.Details.TypeName);
        Assert.Equal("fatal one", info.Details.Message);
        Assert.Contains("TestAppFx.Program.Fail", info.Details.StackTrace);
        ExceptionDetails inner = Assert.Single(info.Details.InnerException!);
        Assert.Equal("System.FormatException", inner.FullTypeName);
        Assert.Equal("inner cause", inner.Message);

        StackFrame[] frames = client.StackTrace(stop.ThreadId!.Value);
        StackFrame top = frames.First(f => f.Source != null);
        Assert.Equal("TestAppFx.Program.Fail()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_throwFatal"), top.Line);
        Assert.Contains(frames, f => f.Name == "TestAppFx.Program.UnhandledException()");
        Assert.Equal("\"fatal one\"", client.Evaluate("$exception.Message", top.Id).Result);
        Assert.Equal("\"inner cause\"", client.Evaluate("inner.Message", top.Id).Result);

        client.Request("continue", new { threadId = stop.ThreadId });
        int exitCode = client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode;
        Assert.Equal(unchecked((int)0xE0434352), exitCode); // the CLR's SEH exception code, as without a debugger
    }

    // ---------------------------------------------------------------- mscorlib layouts

    // The debugger reads these types from memory, and mscorlib names their private fields differently than CoreLib.
    [WindowsFact]
    public void DatesDecimalsAndHashSetsAreReadFromMscorlibLayouts()
    {
        using var client = new DapClient();
        var (_, top) = client.RunToFx("collections", "fx_collections");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Assert.Equal("{2024-05-17 13:45:30 UTC}", locals["moment"].Value);
        Assert.Equal("{2024-05-17 13:45:30 +03:00}", locals["stamp"].Value);
        Assert.Equal("{01:30:00}", locals["span"].Value);
        Assert.Equal("-79228162514264.337593543950335", locals["price"].Value);

        Assert.Equal("Count = 3", locals["set"].Value);
        Dictionary<string, Variable> set = client.Variables(locals["set"].VariablesReference);
        Assert.Equal(new[] { "1", "2", "3" }, new[] { set["[0]"].Value, set["[1]"].Value, set["[2]"].Value });

        // a removed element leaves a free slot behind, which must not be shown
        Assert.Equal("Count = 3", locals["holes"].Value);
        Dictionary<string, Variable> holes = client.Variables(locals["holes"].VariablesReference);
        Assert.Equal(new[] { "\"a\"", "\"c\"", "\"d\"" }, new[] { holes["[0]"].Value, holes["[1]"].Value, holes["[2]"].Value });
        Assert.DoesNotContain("[3]", holes.Keys);
    }

    [WindowsFact]
    public void DictionariesWithRemovedEntriesAndConcurrentDictionaries()
    {
        using var client = new DapClient();
        var (_, top) = client.RunToFx("collections", "fx_collections");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Assert.Equal("Count = 2", locals["removed"].Value);
        Dictionary<string, Variable> removed = client.Variables(locals["removed"].VariablesReference);
        Assert.Equal("1", removed["[\"one\"]"].Value);
        Assert.Equal("3", removed["[\"three\"]"].Value);
        Assert.DoesNotContain("[\"two\"]", removed.Keys);

        Assert.Equal("Count = 2", locals["concurrent"].Value);
        Dictionary<string, Variable> concurrent = client.Variables(locals["concurrent"].VariablesReference);
        Assert.Equal("5", concurrent["[\"k\"]"].Value);
        Assert.Equal("7", concurrent["[\"m\"]"].Value);
    }

    // ---------------------------------------------------------------- async chains

    [WindowsFact]
    public void AsyncCallStackShowsTheAwaitingMethods()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunToFx("asyncNested", "fx_innerAfter");
        StackFrame[] frames = client.StackTrace(threadId);

        Assert.Equal("TestAppFx.Collections.Inner()", frames[0].Name);
        int label = Array.FindIndex(frames, f => f.Name == "[Async Call Stack]");
        Assert.True(label > 0, "no async call stack: " + string.Join(" | ", frames.Select(f => f.Name)));

        // Inner's task is awaited by Middle, Middle's (a plain Task: the non-generic builder) by Outer
        StackFrame middle = frames[label + 1], outer = frames[label + 2];
        Assert.Equal("TestAppFx.Collections.Middle()", middle.Name);
        Assert.Equal(TestPaths.LineOf("fx_middleAwait"), middle.Line);
        Assert.Equal("TestAppFx.Collections.Outer()", outer.Name);
        Assert.Equal(TestPaths.LineOf("fx_outerAwait"), outer.Line);
        Assert.Equal("0", client.Locals(middle.Id)["result"].Value);
    }

    [WindowsFact]
    public void StepOutOfAsyncMethodsLandsInTheAwaitingCallers()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunToFx("asyncNested", "fx_innerAfter");

        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("TestAppFx.Collections.Middle()", top.Name);
        Assert.InRange(top.Line, TestPaths.LineOf("fx_middleAwait"), TestPaths.LineOf("fx_middleAfter"));
        if (top.Line == TestPaths.LineOf("fx_middleAwait"))
            (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal("42", client.Locals(top.Id)["result"].Value);

        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("TestAppFx.Collections.Outer()", top.Name);
        Assert.InRange(top.Line, TestPaths.LineOf("fx_outerAwait"), TestPaths.LineOf("fx_outerAfter"));

        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.Contains("outer got 42", client.Output("stdout"));
    }

    // ---------------------------------------------------------------- threads

    [WindowsFact]
    public void ThreadsHaveNamesAndIndependentStacks()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunToFx("threads", "fx_worker");
        Assert.Equal("TestAppFx.Program.WorkerBody()", top.Name);

        Protocol.Thread[] threads = client.Request<ThreadsResponseBody>("threads").Threads;
        Assert.Equal("fx-worker", threads.Single(t => t.Id == threadId).Name);
        Assert.Equal("11", client.Locals(top.Id)["workerValue"].Value);

        Protocol.Thread main = threads.Single(t => t.Name == "Main Thread");
        // Thread.Join of .NET Framework is precompiled x64 code that tail-calls into the runtime: there may be no frame for it
        StackFrame[] mainFrames = client.StackTrace(main.Id).Where(f => f.Name != "[External Code]").ToArray();
        Assert.Equal("TestAppFx.Program.Threads()", mainFrames[0].Name);
        Assert.Equal(ProgramMain, mainFrames[1].Name);
        Assert.Equal("\"threads\"", client.Evaluate("mode", mainFrames[1].Id).Result);

        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.Contains("worker running 11", client.Output("stdout"));
    }

    // ---------------------------------------------------------------- evaluation

    [WindowsTheory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(number + 8) / 5", "10")]
    [InlineData("number % 5", "2")]
    [InlineData("ratio * 2", "3")]
    [InlineData("number / 4.0", "10.5")]
    [InlineData("number > 40 && flag", "true")]
    [InlineData("flag ? number : 0", "42")]
    [InlineData("\"a\" + \"b\"", "\"ab\"")]
    [InlineData("text + number", "\"hello \\\"world\\\"42\"")]
    [InlineData("greeting + \"!\"", "\"Привет!\"")]
    [InlineData("text.Length", "13")]
    [InlineData("person.Name", "\"Ann\"")]
    [InlineData("person.Friend.Age * 2", "62")]
    [InlineData("nobody == null", "true")]
    [InlineData("nobody?.Name", "null")]
    [InlineData("person.Summary", "\"Ann (30)\"")]
    [InlineData("point.X + point.Y", "3")]
    [InlineData("point.Sum", "3")]
    [InlineData("((TestAppFx.Employee)employee).Company", "\"Initech\"")]
    [InlineData("maybe.Value + 1", "8")]
    [InlineData("money", "12.34")]
    [InlineData("numbers[1]", "20")]
    [InlineData("numbers.Length", "3")]
    [InlineData("grid[1, 0]", "3")]
    [InlineData("list[0]", "\"a\"")]
    [InlineData("list.Count", "2")]
    [InlineData("map[\"one\"]", "1")]
    [InlineData("person.Greet(\"hi\")", "\"hi, Ann\"")]
    [InlineData("person.ToString()", "\"Person:Ann\"")]
    [InlineData("number.ToString()", "\"42\"")]
    [InlineData("text.ToUpper()", "\"HELLO \\\"WORLD\\\"\"")]
    [InlineData("text.Substring(1, 3)", "\"ell\"")]
    [InlineData("list.Contains(\"b\")", "true")]
    [InlineData("Add(number, 8)", "50")]
    [InlineData("TestAppFx.Program.Add(1, 2) + 1", "4")]
    [InlineData("string.Concat(\"x\", \"y\")", "\"xy\"")]
    [InlineData("System.Math.Max(3, number)", "42")]
    [InlineData("Person.Instances", "3")]
    [InlineData("Person.Species", "\"human\"")]
    [InlineData("color == Color.Green", "true")]
    [InlineData("(int)color", "1")]
    [InlineData("access", "Read | Write")]
    [InlineData("int.MaxValue", "2147483647")]
    [InlineData("employee is TestAppFx.Employee", "true")]
    public void EvaluatesExpression(string expression, string expected)
    {
        using var client = new DapClient();
        var (_, top) = client.RunToFx("basic", "fx_locals");
        Assert.Equal(expected, client.Evaluate(expression, top.Id).Result);
    }

    [WindowsFact]
    public void EvaluatesMethodCallsPropertiesAndStatics()
    {
        // func-evals resume the process; frames and handles must survive that
        using var client = new DapClient();
        var (threadId, top) = client.RunToFx("evaluate", "fx_evaluate");
        Assert.Equal("TestAppFx.Evaluation.Run()", top.Name);
        Dictionary<string, Variable> before = client.Locals(top.Id);
        Assert.Equal("TestAppFx.Calculator", before["calc"].Type);

        // instance methods (overloads with side effects), getters with bodies, auto-properties, private fields
        Assert.Equal("42", client.Evaluate("calc.Multiply(x, y)", top.Id).Result);
        Assert.Equal("1", client.Evaluate("calc.Calls", top.Id).Result);
        Assert.Equal("23", client.Evaluate("calc.Add(x, y)", top.Id).Result);
        Assert.Equal("10", client.Evaluate("calc.Offset", top.Id).Result);
        Assert.Equal("\"main\"", client.Evaluate("calc.Name", top.Id).Result);
        Assert.Equal("\"main\"", client.Evaluate("calc._name", top.Id).Result);
        Assert.Equal("\"calc:main\"", client.Evaluate("calc.Describe(\"calc:\")", top.Id).Result);

        // statics
        Assert.Equal("\"1.0-fx\"", client.Evaluate("Calculator.Version", top.Id).Result);
        Assert.Equal("49", client.Evaluate("TestAppFx.Calculator.Square(y)", top.Id).Result);

        // arithmetic and strings (non-ASCII too)
        Assert.Equal("85", client.Evaluate("x * y * 2 + 1", top.Id).Result);
        Assert.Equal("3.5", client.Evaluate("y * half", top.Id).Result);
        Assert.Equal("\"Привет, world\"", client.Evaluate("greeting + \", \" + name", top.Id).Result);
        Assert.Equal("6", client.Evaluate("greeting.Length", top.Id).Result);
        Assert.Equal("\"ПРИВЕТ\"", client.Evaluate("greeting.ToUpper()", top.Id).Result);
        Assert.Equal("\"6x7\"", client.Evaluate("x.ToString() + \"x\" + y", top.Id).Result);

        // LINQ over a mscorlib List<T> (System.Core.dll extension methods)
        Assert.Equal("10", client.Evaluate("values.Sum()", top.Id).Result);
        Assert.Equal("4", client.Evaluate("values.Max()", top.Id).Result);
        Assert.Equal("\"Ann (30)\"", client.Evaluate("person.Summary", top.Id).Result);

        // a result is expandable
        EvaluateResponseBody calc = client.Evaluate("calc", top.Id);
        Assert.Equal("TestAppFx.Calculator", calc.Type);
        Assert.True(calc.VariablesReference > 0);
        Assert.Equal("10", client.Variables(calc.VariablesReference)["Offset"].Value);

        // handles obtained before the evals are still usable, and the caller's frame is too
        Assert.Equal("\"Ann\"", client.Variables(before["person"].VariablesReference)["Name"].Value);
        StackFrame main = client.StackTrace(threadId)[1];
        Assert.Equal(ProgramMain, main.Name);
        Assert.Equal("\"evaluate\"", client.Evaluate("mode", main.Id).Result);
        Assert.Equal("\"evaluate\"", client.Evaluate("args[0]", main.Id).Result);

        // the func-evals really ran in the debuggee (Multiply bumped the counter)
        client.Request("next", new { threadId });
        (threadId, top) = client.Top(client.WaitForStop("step"));
        Assert.Equal(TestPaths.LineOf("fx_afterEvaluate"), top.Line);
        Assert.Equal("2", client.Evaluate("calc.Calls", top.Id).Result);
        Assert.Equal("42", client.Locals(top.Id)["product"].Value);
    }

    [WindowsTheory]
    [InlineData("missing", "missing")]
    [InlineData("person.Missing", "Missing")]
    [InlineData("nobody.Name", "NullReferenceException")]
    [InlineData("numbers[10]", "IndexOutOfRangeException")]
    [InlineData("number / 0", "DivideByZeroException")]
    public void ReportsEvaluationErrors(string expression, string expectedInMessage)
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunToFx("basic", "fx_locals");
        Assert.Contains(expectedInMessage, client.EvaluateError(expression, top.Id));

        // a failed evaluation must leave the session usable
        Assert.Equal("42", client.Evaluate("number", top.Id).Result);
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
    }

    // ---------------------------------------------------------------- pause, terminate, attach

    [WindowsFact]
    public void PauseBreakpointWhileRunningAndTerminate()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("wait");
        client.Request("configurationDone");
        client.WaitForOutput("stdout", "mode: wait");

        client.Request("pause", new { threadId = 0 });
        StoppedEventBody stop = client.WaitForStop("pause");
        Assert.NotEmpty(client.Request<ThreadsResponseBody>("threads").Threads);
        client.Request("continue", new { threadId = stop.ThreadId });

        // a breakpoint added while the debuggee is running binds immediately
        Breakpoint bp = Assert.Single(client.SetBreakpoints("fx_waitLoop"));
        Assert.True(bp.Verified);
        stop = client.WaitForStop("breakpoint");
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.Equal("TestAppFx.Program.Wait()", top.Name);
        Assert.True(int.Parse(client.Locals(top.Id)["counter"].Value) >= 0);

        // requests that need a stopped process fail cleanly while running
        client.SetBreakpointsIn(TestPaths.TestAppFxProgramSource);
        client.Request("continue", new { threadId = stop.ThreadId });
        Assert.False(client.RequestRaw("stackTrace", new { threadId = stop.ThreadId }).Success);

        client.Request("terminate");
        client.WaitForEvent("exited");
        client.WaitForEvent("terminated");
    }

    [WindowsFact]
    public void AttachesToRunningProcessAndDetaches()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(TestPaths.TestAppFxExe) { UseShellExecute = false, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add("wait");
        using System.Diagnostics.Process debuggee = System.Diagnostics.Process.Start(startInfo)!;
        try
        {
            Assert.Equal("mode: wait", debuggee.StandardOutput.ReadLine());

            using (var client = new DapClient())
            {
                client.Initialize();
                client.Request("attach", new { processId = debuggee.Id });
                client.Request("configurationDone");

                // modules are reported asynchronously after attach; retry until the PDB is known
                Breakpoint bp;
                int attempts = 0;
                while (!(bp = Assert.Single(client.SetBreakpoints("fx_waitLoop"))).Verified && attempts++ < 100)
                    System.Threading.Thread.Sleep(50);
                Assert.True(bp.Verified);

                StoppedEventBody stop = client.WaitForStop("breakpoint");
                StackFrame[] frames = client.StackTrace(stop.ThreadId!.Value);
                Assert.Equal("TestAppFx.Program.Wait()", frames[0].Name);
                Assert.Equal(ProgramMain, frames[1].Name);
                Assert.True(int.Parse(client.Locals(frames[0].Id)["counter"].Value) >= 0);

                client.Request("disconnect", new { terminateDebuggee = false });
            }

            Assert.False(debuggee.WaitForExit(500), "Detaching must leave the debuggee running.");
        }
        finally
        {
            debuggee.Kill();
        }
    }
}

/// <summary>A fact that only runs on Windows (.NET Framework exists nowhere else); reported as skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = ".NET Framework debugging is Windows only.";
    }
}

/// <summary>The <see cref="WindowsFactAttribute"/> of theories.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = ".NET Framework debugging is Windows only.";
    }
}
