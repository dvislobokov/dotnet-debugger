using System.Diagnostics;
using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

/// <summary>
/// Launching .NET Framework programs (Windows only): in the client's terminal, with NGen images disabled for
/// justMyCode=false, and from projects that `dotnet build` cannot build (non-SDK-style) or that target several frameworks.
/// </summary>
public class FrameworkLaunchTests
{
    private const string ProgramMain = "TestAppFx.Program.Main()";

    // ---------------------------------------------------------------- runInTerminal

    [WindowsTheory]
    [InlineData("integratedTerminal", "integrated")]
    [InlineData("externalTerminal", "external")]
    public void RunInTerminalHitsEarlyBreakpointsAndGivesARealStdin(string console, string expectedKind)
    {
        using var client = new DapClient();
        client.Initialize(supportsRunInTerminal: true);
        client.Request("launch", new
        {
            program = TestPaths.TestAppFxExe,
            args = new[] { "stdin" },
            console,
            env = new Dictionary<string, string?> { ["DBG_TEST"] = "terminal-env" },
        });
        // the first statement of Main: the debugger must be there before any managed code runs
        client.SetBreakpoints("fx_entry", "fx_stdinEcho");
        client.Request("configurationDone");

        TerminalSession terminal = client.WaitForTerminal();
        Assert.Equal(expectedKind, terminal.Request.Kind);
        Assert.Equal("terminal-env", terminal.Request.Env!["DBG_TEST"]);

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal(ProgramMain, top.Name);
        Assert.Equal(TestPaths.LineOf("fx_entry"), top.Line);
        client.Request("continue", new { threadId });

        // the debuggee talks to the "terminal" (here: pipes owned by the test), not to the adapter
        terminal.WaitForOutput("ready");
        terminal.Process.StandardInput.WriteLine("hello from the terminal");

        (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestAppFx.LaunchModes.Echo()", top.Name);
        Assert.Equal("\"hello from the terminal\"", client.Locals(top.Id)["line"].Value);
        Assert.Equal("\"HELLO FROM THE TERMINAL\"", client.Evaluate("line.ToUpper()", top.Id).Result);

        client.Request("continue", new { threadId });
        var exited = client.WaitForEvent("exited").GetBody<ExitedEventBody>()!;
        Assert.Equal(3, exited.ExitCode);
        terminal.WaitForOutput("echo: hello from the terminal");
        terminal.WaitForOutput("env: terminal-env");
        Assert.True(terminal.Process.WaitForExit(10000), "The terminal helper must exit with the debuggee.");
        Assert.Equal(3, terminal.Process.ExitCode);
        Assert.DoesNotContain("echo:", client.Output("stdout"));
    }

    [WindowsFact]
    public void RunInTerminalStopsAtEntry()
    {
        using var client = new DapClient();
        client.Initialize(supportsRunInTerminal: true);
        client.Request("launch", new { program = TestPaths.TestAppFxExe, args = new[] { "none" }, console = "integratedTerminal", stopAtEntry = true });
        client.Request("configurationDone");
        TerminalSession terminal = client.WaitForTerminal();

        StoppedEventBody stop = client.WaitForStop("entry");
        Assert.Equal(ProgramMain, client.StackTrace(stop.ThreadId!.Value)[0].Name);
        client.Request("continue", new { threadId = stop.ThreadId });

        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        terminal.WaitForOutput("mode: none");
    }

    [WindowsFact]
    public void RunInTerminalAttachesTheDebuggeeToTheTerminalsConsole()
    {
        using var client = new DapClient { TerminalHasConsole = true };
        client.Initialize(supportsRunInTerminal: true);
        client.Request("launch", new { program = TestPaths.TestAppFxExe, args = new[] { "terminal" }, console = "integratedTerminal" });
        client.Request("configurationDone");
        TerminalSession terminal = client.WaitForTerminal();

        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        terminal.WaitForOutput("console processes: ");
        // Ctrl+C, the window size, Console.ReadKey: all of it is the console the helper runs in
        string line = terminal.Output.Split('\n').First(l => l.StartsWith("console processes: ", StringComparison.Ordinal));
        int[] processes = line["console processes: ".Length..].Trim().Split(',').Select(int.Parse).ToArray();
        Assert.Contains(terminal.Process.Id, processes);
        Assert.True(terminal.Process.WaitForExit(10000));
    }

    [WindowsFact]
    public void RunInTerminalIsKilledOnDisconnect()
    {
        using var client = new DapClient();
        client.Initialize(supportsRunInTerminal: true);
        client.Request("launch", new { program = TestPaths.TestAppFxExe, args = new[] { "wait" }, console = "integratedTerminal" });
        client.Request("configurationDone");
        TerminalSession terminal = client.WaitForTerminal();
        terminal.WaitForOutput("mode: wait");

        client.Request("disconnect");
        Assert.True(terminal.Process.WaitForExit(10000));
    }

    // ---------------------------------------------------------------- NGen images

    [WindowsFact]
    public void NgenImagesAreDisabledWithoutJustMyCode()
    {
        Assert.Contains("ZapDisable: 1", RunZap(justMyCode: false, env: null));
    }

    [WindowsFact]
    public void NgenImagesStayWithJustMyCode()
    {
        Assert.Contains("ZapDisable: <unset>", RunZap(justMyCode: true, env: null));
    }

    [WindowsFact]
    public void UserProvidedZapDisableWins()
    {
        Assert.Contains("ZapDisable: 0", RunZap(justMyCode: false, env: new Dictionary<string, string?> { ["COMPlus_ZapDisable"] = "0" }));
    }

    private static string RunZap(bool justMyCode, Dictionary<string, string?>? env)
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = TestPaths.TestAppFxExe, args = new[] { "zap" }, justMyCode, env });
        client.Request("configurationDone");
        client.WaitForEvent("exited");
        client.WaitForEvent("terminated");
        return client.Output("stdout");
    }

    // ---------------------------------------------------------------- projects

    private static string LegacyProject => Path.Combine(TestPaths.Root, "tests", "LegacyFxApp", "LegacyFxApp.csproj");
    private static string MultiTargetProject => Path.Combine(TestPaths.Root, "tests", "MultiFxApp", "MultiFxApp.csproj");

    /// <summary>A non-SDK-style project: built with MSBuild.exe of Visual Studio, which `dotnet build` cannot replace.</summary>
    [MSBuildFact]
    public void LegacyProjectIsBuiltWithMSBuildAndDebugged()
    {
        using var client = new DapClient(TimeSpan.FromSeconds(180));
        client.Initialize();
        client.Request("launch", new { project = LegacyProject, build = true, configuration = TestPaths.Configuration });
        SetBreakpointAt(client, Path.Combine(TestPaths.Root, "tests", "LegacyFxApp", "Program.cs"), "legacy_main");
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("LegacyFxApp.Program.Main()", top.Name);
        Assert.Equal("\"legacy\"", client.Locals(top.Id)["kind"].Value);
        client.Request("continue", new { threadId });

        Assert.Equal(7, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        Assert.Contains("legacy app on mscorlib", client.Output("stdout"));
        Assert.Contains("MSBuild.exe", client.Output("console"));
    }

    [WindowsFact]
    public void MultiTargetedProjectRunsTheRequestedFramework()
    {
        using var client = new DapClient(TimeSpan.FromSeconds(180));
        client.Initialize();
        client.Request("launch", new { project = MultiTargetProject, build = true, configuration = TestPaths.Configuration, framework = "net48" });
        SetBreakpointAt(client, Path.Combine(TestPaths.Root, "tests", "MultiFxApp", "Program.cs"), "multi_main");
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("MultiFxApp.Program.Main()", top.Name);
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.Contains("runtime: mscorlib", client.Output("stdout"));
    }

    [WindowsFact]
    public void MultiTargetedProjectDefaultsToItsFirstFrameworkAndSaysSo()
    {
        using var client = new DapClient(TimeSpan.FromSeconds(180));
        client.Initialize();
        client.Request("launch", new { project = MultiTargetProject, build = true, configuration = TestPaths.Configuration });
        client.Request("configurationDone");
        client.WaitForEvent("exited");

        Assert.Contains("runtime: System.Private.CoreLib", client.Output("stdout"));
        string console = client.Output("console");
        Assert.Contains("net8.0;net48", console);
        Assert.Contains("\"framework\"", console);
    }

    [WindowsFact]
    public void FrameworkTheProjectDoesNotTargetIsRejected()
    {
        using var client = new DapClient(TimeSpan.FromSeconds(180));
        client.Initialize();
        DapMessage response = client.RequestRaw("launch", new { project = MultiTargetProject, configuration = TestPaths.Configuration, framework = "net6.0" });
        Assert.False(response.Success);
        Assert.Contains("net6.0", response.Message);
        Assert.Contains("net8.0;net48", response.Message);
    }

    private static void SetBreakpointAt(DapClient client, string path, string marker)
    {
        int line = Array.FindIndex(File.ReadAllLines(path), l => l.TrimEnd().EndsWith("// bp:" + marker, StringComparison.Ordinal)) + 1;
        Assert.True(line > 0, $"Marker '{marker}' not found in {path}");
        client.Request("setBreakpoints", new { source = new { path }, breakpoints = new[] { new { line } } });
    }
}

/// <summary>Windows, and MSBuild.exe of Visual Studio (or its Build Tools) is installed; skipped otherwise.</summary>
public sealed class MSBuildFactAttribute : FactAttribute
{
    public MSBuildFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = ".NET Framework debugging is Windows only.";
        else if (!HasMSBuild())
            Skip = "MSBuild.exe (Visual Studio or its Build Tools) is not installed.";
    }

    private static bool HasMSBuild()
    {
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
            return false;
        var startInfo = new ProcessStartInfo(vswhere) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe" })
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n').Any(l => File.Exists(l.Trim()));
    }
}
