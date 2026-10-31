namespace Gravityless.Core.Models;

public class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string InstallationId { get; set; } = Guid.NewGuid().ToString(); // Per-account device fingerprint
    public string ShortInstallationId => InstallationId.Length >= 8 ? InstallationId.Substring(0, 8) : InstallationId;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;

    // Raw credential blob captured from Windows Credential Manager (access/refresh tokens + id_token)
    public string? CredentialBlob { get; set; }

    // Exclusive-run lock state
    public bool IsRunning { get; set; }
    public string? BoundInstanceId { get; set; }

    // Path of the persisted credential copy, relative to the data root
    public string RelativeCredsPath { get; set; } = string.Empty;

    public int FailedRefreshAttempts { get; set; } = 0;

    // Quota remaining in 0.0-1.0 plus the 5h / 7d window reset instants
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

    // Legacy single-value quota members, not serialized
    [System.Text.Json.Serialization.JsonIgnore]
    public double? GeminiRemainingFraction => Gemini5hFraction ?? Gemini7dFraction;
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? GeminiResetTime => Gemini5hResetTime ?? Gemini7dResetTime;
    [System.Text.Json.Serialization.JsonIgnore]
    public double? ClaudeGptRemainingFraction => Claude5hFraction ?? Claude7dFraction;
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? ClaudeGptResetTime => Claude5hResetTime ?? Claude7dResetTime;

    // Card label, e.g. "Gemini: 68%(5h)/36%(7d)  Claude: 100%(5h)/50%(7d)"
    [System.Text.Json.Serialization.JsonIgnore]
    public string QuotaCardSummaryText
    {
        get
        {
            var parts = new List<string>();

            // Label each pool with the windows it actually has
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
