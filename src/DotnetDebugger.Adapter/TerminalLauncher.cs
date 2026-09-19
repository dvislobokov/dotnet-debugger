using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DotnetDebugger.Engine;
using DotnetDebugger.Protocol;

namespace DotnetDebugger.Adapter;

/// <summary>
/// Starts the debuggee in the client's terminal: the client is asked (runInTerminal) to run this very executable in
/// helper mode, and the helper reports back over a loopback connection. See <see cref="Engine.Launch.TerminalHelper"/>.
/// </summary>
internal static class TerminalLauncher
{
    public const string HelperOption = "--run-in-terminal";

    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(60);

    public static ExternalLaunch Launch(DapConnection connection, ResolvedLaunch launch, string kind)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string token = Guid.NewGuid().ToString("N");

            var command = new List<string>(SelfCommand()) { HelperOption, port.ToString(), token, "--", launch.Program };
            command.AddRange(launch.Args);

            Task<DapMessage> response = connection.SendRequestAsync("runInTerminal", new RunInTerminalArguments
            {
                Kind = kind,
                Title = Path.GetFileNameWithoutExtension(launch.Program),
                Cwd = launch.WorkingDirectory,
                Args = command.ToArray(),
                Env = launch.Environment.Count == 0 ? null : launch.Environment,
            });

            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            DateTime deadline = DateTime.UtcNow + ClientTimeout;
            while (!accept.IsCompleted)
            {
                if (response.IsCompleted && response.Result.Success != true)
                    throw new DebuggerException("The client could not start a terminal: " + response.Result.Message);
                if (DateTime.UtcNow > deadline)
                    throw new DebuggerException("The debuggee did not start in the terminal in time.");
                Task.WaitAny([accept, response], TimeSpan.FromMilliseconds(200));
            }

            TcpClient helper = accept.Result;
            var reader = new StreamReader(helper.GetStream());
            var writer = new StreamWriter(helper.GetStream()) { AutoFlush = true, NewLine = "\n" };

            string[] hello = (reader.ReadLine() ?? "").Split(' ');
            if (hello.Length != 2 || hello[0] != token || !int.TryParse(hello[1], out int processId))
            {
                helper.Dispose();
                throw new DebuggerException("Unexpected connection while waiting for the debuggee to start.");
            }

            Task<int?> exitCode = Task.Run(() =>
            {
                try
                {
                    string? line = reader.ReadLine();
                    return line != null && line.StartsWith("exit ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(5), out int code)
                        ? code
                        : (int?)null;
                }
                catch (IOException)
                {
                    return null;
                }
                finally
                {
                    helper.Dispose();
                }
            });
            return new ExternalLaunch(processId, () => writer.WriteLine("resume"), exitCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    // How to start this program again: "dotnet-debugger" as an executable, or "dotnet dotnet-debugger.dll".
    private static string[] SelfCommand()
    {
        string processPath = Environment.ProcessPath ?? throw new DebuggerException("Cannot determine the path of the debugger.");
        string host = Path.GetFileNameWithoutExtension(processPath);
        return host.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [processPath, Assembly.GetEntryAssembly()!.Location]
            : [processPath];
    }
}
