using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>
/// Windows ("full"/"pdbonly") PDBs, the default of old non-SDK .NET Framework projects (roadmap 3.5): tests/TestAppFxFull
/// is TestAppFx compiled with DebugType=full. Everything that works with the portable PDB of TestAppFx must work the
/// same: breakpoints, source lines, stepping, local names, async methods, symbol stores.
/// </summary>
public class FrameworkPdbTests
{
    private const string ProgramMain = "TestAppFx.Program.Main()";

    private static string FullDirectory =>
        Path.Combine(TestPaths.Root, "tests", "TestAppFxFull", "bin", TestPaths.Configuration, TestPaths.FxTargetFramework);

    private static string FullExe => Path.Combine(FullDirectory, "TestAppFxFull.exe");

    private static void Launch(DapClient client, string mode, string? program = null, object? symbolOptions = null) =>
        client.Request("launch", new { program = program ?? FullExe, args = new[] { mode }, justMyCode = true, symbolOptions });

    private static (int ThreadId, StackFrame Top) RunTo(DapClient client, string mode, params string[] markers)
    {
        client.Initialize();
        Launch(client, mode);
        client.SetBreakpoints(markers);
        client.Request("configurationDone");
        return client.Top(client.WaitForStop("breakpoint"));
    }

    [WindowsFact]
    public void TheDebuggeeReallyHasAWindowsPdb()
    {
        byte[] header = new byte[28];
        using (FileStream pdb = File.OpenRead(Path.ChangeExtension(FullExe, ".pdb")))
            pdb.ReadExactly(header);
        Assert.Equal("Microsoft C/C++ MSF 7.00\r\n\x1A", Encoding.ASCII.GetString(header, 0, 27));

        using var pe = new PEReader(File.OpenRead(FullExe));
        DebugDirectoryEntry codeView = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
        Assert.False(codeView.IsPortableCodeView);
    }

    // ---------------------------------------------------------------- breakpoints, stack, locals

    [WindowsFact]
    public void BreakpointsBindAndHitWithSourceAndLocalNames()
    {
        using var client = new DapClient();
        client.Initialize();
        Launch(client, "basic");
        Breakpoint[] breakpoints = client.SetBreakpoints("fx_locals", "fx_add");
        client.Request("configurationDone");

        foreach (Breakpoint bp in breakpoints)
        {
            if (!bp.Verified)
                client.WaitForEvent("breakpoint", e => e.GetBody<BreakpointEventBody>()!.Breakpoint is { Verified: true } b && b.Id == bp.Id);
        }

        StoppedEventBody stop = client.WaitForStop("breakpoint");
        Assert.Equal([breakpoints[0].Id!.Value], stop.HitBreakpointIds!);

        StackFrame[] frames = client.StackTrace(stop.ThreadId!.Value);
        Assert.Equal("TestAppFx.Program.Basic()", frames[0].Name);
        Assert.Equal(TestPaths.LineOf("fx_locals"), frames[0].Line);
        Assert.Equal(TestPaths.TestAppFxProgramSource, frames[0].Source!.Path, ignoreCase: true);
        Assert.Equal(ProgramMain, frames[1].Name);
        Assert.NotNull(frames[1].Source);

        // names come from the local scopes of the Windows PDB
        Dictionary<string, Variable> locals = client.Locals(frames[0].Id);
        Assert.Equal("42", locals["number"].Value);
        Assert.Equal("int", locals["number"].Type);
        Assert.Equal("\"hello \\\"world\\\"\"", locals["text"].Value);
        Assert.Equal("{int[3]}", locals["numbers"].Value);
        Assert.Equal("{Person:Ann}", locals["person"].Value);
        Assert.Equal("42", client.Evaluate("number", frames[0].Id).Result);

        client.Request("continue", new { threadId = stop.ThreadId });
        (int threadId, StackFrame top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestAppFx.Program.Add()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_add"), top.Line);
        Dictionary<string, Variable> args = client.Locals(top.Id);
        Assert.Equal("42", args["a"].Value);
        Assert.Equal("10", args["b"].Value);

        client.Request("continue", new { threadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
    }

    [WindowsFact]
    public void ModuleReportsLoadedSymbols()
    {
        using var client = new DapClient();
        RunTo(client, "basic", "fx_locals");
        var modules = client.Request<ModulesResponseBody>("modules").Modules;
        Assert.Equal("Symbols loaded.", modules.Single(m => m.Name == "TestAppFxFull.exe").SymbolStatus);
    }

    // ---------------------------------------------------------------- stepping

    [WindowsFact]
    public void SteppingInOverAndOut()
    {
        using var client = new DapClient();
        var (threadId, top) = RunTo(client, "basic", "fx_locals");

        (threadId, top) = client.StepAndWait("stepIn", threadId);
        Assert.Equal("TestAppFx.Program.Add()", top.Name);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_add"), top.Line);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_add") + 1, top.Line);
        Assert.Equal("52", client.Locals(top.Id)["result"].Value);

        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("TestAppFx.Program.Basic()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_locals"), top.Line);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_afterAdd"), top.Line);
        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_state"), top.Line);
        Assert.Equal("52", client.Locals(top.Id)["sum"].Value);
    }

    // ---------------------------------------------------------------- async

    [WindowsFact]
    public void AsyncMethodShowsHoistedLocalsAfterAwait()
    {
        using var client = new DapClient();
        var (_, top) = RunTo(client, "async", "fx_async");
        Assert.Equal("TestAppFx.Program.AsyncWork()", top.Name);
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("5", locals["input"].Value);
        Assert.Equal("10", locals["doubled"].Value);
        Assert.Equal("15", client.Evaluate("doubled + input", top.Id).Result);
    }

    [WindowsFact]
    public void StepOverAwaitStaysInTheAsyncMethod()
    {
        using var client = new DapClient();
        var (threadId, top) = RunTo(client, "async", "fx_asyncStart");
        Assert.Equal("TestAppFx.Program.AsyncWork()", top.Name);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_asyncStart") + 1, top.Line);

        // the async stepping information (yield/resume offsets) comes from the Windows PDB too
        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal("TestAppFx.Program.AsyncWork()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_async"), top.Line);
        Assert.Equal("10", client.Locals(top.Id)["doubled"].Value);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("fx_async") + 1, top.Line);
        Assert.Equal("11", client.Locals(top.Id)["final"].Value);
    }

    [WindowsFact]
    public void StepOutOfAsyncMethodLandsInTheAwaitingCaller()
    {
        using var client = new DapClient();
        var (threadId, top) = RunTo(client, "asyncNested", "fx_innerAfter");
        Assert.Equal("TestAppFx.Collections.Inner()", top.Name);

        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("TestAppFx.Collections.Middle()", top.Name);
        Assert.InRange(top.Line, TestPaths.LineOf("fx_middleAwait"), TestPaths.LineOf("fx_middleAfter"));
    }

    // ---------------------------------------------------------------- symbols found elsewhere

    [WindowsFact]
    public void WindowsPdbFromASymbolStoreDirectory()
    {
        using var deployment = new FullPdbDeployment(FullDirectory);
        using var client = new DapClient();
        client.Initialize();
        Launch(client, "basic", deployment.Exe,
            new { searchPaths = new[] { deployment.StoreDirectory }, cachePath = deployment.CacheDirectory });
        client.SetBreakpoints("fx_add");
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestAppFx.Program.Add()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_add"), top.Line);
        Assert.Equal("42", client.Locals(top.Id)["a"].Value);
        var modules = client.Request<ModulesResponseBody>("modules").Modules;
        Assert.Equal("Symbols loaded.", modules.Single(m => m.Name == "TestAppFxFull.exe").SymbolStatus);
    }

    [WindowsFact]
    public void CorruptWindowsPdbMeansNoSymbolsRatherThanACrash()
    {
        using var deployment = new FullPdbDeployment(FullDirectory, corrupt: true);
        using var client = new DapClient();
        client.Initialize();
        Launch(client, "basic", deployment.Exe);
        Breakpoint unbound = Assert.Single(client.SetBreakpoints("fx_add"));
        client.Request("configurationDone");

        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        Assert.False(unbound.Verified);
        Assert.False(client.HasPendingEvent("stopped"));
    }

    // ---------------------------------------------------------------- .NET (CoreCLR)

    [WindowsFact]
    public void CoreClrAppWithAWindowsPdb()
    {
        string dll = Path.Combine(TestPaths.Root, "tests", "AsyncMainAppFull", "bin", TestPaths.Configuration, TestPaths.TargetFramework, "AsyncMainAppFull.dll");
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = dll, stopAtEntry = true });
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("entry"));
        (string path, int line) = TestPaths.Find("asyncMainEntry");
        Assert.Equal(path, top.Source?.Path, ignoreCase: true);
        Assert.Equal(line, top.Line);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(line + 1, top.Line);
        (threadId, top) = client.StepAndWait("next", threadId); // over the await
        Assert.Equal(line + 2, top.Line);

        client.Request("continue", new { threadId });
        Assert.Equal(0, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
    }
}

/// <summary>
/// A copy of TestAppFxFull whose Windows PDB is either moved into a symbol store
/// (&lt;store&gt;/TestAppFxFull.pdb/&lt;GUID&gt;&lt;age in hex&gt;/TestAppFxFull.pdb) or replaced by garbage that only
/// looks like a Windows PDB.
/// </summary>
internal sealed class FullPdbDeployment : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotnet-debugger-fullpdb-" + Guid.NewGuid().ToString("N"));

    public string Exe => Path.Combine(_root, "app", "TestAppFxFull.exe");
    public string StoreDirectory => Path.Combine(_root, "store");
    public string CacheDirectory => Path.Combine(_root, "cache");

    public FullPdbDeployment(string source, bool corrupt = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, "app"));
        Directory.CreateDirectory(CacheDirectory);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(_root, "app", Path.GetFileName(file)));

        string pdb = Path.ChangeExtension(Exe, ".pdb");
        if (corrupt)
        {
            byte[] garbage = new byte[4096];
            new Random(42).NextBytes(garbage);
            byte[] signature = Encoding.ASCII.GetBytes("Microsoft C/C++ MSF 7.00\r\n\x1A" + "DS\0\0\0");
            signature.CopyTo(garbage, 0);
            File.WriteAllBytes(pdb, garbage);
            return;
        }

        using var pe = new PEReader(File.OpenRead(Exe));
        DebugDirectoryEntry entry = pe.ReadDebugDirectory().First(e => e.Type == DebugDirectoryEntryType.CodeView);
        CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(entry);
        string key = codeView.Guid.ToString("N").ToUpperInvariant() + codeView.Age.ToString("X");
        string target = Path.Combine(StoreDirectory, "TestAppFxFull.pdb", key);
        Directory.CreateDirectory(target);
        File.Move(pdb, Path.Combine(target, "TestAppFxFull.pdb"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
