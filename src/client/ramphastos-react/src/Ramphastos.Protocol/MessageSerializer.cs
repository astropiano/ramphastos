using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ramphastos.Protocol;

/// <summary>
/// Reads and writes wire envelopes. Type dispatch is on the "type" field;
/// payload fields live flat on the envelope per the protocol spec.
/// </summary>
public static class MessageSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    public static JsonObject Serialize(RamphastosMessage message) =>
        JsonSerializer.SerializeToNode(message, message.GetType(), Options)!.AsObject();

    public static RamphastosMessage Deserialize(JsonObject envelope)
    {
        var type = envelope["type"]?.GetValue<string>()
            ?? throw new JsonException("Missing 'type' on message envelope.");

        RamphastosMessage message = type switch
        {
            MessageType.Hello => envelope.Deserialize<HelloMessage>(Options)!,
            MessageType.Welcome => envelope.Deserialize<WelcomeMessage>(Options)!,
            MessageType.Snapshot => envelope.Deserialize<SnapshotMessage>(Options)!,
            MessageType.Patch => envelope.Deserialize<PatchMessage>(Options)!,
            MessageType.Invoke => envelope.Deserialize<InvokeMessage>(Options)!,
            MessageType.Result => envelope.Deserialize<ResultMessage>(Options)!,
            MessageType.Call => envelope.Deserialize<CallMessage>(Options)!,
            MessageType.Error => envelope.Deserialize<ErrorMessage>(Options)!,
            MessageType.Bye => envelope.Deserialize<ByeMessage>(Options)!,
            MessageType.StreamOpen => envelope.Deserialize<StreamOpenMessage>(Options)!,
            MessageType.StreamOpened => envelope.Deserialize<StreamOpenedMessage>(Options)!,
            MessageType.StreamClose => envelope.Deserialize<StreamCloseMessage>(Options)!,
            _ => throw new JsonException($"Unknown message type '{type}'.")
        };

        if (message.V != ProtocolConstants.Version)
            throw new JsonException($"Unsupported protocol version {message.V}.");

        return message;
    }
}
