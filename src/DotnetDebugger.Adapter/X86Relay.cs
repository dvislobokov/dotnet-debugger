using System.Diagnostics;
using System.Text;
using DotnetDebugger.Engine;
using DotnetDebugger.Protocol;

namespace DotnetDebugger.Adapter;

/// <summary>
/// The session of a 32-bit debuggee, carried on by the 32-bit adapter: this adapter only relays the protocol.
/// <para>
/// The client already went through "initialize" (and maybe configured breakpoints) with this adapter. The 32-bit
/// adapter gets the same initialize request first, then the configuration requests that were answered here, then the
/// launch or attach request itself; the client hears nothing of these replays. From then on everything passes
/// unchanged, except for sequence numbers: the client's ones are kept (responses refer to them), the 32-bit adapter's
/// ones are replaced by this connection's, and the requests it sends to the client (runInTerminal) are mapped back.
/// </para>
/// </summary>
internal sealed class X86Relay : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly DapConnection _client;
    private readonly Process _process;
    private readonly DapConnection _adapter;
    private readonly Action<string> _log;
    private readonly Action _ended;
    private readonly object _lock = new();

    // the client's requests whose responses it already got from this adapter
    private readonly HashSet<int> _replayed = [];
    // sequence number the client knows a request of the 32-bit adapter by -> the one it has there
    private readonly Dictionary<int, int> _reverseRequests = [];
    // what the client sent before the replay was through
    private readonly List<DapMessage> _pending = [];
    private bool _active;
    private int _initializedSkipped;
    private int _terminatedSeen;
    private readonly StringBuilder _stderr = new();

    private X86Relay(DapConnection client, Process process, Action<string> log, Action ended)
    {
        _ended = ended;
        _client = client;
        _process = process;
        _log = log;
        _adapter = new DapConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
    }

    /// <summary>
    /// Starts the 32-bit adapter and takes it through "initialize". <paramref name="ended"/> is called when it is gone.
    /// </summary>
    /// <exception cref="DebuggerException">It did not start or did not answer.</exception>
    public static X86Relay Start(X86AdapterCommand command, DapMessage initialize, DapConnection client, Action<string> log, string? logPath, Action ended)
    {
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);
        if (logPath != null)
            startInfo.ArgumentList.Add("--log=" + Path.Combine(Path.GetDirectoryName(Path.GetFullPath(logPath))!, Path.GetFileNameWithoutExtension(logPath) + ".x86" + Path.GetExtension(logPath)));

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new DebuggerException("no process");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or DebuggerException)
        {
            throw new DebuggerException($"The 32-bit adapter '{command.FileName}' could not be started: {e.Message}");
        }

        var relay = new X86Relay(client, process, log, ended);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            log("x86 adapter stderr: " + e.Data);
            lock (relay._stderr)
                relay._stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        log($"Started the 32-bit adapter, process {process.Id}: {command}");

        try
        {
            relay._adapter.Relay(initialize, renumber: false);
            Task<DapMessage?> response = Task.Run(() => relay._adapter.Read());
            if (!response.Wait(StartTimeout) || response.Result is not { Type: "response", Success: true })
            {
                // gone already: what it said on stderr is the story (the parameterless wait drains it)
                if (response.IsCompleted && response.Result == null && process.WaitForExit(5000))
                    process.WaitForExit();
                string detail = response.IsCompleted && response.Result is { Message: { } message } ? message : relay.Stderr();
                throw new DebuggerException($"The 32-bit adapter '{command.FileName}' did not start" + (detail.Length > 0 ? ": " + detail : "."));
            }
        }
        catch (Exception e) when (e is not DebuggerException)
        {
            relay.Dispose();
            throw new DebuggerException($"The 32-bit adapter '{command.FileName}' did not start: {e.Message}");
        }
        catch (DebuggerException)
        {
            relay.Dispose();
            throw;
        }

        new System.Threading.Thread(relay.Pump) { IsBackground = true, Name = "x86 adapter relay" }.Start();
        return relay;
    }

    /// <summary>
    /// Replays what this adapter answered (<paramref name="configuration"/>), then sends <paramref name="handedOver"/>
    /// (launch or attach) and whatever the client sent meanwhile.
    /// </summary>
    public void Activate(IReadOnlyList<DapMessage> configuration, DapMessage handedOver)
    {
        lock (_lock)
        {
            foreach (DapMessage request in configuration)
            {
                _replayed.Add(request.Seq);
                _adapter.Relay(request, renumber: false);
            }
            _adapter.Relay(handedOver, renumber: false);
            foreach (DapMessage message in _pending)
                _adapter.Relay(message, renumber: false);
            _pending.Clear();
            _active = true;
        }
    }

    /// <summary>A message of the client.</summary>
    public void FromClient(DapMessage message)
    {
        try
        {
            lock (_lock)
            {
                if (message.Type == "response")
                {
                    if (message.RequestSeq is not { } seq || !_reverseRequests.Remove(seq, out int original))
                        return;
                    message.RequestSeq = original;
                    _adapter.Relay(message, renumber: false);
                }
                else if (!_active)
                {
                    _pending.Add(message);
                }
                else
                {
                    _adapter.Relay(message, renumber: false);
                }
            }
        }
        catch (IOException e)
        {
            _log($"x86 adapter: cannot pass on '{message.Command}': {e.Message}");
        }
    }

    private void Pump()
    {
        try
        {
            while (_adapter.Read() is { } message)
            {
                switch (message.Type)
                {
                    case "response":
                        bool replayed;
                        lock (_lock)
                            replayed = message.RequestSeq is { } seq && _replayed.Remove(seq);
                        if (!replayed)
                            _client.Relay(message, renumber: true);
                        break;

                    case "event":
                        // the client got "initialized" from this adapter already
                        if (message.Event == "initialized" && Interlocked.Exchange(ref _initializedSkipped, 1) == 0)
                            break;
                        if (message.Event == "terminated")
                            Interlocked.Exchange(ref _terminatedSeen, 1);
                        _client.Relay(message, renumber: true);
                        break;

                    case "request":
                        lock (_lock)
                        {
                            int original = message.Seq;
                            _reverseRequests[_client.Relay(message, renumber: true)] = original;
                        }
                        break;
                }
            }
        }
        catch (IOException e)
        {
            _log("x86 adapter relay: " + e.Message);
        }

        bool exited = _process.WaitForExit(5000);
        _log("The 32-bit adapter has ended" + (exited ? $" (exit code {_process.ExitCode})." : "."));
        if (Volatile.Read(ref _terminatedSeen) == 0)
        {
            // it went away in the middle of the session: the client must not wait for it
            try
            {
                string stderr = Stderr();
                _client.SendEvent("output", new OutputEventBody
                {
                    Category = "stderr",
                    Output = "The 32-bit adapter ended unexpectedly." + (stderr.Length > 0 ? Environment.NewLine + stderr : "") + Environment.NewLine,
                });
                _client.SendEvent("terminated");
            }
            catch (IOException)
            {
            }
        }
        _ended();
    }

    private string Stderr()
    {
        lock (_stderr)
            return _stderr.ToString().Trim();
    }

    public void Dispose()
    {
        // without its input the 32-bit adapter ends the session the way this one would
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        try
        {
            if (!_process.WaitForExit(10000))
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
