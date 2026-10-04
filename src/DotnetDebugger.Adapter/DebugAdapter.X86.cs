using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDebugger.Engine;
using DotnetDebugger.Engine.Launch;
using DotnetDebugger.Protocol;

namespace DotnetDebugger.Adapter;

// 32-bit debuggees on 64-bit Windows: ICorDebug only debugs processes of its own bitness, so once launch or attach
// shows that the target is 32-bit, the session is handed over to the 32-bit build of the adapter (see X86Relay).
internal sealed partial class DebugAdapter
{
    /// <summary>The trace file of this adapter, if any: the 32-bit adapter writes its own next to it.</summary>
    public string? LogPath { get; init; }

    private volatile X86Relay? _relay;
    private DapMessage? _initializeRequest;
    private DapMessage? _handedOver;
    // requests answered here that set up the session: the 32-bit adapter gets them too
    private readonly List<DapMessage> _configurationRequests = [];

    // Queued at the hand-over: when the worker gets to it, each request before it was either handled here (and
    // remembered) or passed to the relay, which holds it back until the replay is through.
    private static readonly DapMessage s_handOverMarker = new() { Type = "request", Command = "(hand-over)" };

    /// <summary>The request was passed on to the 32-bit adapter, which answers it.</summary>
    private sealed class HandedOverException() : Exception("handed over to the 32-bit adapter");

    /// <summary>Before a request is handled here: remembers what the 32-bit adapter needs, and hands attach over.</summary>
    private void PrepareHandOver(DapMessage request)
    {
        switch (request.Command)
        {
            case "initialize":
                _initializeRequest = request;
                break;
            case "setBreakpoints" or "setFunctionBreakpoints" or "setExceptionBreakpoints" or "configurationDone":
                lock (_configurationRequests)
                    _configurationRequests.Add(request);
                break;
            case "attach" when request.GetArguments<AttachArguments>() is { } args:
                HandOverIfThirtyTwoBit(request, args.GetProcessId());
                break;
        }
    }

    /// <summary>Reader thread: whether <paramref name="message"/> belongs to the 32-bit adapter.</summary>
    private bool RelayFromReader(DapMessage message)
    {
        if (_relay is not { } relay)
            return false;
        if (message.Type == "response")
            relay.FromClient(message);
        else if (message.Type == "request" && !_queue.IsAddingCompleted)
            _queue.Add(message); // through the queue, behind what was read before the hand-over
        return true;
    }

    /// <summary>Worker thread: whether <paramref name="message"/> belongs to the 32-bit adapter.</summary>
    private bool RelayFromQueue(DapMessage message)
    {
        if (_relay is not { } relay)
            return false;
        if (ReferenceEquals(message, s_handOverMarker))
        {
            // everything that was taken from the queue before has been handled here (and remembered) by now
            DapMessage[] configuration;
            lock (_configurationRequests)
                configuration = [.. _configurationRequests];
            relay.Activate(configuration, _handedOver!);
        }
        else
        {
            relay.FromClient(message);
        }
        return true;
    }

    /// <summary>Launch: hands the session over if the resolved program runs as a 32-bit process.</summary>
    private void HandOverIfThirtyTwoBit(DapMessage request, ResolvedLaunch launch, LaunchArguments args)
    {
        if (!MustHandOver(ProcessBitness.Is32BitProgram(launch.Program)))
            return;

        // The 32-bit adapter resolves the launch again: what had to be found out or built here is settled.
        JsonObject arguments = request.Arguments is { ValueKind: JsonValueKind.Object } a ? JsonNode.Parse(a.GetRawText())!.AsObject() : [];
        arguments["program"] = launch.Program;
        arguments["build"] = false;
        if (args.SuppressJitOptimizations == null)
            arguments["suppressJitOptimizations"] = launch.Environment.TryGetValue("DOTNET_ReadyToRun", out string? readyToRun) && readyToRun == "0";
        var launchThere = new DapMessage
        {
            Seq = request.Seq,
            Type = request.Type,
            Command = request.Command,
            Arguments = JsonSerializer.SerializeToElement(arguments),
        };
        HandOver(request, launchThere, $"'{Path.GetFileName(launch.Program)}'");
    }

    /// <summary>Attach: hands the session over if the process is a 32-bit one.</summary>
    private void HandOverIfThirtyTwoBit(DapMessage request, int processId)
    {
        if (MustHandOver(ProcessBitness.Is32BitProcess(processId)))
            HandOver(request, request, $"Process {processId}");
    }

    private bool MustHandOver(bool? is32Bit) =>
        is32Bit == true && OperatingSystem.IsWindows() && Environment.Is64BitProcess && _relay == null;

    private void HandOver(DapMessage request, DapMessage requestThere, string target)
    {
        string? option = request.Arguments is { ValueKind: JsonValueKind.Object } a
            && a.TryGetProperty(X86Adapter.OptionName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        X86AdapterCommand command;
        try
        {
            command = X86Adapter.Locate(option);
        }
        catch (DebuggerException e)
        {
            _log($"{target} is 32-bit, and there is no 32-bit adapter: {e.Message}");
            throw new DebuggerException($"{target} runs as a 32-bit process, which this (64-bit) adapter cannot debug. {e.Message} {ProcessBitness.X86AdapterHint}");
        }

        _log($"{target} is 32-bit: the session goes on in the 32-bit adapter {command}");
        DapMessage initialize = _initializeRequest ?? new DapMessage
        {
            Type = "request",
            Command = "initialize",
            Arguments = JsonSerializer.SerializeToElement(new { adapterID = "dotnet-debugger" }),
        };
        // When it ends, so does this adapter: the reader of the client's stream cannot be interrupted, and there is
        // nothing left to do here anyway.
        X86Relay relay = X86Relay.Start(command, initialize, _connection, _log, LogPath, () => Environment.Exit(0));
        _connection.SendEvent("output", new OutputEventBody
        {
            Category = "console",
            Output = $"{target} runs as a 32-bit process: it is debugged by the 32-bit adapter ({command.FileName}).{Environment.NewLine}",
        });

        _handedOver = requestThere;
        _relay = relay;
        _queue.Add(s_handOverMarker);
        throw new HandedOverException();
    }
}
