using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

/// <summary>
/// Function breakpoints (setFunctionBreakpoints) on methods of library code: assemblies without symbols, overloads,
/// generic methods, nested types, and assemblies that load after the breakpoint was set. The library is SymbolLib,
/// deployed without its PDB (SymbolDeployment); the user's code is SymbolApp.
/// </summary>
public class FunctionBreakpointTests
{
    // ---------------------------------------------------------------- a module without symbols, Just My Code off

    [Theory]
    [InlineData("SymbolLib.Calculator.Triple", 2, "SymbolLib.Calculator.Triple()")]
    [InlineData("Calculator.Triple", 2, "SymbolLib.Calculator.Triple()")]
    [InlineData("Triple", 2, "SymbolLib.Calculator.Triple()")]
    [InlineData("Calculator.Describe", 1, "SymbolLib.Calculator.Describe<int>()")]
    [InlineData("Calculator.Describe<int>", 1, "SymbolLib.Calculator.Describe<int>()")]
    [InlineData("Calculator.Inner.Run", 1, "SymbolLib.Calculator.Inner.Run()")]
    [InlineData("Inner.Run", 1, "SymbolLib.Calculator.Inner.Run()")]
    [InlineData("Calculator+Inner.Run", 1, "SymbolLib.Calculator.Inner.Run()")]
    public void StopsInALibraryWithoutSymbolsWhenJustMyCodeIsOff(string name, int expectedStops, string frameName)
    {
        using var deployment = new SymbolDeployment();
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = deployment.AppDll, justMyCode = false });
        Breakpoint requested = Assert.Single(client.Request<SetBreakpointsResponseBody>("setFunctionBreakpoints",
            new { breakpoints = new[] { new { name } } }).Breakpoints);
        Assert.False(requested.Verified); // the library is not loaded yet
        client.Request("configurationDone");

        for (int stop = 0; stop < expectedStops; stop++)
        {
            StoppedEventBody stopped = client.WaitForStop("function breakpoint");
            Assert.Equal(requested.Id, Assert.Single(stopped.HitBreakpointIds!));
            var (threadId, top) = client.Top(stopped);
            Assert.Equal(frameName, top.Name);
            Assert.Null(top.Source); // no symbols: the first instruction of the method, no line
            client.Request("continue", new { threadId });
        }
        client.WaitForEvent("exited");
        Assert.False(client.HasPendingEvent("stopped"));

        // once the library was loaded, the breakpoint reported itself bound
        Breakpoint bound = client.WaitForEvent("breakpoint", e => e.GetBody<BreakpointEventBody>()!.Breakpoint.Id == requested.Id).GetBody<BreakpointEventBody>()!.Breakpoint;
        Assert.True(bound.Verified);
    }

    // ---------------------------------------------------------------- a module without symbols, Just My Code on

    [Fact]
    public void RefusesALibraryWithoutSymbolsWhenJustMyCodeIsOn()
    {
        using var deployment = new SymbolDeployment();
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = deployment.AppDll, justMyCode = true });
        client.Request("setFunctionBreakpoints", new { breakpoints = new[] { new { name = "Calculator.Triple" } } });
        client.SetBreakpointsIn(deployment.AppSource, new BreakpointSpec("symbolAppCall"));
        client.Request("configurationDone");

        var (threadId, _) = client.Top(client.WaitForStop("breakpoint")); // the library is loaded by now
        Assert.Contains(client.Request<ModulesResponseBody>("modules").Modules, m => m.Name == "SymbolLib.dll");
        Breakpoint refused = Assert.Single(client.Request<SetBreakpointsResponseBody>("setFunctionBreakpoints",
            new { breakpoints = new[] { new { name = "Calculator.Triple" } } }).Breakpoints);
        Assert.False(refused.Verified);
        Assert.Contains("SymbolLib.dll", refused.Message);
        Assert.Contains("no symbols", refused.Message);
        Assert.Contains("justMyCode", refused.Message);

        client.Request("continue", new { threadId });
        client.WaitForEvent("exited");
        Assert.False(client.HasPendingEvent("stopped"));
    }

    [Fact]
    public void UnknownFunctionSaysSo()
    {
        using var client = new DapClient();
        client.Initialize();
        client.Launch("basic");
        Breakpoint unknown = Assert.Single(client.Request<SetBreakpointsResponseBody>("setFunctionBreakpoints",
            new { breakpoints = new[] { new { name = "Program.NoSuchMethod" } } }).Breakpoints);
        Assert.False(unknown.Verified);
        Assert.Equal("No loaded module contains a function named 'Program.NoSuchMethod'.", unknown.Message);
    }

    // ---------------------------------------------------------------- an assembly loaded later

    [Fact]
    public void BindsWhenTheAssemblyLoadsLater()
    {
        using var deployment = new SymbolDeployment();
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = deployment.AppDll, justMyCode = false });
        client.SetBreakpointsIn(deployment.AppSource, new BreakpointSpec("symbolAppStart"));
        client.Request("configurationDone");

        var (threadId, _) = client.Top(client.WaitForStop("breakpoint"));
        Assert.DoesNotContain(client.Request<ModulesResponseBody>("modules").Modules, m => m.Name == "SymbolLib.dll");
        Breakpoint pending = Assert.Single(client.Request<SetBreakpointsResponseBody>("setFunctionBreakpoints",
            new { breakpoints = new[] { new { name = "Calculator.Inner.Run" } } }).Breakpoints);
        Assert.False(pending.Verified);
        Assert.Contains("No loaded module", pending.Message);

        client.Request("continue", new { threadId });
        StoppedEventBody stop = client.WaitForStop("function breakpoint");
        Assert.Equal("SymbolLib.Calculator.Inner.Run()", client.Top(stop).Top.Name);
        Breakpoint bound = client.WaitForEvent("breakpoint", e => e.GetBody<BreakpointEventBody>()!.Breakpoint.Id == pending.Id).GetBody<BreakpointEventBody>()!.Breakpoint;
        Assert.True(bound.Verified);
    }

    // ---------------------------------------------------------------- the same library with symbols from a store

    [Theory]
    [InlineData("Calculator.Inner.Run", "symbolLibInnerRun")]
    [InlineData("Calculator.Describe<T>", "symbolLibDescribe")]
    [InlineData("SymbolLib.Calculator.Triple", "symbolLibTriple")]
    public void StopsOnTheFirstLineWhenSymbolsAreFound(string name, string marker)
    {
        using var deployment = new SymbolDeployment();
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new
        {
            program = deployment.AppDll,
            symbolOptions = new { searchPaths = new[] { deployment.StoreDirectory }, cachePath = deployment.CacheDirectory },
        });
        client.Request("setFunctionBreakpoints", new { breakpoints = new[] { new { name } } });
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("function breakpoint"));
        int firstStatement = SymbolDeployment.LineOf(deployment.LibSource, marker);
        Assert.InRange(top.Line, firstStatement - 1, firstStatement); // the method's "{" (its first sequence point)
        Assert.EndsWith("Calculator.cs", top.Source!.Path);
    }
}
