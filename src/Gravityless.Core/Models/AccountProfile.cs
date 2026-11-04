namespace Gravityless.Core.Models;

/// <summary>
/// One pooled account.
///
/// Credentials are stored as typed fields rather than an opaque JSON blob. The identity half —
/// email, refresh token, id token — is written once when the account is captured and never
/// modified afterwards, so an account cannot drift into holding another account's identity.
/// Only the access token and its expiry change over time; both are a cache that can be thrown
/// away and rebuilt from the refresh token.
/// </summary>
public class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Account address. Part of the identity; also the value matched against tokens.</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Per-account device fingerprint, so two accounts do not look like one machine.</summary>
    public string InstallationId { get; set; } = Guid.NewGuid().ToString();

    public string ShortInstallationId =>
        InstallationId.Length >= 8 ? InstallationId[..8] : InstallationId;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;

    // ---------------------------------------------------------------- identity (immutable)

    /// <summary>
    /// The refresh token. This is the account's identity as far as the token endpoint is
    /// concerned: everything else is derived from it. Set at capture, never written again.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>OpenID token carrying the email claim. Also part of the identity.</summary>
    public string? IdToken { get; set; }

    public string TokenType { get; set; } = "Bearer";

    // ---------------------------------------------------------------- access token (cache)

    public string? AccessToken { get; set; }

    public DateTime? AccessTokenExpiresAt { get; set; }

    /// <summary>True when the cached access token is missing or within a minute of expiring.</summary>
    public bool NeedsRefresh =>
        string.IsNullOrEmpty(AccessToken)
        || AccessTokenExpiresAt is null
        || AccessTokenExpiresAt.Value <= DateTime.UtcNow.AddMinutes(1);

    // ---------------------------------------------------------------- runtime lock

    public bool IsRunning { get; set; }

    public string? BoundInstanceId { get; set; }

    public int FailedRefreshAttempts { get; set; }

    // ---------------------------------------------------------------- quota

    public double? Gemini5hFraction { get; set; }
    public DateTime? Gemini5hResetTime { get; set; }
    public double? Gemini7dFraction { get; set; }
    public DateTime? Gemini7dResetTime { get; set; }

    public double? Claude5hFraction { get; set; }
    public DateTime? Claude5hResetTime { get; set; }
    public double? Claude7dFraction { get; set; }
    public DateTime? Claude7dResetTime { get; set; }

    public DateTime? QuotaLastFetchedAt { get; set; }
    public string? QuotaRawText { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public double? GeminiRemainingFraction => Gemini5hFraction ?? Gemini7dFraction;

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? GeminiResetTime => Gemini5hResetTime ?? Gemini7dResetTime;

    [System.Text.Json.Serialization.JsonIgnore]
    public double? ClaudeGptRemainingFraction => Claude5hFraction ?? Claude7dFraction;

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? ClaudeGptResetTime => Claude5hResetTime ?? Claude7dResetTime;

    /// <summary>Card label, e.g. "Gemini: 68%(5h)/36%(7d)  Claude: 100%(5h)/50%(7d)".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string QuotaCardSummaryText
    {
        get
        {
            var parts = new List<string>();

            if (Gemini5hFraction.HasValue && Gemini7dFraction.HasValue)
            {
                parts.Add($"Gemini: {(int)(Gemini5hFraction.Value * 100)}%(5h)/{(int)(Gemini7dFraction.Value * 100)}%(7d)");
            }
            else if (Gemini5hFraction.HasValue)
            {
                parts.Add($"Gemini: {(int)(Gemini5hFraction.Value * 100)}%(5h)");
            }
            else if (Gemini7dFraction.HasValue)
            {
                parts.Add($"Gemini: {(int)(Gemini7dFraction.Value * 100)}%(7d)");
            }

            if (Claude5hFraction.HasValue && Claude7dFraction.HasValue)
            {
                parts.Add($"Claude: {(int)(Claude5hFraction.Value * 100)}%(5h)/{(int)(Claude7dFraction.Value * 100)}%(7d)");
            }
            else if (Claude7dFraction.HasValue)
            {
                parts.Add($"Claude: {(int)(Claude7dFraction.Value * 100)}%(7d)");
            }
            else if (Claude5hFraction.HasValue)
            {
                parts.Add($"Claude: {(int)(Claude5hFraction.Value * 100)}%(5h)");
            }

            return parts.Count > 0 ? string.Join("  ", parts) : string.Empty;
        }
    }
}
