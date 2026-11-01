namespace Gravityless.Core.Models;

/// <summary>
/// OAuth client pair used to refresh account tokens. Recovered from the installed
/// Antigravity binaries and cached in data/oauth_client.json.
/// </summary>
public class OAuthClientConfig
{
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;
}
