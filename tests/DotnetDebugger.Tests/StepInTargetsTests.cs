using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>
/// "Smart step into": stepInTargets lists the calls of the current line, stepIn with a targetId enters the chosen
/// one and no other. Scenarios live in tests/TestApp/StepInto.cs.
/// </summary>
public class StepInTargetsTests
{
    private static StepInTarget[] TargetsOf(DapClient client, StackFrame top) =>
        client.Request<StepInTargetsResponseBody>("stepInTargets", new { frameId = top.Id }).Targets;

    private static (int ThreadId, StackFrame Top) StepInto(DapClient client, int threadId, StepInTarget target)
    {
        client.Request("stepIn", new { threadId, targetId = target.Id });
        return client.Top(client.WaitForStop("step"));
    }

    [Fact]
    public void CapabilityIsAnnounced()
    {
        using var client = new DapClient();
        var capabilities = client.Request<Capabilities>("initialize", new { adapterID = "test" });
        Assert.True(capabilities.SupportsStepInTargetsRequest);
    }

    // ---------------------------------------------------------------- a line with several calls

    [Fact]
    public void TargetsOfALineWithSeveralCallsComeWithLabelsAndColumns()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("stepInto", "stepIntoMulti");
        StepInTarget[] targets = TargetsOf(client, top);

        // in the order the calls are made: Total, Total (the argument of Tax), Tax
        Assert.Equal(["Pricing.Total(lines, discount)", "Pricing.Total(lines, discount)", "StepInto.Tax(value)"], targets.Select(t => t.Label));
        int line = TestPaths.LineOf("stepIntoMulti");
        string text = File.ReadAllLines(TestPaths.Find("stepIntoMulti").Path)[line - 1];
        int firstTotal = text.IndexOf("Total(", StringComparison.Ordinal) + 1;
        int secondTotal = text.IndexOf("Total(", firstTotal, StringComparison.Ordinal) + 1;
        Assert.Equal((line, firstTotal, line, firstTotal + "Total".Length), (targets[0].Line, targets[0].Column, targets[0].EndLine, targets[0].EndColumn));
        Assert.Equal((line, secondTotal, line, secondTotal + "Total".Length), (targets[1].Line, targets[1].Column, targets[1].EndLine, targets[1].EndColumn));
        Assert.Equal(TestPaths.ColumnOf("stepIntoMulti", "Tax("), targets[2].Column);
        Assert.True(targets.Select(t => t.Id).Distinct().Count() == 3);
    }

    [Theory]
    [InlineData(0, "TestApp.Pricing.Total()", "stepIntoTotal", "0.1")]
    [InlineData(1, "TestApp.Pricing.Total()", "stepIntoTotal", "0")]
    [InlineData(2, "TestApp.StepInto.Tax()", "stepIntoTax", null)]
    public void StepsIntoTheChosenCallAndSkipsTheOthers(int index, string frameName, string marker, string? discount)
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepInto", "stepIntoMulti");
        StepInTarget[] targets = TargetsOf(client, top);

        (_, top) = StepInto(client, threadId, targets[index]);
        Assert.Equal(frameName, top.Name);
        int firstLine = TestPaths.LineOf(marker);
        Assert.InRange(top.Line, firstLine - 1, firstLine); // the method's "{" or its first statement
        if (discount != null)
            Assert.Equal(discount, client.Locals(top.Id)["discount"].Value);
    }

    // ---------------------------------------------------------------- A(B())

    [Fact]
    public void NestedCallsAreListedInnerFirstAndTheOuterOneCanBeEntered()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepInto", "stepIntoNested");
        StepInTarget[] targets = TargetsOf(client, top);
        Assert.Equal(["StepInto.Inner(value)", "StepInto.Outer(value)"], targets.Select(t => t.Label));
        Assert.True(targets[0].Column < targets[1].Column == false, "Inner is written after Outer");

        (_, top) = StepInto(client, threadId, targets[1]);
        Assert.Equal("TestApp.StepInto.Outer()", top.Name);
        Assert.Equal("6", client.Locals(top.Id)["value"].Value); // Inner ran without a stop
    }

    [Fact]
    public void TheInnerCallCanBeEnteredToo()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepInto", "stepIntoNested");
        (_, top) = StepInto(client, threadId, TargetsOf(client, top)[0]);
        Assert.Equal("TestApp.StepInto.Inner()", top.Name);
        Assert.Equal("3", client.Locals(top.Id)["value"].Value);
    }

    // ---------------------------------------------------------------- property getters

    [Fact]
    public void PropertyGettersAreTargetsEvenThoughAPlainStepInSkipsThem()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepInto", "stepIntoProperty");
        StepInTarget[] targets = TargetsOf(client, top);
        Assert.Equal(["Pricing.Count.get", "List.Count.get"], targets.Select(t => t.Label));
        Assert.Equal(TestPaths.ColumnOf("stepIntoProperty", "Count"), targets[0].Column);

        (_, top) = StepInto(client, threadId, targets[0]);
        Assert.Equal("TestApp.Pricing.Count.get()", top.Name);
        Assert.Equal(TestPaths.LineOf("stepIntoCountGetter"), top.Line);
    }

    // ---------------------------------------------------------------- lambdas

    [Fact]
    public void LambdasOfTheLineAreTargets()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepInto", "stepIntoLambda");
        StepInTarget[] targets = TargetsOf(client, top);
        Assert.Equal(["x => x > 1", "Enumerable.Where(source, predicate)", "x => x * factor", "Enumerable.Select(source, selector)", "Enumerable.Count(source)"],
            targets.Select(t => t.Label));
        Assert.Equal(TestPaths.ColumnOf("stepIntoLambda", "x => x * factor"), targets[2].Column);

        (_, top) = StepInto(client, threadId, targets[2]);
        Assert.StartsWith("TestApp.StepInto.Run.AnonymousMethod__", top.Name);
        Assert.Equal(TestPaths.LineOf("stepIntoLambda"), top.Line);
        Assert.Equal("2", client.Locals(top.Id)["x"].Value); // the first element Where let through
        Assert.Equal("2", client.Locals(top.Id)["factor"].Value);
    }

    // ---------------------------------------------------------------- async methods

    [Fact]
    public void AnAsyncMethodIsEnteredAtItsBody()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepInto", "stepIntoAsync");
        StepInTarget[] targets = TargetsOf(client, top);
        Assert.Equal("StepInto.ComputeAsync(value)", targets[0].Label);
        Assert.Contains(targets, t => t.Label.EndsWith("GetResult()", StringComparison.Ordinal));

        (_, top) = StepInto(client, threadId, targets[0]);
        Assert.Equal("TestApp.StepInto.ComputeAsync()", top.Name);
        int firstLine = TestPaths.LineOf("stepIntoAsyncBody");
        Assert.InRange(top.Line, firstLine - 1, firstLine);
    }

    // ---------------------------------------------------------------- constructors, and a step without a target

    [Fact]
    public void ConstructorsAreTargetsAndAStepInWithoutATargetStillEntersTheFirstCall()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepInto", "stepIntoNew", "stepIntoEnd");
        StepInTarget[] targets = TargetsOf(client, top);
        Assert.Equal(["new Pricing()", "StepInto.Tax(value)", "Pricing.Total(lines, discount)"], targets.Select(t => t.Label));

        (_, top) = StepInto(client, threadId, targets[2]);
        Assert.Equal("TestApp.Pricing.Total()", top.Name);
        Assert.Equal("0.1", client.Locals(top.Id)["discount"].Value); // Tax(1m) ran without a stop

        (threadId, top) = client.StepAndWait("stepOut", threadId);
        (_, top) = client.ContinueToStop(threadId);
        Assert.Equal(TestPaths.LineOf("stepIntoEnd"), top.Line);
        Assert.Empty(TargetsOf(client, top).Where(t => t.Label.Contains("Concat")));
    }

    [Fact]
    public void TargetsOfAFrameBelowTheTopAreEmptyAndAStaleTargetIsRefused()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("stepInto", "stepIntoTotal");
        StackFrame[] frames = client.StackTrace(threadId);
        Assert.Equal("TestApp.StepInto.Run()", frames[1].Name);
        Assert.Empty(TargetsOf(client, frames[1]));

        // targets are only good for the stop they were asked at
        StepInTarget[] stale = TargetsOf(client, top);
        Assert.Equal(["Enumerable.Sum(source)"], stale.Select(t => t.Label));
        client.SetBreakpoints("stepIntoNested");
        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal(TestPaths.LineOf("stepIntoNested"), top.Line);
        DapMessage refused = client.RequestRaw("stepIn", new { threadId, targetId = stale[0].Id });
        Assert.False(refused.Success);
        Assert.Contains("target", refused.Message);
    }
}
