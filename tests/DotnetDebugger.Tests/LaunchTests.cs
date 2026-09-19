using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

public class LaunchTests
{
    private static string TestAppProject => Path.Combine(TestPaths.TestAppDirectory, "TestApp.csproj");

    [Theory]
    [InlineData("integratedTerminal", "integrated")]
    [InlineData("externalTerminal", "external")]
    public void RunInTerminalGivesTheDebuggeeARealStdin(string console, string expectedKind)
    {
        using var client = new DapClient();
        client.Initialize(supportsRunInTerminal: true);
        client.Request("launch", new
        {
            program = TestPaths.TestAppDll,
            args = new[] { "stdin" },
            console,
            env = new Dictionary<string, string?> { ["DBG_TEST"] = "terminal-env" },
        });
        client.SetBreakpoints("stdinEcho");
        client.Request("configurationDone");

        TerminalSession terminal = client.WaitForTerminal();
        Assert.Equal(expectedKind, terminal.Request.Kind);
        Assert.Equal("terminal-env", terminal.Request.Env!["DBG_TEST"]);

        // the debuggee talks to the "terminal" (here: pipes owned by the test), not to the adapter
        terminal.WaitForOutput("ready");
        terminal.Process.StandardInput.WriteLine("hello from the terminal");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestApp.Program.Echo()", top.Name);
        Assert.Equal("\"hello from the terminal\"", client.Locals(top.Id)["line"].Value);
        Assert.Equal("\"HELLO FROM THE TERMINAL\"", client.Evaluate("line.ToUpper()", top.Id).Result);

        client.Request("continue", new { threadId });
        var exited = client.WaitForEvent("exited").GetBody<ExitedEventBody>()!;
        Assert.Equal(3, exited.ExitCode);
        terminal.WaitForOutput("echo: hello from the terminal");
        Assert.True(terminal.Process.WaitForExit(10000), "The terminal helper must exit with the debuggee.");
        Assert.Equal(3, terminal.Process.ExitCode);
        Assert.DoesNotContain("echo:", client.Output("stdout"));
    }

    [Fact]
    public void TerminalLaunchIsKilledOnDisconnect()
    {
        using var client = new DapClient();
        client.Initialize(supportsRunInTerminal: true);
        client.Request("launch", new { program = TestPaths.TestAppDll, args = new[] { "wait" }, console = "integratedTerminal" });
        client.Request("configurationDone");
        TerminalSession terminal = client.WaitForTerminal();
        terminal.WaitForOutput("mode: wait");

        client.Request("disconnect");
        Assert.True(terminal.Process.WaitForExit(10000));
    }

    [Fact]
    public void TerminalIsNotUsedWhenTheClientCannotProvideOne()
    {
        using var client = new DapClient();
        client.Initialize(supportsRunInTerminal: false);
        client.Request("launch", new { program = TestPaths.TestAppDll, args = new[] { "none" }, console = "integratedTerminal" });
        client.Request("configurationDone");

        client.WaitForEvent("exited");
        Assert.Contains("mode: none", client.Output("stdout"));
    }

    [Fact]
    public void ProgramCanBeAnApphost()
    {
        string apphost = Path.ChangeExtension(TestPaths.TestAppDll, OperatingSystem.IsWindows() ? ".exe" : null)!.TrimEnd('.');
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = apphost, args = new[] { "basic" } });
        client.SetBreakpoints("add");
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestApp.Program.Add()", top.Name);
    }

    [Fact]
    public void ProjectIsResolvedToItsOutputAssembly()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { project = TestAppProject, configuration = TestPaths.Configuration, args = new[] { "basic" } });
        client.SetBreakpoints("add");
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestApp.Program.Add()", top.Name);
        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.Contains("mode: basic", client.Output("stdout")); // explicit args win over the default launch profile
    }

    [Fact]
    public void ProjectDirectoryWorksToo()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { project = TestPaths.TestAppDirectory, configuration = TestPaths.Configuration, args = new[] { "none" } });
        client.Request("configurationDone");
        client.WaitForEvent("exited");
        Assert.Contains("mode: none", client.Output("stdout"));
    }

    [Fact]
    public void DefaultLaunchProfileIsApplied()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { project = TestAppProject, configuration = TestPaths.Configuration });
        client.Request("configurationDone");

        client.WaitForEvent("exited");
        string stdout = client.Output("stdout");
        Assert.Contains("arg: from default profile; env: default-env;", stdout); // first profile with commandName=Project
    }

    [Fact]
    public void NamedLaunchProfileAndOverrides()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new
        {
            project = TestAppProject,
            configuration = TestPaths.Configuration,
            launchSettingsProfile = "TestProfile",
            env = new Dictionary<string, string?> { ["DBG_SECOND"] = "from-launch-json" },
        });
        client.Request("configurationDone");

        client.WaitForEvent("exited");
        string stdout = client.Output("stdout");
        Assert.Contains("arg: fromProfile; env: profile-env;", stdout);
        Assert.Contains("cwd: " + Path.Combine(TestPaths.TestAppDirectory, "Properties"), stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("second: from-launch-json; urls: http://localhost:5123", stdout);
    }

    [Fact]
    public void LaunchSettingsCanBeUsedWithProgramAndDisabled()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new
        {
            program = TestPaths.TestAppDll,
            launchSettingsFilePath = Path.Combine(TestPaths.TestAppDirectory, "Properties", "launchSettings.json"),
            launchSettingsProfile = "TestProfile",
            cwd = TestPaths.TestAppDirectory,
        });
        client.Request("configurationDone");
        client.WaitForEvent("exited");
        Assert.Contains("arg: fromProfile; env: profile-env; cwd: " + TestPaths.TestAppDirectory, client.Output("stdout"), StringComparison.OrdinalIgnoreCase);

        using var disabled = new DapClient();
        disabled.Initialize();
        disabled.Request("launch", new { project = TestAppProject, configuration = TestPaths.Configuration, launchSettingsProfile = "", args = new[] { "env", "x" } });
        disabled.Request("configurationDone");
        disabled.WaitForEvent("exited");
        Assert.Contains("arg: x; env: ;", disabled.Output("stdout"));
    }

    [Fact]
    public void UnknownProfileOrProjectFails()
    {
        using var client = new DapClient();
        client.Initialize();
        DapMessage response = client.RequestRaw("launch", new { project = TestAppProject, launchSettingsProfile = "Nope" });
        Assert.False(response.Success);
        Assert.Contains("Nope", response.Message);

        response = client.RequestRaw("launch", new { project = Path.Combine(TestPaths.TestAppDirectory, "Missing.csproj") });
        Assert.False(response.Success);
        Assert.Contains("Missing.csproj", response.Message);

        response = client.RequestRaw("launch", new { });
        Assert.False(response.Success);
    }

    [Fact]
    public void BuildBeforeLaunch()
    {
        string projectDirectory = Path.Combine(TestPaths.Root, "tests", "BuildApp");
        foreach (string dir in new[] { "bin", "obj" })
        {
            string path = Path.Combine(projectDirectory, dir);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        using var client = new DapClient(TimeSpan.FromMinutes(3));
        client.Initialize();
        client.Request("launch", new { project = projectDirectory, build = true, args = new[] { "a", "b" } });
        client.Request("configurationDone");

        client.WaitForEvent("exited");
        Assert.Contains("built app says hi: a,b", client.Output("stdout"));

        // a build failure is reported instead of launching something stale
        using var failing = new DapClient(TimeSpan.FromMinutes(3));
        failing.Initialize();
        DapMessage response = failing.RequestRaw("launch", new { project = Path.Combine(TestPaths.Root, "tests", "BrokenApp"), build = true });
        Assert.False(response.Success);
        Assert.Contains("build", response.Message, StringComparison.OrdinalIgnoreCase);
        failing.WaitForOutput("console", "error CS");
    }
}
