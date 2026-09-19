using System.Diagnostics;
using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

public class SessionTests
{
    [Fact]
    public void InitializeAdvertisesCapabilities()
    {
        using var client = new DapClient();
        var capabilities = client.Request<Capabilities>("initialize", new { adapterID = "dotnet-debugger" });
        Assert.True(capabilities.SupportsConfigurationDoneRequest);
        Assert.True(capabilities.SupportsConditionalBreakpoints);
        Assert.True(capabilities.SupportsHitConditionalBreakpoints);
        Assert.True(capabilities.SupportsLogPoints);
        Assert.True(capabilities.SupportsFunctionBreakpoints);
        Assert.True(capabilities.SupportsSetVariable);
        Assert.True(capabilities.SupportsEvaluateForHovers);
        Assert.True(capabilities.SupportsExceptionInfoRequest);
        Assert.True(capabilities.SupportsTerminateRequest);
        Assert.Equal(["all", "user-unhandled", "unhandled"], capabilities.ExceptionBreakpointFilters!.Select(f => f.Filter));
        Assert.True(capabilities.SupportsModulesRequest);
        Assert.True(capabilities.SupportsLoadedSourcesRequest);
        Assert.True(capabilities.SupportsCancelRequest);
        Assert.True(capabilities.SupportsValueFormattingOptions);
    }

    [Fact]
    public void LaunchOfMissingProgramFails()
    {
        using var client = new DapClient();
        client.Initialize();
        DapMessage response = client.RequestRaw("launch", new { program = Path.Combine(TestPaths.TestAppDirectory, "nope.dll") });
        Assert.False(response.Success);
        Assert.Contains("nope.dll", response.Message);
    }

    [Fact]
    public void UnknownRequestFailsGracefully()
    {
        using var client = new DapClient();
        client.Initialize();
        Assert.False(client.RequestRaw("definitelyNotARequest").Success);
        Assert.False(client.RequestRaw("stackTrace", new { threadId = 1 }).Success); // nothing launched yet
        client.Launch("none");
        client.Request("configurationDone");
        client.WaitForEvent("exited");
    }

    [Fact]
    public void LaunchPassesArgumentsEnvironmentAndWorkingDirectory()
    {
        using var client = new DapClient();
        client.Initialize();
        string cwd = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        client.Request("launch", new
        {
            program = TestPaths.TestAppDll,
            args = new[] { "env", "with space and \"quotes\"" },
            cwd,
            env = new Dictionary<string, string?> { ["DBG_TEST"] = "from-launch" },
        });
        client.Request("configurationDone");

        client.WaitForEvent("exited");
        string stdout = client.Output("stdout");
        Assert.Contains("arg: with space and \"quotes\";", stdout);
        Assert.Contains("env: from-launch;", stdout);
        Assert.Contains("cwd: " + cwd, stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessEventReportsThePid()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("wait");
        client.Request("configurationDone");

        var process = client.WaitForEvent("process").GetBody<ProcessEventBody>()!;
        Assert.Equal("launch", process.StartMethod);
        using Process debuggee = Process.GetProcessById(process.SystemProcessId!.Value);
        Assert.False(debuggee.HasExited);

        // disconnecting from a launched process kills it, even while it is running
        client.Request("disconnect");
        Assert.True(debuggee.WaitForExit(10000));
    }

    [Fact]
    public void TerminateWhileStoppedAtBreakpoint()
    {
        using var client = new DapClient();
        client.RunTo("wait", "loop");
        client.Request("terminate");
        client.WaitForEvent("exited");
        client.WaitForEvent("terminated");
    }

    [Fact]
    public void DebuggerBreakAndDebugOutput()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("break");
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("pause");
        StackFrame[] frames = client.StackTrace(stop.ThreadId!.Value);
        StackFrame user = frames.First(f => f.Source != null);
        Assert.Equal("TestApp.Program.Break()", user.Name);
        Assert.InRange(user.Line, TestPaths.LineOf("afterBreak") - 1, TestPaths.LineOf("afterBreak"));
        client.WaitForOutput("console", "dbg message");

        client.Request("continue", new { threadId = stop.ThreadId });
        client.WaitForEvent("exited");
        Assert.Contains("after break", client.Output("stdout"));
    }

    [Fact]
    public void ContinuedAndThreadEventsAreConsistent()
    {
        using var client = new DapClient();
        var (threadId, _) = client.RunTo("threads", "worker");
        client.WaitForEvent("thread", e => e.GetBody<ThreadEventBody>() is { Reason: "started" } t && t.ThreadId == threadId);
        client.Request("continue", new { threadId });
        client.WaitForEvent("thread", e => e.GetBody<ThreadEventBody>() is { Reason: "exited" } t && t.ThreadId == threadId);
        client.WaitForEvent("exited");
    }
}
