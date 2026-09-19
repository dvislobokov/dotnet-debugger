using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DotnetDebugger.Adapter;
using DotnetDebugger.Protocol;
using Microsoft.Win32.SafeHandles;

// dotnet-debugger: a .NET debugger speaking the Debug Adapter Protocol.
//
//   dotnet-debugger                   DAP over stdin/stdout (default)
//   dotnet-debugger --server[=PORT]   DAP over TCP, one session (default port 4711)
//   --log=FILE                        write a protocol/engine trace
//   --version

// Helper mode, started by the DAP client inside its terminal:  --run-in-terminal PORT TOKEN -- PROGRAM [ARGS...]
if (args.Length >= 5 && args[0] == TerminalLauncher.HelperOption && args[3] == "--")
{
    try
    {
        return DotnetDebugger.Engine.Launch.TerminalHelper.Run(int.Parse(args[1]), args[2], args[4], args[5..]);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine("dotnet-debugger: " + e.Message);
        return 127;
    }
}

int? port = null;
string? logPath = null;
foreach (string arg in args)
{
    if (arg == "--server")
        port = 4711;
    else if (arg.StartsWith("--server="))
        port = int.Parse(arg["--server=".Length..]);
    else if (arg.StartsWith("--log="))
        logPath = arg["--log=".Length..];
    else if (arg == "--version")
    {
        Console.WriteLine(typeof(DebugAdapter).Assembly.GetName().Version);
        return 0;
    }
    else if (arg is "--help" or "-h")
    {
        Console.Error.WriteLine("Usage: dotnet-debugger [--server[=PORT]] [--log=FILE]");
        return 0;
    }
    else
    {
        Console.Error.WriteLine($"Unknown option '{arg}'.");
        return 1;
    }
}

// DOTNET_DEBUGGER_LOG_DIR: a trace per adapter process without touching the client's configuration (CI, bug reports)
if (logPath == null && Environment.GetEnvironmentVariable("DOTNET_DEBUGGER_LOG_DIR") is { Length: > 0 } logDirectory)
{
    try
    {
        Directory.CreateDirectory(logDirectory);
        logPath = Path.Combine(logDirectory, $"adapter-{DateTime.Now:HHmmss-fff}-{Environment.ProcessId}.log");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Cannot create the log directory: " + e.Message);
    }
}

StreamWriter? logWriter = logPath == null ? null : new StreamWriter(logPath, append: false) { AutoFlush = true };
void Log(string message)
{
    if (logWriter == null)
        return;
    lock (logWriter)
        logWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
}

Stream input, output;
TcpClient? client = null;
if (port is { } p)
{
    var listener = new TcpListener(IPAddress.Loopback, p);
    listener.Start();
    Console.Error.WriteLine($"Waiting for a DAP client on 127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
    client = listener.AcceptTcpClient();
    listener.Stop();
    input = output = client.GetStream();
}
else
{
    (input, output) = StdioTransport.Open();
}

using var connection = new DapConnection(input, output) { Trace = logWriter == null ? null : Log };
using (var adapter = new DebugAdapter(connection, Log))
{
    try
    {
        adapter.Run();
    }
    catch (Exception e)
    {
        Log("Fatal: " + e);
        return 1;
    }
}
client?.Dispose();
return 0;

internal static class StdioTransport
{
    /// <summary>
    /// Opens the protocol streams. On Unix the debuggee is forked with whatever is on fds 0/1/2, which the
    /// launcher swaps temporarily; private duplicates keep the DAP channel out of that.
    /// </summary>
    public static (Stream Input, Stream Output) Open()
    {
        if (OperatingSystem.IsWindows())
            return (Console.OpenStandardInput(), Console.OpenStandardOutput());

        int inFd = dup(0), outFd = dup(1);
        if (inFd < 0 || outFd < 0)
            return (Console.OpenStandardInput(), Console.OpenStandardOutput());
        return (
            new FileStream(new SafeFileHandle(inFd, ownsHandle: true), FileAccess.Read, 1, isAsync: false),
            new FileStream(new SafeFileHandle(outFd, ownsHandle: true), FileAccess.Write, 1, isAsync: false));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);
}
