using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>
/// Several AppDomains in one .NET Framework process (ASP.NET on IIS, test runners): tests/TestAppFx in "domains" mode
/// runs the same method in the default domain, in a second domain (through a MarshalByRefObject proxy), and again in
/// the default domain after the second one was unloaded; "domainsShared" loads the second domain's assemblies
/// domain-neutral.
/// </summary>
public class FrameworkDomainsTests
{
    private const string Work = "TestAppFx.DomainWorker.Work()";
    private const string Run = "TestAppFx.Domains.Run()";
    private const string SecondDomain = "SecondDomain";

    [WindowsTheory]
    [InlineData("domains")]
    [InlineData("domainsShared")]
    public void BreakpointHitsInEveryDomainWithUserFramesOnBothSidesOfTheCall(string mode)
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx(mode);
        client.SetBreakpoints("fx_domainWork", "fx_domainUnloaded");
        client.Request("configurationDone");

        // default domain
        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal(Work, top.Name);
        Assert.Equal("\"TestAppFx.exe\"", client.Evaluate("AppDomain.CurrentDomain.FriendlyName", top.Id).Result);
        Assert.Equal("2", client.Locals(top.Id)["doubled"].Value);

        // second domain: the frames of the caller in the default domain are there too
        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal(Work, top.Name);
        Assert.Equal(TestPaths.LineOf("fx_domainWork"), top.Line);
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("2", locals["input"].Value);
        Assert.Equal("4", locals["doubled"].Value);
        Assert.Equal("\"SecondDomain\"", locals["domainName"].Value);
        StackFrame[] frames = client.StackTrace(threadId);
        StackFrame caller = Assert.Single(frames, f => f.Name == Run);
        Assert.Equal(TestPaths.LineOf("fx_domainCall"), caller.Line);
        Assert.Equal("TestAppFx.Program.Main()", frames[^1].Name);
        Assert.True(Array.IndexOf(frames, caller) > 0);
        Assert.Equal("103", client.Locals(caller.Id)["first"].Value);

        // after the unload, back in the default domain
        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal(Run, top.Name);
        Assert.Equal(TestPaths.LineOf("fx_domainUnloaded"), top.Line);
        Assert.Equal("104", client.Locals(top.Id)["second"].Value);

        // the same method once more in the default domain: its breakpoint survived the unload of the other domain
        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal(Work, top.Name);
        Assert.Equal("6", client.Locals(top.Id)["doubled"].Value);
        Assert.Equal("201", client.Evaluate("Counter", top.Id).Result);
        Assert.Equal(TestPaths.LineOf("fx_domainAgain"), client.StackTrace(threadId)[1].Line);

        client.Request("continue", new { threadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        Assert.False(client.HasPendingEvent("stopped"));
        Assert.Contains("unloaded: 103,104", client.Output("stdout"));
        Assert.Contains("third: 207", client.Output("stdout"));
    }

    [WindowsTheory]
    [InlineData("domains")]
    [InlineData("domainsShared")]
    public void StaticsAndNewResolveInTheDomainOfTheFrame(string mode)
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx(mode);
        client.SetBreakpoints("fx_domainWork");
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("101", client.Evaluate("Counter", top.Id).Result);
        Assert.Equal("101", client.Evaluate("new DomainProbe().Seen", top.Id).Result);

        // statics are per domain: the second domain has its own counter, and its own copy of every type
        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal("\"SecondDomain\"", client.Locals(top.Id)["domainName"].Value);
        Assert.Equal("100", client.Evaluate("Counter", top.Id).Result);
        Assert.Equal("100", client.Evaluate("DomainWorker.Counter", top.Id).Result);
        Assert.Equal("100", client.Evaluate("TestAppFx.DomainWorker.Counter", top.Id).Result);
        Assert.Equal("100", client.Evaluate("new DomainProbe().Seen", top.Id).Result);
        Assert.Equal("100", client.Evaluate("new TestAppFx.DomainProbe().Seen", top.Id).Result);
        Assert.Equal("\"SecondDomain\"", client.Evaluate("AppDomain.CurrentDomain.FriendlyName", top.Id).Result);
        Assert.Equal("\"ab\"", client.Evaluate("new System.Text.StringBuilder(\"a\").Append(\"b\").ToString()", top.Id).Result);

        // the caller's frame is in the default domain: its statics are the default domain's
        StackFrame caller = client.StackTrace(threadId).Single(f => f.Name == Run);
        Assert.Equal("101", client.Evaluate("DomainWorker.Counter", caller.Id).Result);

        // after the unload only the default domain is left
        (threadId, top) = client.ContinueToStop(threadId);
        Assert.Equal("201", client.Evaluate("Counter", top.Id).Result);
        Assert.Equal("201", client.Evaluate("new DomainProbe().Seen", top.Id).Result);
    }

    [WindowsTheory]
    [InlineData("domains")]
    [InlineData("domainsShared")]
    public void StepInAndOutAcrossTheDomainBoundary(string mode)
    {
        using var client = new DapClient();
        client.Initialize();
        client.LaunchFx(mode);
        client.SetBreakpoints("fx_domainCall");
        client.Request("configurationDone");
        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal(Run, top.Name);

        // through the proxy and the remoting plumbing, straight into the user code in the second domain
        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal(Work, top.Name);
        Assert.Equal("0", client.Evaluate("Counter", top.Id).Result);
        Assert.Equal("\"SecondDomain\"", client.Evaluate("AppDomain.CurrentDomain.FriendlyName", top.Id).Result);

        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal(Run, top.Name);
        Assert.Equal(TestPaths.LineOf("fx_domainCall"), top.Line);
        Assert.Equal("101", client.Evaluate("DomainWorker.Counter", top.Id).Result);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(Run, top.Name);
        Assert.Equal(TestPaths.LineOf("fx_domainCall") + 1, top.Line);
        Assert.Equal("104", client.Locals(top.Id)["second"].Value);
    }

    [WindowsTheory]
    [InlineData("domains")]
    [InlineData("domainsShared")]
    public void ModulesOfTheSecondDomainAreListedSeparatelyAndRemovedOnUnload(string mode)
    {
        string log = Path.Combine(Path.GetTempPath(), $"dotnet-debugger-test-{Guid.NewGuid():N}.log");
        try
        {
            using (var client = new DapClient(logFile: log))
            {
                client.Initialize();
                client.LaunchFx(mode);
                client.SetBreakpoints("fx_domainWork", "fx_domainUnloaded");
                client.Request("configurationDone");

                var (threadId, _) = client.Top(client.WaitForStop("breakpoint"));
                (threadId, _) = client.ContinueToStop(threadId);

                // one entry per module instance, the ones of the second domain named after it
                Module[] modules = client.Request<ModulesResponseBody>("modules").Modules;
                Assert.Equal(modules.Length, modules.Select(m => m.Id).Distinct().Count());
                Module app = Assert.Single(modules, m => m.Name == "TestAppFx.exe");
                Module appThere = Assert.Single(modules, m => m.Name == $"TestAppFx.exe [{SecondDomain}]");
                Assert.Equal(TestPaths.TestAppFxExe, app.Path, ignoreCase: true);
                Assert.Equal(TestPaths.TestAppFxExe, appThere.Path, ignoreCase: true);
                Assert.Equal("Symbols loaded.", appThere.SymbolStatus);
                Assert.Single(modules, m => m.Name == "mscorlib.dll");
                Assert.Single(modules, m => m.Name == $"mscorlib.dll [{SecondDomain}]");

                // unloaded: the second domain's modules are gone, the default domain's (shared mscorlib included) work on
                var (_, top) = client.ContinueToStop(threadId);
                Assert.Equal(TestPaths.LineOf("fx_domainUnloaded"), top.Line);
                modules = client.Request<ModulesResponseBody>("modules").Modules;
                Assert.DoesNotContain(modules, m => m.Name.Contains(SecondDomain));
                Assert.Single(modules, m => m.Name == "TestAppFx.exe");
                Assert.Single(modules, m => m.Name == "mscorlib.dll");
                client.WaitForEvent("module", e => e.GetBody<ModuleEventBody>() is { Reason: "removed" } b && b.Module.Id == appThere.Id);

                Dictionary<string, Variable> locals = client.Locals(top.Id);
                Assert.Equal("Count = 2", locals["results"].Value);
                Dictionary<string, Variable> results = client.Variables(locals["results"].VariablesReference);
                Assert.Equal("\"103\"", results["[0]"].Value);
                Assert.Equal("\"104\"", results["[1]"].Value);
                Assert.Equal("3", client.Evaluate("results[0].Length", top.Id).Result);

                (threadId, top) = client.ContinueToStop(threadId);
                Assert.Equal(Work, top.Name);
                client.Request("continue", new { threadId });
                client.WaitForEvent("exited");
            }

            string[] complaints = File.ReadAllLines(log)
                .Where(l => !l.Contains("\"type\"") && (l.Contains("Error in ") || l.Contains("Exception:") || l.Contains("failed", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            Assert.Empty(complaints);
        }
        finally
        {
            File.Delete(log);
        }
    }
}
