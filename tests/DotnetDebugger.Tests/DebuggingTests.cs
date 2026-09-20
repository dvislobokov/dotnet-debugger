using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

public class DebuggingTests
{
    [Fact]
    public void RunsToCompletionAndReportsOutputAndExitCode()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("none");
        client.Request("configurationDone");

        var exited = client.WaitForEvent("exited").GetBody<ExitedEventBody>()!;
        client.WaitForEvent("terminated");

        Assert.Equal(3, exited.ExitCode);
        Assert.Contains("mode: none", client.Output("stdout"));
        Assert.Contains("done", client.Output("stderr"));
    }

    [Fact]
    public void StopsAtEntry()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("none", stopAtEntry: true);
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("entry");
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.Equal("TestApp.Program.Main()", top.Name);
        Assert.Equal(TestPaths.ProgramSource, top.Source!.Path, ignoreCase: true);

        client.Request("continue", new { threadId = stop.ThreadId });
        client.WaitForEvent("exited");
    }

    [Fact]
    public void BreakpointHitShowsStackAndLocals()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("basic");
        Breakpoint[] breakpoints = client.SetBreakpoints("locals", "add");
        client.Request("configurationDone");

        // Set before the module was loaded, so they get verified through events.
        foreach (Breakpoint bp in breakpoints)
        {
            if (!bp.Verified)
                client.WaitForEvent("breakpoint", e => e.GetBody<BreakpointEventBody>()!.Breakpoint is { Verified: true } b && b.Id == bp.Id);
        }

        StoppedEventBody stop = client.WaitForStop("breakpoint");
        Assert.Equal([breakpoints[0].Id!.Value], stop.HitBreakpointIds!);

        StackFrame[] frames = client.StackTrace(stop.ThreadId!.Value);
        Assert.Equal("TestApp.Program.Basic()", frames[0].Name);
        Assert.Equal(TestPaths.LineOf("locals"), frames[0].Line);
        Assert.Equal("TestApp.Program.Main()", frames[1].Name);

        Dictionary<string, Variable> locals = client.Locals(frames[0].Id);
        Assert.Equal("42", locals["number"].Value);
        Assert.Equal("int", locals["number"].Type);
        Assert.Equal("\"hello \\\"world\\\"\"", locals["text"].Value);
        Assert.Equal("1.5", locals["ratio"].Value);
        Assert.Equal("true", locals["flag"].Value);
        Assert.Equal("'x'", locals["letter"].Value);
        Assert.Equal("12.34", locals["money"].Value);
        Assert.Equal("7", locals["maybe"].Value);
        Assert.Equal("null", locals["nothing"].Value);
        Assert.Equal("Green", locals["color"].Value);
        Assert.Equal("99", locals["boxed"].Value);
        Assert.Equal("null", locals["nobody"].Value);
        Assert.Equal("{int[3]}", locals["numbers"].Value);
        Assert.Equal("{Person:Ann}", locals["person"].Value);
        Assert.Equal("Count = 2", locals["list"].Value);

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

        // second breakpoint, inside the callee
        client.Request("continue", new { threadId = stop.ThreadId });
        stop = client.WaitForStop("breakpoint");
        frames = client.StackTrace(stop.ThreadId!.Value);
        Assert.Equal("TestApp.Program.Add()", frames[0].Name);
        locals = client.Locals(frames[0].Id);
        Assert.Equal("42", locals["a"].Value);
        Assert.Equal("10", locals["b"].Value);
    }

    [Fact]
    public void SteppingInOverAndOut()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("basic");
        client.SetBreakpoints("locals");
        client.Request("configurationDone");
        int threadId = client.WaitForStop("breakpoint").ThreadId!.Value;

        client.Request("stepIn", new { threadId });
        client.WaitForStop("step");
        StackFrame top = client.StackTrace(threadId)[0];
        Assert.Equal("TestApp.Program.Add()", top.Name);

        client.Request("next", new { threadId });
        client.WaitForStop("step");
        top = client.StackTrace(threadId)[0];
        Assert.Equal(TestPaths.LineOf("add"), top.Line);

        client.Request("next", new { threadId });
        client.WaitForStop("step");
        top = client.StackTrace(threadId)[0];
        Assert.Equal(TestPaths.LineOf("add") + 1, top.Line);
        Assert.Equal("52", client.Locals(top.Id)["result"].Value);

        client.Request("stepOut", new { threadId });
        client.WaitForStop("step");
        top = client.StackTrace(threadId)[0];
        Assert.Equal("TestApp.Program.Basic()", top.Name);
        Assert.Equal(TestPaths.LineOf("locals"), top.Line);

        // "next" must step over framework calls (Console.WriteLine) without stopping inside them
        client.Request("next", new { threadId });
        client.WaitForStop("step");
        client.Request("next", new { threadId });
        client.WaitForStop("step");
        top = client.StackTrace(threadId)[0];
        Assert.Equal("TestApp.Program.Basic()", top.Name);
        Assert.Equal(TestPaths.LineOf("afterAdd") + 2, top.Line);
        Assert.Equal("52", client.Locals(top.Id)["sum"].Value);
    }

    [Fact]
    public void BreaksOnThrownAndUnhandledExceptions()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("exception");
        client.Request("setExceptionBreakpoints", new { filters = new[] { "all", "unhandled" } });
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("exception");
        var info = client.Request<ExceptionInfoResponseBody>("exceptionInfo", new { threadId = stop.ThreadId });
        Assert.Equal("System.InvalidOperationException", info.ExceptionId);
        Assert.Equal("caught one", info.Description);
        Assert.Equal("always", info.BreakMode);
        Assert.Equal(TestPaths.LineOf("throwCaught"), client.StackTrace(stop.ThreadId!.Value)[0].Line);

        client.Request("continue", new { threadId = stop.ThreadId });
        stop = client.WaitForStop("exception"); // first chance of the second exception
        client.Request("continue", new { threadId = stop.ThreadId });
        stop = client.WaitForStop("exception"); // ... which then turns out to be unhandled
        info = client.Request<ExceptionInfoResponseBody>("exceptionInfo", new { threadId = stop.ThreadId });
        Assert.Equal("System.ArgumentException", info.ExceptionId);
        Assert.Equal("fatal one", info.Description);
        Assert.Equal("unhandled", info.BreakMode);

        client.Request("continue", new { threadId = stop.ThreadId });
        client.WaitForEvent("exited");
        Assert.Contains("handled", client.Output("stdout"));
    }

    [Fact]
    public void AsyncMethodShowsHoistedLocals()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("async");
        client.SetBreakpoints("async");
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("breakpoint");
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.Equal("TestApp.Program.AsyncWork()", top.Name);
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("5", locals["input"].Value);
        Assert.Equal("10", locals["doubled"].Value);
    }

    [Fact]
    public void PauseBreakpointWhileRunningAndTerminate()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("wait");
        client.Request("configurationDone");
        client.WaitForOutput("stdout", "mode: wait");

        client.Request("pause", new { threadId = 0 });
        StoppedEventBody stop = client.WaitForStop("pause");
        // "mode: wait" is printed by Main: on a busy machine the pause can come before Wait() is even called
        for (int attempt = 0; attempt < 20 && client.StackTrace(stop.ThreadId!.Value).All(f => f.Name != "TestApp.Program.Wait()"); attempt++)
        {
            client.Request("continue", new { threadId = stop.ThreadId });
            System.Threading.Thread.Sleep(50);
            client.Request("pause", new { threadId = 0 });
            stop = client.WaitForStop("pause");
        }
        Assert.NotEmpty(client.Request<ThreadsResponseBody>("threads").Threads);
        Assert.Contains(client.StackTrace(stop.ThreadId!.Value), f => f.Name == "TestApp.Program.Wait()");
        client.Request("continue", new { threadId = stop.ThreadId });

        // a breakpoint added while the debuggee is running binds immediately
        Breakpoint bp = Assert.Single(client.SetBreakpoints("loop"));
        Assert.True(bp.Verified);
        stop = client.WaitForStop("breakpoint");
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.True(int.Parse(client.Locals(top.Id)["counter"].Value) >= 0);

        // requests that need a stopped process fail cleanly while running
        client.SetBreakpoints(Array.Empty<string>());
        client.Request("continue", new { threadId = stop.ThreadId });
        Assert.False(client.RequestRaw("stackTrace", new { threadId = stop.ThreadId }).Success);

        client.Request("terminate");
        client.WaitForEvent("exited");
        client.WaitForEvent("terminated");
        // on Unix a shell stands between the adapter and the debuggee: its remarks are not the debuggee's output
        Assert.DoesNotContain("Killed", client.Output("stderr"));
    }

    [Fact]
    public void AttachesToRunningProcessAndDetaches()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add(TestPaths.TestAppDll);
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
                while (!(bp = Assert.Single(client.SetBreakpoints("loop"))).Verified && attempts++ < 100)
                    System.Threading.Thread.Sleep(50);
                Assert.True(bp.Verified);

                StoppedEventBody stop = client.WaitForStop("breakpoint");
                StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
                Assert.Equal("TestApp.Program.Wait()", top.Name);

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
