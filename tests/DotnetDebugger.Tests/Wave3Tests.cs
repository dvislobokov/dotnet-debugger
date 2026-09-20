using System.Diagnostics;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>Roadmap wave 3: extension methods (3.1), symbol server (3.3), async call stack (3.4). The runtime matrix (3.9) is below.</summary>
public class Wave3Tests
{
    // ---------------------------------------------------------------- 3.1 extension methods

    [Theory]
    // LINQ over arrays, lists, strings, dictionaries and lazy sequences
    [InlineData("numbers.Count()", "3")]
    [InlineData("numbers.First()", "1")]
    [InlineData("numbers.Last()", "3")]
    [InlineData("numbers.Sum()", "6")]
    [InlineData("numbers.Max()", "3")]
    [InlineData("numbers.Min() + numbers.Max()", "4")]
    [InlineData("numbers.Average()", "2")]
    [InlineData("numbers.Contains(2)", "true")]
    [InlineData("numbers.Contains(number)", "false")]
    [InlineData("numbers.Append(4).Count()", "4")]
    [InlineData("((System.Collections.IList)numbers).Contains(2)", "true")] // explicit implementation, boxed argument
    [InlineData("string.Format(\"{0}-{1}\", number, 7)", "\"42-7\"")]
    [InlineData("object.Equals(number, 42)", "true")]
    [InlineData("numbers.ElementAt(2)", "3")]
    [InlineData("numbers.Skip(1).First()", "2")]
    [InlineData("numbers.Take(2).Sum()", "3")]
    [InlineData("numbers.Reverse().First()", "3")]
    [InlineData("list.Any()", "true")]
    [InlineData("empty.Any()", "false")]
    [InlineData("empty.FirstOrDefault()", "0")]
    [InlineData("list.First()", "\"a\"")]
    [InlineData("list.Distinct().Count()", "2")]
    [InlineData("list.Last()", "\"b\"")]
    [InlineData("text.Count()", "5")]
    [InlineData("text.First()", "'h'")]
    [InlineData("map.Count()", "2")]
    [InlineData("map.First().Value", "1")]
    [InlineData("map.Keys.First()", "\"one\"")]
    [InlineData("lazy.ToList().Count", "2")]
    [InlineData("lazy.ToArray().Length", "2")]
    [InlineData("lazy.First()", "2")]
    [InlineData("System.Linq.Enumerable.Range(1, 4).Sum()", "10")]
    [InlineData("string.Join(\"-\", list.Distinct())", "\"a-b\"")]
    // extension methods of the user
    [InlineData("number.Doubled()", "84")]
    [InlineData("number.Doubled().Doubled()", "168")]
    [InlineData("text.Shout()", "\"HELLO!\"")]
    [InlineData("\"abc\".Shout()", "\"ABC!\"")]
    [InlineData("person.Initials()", "\"E\"")] // declared for the base class
    [InlineData("numbers.Second()", "2")]
    [InlineData("list.Second()", "\"b\"")]
    [InlineData("TextExtensions.Doubled(number)", "84")]
    public void ExtensionMethods(string expression, string expected)
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("wave3", "wave3");
        Assert.Equal(expected, client.Evaluate(expression, top.Id).Result);
    }

    [Fact]
    public void ExtensionMethodErrorsAndContexts()
    {
        using var client = new DapClient();
        var (_, top) = client.RunTo("wave3", "wave3");
        Assert.Contains("NoSuchExtension", client.EvaluateError("numbers.NoSuchExtension()", top.Id));
        Assert.Contains("InvalidOperationException", client.EvaluateError("empty.First()", top.Id)); // thrown by LINQ itself
        Assert.False(client.RequestRaw("evaluate", new { expression = "numbers.Count()", frameId = top.Id, context = "hover" }).Success);

        // usable where expressions are: conditions and log messages
        Assert.Equal("3", client.Evaluate("numbers.Count()", top.Id, "repl").Result);
    }

    // ---------------------------------------------------------------- 3.3 symbol server

    [Fact]
    public void SymbolsFromASymbolStoreDirectory()
    {
        using var deployment = new SymbolDeployment();
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new
        {
            program = deployment.AppDll,
            symbolOptions = new { searchPaths = new[] { deployment.StoreDirectory }, cachePath = deployment.CacheDirectory },
        });
        Breakpoint bp = Assert.Single(client.SetBreakpointsIn(deployment.LibSource, new BreakpointSpec("symbolLibTriple")));
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("SymbolLib.Calculator.Triple()", top.Name);
        Assert.Equal("14", client.Locals(top.Id)["value"].Value);
        var modules = client.Request<ModulesResponseBody>("modules").Modules;
        Assert.Equal("Symbols loaded.", modules.Single(m => m.Name == "SymbolLib.dll").SymbolStatus);

        // code whose symbols come from a directory the user pointed to is the user's code
        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("SymbolApp.Program.Main()", top.Name);
    }

    [Fact]
    public void WithoutSymbolOptionsTheLibraryIsExternalCode()
    {
        using var deployment = new SymbolDeployment();
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new { program = deployment.AppDll });
        Breakpoint unbound = Assert.Single(client.SetBreakpointsIn(deployment.LibSource, new BreakpointSpec("symbolLibTriple")));
        client.SetBreakpointsIn(deployment.AppSource, new BreakpointSpec("symbolAppCall"));
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("SymbolApp.Program.Main()", top.Name);
        (threadId, top) = client.StepAndWait("stepIn", threadId); // nothing to step into
        Assert.Equal("SymbolApp.Program.Main()", top.Name);
        Assert.False(unbound.Verified);
    }

    [Fact]
    public void SymbolsFromAnHttpSymbolServerAreCachedAndLoadableOnDemand()
    {
        using var deployment = new SymbolDeployment();
        using var server = new SymbolHttpServer(deployment.StoreDirectory);
        using var client = new DapClient();
        client.Initialize();
        client.Request("launch", new
        {
            program = deployment.AppDll,
            justMyCode = false,
            symbolOptions = new { searchPaths = new[] { server.Url }, cachePath = deployment.CacheDirectory },
        });
        client.SetBreakpointsIn(deployment.LibSource, new BreakpointSpec("symbolLibTriple"));
        client.Request("configurationDone");

        var (_, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("SymbolLib.Calculator.Triple()", top.Name);
        Assert.Contains(server.Requests, r => r.Contains("SymbolLib.pdb", StringComparison.OrdinalIgnoreCase));
        Assert.Single(Directory.GetFiles(deployment.CacheDirectory, "SymbolLib.pdb", SearchOption.AllDirectories));

        // second session: served from the cache, the server is not asked again for what is cached
        int requestsBefore = server.Requests.Count(r => r.Contains("SymbolLib.pdb", StringComparison.OrdinalIgnoreCase));
        using var second = new DapClient();
        second.Initialize();
        second.Request("launch", new
        {
            program = deployment.AppDll,
            justMyCode = false,
            symbolOptions = new { searchPaths = new[] { server.Url }, cachePath = deployment.CacheDirectory },
        });
        second.SetBreakpointsIn(deployment.LibSource, new BreakpointSpec("symbolLibTriple"));
        second.Request("configurationDone");
        second.WaitForStop("breakpoint");
        Assert.Equal(requestsBefore, server.Requests.Count(r => r.Contains("SymbolLib.pdb", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SymbolsCanBeLoadedOnDemandForOneModule()
    {
        using var deployment = new SymbolDeployment();
        using var server = new SymbolHttpServer(deployment.StoreDirectory);
        using var client = new DapClient();
        client.Initialize();
        // servers are only consulted automatically with justMyCode off; here the user asks for one module
        client.Request("launch", new
        {
            program = deployment.AppDll,
            symbolOptions = new { searchPaths = new[] { server.Url }, cachePath = deployment.CacheDirectory },
        });
        client.SetBreakpointsIn(deployment.AppSource, new BreakpointSpec("symbolAppCall"));
        Breakpoint pending = Assert.Single(client.SetBreakpointsIn(deployment.LibSource, new BreakpointSpec("symbolLibTriple")));
        client.Request("configurationDone");
        var (threadId, _) = client.Top(client.WaitForStop("breakpoint"));
        Assert.False(pending.Verified);
        Assert.DoesNotContain(server.Requests, r => r.Contains("SymbolLib.pdb", StringComparison.OrdinalIgnoreCase));

        Module lib = client.Request<ModulesResponseBody>("modules").Modules.Single(m => m.Name == "SymbolLib.dll");
        var loaded = client.Request<Dictionary<string, System.Text.Json.JsonElement>>("dotnet/loadSymbols", new { moduleId = lib.Id });
        Assert.True(loaded["loaded"].GetBoolean());
        client.WaitForEvent("module", e => e.GetBody<ModuleEventBody>() is { Reason: "changed" } m && m.Module.SymbolStatus == "Symbols loaded.");
        client.WaitForEvent("breakpoint", e => e.GetBody<BreakpointEventBody>()!.Breakpoint is { Verified: true } b && b.Id == pending.Id);

        var (_, top) = client.ContinueToStop(threadId);
        Assert.Equal("SymbolLib.Calculator.Triple()", top.Name);
    }

    // ---------------------------------------------------------------- 3.4 async call stack

    [Fact]
    public void AsyncCallStackShowsTheAwaitingMethods()
    {
        using var client = new DapClient();
        var (threadId, top) = client.RunTo("asyncNested", "innerAfter");
        StackFrame[] frames = client.StackTrace(threadId);

        // physical stack: Inner() resumed by the thread pool; logical stack: awaited by Outer()
        Assert.Equal("TestApp.Wave2.Inner()", frames[0].Name);
        int label = Array.FindIndex(frames, f => f.Name == "[Async Call Stack]");
        Assert.True(label > 0, "no async call stack: " + string.Join(" | ", frames.Select(f => f.Name)));
        Assert.Equal("label", frames[label].PresentationHint);

        StackFrame outer = frames[label + 1];
        Assert.Equal("TestApp.Wave2.Outer()", outer.Name);
        Assert.Equal(TestPaths.LineOf("outerAwait"), outer.Line);
        Assert.Equal(TestPaths.Find("outerAwait").Path, outer.Source!.Path, ignoreCase: true);

        // the awaiting method's variables live in its state machine
        Assert.Equal("0", client.Locals(outer.Id)["result"].Value);

        // paging sees the same frames
        var page = client.Request<StackTraceResponseBody>("stackTrace", new { threadId, startFrame = label + 1, levels = 1 });
        Assert.Equal(frames.Length, page.TotalFrames);
        Assert.Equal("TestApp.Wave2.Outer()", Assert.Single(page.StackFrames).Name);
    }

    [Fact]
    public void NoAsyncCallStackForSynchronousCallers()
    {
        using var client = new DapClient();
        // the first part of an async method runs on the caller's stack: nothing to add
        var (threadId, _) = client.RunTo("asyncParallel", "asyncWorker");
        Assert.DoesNotContain(client.StackTrace(threadId), f => f.Name == "[Async Call Stack]");
    }
}

/// <summary>
/// A copy of SymbolApp where SymbolLib.pdb is not next to SymbolLib.dll but in a symbol store
/// (&lt;store&gt;/SymbolLib.pdb/&lt;guid&gt;FFFFFFFF/SymbolLib.pdb), which is what symbol servers serve.
/// </summary>
internal sealed class SymbolDeployment : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotnet-debugger-symbols-" + Guid.NewGuid().ToString("N"));

    public string AppDll => Path.Combine(_root, "app", "SymbolApp.dll");
    public string StoreDirectory => Path.Combine(_root, "store");
    public string CacheDirectory => Path.Combine(_root, "cache");
    public string LibSource => Path.Combine(TestPaths.Root, "tests", "SymbolLib", "Calculator.cs");
    public string AppSource => Path.Combine(TestPaths.Root, "tests", "SymbolApp", "Program.cs");

    public SymbolDeployment()
    {
        string source = Path.Combine(TestPaths.Root, "tests", "SymbolApp", "bin", TestPaths.Configuration, TestPaths.TargetFramework);
        Directory.CreateDirectory(Path.Combine(_root, "app"));
        Directory.CreateDirectory(CacheDirectory);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(_root, "app", Path.GetFileName(file)));

        string pdb = Path.Combine(_root, "app", "SymbolLib.pdb");
        using (var pe = new PEReader(File.OpenRead(Path.Combine(_root, "app", "SymbolLib.dll"))))
        {
            DebugDirectoryEntry entry = pe.ReadDebugDirectory().First(e => e.Type == DebugDirectoryEntryType.CodeView);
            CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(entry);
            string key = codeView.Guid.ToString("N") + "FFFFFFFF";
            string target = Path.Combine(StoreDirectory, "SymbolLib.pdb", key);
            Directory.CreateDirectory(target);
            File.Move(pdb, Path.Combine(target, "SymbolLib.pdb"));
        }
    }

    public static int LineOf(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.TrimEnd().EndsWith("// bp:" + marker, StringComparison.Ordinal)) + 1;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Serves a symbol store directory over HTTP on a free local port and records what was asked for.</summary>
internal sealed class SymbolHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<string> _requests = [];

    public string Url { get; }

    public List<string> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    public SymbolHttpServer(string directory)
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();

        Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                string relative = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath.TrimStart('/'));
                lock (_requests)
                    _requests.Add(relative);
                // symbol servers are case-insensitive about the key
                string? file = Directory.Exists(directory)
                    ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).FirstOrDefault(f =>
                        Path.GetRelativePath(directory, f).Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (file == null)
                {
                    context.Response.StatusCode = 404;
                }
                else
                {
                    byte[] content = File.ReadAllBytes(file);
                    context.Response.ContentLength64 = content.Length;
                    await context.Response.OutputStream.WriteAsync(content);
                }
                context.Response.Close();
            }
        });
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}

/// <summary>
/// 3.9: the same debuggee published for other runtimes and deployment models. Publishing takes minutes, so the
/// matrix only runs with DOTNET_DEBUGGER_MATRIX=1.
/// </summary>
public class RuntimeMatrixTests : IClassFixture<RuntimeMatrixTests.PublishedApps>
{
    private readonly PublishedApps _apps;

    public RuntimeMatrixTests(PublishedApps apps) => _apps = apps;

    public sealed class PublishedApps
    {
        private readonly Dictionary<string, string> _programs = [];
        private readonly object _sync = new();

        public static bool Enabled => Environment.GetEnvironmentVariable("DOTNET_DEBUGGER_MATRIX") == "1";

        /// <summary>Publishes on first use; returns the program to launch.</summary>
        public string Get(string variant)
        {
            lock (_sync)
            {
                if (_programs.TryGetValue(variant, out string? program))
                    return program;

                string rid = RuntimeInformation.RuntimeIdentifier;
                string output = Path.Combine(TestPaths.Root, "artifacts", "matrix", variant);
                string arguments = variant switch
                {
                    "net9-self-contained" => $"-f net9.0 -r {rid} --self-contained true",
                    "net9-single-file" => $"-f net9.0 -r {rid} --self-contained true -p:PublishSingleFile=true",
                    "net9-ready-to-run" => $"-f net9.0 -r {rid} --self-contained false -p:PublishReadyToRun=true",
                    "net8-self-contained" => $"-f net8.0 -r {rid} --self-contained true",
                    "net10-self-contained" => $"-f net10.0 -r {rid} --self-contained true",
                    "net10-framework-dependent" => $"-f net10.0 -r {rid} --self-contained false",
                    "net10-single-file" => $"-f net10.0 -r {rid} --self-contained true -p:PublishSingleFile=true",
                    _ => throw new ArgumentException(variant),
                };
                string project = Path.Combine(TestPaths.Root, "tests", "MatrixApp", "MatrixApp.csproj");
                var startInfo = new ProcessStartInfo("dotnet", $"publish \"{project}\" -c Debug {arguments} -o \"{output}\" -nologo -v q")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using Process publish = Process.Start(startInfo)!;
                string log = publish.StandardOutput.ReadToEnd() + publish.StandardError.ReadToEnd();
                publish.WaitForExit();
                Assert.True(publish.ExitCode == 0, $"publishing {variant} failed:\n{log}");

                string exe = Path.Combine(output, OperatingSystem.IsWindows() ? "TestApp.exe" : "TestApp");
                return _programs[variant] = exe;
            }
        }
    }

    [Theory]
    [InlineData("net9-self-contained")]
    [InlineData("net9-single-file")]
    [InlineData("net9-ready-to-run")]
    [InlineData("net8-self-contained")]
    [InlineData("net10-self-contained")]
    [InlineData("net10-framework-dependent")]
    [InlineData("net10-single-file")]
    public void CoreScenarioWorks(string variant)
    {
        if (!PublishedApps.Enabled)
            return;
        string program = _apps.Get(variant);

        using var client = new DapClient(TimeSpan.FromMinutes(2));
        client.Initialize();
        client.Request("launch", new { program, args = new[] { "basic" } });
        client.SetBreakpoints("add");
        client.Request("configurationDone");

        var (threadId, top) = client.Top(client.WaitForStop("breakpoint"));
        Assert.Equal("TestApp.Program.Add()", top.Name);
        Assert.Equal(TestPaths.LineOf("add"), top.Line);
        Assert.Equal("42", client.Locals(top.Id)["a"].Value);
        Assert.Equal("52", client.Evaluate("a + b", top.Id).Result);
        Assert.Equal("3", client.Evaluate("TestApp.Person.Instances", top.Id).Result);

        (threadId, top) = client.StepAndWait("stepOut", threadId);
        Assert.Equal("TestApp.Program.Basic()", top.Name);
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("52", locals["TestApp.Program.Add() returned"].Value);
        Assert.Equal("{Person:Ann}", locals["person"].Value); // func-eval: ToString()
        Assert.Equal("Count = 2", locals["list"].Value); // memory layout of List<T>
        Assert.Equal("Count = 1", locals["map"].Value); // ... and of Dictionary<K,V>
        Assert.Equal("\"a\"", client.Variables(locals["list"].VariablesReference)["[0]"].Value);
        Assert.Equal("\"hi, Ann\"", client.Evaluate("person.Greet(\"hi\")", top.Id).Result);

        (threadId, top) = client.StepAndWait("next", threadId);
        Assert.Equal(TestPaths.LineOf("afterAdd"), top.Line);

        client.Request("continue", new { threadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
        Assert.Contains("mode: basic", client.Output("stdout"));
    }
}
