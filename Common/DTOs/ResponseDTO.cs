using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Contracts;

namespace Common.DTOs;

public record ResponseDTO
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    [JsonPropertyName("result")]
    public JsonElement Result { get; init; }

    [JsonPropertyName("meta")]
    public Meta Meta { get; init; } = null!;
}