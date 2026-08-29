using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.DTOs;

public record RequestDTO
{
    [JsonPropertyName("context")]
    public JsonElement Context { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}