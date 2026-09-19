using System.Text;
using System.Text.Json;

namespace DotnetDebugger.Protocol;

/// <summary>
/// Reads and writes DAP messages framed with "Content-Length" headers over a pair of streams.
/// </summary>
public sealed class DapConnection : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _writeLock = new();
    private readonly Dictionary<int, TaskCompletionSource<DapMessage>> _pendingRequests = [];
    private int _seq;

    /// <summary>Optional sink that receives every raw message ("-> " sent, "<- " received).</summary>
    public Action<string>? Trace { get; set; }

    public DapConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Reads the next message, or returns null when the peer closed the stream.</summary>
    public DapMessage? Read()
    {
        int contentLength = -1;
        while (true)
        {
            string? line = ReadHeaderLine();
            if (line == null)
                return null;
            if (line.Length == 0)
            {
                if (contentLength >= 0)
                    break;
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line.AsSpan(colon + 1).Trim());
        }

        var buffer = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = _input.Read(buffer, read, contentLength - read);
            if (n == 0)
                return null;
            read += n;
        }

        Trace?.Invoke("<- " + Encoding.UTF8.GetString(buffer));
        return JsonSerializer.Deserialize<DapMessage>(buffer, DapJson.Options);
    }

    private string? ReadHeaderLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = _input.ReadByte();
            if (b < 0)
                return null;
            if (b == '\n')
                return sb.ToString().TrimEnd('\r');
            sb.Append((char)b);
        }
    }

    public int SendRequest(string command, object? arguments = null)
    {
        var msg = new DapMessage
        {
            Type = "request",
            Command = command,
            Arguments = arguments == null ? null : JsonSerializer.SerializeToElement(arguments, DapJson.Options),
        };
        return Send(msg);
    }

    /// <summary>
    /// Sends a request whose response is delivered through <see cref="CompleteRequest"/> by whoever reads the stream.
    /// </summary>
    public Task<DapMessage> SendRequestAsync(string command, object? arguments = null)
    {
        var completion = new TaskCompletionSource<DapMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_writeLock)
        {
            // registered under the write lock so the response cannot overtake the registration
            int seq = SendRequest(command, arguments);
            lock (_pendingRequests)
                _pendingRequests[seq] = completion;
        }
        return completion.Task;
    }

    /// <returns>false if the response does not belong to a request sent with <see cref="SendRequestAsync"/>.</returns>
    public bool CompleteRequest(DapMessage response)
    {
        TaskCompletionSource<DapMessage>? completion;
        lock (_pendingRequests)
        {
            if (response.RequestSeq is not { } seq || !_pendingRequests.Remove(seq, out completion))
                return false;
        }
        completion.SetResult(response);
        return true;
    }

    public void SendResponse(DapMessage request, object? body = null) =>
        Send(new DapMessage { Type = "response", RequestSeq = request.Seq, Command = request.Command, Success = true, Body = body });

    public void SendErrorResponse(DapMessage request, string message) =>
        Send(new DapMessage
        {
            Type = "response",
            RequestSeq = request.Seq,
            Command = request.Command,
            Success = false,
            Message = message,
            Body = new ErrorResponseBody { Error = new ErrorMessage { Id = 1, Format = message } },
        });

    public void SendEvent(string name, object? body = null) =>
        Send(new DapMessage { Type = "event", Event = name, Body = body });

    private int Send(DapMessage msg)
    {
        lock (_writeLock)
        {
            msg.Seq = ++_seq;
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(msg, DapJson.Options);
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            Trace?.Invoke("-> " + Encoding.UTF8.GetString(payload));
            _output.Write(header);
            _output.Write(payload);
            _output.Flush();
            return msg.Seq;
        }
    }

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
    }
}
