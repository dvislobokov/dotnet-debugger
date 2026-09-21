using System.Diagnostics;
using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>What the DAP client of the JetBrains platform (IntelliJ IDEA 2026.1) ran into. The client cannot be changed.</summary>
public class IntelliJClientTests
{
    private const int UnknownThread = 0x7FFFFF0;

    // ---------------------------------------------------------------- A. variables: filter "named"

    /// <summary>The client pages the elements itself; what it asks for first are the named children only.</summary>
    [Theory]
    [InlineData("million", 1)] // Raw View
    [InlineData("bytes", 0)]
    public void NamedFilterDoesNotMaterializeElements(string name, int expectedNamed)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("huge", "huge");
        Variable huge = client.Locals(top.Id)[name];
        Assert.True(huge.IndexedVariables >= 1_000_000);
        Assert.Equal(expectedNamed, huge.NamedVariables);

        var watch = Stopwatch.StartNew();
        DapMessage response = client.Request("variables", new { variablesReference = huge.VariablesReference, filter = "named" });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
        Variable[] children = response.GetBody<VariablesResponseBody>()!.Variables;
        Assert.Equal(expectedNamed, children.Length);
        Assert.DoesNotContain(children, c => c.Name.StartsWith('['));
        Assert.True(System.Text.Json.JsonSerializer.Serialize(response.Body).Length < 16 * 1024);
    }

    [Fact]
    public void IndexedFilterReturnsThePageAndNothingElse()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("huge", "huge");
        Variable million = client.Locals(top.Id)["million"];

        foreach (int start in new[] { 0, 100, 999_950 })
        {
            Variable[] page = client.Request<VariablesResponseBody>("variables",
                new { variablesReference = million.VariablesReference, filter = "indexed", start, count = 100 }).Variables;
            Assert.Equal(Math.Min(100, 1_000_000 - start), page.Length);
            Assert.Equal($"[{start}]", page[0].Name);
            Assert.Equal(start.ToString(), page[0].Value);
            Assert.All(page, v => Assert.StartsWith("[", v.Name));
        }
    }

    [Fact]
    public void NamedFilterOfAPlainObjectReturnsItsMembers()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("basic", "state");
        Variable person = client.Locals(top.Id)["person"];
        Assert.Null(person.IndexedVariables);

        Variable[] all = client.Request<VariablesResponseBody>("variables", new { variablesReference = person.VariablesReference }).Variables;
        Variable[] named = client.Request<VariablesResponseBody>("variables", new { variablesReference = person.VariablesReference, filter = "named" }).Variables;
        Assert.Equal(all.Select(v => v.Name), named.Select(v => v.Name));
        Assert.Empty(client.Request<VariablesResponseBody>("variables", new { variablesReference = person.VariablesReference, filter = "indexed" }).Variables);
    }

    [Fact]
    public void EvaluateAndScopesReportNamedVariables()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("huge", "huge");

        EvaluateResponseBody million = client.Evaluate("million", top.Id);
        Assert.Equal(1_000_000, million.IndexedVariables);
        Assert.Equal(1, million.NamedVariables);

        Scope scope = Assert.Single(client.Request<ScopesResponseBody>("scopes", new { frameId = top.Id }).Scopes);
        Assert.Equal(client.Variables(scope.VariablesReference).Count, scope.NamedVariables);
    }

    // ---------------------------------------------------------------- B. threads are sorted

    /// <summary>The client looks the stopped thread up with a binary search over the response as it is.</summary>
    [Fact]
    public void ThreadsAreSortedById()
    {
        using var client = new DapClient();
        client.RunTo("workers", "workerCount");
        int[] ids = client.Request<ThreadsResponseBody>("threads").Threads.Select(t => t.Id).ToArray();
        Assert.True(ids.Length > 2, "the scenario is expected to have several threads");
        Assert.Equal(ids.Order(), ids);
    }

    // ---------------------------------------------------------------- C. unknown thread

    [Theory]
    [InlineData("next")]
    [InlineData("stepIn")]
    [InlineData("stepOut")]
    [InlineData("stackTrace")]
    [InlineData("exceptionInfo")]
    [InlineData("goto")]
    [InlineData("dotnet/freezeThread")]
    public void RequestForAnUnknownThreadIsRefusedInPlainWords(string command)
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("loop", "loopEnd");
        int targetId = client.Request<GotoTargetsResponseBody>("gotoTargets",
            new { source = new { path = TestPaths.ProgramSource }, line = TestPaths.LineOf("loopBody") }).Targets[0].Id;

        DapMessage response = client.RequestRaw(command, new { threadId = UnknownThread, targetId });
        Assert.False(response.Success);
        Assert.Equal($"Unknown thread {UnknownThread}.", response.Message);

        // nothing happened: the process is still stopped where it was, and the right thread steps
        System.Threading.Thread.Sleep(200);
        Assert.False(client.HasPendingEvent("stopped"));
        Assert.False(client.HasPendingEvent("continued"));
        Assert.Equal(top.Line, client.StackTrace(threadId)[0].Line);
        client.Request("goto", new { threadId, targetId });
        client.WaitForStop("goto");
        (_, StackFrame after) = client.StepAndWait("next", threadId);
        Assert.Equal("55", client.Locals(after.Id)["total"].Value); // total += i ran once more
    }

    /// <summary>The client sends "continue" (and "pause") with whatever thread it has selected: all threads are meant.</summary>
    [Fact]
    public void ContinueWithAnUnknownThreadContinuesAllThreads()
    {
        using var client = new DapClient();
        client.RunTo("basic", "locals", "state");
        ContinueResponseBody body = client.Request<ContinueResponseBody>("continue", new { threadId = UnknownThread });
        Assert.True(body.AllThreadsContinued);
        client.WaitForStop("breakpoint");
    }

    // ---------------------------------------------------------------- D. the end of the session

    [Theory]
    [InlineData(true, "disconnect")]
    [InlineData(false, "disconnect")]
    [InlineData(true, "terminate")]
    [InlineData(false, "terminate")]
    public void StoppingTheSessionEndsTheDebuggeeWithoutComplaints(bool paused, string how)
    {
        string log = Path.Combine(Path.GetTempPath(), $"dotnet-debugger-test-{Guid.NewGuid():N}.log");
        try
        {
            int processId;
            using (var client = new DapClient(logFile: log))
            {
                client.Initialize();
                client.Launch("wait");
                if (paused)
                    client.SetBreakpoints("loop");
                client.Request("configurationDone");
                processId = client.WaitForEvent("process").GetBody<ProcessEventBody>()!.SystemProcessId!.Value;
                if (paused)
                    client.WaitForStop("breakpoint");
                else
                    client.WaitForOutput("stdout", "mode: wait");

                if (how == "terminate")
                {
                    client.Request("terminate");
                    client.WaitForEvent("terminated");
                }
                client.Send("disconnect", new { terminateDebuggee = true });
                Assert.True(client.AdapterExited(TimeSpan.FromSeconds(15)), "the adapter did not exit");
            }

            Assert.True(ProcessIsGone(processId), $"the debuggee {processId} is still there");
            string[] complaints = File.ReadAllLines(log).Where(l => l.Contains("failed", StringComparison.OrdinalIgnoreCase) && !l.Contains("\"type\"")).ToArray();
            Assert.Empty(complaints);
        }
        finally
        {
            File.Delete(log);
        }
    }

    private static bool ProcessIsGone(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    // ---------------------------------------------------------------- E. protocol details

    /// <summary>A program that was killed did not end with "success".</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExitCodeOfATerminatedDebuggeeIsNotZero(bool paused)
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("wait");
        if (paused)
            client.SetBreakpoints("loop");
        client.Request("configurationDone");
        if (paused)
            client.WaitForStop("breakpoint");
        else
            client.WaitForOutput("stdout", "mode: wait");

        client.Request("terminate");
        Assert.NotEqual(0, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
    }

    /// <summary>Requests that imply "the program runs" are not followed by a "continued" event (the specification asks for that).</summary>
    [Fact]
    public void ContinuedIsNotSentForRequestsThatImplyIt()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("basic", "locals", "state");
        (_, top) = client.StepAndWait("next", threadId);
        client.Evaluate("Add(1, 2)", top.Id); // runs the debuggee and comes back: still stopped
        client.ContinueToStop(threadId);
        Assert.False(client.HasPendingEvent("continued"));
    }
}
