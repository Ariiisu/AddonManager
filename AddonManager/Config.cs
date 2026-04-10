using System.Text.Json.Serialization;

namespace AddonManager;

internal sealed class Config
{
    [JsonPropertyName("steam_api_key")]
    public string ApiKey { get; init; } = string.Empty;

    [JsonPropertyName("remove_obsolete_files")]
    public bool RemoveObsoleteFiles { get; init; } = false;

    [JsonPropertyName("remove_vpk")]
    public bool RemoveVpkFiles { get; init; } = false;

    [JsonPropertyName("check_interval")]
    public float Interval { get; init; } = 5.0f;
}