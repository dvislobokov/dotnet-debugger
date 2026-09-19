using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDebugger.Protocol;

/// <summary>
/// A single DAP protocol message. Requests, responses and events share one shape;
/// the <see cref="Type"/> discriminator tells which of the optional members are meaningful.
/// </summary>
public sealed class DapMessage
{
    public int Seq { get; set; }
    public string Type { get; set; } = "";

    // request
    public string? Command { get; set; }
    public JsonElement? Arguments { get; set; }

    // response
    [JsonPropertyName("request_seq")]
    public int? RequestSeq { get; set; }
    public bool? Success { get; set; }
    public string? Message { get; set; }

    // event
    public string? Event { get; set; }

    // response + event. Deserializes as JsonElement.
    public object? Body { get; set; }

    public T? GetArguments<T>() =>
        Arguments is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } a
            ? a.Deserialize<T>(DapJson.Options)
            : default;

    public T? GetBody<T>() =>
        Body is JsonElement { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } b
            ? b.Deserialize<T>(DapJson.Options)
            : default;
}

public static class DapJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}
