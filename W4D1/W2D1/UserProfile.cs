using System.Text.Json.Serialization;

namespace W2D1;

// Устойчивые предпочтения взаимодействия, отдельно от памяти задач и диалога.
internal sealed record UserProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    [JsonPropertyName("expertise")]
    public string? Expertise { get; init; }
    [JsonPropertyName("response_style")]
    public string? ResponseStyle { get; init; }
    [JsonPropertyName("response_length")]
    public string? ResponseLength { get; init; }
    [JsonPropertyName("format")]
    public string? Format { get; init; }
    [JsonPropertyName("language")]
    public string? Language { get; init; }
    [JsonPropertyName("constraints")]
    public string? Constraints { get; init; }

    public UserProfile? WithField(string key, string? value) => key.ToLowerInvariant() switch
    {
        "name" => this with { Name = value },
        "expertise" => this with { Expertise = value },
        "response_style" => this with { ResponseStyle = value },
        "response_length" => this with { ResponseLength = value },
        "format" => this with { Format = value },
        "language" => this with { Language = value },
        "constraints" => this with { Constraints = value },
        _ => null
    };
}
