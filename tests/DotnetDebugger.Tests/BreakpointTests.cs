using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

public class BreakpointTests
{
    [Fact]
    public void LineBreakpointOnInlineLambdaBindsToTheOuterStatement()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("advanced", "lambdaInline");
        Assert.Equal("TestApp.Advanced.Run()", top.Name);
    }

    [Fact]
    public void ColumnBreakpointStopsInsideInlineLambda()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("advanced");
        Breakpoint bp = Assert.Single(client.SetBreakpoints(new BreakpointSpec("lambdaInline", ColumnOf: "x * factor")));
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.StartsWith("TestApp.Advanced.Run.AnonymousMethod", top.Name);
        Assert.Equal(TestPaths.LineOf("lambdaInline"), top.Line);
        Assert.Equal(TestPaths.ColumnOf("lambdaInline", "x * factor"), top.Column);
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("5", locals["x"].Value);
        Assert.Equal("3", locals["factor"].Value);
        Assert.Equal(TestPaths.ColumnOf("lambdaInline", "x * factor"), bp.Column ?? TestPaths.ColumnOf("lambdaInline", "x * factor"));
    }

    [Fact]
    public void BreakpointsInLambdasLocalFunctionsIteratorsAndGenerics()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("advanced", "lambdaBody", "linqWhere", "localFunction", "iterator", "expressionBodied");

        Assert.StartsWith("TestApp.Advanced.Run.AnonymousMethod", top.Name);
        Assert.Equal(TestPaths.LineOf("lambdaBody"), top.Line);
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("4", locals["x"].Value);
        Assert.Equal("3", locals["factor"].Value);
        StackFrame[] frames = client.StackTrace(threadId);
        Assert.Equal("TestApp.Advanced.Run()", frames[1].Name);
        Assert.Equal(TestPaths.LineOf("callLambda"), frames[1].Line);

        // the LINQ predicate runs once per element, called from framework code
        for (int n = 1; n <= 4; n++)
        {
            (threadId, top) = client.ContinueToStop(threadId);
            Assert.Equal(TestPaths.LineOf("linqWhere"), top.Line);
            Assert.Equal(n.ToString(), client.Locals(top.Id)["n"].Value);
        }
        frames = client.StackTrace(threadId);
        Assert.Equal("[External Code]", frames[1].Name);
        Assert.Equal("TestApp.Advanced.Run()", frames[2].Name);

        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal("TestApp.Advanced.Run.LocalFunction()", top.Name);
        locals = client.Locals(top.Id);
        Assert.Equal("1", locals["value"].Value);
        Assert.Equal("3", locals["factor"].Value);
        Assert.Equal("2", locals["seed"].Value);

        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal("TestApp.Advanced.Iterate()", top.Name);
        Assert.Equal("0", client.Locals(top.Id)["i"].Value);
        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal("1", client.Locals(top.Id)["i"].Value);
        Assert.Equal("2", client.Locals(top.Id)["count"].Value);

        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal("TestApp.Advanced.Square()", top.Name);
        Assert.Equal("6", client.Locals(top.Id)["value"].Value);
    }

    [Fact]
    public void GenericFrameShowsTypeArguments()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("advanced", "generic");
        Assert.Equal("TestApp.Box<string>.Describe<int>()", top.Name);
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("{TestApp.Box<string>}", locals["this"].Value);
        Assert.Equal("7", locals["extra"].Value);
        Assert.Equal("\"payload\"", client.Variables(locals["this"].VariablesReference)["_value"].Value);
    }

    [Fact]
    public void ConditionalBreakpoint()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("loop");
        client.SetBreakpoints(new BreakpointSpec("loopBody", Condition: "i == 7 && total > 0"));
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("7", client.Locals(top.Id)["i"].Value);

        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
    }

    [Fact]
    public void InvalidConditionStopsAndReportsTheError()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("loop");
        client.SetBreakpoints(new BreakpointSpec("loopBody", Condition: "missingVariable > 1"));
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("0", client.Locals(top.Id)["i"].Value);
        client.WaitForOutput("console", "missingVariable");
    }

    [Theory]
    [InlineData("3", new[] { 2 })]
    [InlineData(">=9", new[] { 8, 9 })]
    [InlineData("%4", new[] { 3, 7 })]
    public void HitCountBreakpoint(string hitCondition, int[] expectedValuesOfI)
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("loop");
        client.SetBreakpoints(new BreakpointSpec("loopBody", HitCondition: hitCondition));
        client.Request("configurationDone");

        int threadId = 0;
        foreach (int expected in expectedValuesOfI)
        {
            (threadId, StackFrame top) = client.Top(client.WaitForStop("breakpoint"));
            Assert.Equal(expected.ToString(), client.Locals(top.Id)["i"].Value);
            client.Request("continue", new { threadId });
        }
        client.WaitForEvent("exited");
    }

    [Fact]
    public void LogpointPrintsInterpolatedMessageWithoutStopping()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("loop");
        client.SetBreakpoints(new BreakpointSpec("loopBody", LogMessage: "i={i} next={i + 1} {{literal}}", Condition: "i >= 8"));
        client.Request("configurationDone");

        client.WaitForEvent("exited");
        string console = client.Output("console");
        Assert.Contains("i=8 next=9 {literal}", console);
        Assert.Contains("i=9 next=10 {literal}", console);
        Assert.DoesNotContain("i=7", console);
        Assert.Contains("total: 45", client.Output("stdout"));
    }

    [Theory]
    [InlineData("TestApp.Program.Add")]
    [InlineData("Program.Add")]
    [InlineData("Add")]
    public void FunctionBreakpoint(string name)
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("basic");
        client.Request("setFunctionBreakpoints", new { breakpoints = new[] { new { name } } });
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("function breakpoint"));
        Assert.Equal("TestApp.Program.Add()", top.Name);
        Assert.Equal("42", client.Locals(top.Id)["a"].Value);
    }

    [Fact]
    public void BreakpointInUnknownFileOrBeyondTheCodeStaysUnverified()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("loop");
        Breakpoint unknownFile = Assert.Single(client.Request<SetBreakpointsResponseBody>("setBreakpoints", new
        {
            source = new { path = Path.Combine(TestPaths.TestAppDirectory, "Missing.cs") },
            breakpoints = new[] { new { line = 3 } },
        }).Breakpoints);
        Breakpoint real = Assert.Single(client.SetBreakpoints("loopEnd"));
        client.Request("configurationDone");

        client.WaitForStop("breakpoint");
        Assert.False(unknownFile.Verified);
        Assert.NotNull(unknownFile.Message);
        Breakpoint beyond = Assert.Single(client.Request<SetBreakpointsResponseBody>("setBreakpoints", new
        {
            source = new { path = TestPaths.ProgramSource },
            breakpoints = new[] { new { line = 100000 } },
        }).Breakpoints);
        Assert.False(beyond.Verified);
        Assert.NotEqual(real.Id, beyond.Id);
    }

    [Fact]
    public void RemovedBreakpointDoesNotHit()
    {
        using var client = new DapClient();
        var (threadId, _) = client.RunTo("loop", "loopBody", "loopEnd");

        Breakpoint remaining = Assert.Single(client.SetBreakpoints("loopEnd"));
        Assert.True(remaining.Verified);
        var (_, top) = client.ContinueToStop(threadId);
        Assert.Equal(TestPaths.LineOf("loopEnd"), top.Line);
        Assert.Equal("45", client.Locals(top.Id)["total"].Value);
    }

    [Fact]
    public void BreakpointMovesToTheNextExecutableLine()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("loop");
        // the line with "{" before the marker has no code of its own... but the one before "for" is blank-free,
        // so use the method's opening brace + declaration: request the line above the first statement.
        int requested = TestPaths.LineOf("loopBody") - 1;
        Breakpoint bp = Assert.Single(client.Request<SetBreakpointsResponseBody>("setBreakpoints", new
        {
            source = new { path = TestPaths.ProgramSource },
            breakpoints = new[] { new { line = requested } },
        }).Breakpoints);
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.True(top.Line >= requested);
        if (!bp.Verified)
            bp = client.WaitForEvent("breakpoint").GetBody<BreakpointEventBody>()!.Breakpoint;
        Assert.Equal(top.Line, bp.Line);
    }
}
