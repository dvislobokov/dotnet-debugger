using System.Text;
using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

public class DapConnectionTests
{
    [Fact]
    public void WritesContentLengthFramedJson()
    {
        var output = new MemoryStream();
        var connection = new DapConnection(Stream.Null, output);

        connection.SendEvent("output", new OutputEventBody { Category = "stdout", Output = "привет" });

        string text = Encoding.UTF8.GetString(output.ToArray());
        int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string payload = text[(split + 4)..];
        Assert.Equal($"Content-Length: {Encoding.UTF8.GetByteCount(payload)}", text[..split]);
        Assert.Equal("""{"seq":1,"type":"event","event":"output","body":{"category":"stdout","output":"привет"}}""",
            System.Text.RegularExpressions.Regex.Unescape(payload));
    }

    [Fact]
    public void ReadsConsecutiveMessagesAndIgnoresUnknownHeaders()
    {
        string first = """{"seq":1,"type":"request","command":"launch","arguments":{"program":"a.dll","stopAtEntry":true}}""";
        string second = """{"seq":2,"type":"request","command":"threads"}""";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(
            $"Content-Length: {first.Length}\r\nContent-Type: application/json\r\n\r\n{first}" +
            $"Content-Length: {second.Length}\r\n\r\n{second}"));
        var connection = new DapConnection(input, Stream.Null);

        DapMessage launch = connection.Read()!;
        Assert.Equal("launch", launch.Command);
        var arguments = launch.GetArguments<LaunchArguments>()!;
        Assert.Equal("a.dll", arguments.Program);
        Assert.True(arguments.StopAtEntry);

        Assert.Equal("threads", connection.Read()!.Command);
        Assert.Null(connection.Read());
    }

    [Fact]
    public void ErrorResponseCarriesMessage()
    {
        var output = new MemoryStream();
        var connection = new DapConnection(Stream.Null, output);
        connection.SendErrorResponse(new DapMessage { Seq = 7, Type = "request", Command = "next" }, "The debuggee is running.");

        var reader = new DapConnection(new MemoryStream(output.ToArray()), Stream.Null);
        DapMessage response = reader.Read()!;
        Assert.Equal(7, response.RequestSeq);
        Assert.False(response.Success);
        Assert.Equal("The debuggee is running.", response.Message);
        Assert.Equal("The debuggee is running.", response.GetBody<ErrorResponseBody>()!.Error!.Format);
    }
}
