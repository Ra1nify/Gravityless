namespace Gravityless.Core.Models;

/// <summary>
/// One pooled account.
///
/// Credentials are stored as typed fields rather than an opaque JSON blob. Email is the
/// stable identity anchor; the refresh token is the long-lived base credential (updated only
/// on re-capture or if the OAuth token endpoint explicitly rotates it), while the access
/// token is a short-lived runtime credential rebuilt from the refresh token.
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

    // ---------------------------------------------------------------- refresh token (long-lived base credential)

    /// <summary>
    /// The long-lived refresh token used to mint short-lived access tokens at the OAuth
    /// token endpoint. Set at capture and only updated if re-captured or rotated by Google.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    public DateTime? RefreshTokenLastUsedAt { get; set; }

    /// <summary>
    /// Refresh token expiration when Google returns <c>refresh_token_expires_in</c>;
    /// null when the server issues a non-expiring desktop token.
    /// </summary>
    public DateTime? RefreshTokenExpiresAt { get; set; }

    /// <summary>True when the token endpoint rejected the refresh token with <c>invalid_grant</c>.</summary>
    public bool RefreshTokenRevoked { get; set; }

    /// <summary>OAuth client ID that issued/verified this account's refresh token (personal vs GCP-TOS).</summary>
    public string? OAuthClientId { get; set; }

    /// <summary>OpenID token carrying the email claim. Also part of the identity.</summary>
    public string? IdToken { get; set; }

    public string TokenType { get; set; } = "Bearer";

    // ---------------------------------------------------------------- access token (short-lived runtime credential)

    public string? AccessToken { get; set; }

    public DateTime? AccessTokenExpiresAt { get; set; }

    public DateTime? AccessTokenLastUsedAt { get; set; }

    /// <summary>True when CloudCode reports <c>usesGcpTos</c>, routing requests to prod <c>cloudcode-pa</c>.</summary>
    public bool UsesGcpTos { get; set; }

    /// <summary>True when the cached access token is missing or within a minute of expiring.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsRefresh =>
        string.IsNullOrEmpty(AccessToken)
        || AccessTokenExpiresAt is null
        || AccessTokenExpiresAt.Value <= DateTime.UtcNow.AddMinutes(1);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAccessTokenExpired =>
        string.IsNullOrEmpty(AccessToken)
        || AccessTokenExpiresAt is null
        || AccessTokenExpiresAt.Value <= DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRefreshTokenExpired =>
        RefreshTokenRevoked
        || (RefreshTokenExpiresAt.HasValue && RefreshTokenExpiresAt.Value <= DateTime.UtcNow);

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

    /// <summary>Experimental Gemini 4 Argon quota windows; stay null when the account lacks Argon entitlement.</summary>
    public double? Argon5hFraction { get; set; }
    public DateTime? Argon5hResetTime { get; set; }
    public double? Argon7dFraction { get; set; }
    public DateTime? Argon7dResetTime { get; set; }

    /// <summary>Structured field-matched quota pools returned by Google CloudCode.</summary>
    public List<QuotaGroupInfo> QuotaGroups { get; set; } = new();

    public string? AccountTierId { get; set; }
    public string? AccountTierName { get; set; }
    public string? QuotaSummaryDescription { get; set; }
    public string? QuotaLastError { get; set; }

    public DateTime? QuotaLastFetchedAt { get; set; }
    public string? QuotaRawText { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public double? GeminiRemainingFraction =>
        CombineMinFraction(Gemini5hFraction, Gemini7dFraction);

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? GeminiResetTime => Gemini5hResetTime ?? Gemini7dResetTime;

    [System.Text.Json.Serialization.JsonIgnore]
    public double? ClaudeGptRemainingFraction =>
        CombineMinFraction(Claude5hFraction, Claude7dFraction);

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? ClaudeGptResetTime => Claude5hResetTime ?? Claude7dResetTime;

    [System.Text.Json.Serialization.JsonIgnore]
    public double? ArgonRemainingFraction =>
        CombineMinFraction(Argon5hFraction, Argon7dFraction);

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? ArgonResetTime => Argon5hResetTime ?? Argon7dResetTime;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasArgonEntitlement =>
        Argon5hFraction.HasValue
        || Argon7dFraction.HasValue
        || QuotaGroups.Any(g => string.Equals(g.PoolKey, "argon", StringComparison.OrdinalIgnoreCase) && g.HasAnyQuota);

    /// <summary>
    /// Returns the field-matched quota pools, or reconstructs them from legacy scalar fields when
    /// viewing accounts persisted before structured quota storage existed.
    /// </summary>
    public IReadOnlyList<QuotaGroupInfo> GetDisplayQuotaGroups()
    {
        if (QuotaGroups.Count > 0)
        {
            return QuotaGroups;
        }

        var fallback = new List<QuotaGroupInfo>();
        if (Gemini5hFraction.HasValue || Gemini7dFraction.HasValue)
        {
            fallback.Add(new QuotaGroupInfo
            {
                PoolKey = "gemini",
                Name = "Gemini Models",
                FiveHourFraction = Gemini5hFraction,
                FiveHourResetTime = Gemini5hResetTime,
                WeeklyFraction = Gemini7dFraction,
                WeeklyResetTime = Gemini7dResetTime
            });
        }
        if (Claude5hFraction.HasValue || Claude7dFraction.HasValue)
        {
            fallback.Add(new QuotaGroupInfo
            {
                PoolKey = "claude_gpt",
                Name = "Claude and GPT models",
                FiveHourFraction = Claude5hFraction,
                FiveHourResetTime = Claude5hResetTime,
                WeeklyFraction = Claude7dFraction,
                WeeklyResetTime = Claude7dResetTime
            });
        }
        if (Argon5hFraction.HasValue || Argon7dFraction.HasValue)
        {
            fallback.Add(new QuotaGroupInfo
            {
                PoolKey = "argon",
                Name = "Gemini 4 Argon",
                FiveHourFraction = Argon5hFraction,
                FiveHourResetTime = Argon5hResetTime,
                WeeklyFraction = Argon7dFraction,
                WeeklyResetTime = Argon7dResetTime
            });
        }
        return fallback;
    }

    private static double? CombineMinFraction(double? fiveHour, double? weekly) =>
        (fiveHour.HasValue && weekly.HasValue)
            ? Math.Min(fiveHour.Value, weekly.Value)
            : fiveHour ?? weekly;

    /// <summary>Card label, e.g. "Gemini: 68%(5h)/36%(7d)  Claude: 100%(5h)/50%(7d)".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string QuotaCardSummaryText
    {
        get
        {
            var parts = new List<string>();
            foreach (var group in GetDisplayQuotaGroups())
            {
                var summary = group.FormatWindowsSummary();
                if (string.IsNullOrEmpty(summary)) continue;
                var shortLabel = group.PoolKey switch
                {
                    "gemini" => "Gemini",
                    "claude_gpt" => "Claude",
                    "argon" => "Argon",
                    _ => group.Name
                };
                parts.Add($"{shortLabel}: {summary}");
            }

            return parts.Count > 0 ? string.Join("  ", parts) : string.Empty;
        }
    }
}
