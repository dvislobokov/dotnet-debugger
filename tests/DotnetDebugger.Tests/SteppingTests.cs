using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

public class SteppingTests
{
    [Fact]
    public void StepInDoesNotEnterPInvokeOrCompiledExpressionTrees()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("advanced", "pinvoke", "expressionTree");

        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestApp.Advanced.Run()", top.Name);
        Assert.Equal(TestPaths.LineOf("afterPinvoke"), top.Line);
        Assert.NotEqual("0", client.Locals(top.Id)["native"].Value);

        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal(TestPaths.LineOf("expressionTree"), top.Line);
        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestApp.Advanced.Run()", top.Name);
        Assert.Equal(TestPaths.LineOf("afterExpressionTree"), top.Line);
        Assert.Equal("6", client.Locals(top.Id)["viaTree"].Value);
    }

    [Fact]
    public void StepInEntersLambdaThroughDelegateAndStepOutReturns()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("advanced", "callLambda");

        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.StartsWith("TestApp.Advanced.Run.AnonymousMethod", top.Name);
        Assert.InRange(top.Line, TestPaths.LineOf("lambdaBody") - 1, TestPaths.LineOf("lambdaBody"));

        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("TestApp.Advanced.Run()", top.Name);
        Assert.Equal(TestPaths.LineOf("callLambda"), top.Line);
    }

    [Fact]
    public void StepOverAwaitStaysInTheAsyncMethod()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("async", "asyncStart");
        Assert.Equal("TestApp.Program.AsyncWork()", top.Name);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("asyncStart") + 1, top.Line);

        // the continuation may resume on another thread
        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal("TestApp.Program.AsyncWork()", top.Name);
        Assert.Equal(TestPaths.LineOf("async"), top.Line);
        Assert.Equal("10", client.Locals(top.Id)["doubled"].Value);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("async") + 1, top.Line);
        Assert.Equal("11", client.Locals(top.Id)["final"].Value);
    }

    [Fact]
    public void StepOverALoopIterationHitsBreakpointsOnTheWay()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("basic", "locals", "add");

        // "next" over a call that contains a breakpoint stops at that breakpoint, not after the call
        client.Request("next", new { threadId });
        (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestApp.Program.Add()", top.Name);

        // and the interrupted step must not fire later
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.False(client.HasPendingEvent("stopped"));
    }

    [Fact]
    public void StepOutOfMainRunsToTheEnd()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("loop", "loopEnd");
        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("TestApp.Program.Main()", top.Name);
        client.Request("stepOut", new { threadId });
        client.WaitForEvent("exited");
    }
}
