using System.Diagnostics;
using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>Roadmap wave 1: display attributes, visualizers, format specifiers, step filters, protocol extras.</summary>
public class Wave1Tests
{
    // ---------------------------------------------------------------- 1.1 - 1.3 display

    [Fact]
    public void DebuggerDisplayAttribute()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("display", "display");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Assert.Equal("Order 7: Ann x2", locals["order"].Value);
        Assert.Equal("TestApp.Order", locals["order"].Type);
        Assert.Equal("Order 8: Bob x0", locals["special"].Value); // inherited from the base class
        Assert.Equal("\"Ann\"", locals["quoted"].Value); // strings keep their quotes unless ",nq" is used
        Assert.StartsWith("\"L5\" / ", locals["broken"].Value); // a broken expression does not spoil the rest
        Assert.Contains("Missing", locals["broken"].Value);

        // the attribute wins over ToString(), and expressions see the display too
        Assert.Equal("Order 7: Ann x2", client.Evaluate("order", top.Id).Result);
    }

    [Fact]
    public void ToStringIsUsedWhenOverridden()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("display", "display");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Assert.Equal("{24.5 C}", locals["temperature"].Value);
        Assert.Equal("{TestApp.Plain}", locals["plain"].Value);
        Assert.Equal("{Rec { A = 1, B = x }}", locals["rec"].Value);
        Assert.Equal("{TestApp.Throwing}", locals["throwing"].Value); // a throwing ToString() falls back to the type
        Assert.Equal("{System.ArgumentException: bad argument}", locals["failure"].Value);
        Assert.Equal("{TestApp.Slow}", locals["slow"].Value);

        // still expandable
        Assert.Equal("24.5", client.Variables(locals["temperature"].VariablesReference)["Degrees"].Value);
    }

    [Fact]
    public void DebuggerBrowsableAndCompilerGeneratedMembers()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("display", "display");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Dictionary<string, Variable> order = client.Variables(locals["order"].VariablesReference);
        Assert.Equal("7", order["Id"].Value);
        Assert.DoesNotContain("Secret", order.Keys);
        Assert.DoesNotContain("SecretProperty", order.Keys);
        // RootHidden: the elements appear in place of the member
        Assert.DoesNotContain("Codes", order.Keys);
        Assert.Equal("7", order["[0]"].Value);
        Assert.Equal("8", order["[1]"].Value);

        Dictionary<string, Variable> rec = client.Variables(locals["rec"].VariablesReference);
        Assert.Equal("1", rec["A"].Value);
        Assert.DoesNotContain("EqualityContract", rec.Keys);

        // hidden members stay reachable for expressions
        Assert.Equal("\"hidden\"", client.Evaluate("order.Secret", top.Id).Result);
    }

    // ---------------------------------------------------------------- 1.4 - 1.5 visualizers

    [Fact]
    public void WellKnownValueTypes()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("display", "display");
        Dictionary<string, Variable> locals = client.Locals(top.Id);

        Assert.Equal("{2024-05-06 07:08:09 UTC}", locals["utc"].Value);
        Assert.Equal("{2024-05-06 07:08:09.123}", locals["withMillis"].Value);
        Assert.Equal("{2024-05-06 00:00:00}", locals["dateOnlyTime"].Value);
        Assert.Equal("{01:30:30}", locals["span"].Value);
        Assert.Equal("{0f8fad5b-d9cb-469f-a165-70867728950e}", locals["guid"].Value);
        Assert.Equal("{2024-05-06 07:08:09 +02:00}", locals["offset"].Value);
        Assert.Equal("[\"one\", 1]", locals["pair"].Value);
        Assert.Equal("(1, \"x\")", locals["tuple"].Value);
        Assert.Equal("(2, \"y\")", locals["refTuple"].Value);

        Dictionary<string, Variable> pair = client.Variables(locals["pair"].VariablesReference);
        Assert.Equal("\"one\"", pair["Key"].Value);
        Assert.Equal("1", pair["Value"].Value);
        Assert.Equal("1", client.Variables(locals["tuple"].VariablesReference)["Item1"].Value);
    }

    [Theory]
    [InlineData("set", new[] { "1", "3" })]
    [InlineData("queue", new[] { "2", "3" })]
    [InlineData("stack", new[] { "\"top\"", "\"bottom\"" })]
    [InlineData("immutable", new[] { "4", "5" })]
    [InlineData("readOnly", new[] { "6", "7" })]
    [InlineData("segment", new[] { "2", "3" })]
    [InlineData("emptySet", new string[0])]
    public void CollectionVisualizers(string variable, string[] expectedItems)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("display", "display");
        Variable collection = client.Locals(top.Id)[variable];

        Assert.Equal("Count = " + expectedItems.Length, collection.Value);
        Dictionary<string, Variable> children = client.Variables(collection.VariablesReference);
        for (int i = 0; i < expectedItems.Length; i++)
            Assert.Equal(expectedItems[i], children[$"[{i}]"].Value);
        Assert.Contains("Raw View", children.Keys);
        Assert.Equal(expectedItems.Length + 1, children.Count);
    }

    [Fact]
    public void SortedListShowsKeysAndValues()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("display", "display");
        Variable sorted = client.Locals(top.Id)["sorted"];
        Assert.Equal("Count = 2", sorted.Value);
        Dictionary<string, Variable> children = client.Variables(sorted.VariablesReference);
        Assert.Equal("1", children["[\"a\"]"].Value);
        Assert.Equal("2", children["[\"b\"]"].Value);
    }

    // ---------------------------------------------------------------- 1.6 format specifiers

    [Theory]
    [InlineData("number,h", "0x0000002A")]
    [InlineData("number, h", "0x0000002A")]
    [InlineData("-number,h", "0xFFFFFFD6")]
    [InlineData("(byte)number,h", "0x2A")]
    [InlineData("(long)number,h", "0x000000000000002A")]
    [InlineData("number + 1,d", "43")]
    [InlineData("text,nq", "hello \"world\"")]
    [InlineData("person.Name,nq", "Ann")]
    [InlineData("person,raw", "{TestApp.Person}")]
    [InlineData("list,raw", "{System.Collections.Generic.List<string>}")]
    [InlineData("System.Math.Max(3, number),h", "0x0000002A")]
    [InlineData("grid[1, 0]", "3")]
    [InlineData("letter,h", "0x0078 'x'")]
    public void FormatSpecifiers(string expression, string expected)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("basic", "locals");
        Assert.Equal(expected, client.Evaluate(expression, top.Id).Result);
    }

    [Fact]
    public void HexFormatAppliesToChildrenAndToTheVariablesRequest()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("basic", "locals");

        EvaluateResponseBody numbers = client.Evaluate("numbers,h", top.Id);
        Assert.Equal("0x00000014", client.Variables(numbers.VariablesReference)["[1]"].Value);

        int scope = client.Request<ScopesResponseBody>("scopes", new { frameId = top.Id }).Scopes[0].VariablesReference;
        Dictionary<string, Variable> hexLocals = client.Variables(scope, hex: true);
        Assert.Equal("0x0000002A", hexLocals["number"].Value);
        Assert.Equal("1.5", hexLocals["ratio"].Value);
        Assert.Equal("42", client.Variables(scope)["number"].Value);

        var hexEvaluate = client.Request<EvaluateResponseBody>("evaluate", new { expression = "number", frameId = top.Id, context = "watch", format = new { hex = true } });
        Assert.Equal("0x0000002A", hexEvaluate.Result);
        Assert.Contains("unknownSpecifier", client.EvaluateError("number,unknownSpecifier", top.Id));
    }

    // ---------------------------------------------------------------- 1.7 / 1.15 pseudo variables

    [Fact]
    public void ExceptionPseudoVariableInLocals()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("exception");
        client.Request("setExceptionBreakpoints", new { filters = new[] { "all" } });
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("exception");
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("{System.InvalidOperationException: caught one}", locals["$exception"].Value);
        Assert.Equal("\"caught one\"", client.Variables(locals["$exception"].VariablesReference)["Message"].Value);
    }

    [Fact]
    public void ReturnValueAfterStepOut()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("basic", "add");
        (threadId, top) = client.StepAndWait("stepOut", threadId);

        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("52", locals["TestApp.Program.Add() returned"].Value);
        Assert.Equal("52", client.Evaluate("$ReturnValue", top.Id).Result);
        Assert.Equal("53", client.Evaluate("$ReturnValue + 1", top.Id).Result);

        // only reported right after the return
        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.DoesNotContain(client.Locals(top.Id).Keys, k => k.EndsWith(" returned", StringComparison.Ordinal));
        Assert.Contains("$ReturnValue", client.EvaluateError("$ReturnValue", top.Id));
    }

    [Fact]
    public void ReturnValueOfReferenceTypeAfterSteppingOverTheLastLine()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepFilters", "makeText");
        (threadId, top) = client.StepAndWait("next", threadId); // closing brace
        if (top.Name == "TestApp.StepFilters.MakeText()")
            (threadId, top) = client.StepAndWait("next", threadId); // back in the caller
        Assert.Equal("TestApp.StepFilters.Run()", top.Name);
        Assert.Equal("\"xxx\"", client.Locals(top.Id)["TestApp.StepFilters.MakeText() returned"].Value);
    }

    // ---------------------------------------------------------------- 1.8 user-unhandled exceptions

    [Fact]
    public void UserUnhandledExceptionFilter()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("userUnhandled");
        client.Request("setExceptionBreakpoints", new { filters = new[] { "user-unhandled" } });
        client.Request("configurationDone");

        // "handled by user" is caught in user code: no stop. The exception of the task body is caught by the framework.
        StoppedEventBody stop = client.WaitForStop("exception");
        Assert.Equal("escapes to framework", stop.Text);
        Assert.Contains("user-unhandled", stop.Description);
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.Equal(TestPaths.LineOf("userUnhandledThrow"), top.Line);

        var info = client.Request<ExceptionInfoResponseBody>("exceptionInfo", new { threadId = stop.ThreadId });
        Assert.Equal("System.FormatException", info.ExceptionId);
        Assert.Equal("userUnhandled", info.BreakMode);

        client.Request("continue", new { threadId = stop.ThreadId });
        client.WaitForEvent("exited");
        Assert.False(client.HasPendingEvent("stopped"));
        Assert.Contains("observed", client.Output("stdout"));
    }

    // ---------------------------------------------------------------- 1.9 evaluation budget

    [Fact]
    public void SlowPropertiesBecomeLazyOnceTheBudgetIsSpent()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("display", "display");
        Variable slow = client.Locals(top.Id)["slow"];

        var watch = Stopwatch.StartNew();
        Dictionary<string, Variable> children = client.Variables(slow.VariablesReference);
        watch.Stop();

        Assert.Equal("1", children["Fast"].Value);
        Assert.Equal("2", children["SlowOne"].Value); // started within the budget, so it is allowed to finish
        Assert.True(children["SlowTwo"].PresentationHint?.Lazy, "SlowTwo should be deferred");
        Assert.True(children["After"].PresentationHint?.Lazy, "everything after the budget is deferred");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"listing took {watch.Elapsed}");

        // the client resolves a lazy variable by asking for its children: exactly one, carrying the value
        Variable resolved = Assert.Single(client.Variables(children["SlowTwo"].VariablesReference).Values);
        Assert.Equal("3", resolved.Value);
        Assert.Equal("4", Assert.Single(client.Variables(children["After"].VariablesReference).Values).Value);
    }

    // ---------------------------------------------------------------- 1.10 - 1.11 step filters

    [Fact]
    public void DebuggerHiddenAndStepThroughAreNotSteppedInto()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepFilters", "stepHidden");

        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestApp.StepFilters.Run()", top.Name);
        Assert.Equal(TestPaths.LineOf("stepThrough"), top.Line);

        // stepping "through": user code called from the attributed method is still reachable
        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestApp.StepFilters.Callee()", top.Name);
        StackFrame[] frames = client.StackTrace(threadId);
        Assert.Equal("[External Code]", frames[1].Name);
        Assert.Equal("TestApp.StepFilters.Run()", frames[2].Name);
    }

    [Fact]
    public void PropertiesAndOperatorsAreSteppedOverByDefault()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepFilters", "stepProperty");

        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestApp.StepFilters.Run()", top.Name);
        Assert.Equal(TestPaths.LineOf("stepOperator"), top.Line);

        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestApp.StepFilters.Run()", top.Name);
        Assert.Equal(TestPaths.LineOf("stepEnd"), top.Line);
        Assert.Equal("22", client.Locals(top.Id)["c"].Value);

        // an ordinary method on the same kind of line is still entered
        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestApp.StepFilters.MakeText()", top.Name);
    }

    [Fact]
    public void BreakpointsInsidePropertiesStillWorkAndShowUserFrames()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepFilters", "getter", "operator");
        Assert.Equal("TestApp.Holder.Computed.get()", top.Name);
        Assert.Equal("TestApp.StepFilters.Run()", client.StackTrace(threadId)[1].Name);

        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal("TestApp.Vec.op_Addition()", top.Name);
    }

    [Fact]
    public void StepFilteringCanBeTurnedOff()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = TestPaths.TestAppDll, args = new[] { "stepFilters" }, enableStepFiltering = false });
        client.SetBreakpoints("stepProperty");
        client.Request("configurationDone");
        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));

        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestApp.Holder.Computed.get()", top.Name);
    }

    // ---------------------------------------------------------------- 1.12 - 1.13 protocol

    [Fact]
    public void ModulesAndLoadedSources()
    {
        using var client = new DapClient();
        client.RunTo("basic", "locals");

        var modules = client.Request<ModulesResponseBody>("modules");
        Assert.Equal(modules.Modules.Length, modules.TotalModules);
        Module app = modules.Modules.Single(m => m.Name == "TestApp.dll");
        Assert.Equal(TestPaths.TestAppDll, app.Path, ignoreCase: true);
        Assert.Equal("Symbols loaded.", app.SymbolStatus);
        Assert.Contains(modules.Modules, m => m.Name == "System.Private.CoreLib.dll" && m.SymbolStatus != "Symbols loaded.");

        var paged = client.Request<ModulesResponseBody>("modules", new { startModule = 1, moduleCount = 1 });
        Assert.Equal(modules.Modules[1].Name, Assert.Single(paged.Modules).Name);

        var sources = client.Request<LoadedSourcesResponseBody>("loadedSources");
        Assert.Contains(sources.Sources, s => string.Equals(s.Path, TestPaths.ProgramSource, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sources.Sources, s => s.Name == "Models.cs");
    }

    [Fact]
    public void RunawayEvaluationCanBeCancelled()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("display", "display");

        int evaluate = client.Send("evaluate", new { expression = "Slow.Spin()", frameId = top.Id, context = "watch" });
        System.Threading.Thread.Sleep(500);
        var watch = Stopwatch.StartNew();
        client.Request("cancel", new { requestId = evaluate });
        DapMessage response = client.WaitForResponse(evaluate, "evaluate");
        Assert.False(response.Success);
        Assert.Contains("cancel", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"cancelling took {watch.Elapsed}");

        // the code was really interrupted: same stop, working evaluations (func-evals too), clean continue
        Assert.Equal("TestApp.Display.Run()", client.StackTrace(threadId)[0].Name);
        Assert.Equal("\"24.5 C\"", client.Evaluate("temperature.ToString()", top.Id).Result);
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
    }

    [Fact]
    public void EvaluationBlockedInAWaitIsGivenUpOnWhenCancelled()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("display", "display");

        int evaluate = client.Send("evaluate", new { expression = "Slow.Hang()", frameId = top.Id, context = "watch" });
        System.Threading.Thread.Sleep(500);
        var watch = Stopwatch.StartNew();
        client.Request("cancel", new { requestId = evaluate });
        DapMessage response = client.WaitForResponse(evaluate, "evaluate");
        Assert.False(response.Success);
        Assert.Contains("cancel", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"cancelling took {watch.Elapsed}");

        // A thread sleeping inside the runtime cannot be aborted; the debugger must stay responsive regardless.
        Assert.Contains(client.StackTrace(threadId), f => f.Name == "TestApp.Display.Run()");
        Assert.Equal("2", client.Evaluate("1 + 1", top.Id).Result);
        Assert.Equal("7", client.Evaluate("order.Id", top.Id).Result);
        client.Request("terminate");
        client.WaitForEvent("exited");
    }

    [Fact]
    public void QueuedRequestCanBeCancelledBeforeItRuns()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("display", "display");

        int slow = client.Send("evaluate", new { expression = "slow.SlowOne", frameId = top.Id, context = "watch" });
        int queued = client.Send("evaluate", new { expression = "slow.SlowTwo", frameId = top.Id, context = "watch" });
        client.Request("cancel", new { requestId = queued });

        Assert.Equal("2", client.WaitForResponse(slow).GetBody<EvaluateResponseBody>()!.Result);
        DapMessage cancelled = client.WaitForResponse(queued);
        Assert.False(cancelled.Success);
        Assert.Equal("cancelled", cancelled.Message);
    }

    [Fact]
    public void DisconnectIsNotBlockedByARunningEvaluation()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("display", "display");
        client.Send("evaluate", new { expression = "Slow.Hang()", frameId = top.Id, context = "watch" });
        System.Threading.Thread.Sleep(500);

        var watch = Stopwatch.StartNew();
        client.Send("disconnect", new { terminateDebuggee = true });
        Assert.True(client.AdapterExited(TimeSpan.FromSeconds(8)), "the adapter did not exit");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"disconnect took {watch.Elapsed}");
    }
}
