using System.Diagnostics;
using System.Text.Json;
using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>
/// 32-bit processes (Windows only). The debugging library has to be of the bitness of the debuggee, so the 64-bit
/// adapter hands such a session over to its 32-bit build (the "x86" folder next to it, published there by
/// DotnetDebugger.Tests.csproj) and relays the protocol. To the client nothing may look different.
/// </summary>
public class FrameworkX86Tests
{
    private const string ProgramMain = "TestAppFx.Program.Main()";

    private static string Fx32Exe => DebuggeePath("TestAppFx32", TestPaths.FxTargetFramework, "TestAppFx32.exe");
    private static string FxAnyCpu32Exe => DebuggeePath("TestAppFxAnyCpu32", TestPaths.FxTargetFramework, "TestAppFxAnyCpu32.exe");
    private static string App32Exe => DebuggeePath("TestApp32", Path.Combine(TestPaths.TargetFramework, "win-x86"), "TestApp32.exe");

    private static string DebuggeePath(string project, string outputDirectory, string file) =>
        Path.Combine(TestPaths.Root, "tests", project, "bin", TestPaths.Configuration, outputDirectory, file);

    private static void Launch(DapClient client, string program, string mode, bool stopAtEntry = false) =>
        client.Request("launch", new { program, args = new[] { mode }, stopAtEntry });

    /// <summary>The platform of the adapter that answers the session's requests.</summary>
    private static string AdapterPlatform(DapClient client) =>
        ((JsonElement)client.Request("dotnet/info").Body!).GetProperty("os").GetString()!;

    // ---------------------------------------------------------------- launch

    [WindowsFact]
    public void X86FrameworkProgramIsDebuggedThroughTheX86Adapter()
    {
        using var client = new DapClient();
        client.Initialize();
        Launch(client, Fx32Exe, "basic");
        Breakpoint bp = Assert.Single(client.SetBreakpoints("fx_locals"));
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("breakpoint");
        Assert.Equal([bp.Id!.Value], stop.HitBreakpointIds!);
        Assert.Equal("win-x86", AdapterPlatform(client));
        Assert.Contains("32-bit", client.Output("console"));

        StackFrame[] frames = client.StackTrace(stop.ThreadId!.Value);
        Assert.Equal("TestAppFx.Program.Basic()", frames[0].Name);
        Assert.Equal(TestPaths.LineOf("fx_locals"), frames[0].Line);
        Assert.Equal(TestPaths.TestAppFxProgramSource, frames[0].Source!.Path, ignoreCase: true);
        Assert.Equal(ProgramMain, frames[1].Name);

        Dictionary<string, Variable> locals = client.Locals(frames[0].Id);
        Assert.Equal("42", locals["number"].Value);
        Assert.Equal("int", locals["number"].Type);
        Assert.Equal("{Person:Ann}", locals["person"].Value);
        Assert.Equal("Count = 2", locals["list"].Value);
        Assert.Equal("12.34", locals["money"].Value);
        Assert.Equal("1234567890123", locals["big"].Value);
        Dictionary<string, Variable> person = client.Variables(locals["person"].VariablesReference);
        Assert.Equal("30", person["Age"].Value);

        // the debuggee really is 32-bit, and evaluation (including calls) works in it
        Assert.Equal("4", client.Evaluate("IntPtr.Size", frames[0].Id).Result);
        Assert.Equal("62", client.Evaluate("number + numbers[1]", frames[0].Id).Result);
        Assert.Equal("\"Bob\"", client.Evaluate("person.Friend.Name", frames[0].Id).Result);
        Assert.Equal("3", client.Evaluate("Add(1, 2)", frames[0].Id).Result);

        int threadId = stop.ThreadId.Value;
        StackFrame top;
        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestAppFx.Program.Add()", top.Name);
        (threadId, top) = client.StepAndWait("next", threadId);
        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal("52", client.Locals(top.Id)["result"].Value);
        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("TestAppFx.Program.Basic()", top.Name);
        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_afterAdd"), top.Line);

        client.Request("continue", new { threadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        client.WaitForEvent("terminated");
        Assert.Contains("mode: basic", client.Output("stdout"));
        Assert.Contains("52", client.Output("stdout"));
        Assert.Contains("done", client.Output("stderr"));
    }

    [WindowsFact]
    public void PreferringThirtyTwoBitAnyCpuProgramIsDebuggedThroughTheX86Adapter()
    {
        using var client = new DapClient();
        client.Initialize();
        Launch(client, FxAnyCpu32Exe, "basic");
        client.SetBreakpoints("fx_locals");
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestAppFx.Program.Basic()", top.Name);
        Assert.Equal("win-x86", AdapterPlatform(client));
        Assert.Equal("4", client.Evaluate("IntPtr.Size", top.Id).Result);
    }

    [WindowsFact]
    public void X64ProgramsStayWithTheAdapterItself()
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx("basic");
        client.SetBreakpoints("fx_locals");
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("win-x64", AdapterPlatform(client));
        Assert.Equal("8", client.Evaluate("IntPtr.Size", top.Id).Result);
        Assert.DoesNotContain("32-bit", client.Output("console"));
    }

    [WindowsFact]
    public void StopsAtEntryAndReportsExitCodeAndOutput()
    {
        using var client = new DapClient();
        client.Initialize();
        Launch(client, Fx32Exe, "output", stopAtEntry: true);
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("entry");
        Assert.Equal(ProgramMain, client.StackTrace(stop.ThreadId!.Value)[0].Name);
        client.Request("continue", new { threadId = stop.ThreadId });

        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        client.WaitForEvent("terminated");
        Assert.Contains("Привет, мир! Grüße €", client.Output("stdout"));
        Assert.Contains("error line: Ошибка", client.Output("stderr"));
    }

    // ---------------------------------------------------------------- exceptions

    [WindowsFact]
    public void ExceptionsStopWithDetails()
    {
        using var client = new DapClient();
        client.Initialize();
        Launch(client, Fx32Exe, "unhandled");
        client.Request("setExceptionBreakpoints", new { filters = new[] { "all" } });
        client.Request("configurationDone");

        StoppedEventBody stop = client.WaitForStop("exception");
        var info = client.Request<ExceptionInfoResponseBody>("exceptionInfo", new { threadId = stop.ThreadId });
        Assert.Equal("System.ArgumentException", info.ExceptionId);
        Assert.Equal("fatal one", info.Description);
        Assert.Equal("always", info.BreakMode);
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.Equal("TestAppFx.Program.Fail()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_throwFatal"), top.Line);
        Assert.Equal("\"inner cause\"", client.Evaluate("inner.Message", top.Id).Result);

        client.Request("continue", new { threadId = stop.ThreadId });
        stop = client.WaitForStop("exception");
        Assert.Equal("unhandled", client.Request<ExceptionInfoResponseBody>("exceptionInfo", new { threadId = stop.ThreadId }).BreakMode);

        client.Request("continue", new { threadId = stop.ThreadId });
        Assert.Equal(unchecked((int)0xE0434352), client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
    }

    // ---------------------------------------------------------------- what the client sent before the hand-over

    /// <summary>Requests answered by the 64-bit adapter before it knew the program are replayed to the 32-bit one.</summary>
    [WindowsFact]
    public void ConfigurationSentBeforeLaunchIsCarriedOver()
    {
        using var client = new DapClient();
        client.Initialize();
        Breakpoint bp = Assert.Single(client.SetBreakpoints("fx_loopBody"));
        client.Request("setExceptionBreakpoints", new { filters = Array.Empty<string>() });
        client.Request("setFunctionBreakpoints", new { breakpoints = new[] { new { name = "TestAppFx.Program.Add" } } });
        client.Request("configurationDone");
        Launch(client, Fx32Exe, "loop");

        StoppedEventBody stop = client.WaitForStop("breakpoint");
        Assert.Equal([bp.Id!.Value], stop.HitBreakpointIds!); // the 32-bit adapter numbered it the same way
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.Equal("TestAppFx.Program.Loop()", top.Name);
        Assert.Equal("0", client.Locals(top.Id)["i"].Value);

        client.SetBreakpointsIn(TestPaths.TestAppFxProgramSource);
        client.Request("continue", new { threadId = stop.ThreadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
    }

    /// <summary>Sequence numbers of the client's view stay unique and increasing across the hand-over.</summary>
    [WindowsFact]
    public void SequenceNumbersContinueAcrossTheHandOver()
    {
        using var client = new DapClient();
        DapMessage initialize = client.Request("initialize", new { clientID = "tests", adapterID = "dotnet-debugger", linesStartAt1 = true, columnsStartAt1 = true });
        DapMessage initialized = client.WaitForEvent("initialized");
        DapMessage launch = client.Request("launch", new { program = Fx32Exe, args = new[] { "none" } });
        DapMessage configurationDone = client.Request("configurationDone");
        DapMessage exited = client.WaitForEvent("exited");
        DapMessage terminated = client.WaitForEvent("terminated");

        int[] seqs = [initialize.Seq, initialized.Seq, launch.Seq, exited.Seq, terminated.Seq];
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(seqs.Length, seqs.Distinct().Count());
        Assert.True(configurationDone.Seq > initialized.Seq);
        Assert.NotEqual(launch.Seq, configurationDone.Seq);
    }

    // ---------------------------------------------------------------- attach, terminate, disconnect

    [WindowsFact]
    public void AttachesToRunningX86ProcessAndDetaches()
    {
        var startInfo = new ProcessStartInfo(Fx32Exe) { UseShellExecute = false, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add("wait");
        using Process debuggee = Process.Start(startInfo)!;
        try
        {
            Assert.Equal("mode: wait", debuggee.StandardOutput.ReadLine());

            using (var client = new DapClient())
            {
                client.Initialize();
                client.Request("attach", new { processId = debuggee.Id });
                client.Request("configurationDone");
                Assert.Equal("win-x86", AdapterPlatform(client));

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
                Assert.Equal("4", client.Evaluate("IntPtr.Size", frames[0].Id).Result);

                client.Request("disconnect", new { terminateDebuggee = false });
                Assert.True(client.AdapterExited(TimeSpan.FromSeconds(15)), "the adapter did not exit");
            }

            Assert.False(debuggee.WaitForExit(500), "Detaching must leave the debuggee running.");
        }
        finally
        {
            debuggee.Kill();
        }
    }

    [WindowsFact]
    public void TerminateAndDisconnectEndTheDebuggeeAndBothAdapters()
    {
        using var client = new DapClient();
        client.Initialize();
        Launch(client, Fx32Exe, "wait");
        client.Request("configurationDone");
        int processId = client.WaitForEvent("process").GetBody<ProcessEventBody>()!.SystemProcessId!.Value;
        client.WaitForOutput("stdout", "mode: wait");

        client.Request("pause", new { threadId = 0 });
        client.WaitForStop("pause");
        client.Request("terminate");
        client.WaitForEvent("exited");
        client.WaitForEvent("terminated");
        client.Send("disconnect", new { terminateDebuggee = true });
        Assert.True(client.AdapterExited(TimeSpan.FromSeconds(15)), "the adapter did not exit");
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId).WaitForExit(5000));
    }

    // ---------------------------------------------------------------- the 32-bit adapter

    [WindowsFact]
    public void MissingX86AdapterIsReportedWithAHint()
    {
        string missing = Path.Combine(Path.GetTempPath(), "no-such-dir", "dotnet-debugger.exe");
        using var client = new DapClient();
        client.Initialize();
        DapMessage response = client.RequestRaw("launch", new { program = Fx32Exe, args = new[] { "none" }, x86Adapter = missing });
        Assert.False(response.Success);
        Assert.Contains("32-bit", response.Message);
        Assert.Contains(missing, response.Message);
        Assert.Contains("x86", response.Message);

        // the session is still the 64-bit adapter's and usable
        Assert.Equal("win-x64", AdapterPlatform(client));
    }

    [WindowsFact]
    public void X86AdapterThatDoesNotSpeakDapIsReported()
    {
        using var client = new DapClient();
        client.Initialize();
        // a program that writes some lines and exits instead of answering "initialize"
        DapMessage response = client.RequestRaw("launch", new { program = Fx32Exe, args = new[] { "none" }, x86Adapter = Fx32Exe });
        Assert.False(response.Success);
        Assert.Contains("did not start", response.Message);
        Assert.Equal("win-x64", AdapterPlatform(client));
    }

    // ---------------------------------------------------------------- .NET (Core) as a 32-bit process

    [WindowsFact]
    public void SelfContainedX86CoreClrAppIsDebuggedThroughTheX86Adapter()
    {
        using var client = new DapClient();
        client.Initialize();
        Launch(client, App32Exe, "basic");
        client.SetBreakpoints("locals");
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestApp.Program.Basic()", top.Name);
        Assert.Equal("win-x86", AdapterPlatform(client));
        Assert.Equal("42", client.Locals(top.Id)["number"].Value);
        Assert.Equal("4", client.Evaluate("IntPtr.Size", top.Id).Result);
        Assert.Equal("3", client.Evaluate("Add(1, 2)", top.Id).Result);

        client.Request("continue", new { threadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        Assert.Contains("mode: basic", client.Output("stdout"));
    }

    /// <summary>runInTerminal is a request of the adapter to the client: it has to pass the relay in both directions.</summary>
    [WindowsFact]
    public void RunInTerminalPassesThroughTheRelay()
    {
        using var client = new DapClient();
        client.Initialize(supportsRunInTerminal: true);
        client.Request("launch", new { program = App32Exe, args = new[] { "stdin" }, console = "integratedTerminal" });
        client.SetBreakpoints("stdinEcho");
        client.Request("configurationDone");

        TerminalSession terminal = client.WaitForTerminal();
        Assert.Equal("integrated", terminal.Request.Kind);
        terminal.WaitForOutput("ready");
        terminal.Process.StandardInput.WriteLine("hello from the terminal");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("\"hello from the terminal\"", client.Locals(top.Id)["line"].Value);
        client.Request("continue", new { threadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        terminal.WaitForOutput("echo: hello from the terminal");
    }

    /// <summary>A 32-bit .NET (Core) process that was started without the debugger.</summary>
    [WindowsFact]
    public void AttachesToRunningX86CoreClrProcess()
    {
        var startInfo = new ProcessStartInfo(App32Exe) { UseShellExecute = false, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add("wait");
        using Process debuggee = Process.Start(startInfo)!;
        try
        {
            Assert.Equal("mode: wait", debuggee.StandardOutput.ReadLine());
            using var client = new DapClient();
            client.Initialize();
            client.Request("attach", new { processId = debuggee.Id });
            client.Request("configurationDone");

            Breakpoint bp;
            int attempts = 0;
            while (!(bp = Assert.Single(client.SetBreakpoints("loop"))).Verified && attempts++ < 100)
                System.Threading.Thread.Sleep(50);
            Assert.True(bp.Verified);
            var (_, top) = client.Top(client.WaitForStop("breakpoint"));
            Assert.Equal("TestApp.Program.Wait()", top.Name);
            Assert.Equal("win-x86", AdapterPlatform(client));
        }
        finally
        {
            debuggee.Kill();
        }
    }
}
