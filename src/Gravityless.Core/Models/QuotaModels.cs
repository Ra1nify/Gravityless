using System.Text.Json.Serialization;

namespace Gravityless.Core.Models;

/// <summary>
/// One quota bucket entry returned by Google CloudCode (<c>retrieveUserQuotaSummary</c>,
/// <c>retrieveUserQuota</c>, or <c>fetchAvailableModels</c>).
/// </summary>
public class QuotaBucketInfo
{
    public string BucketId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Window { get; set; } = string.Empty;
    public double? RemainingFraction { get; set; }
    public long? RemainingAmount { get; set; }
    public DateTime? ResetTime { get; set; }
    public bool Disabled { get; set; }
}

/// <summary>
/// Structured field-matched quota pool (name, 5h, weekly, daily, and raw buckets).
/// Nullable fields stay <c>null</c> when the backend does not return that window or entitlement.
/// </summary>
public class QuotaGroupInfo
{
    /// <summary>Canonical pool key: "gemini", "claude_gpt", "argon", or normalized pool slug.</summary>
    public string PoolKey { get; set; } = string.Empty;

    /// <summary>Pool display name, e.g. "Gemini Models", "Claude and GPT models", "Gemini 4 Argon".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional backend description listing included models.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Source endpoint that supplied this group.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Matched 5-hour rolling window remaining fraction (0.0-1.0), or null when absent.</summary>
    public double? FiveHourFraction { get; set; }
    public DateTime? FiveHourResetTime { get; set; }

    /// <summary>Matched weekly (7d) ceiling remaining fraction (0.0-1.0), or null when absent.</summary>
    public double? WeeklyFraction { get; set; }
    public DateTime? WeeklyResetTime { get; set; }

    /// <summary>Matched daily limit remaining fraction (0.0-1.0), or null when absent.</summary>
    public double? DailyFraction { get; set; }
    public DateTime? DailyResetTime { get; set; }

    /// <summary>Raw matched buckets preserved for extra/custom windows (e.g. "5d").</summary>
    public List<QuotaBucketInfo> Buckets { get; set; } = new();

    [JsonIgnore]
    public double? EffectiveFraction
    {
        get
        {
            double? min = null;
            void Consider(double? v)
            {
                if (v.HasValue) min = min.HasValue ? Math.Min(min.Value, v.Value) : v.Value;
            }

            Consider(FiveHourFraction);
            Consider(DailyFraction);
            Consider(WeeklyFraction);
            foreach (var bucket in Buckets)
            {
                if (bucket.Disabled) continue;
                Consider(bucket.RemainingFraction);
            }
            return min;
        }
    }

    [JsonIgnore]
    public DateTime? PrimaryResetTime => FiveHourResetTime ?? WeeklyResetTime ?? DailyResetTime;

    [JsonIgnore]
    public bool HasAnyQuota =>
        FiveHourFraction.HasValue
        || WeeklyFraction.HasValue
        || DailyFraction.HasValue
        || Buckets.Any(b => !b.Disabled && b.RemainingFraction.HasValue);

    /// <summary>Formats non-null window fractions for compact list summaries.</summary>
    public string FormatWindowsSummary()
    {
        var segs = new List<string>();
        if (FiveHourFraction.HasValue)
        {
            segs.Add($"{(int)Math.Round(FiveHourFraction.Value * 100)}%(5h)");
        }
        if (DailyFraction.HasValue)
        {
            segs.Add($"{(int)Math.Round(DailyFraction.Value * 100)}%(daily)");
        }
        if (WeeklyFraction.HasValue)
        {
            segs.Add($"{(int)Math.Round(WeeklyFraction.Value * 100)}%(7d)");
        }

        // Preserve any non-standard windows (e.g. 5d) not already mapped into 5h/daily/weekly.
        foreach (var bucket in Buckets)
        {
            if (bucket.Disabled || !bucket.RemainingFraction.HasValue) continue;
            var w = bucket.Window.Trim().ToLowerInvariant();
            if (w is "5h" or "daily" or "1d" or "24h" or "weekly" or "7d") continue;
            var label = string.IsNullOrWhiteSpace(w) ? "quota" : w;
            var seg = $"{(int)Math.Round(bucket.RemainingFraction.Value * 100)}%({label})";
            if (!segs.Contains(seg, StringComparer.OrdinalIgnoreCase))
            {
                segs.Add(seg);
            }
        }

        return segs.Count > 0 ? string.Join("/", segs) : string.Empty;
    }
}

/// <summary>
/// Credit entitlement metadata returned inside <c>loadCodeAssist.paidTier.availableCredits</c>.
/// </summary>
public class AccountCreditInfo
{
    public string CreditType { get; set; } = string.Empty;
    public string MinimumCreditAmountForUsage { get; set; } = string.Empty;
}

/// <summary>
/// Per-model token window, thinking, and Cascade Checkpointer compaction parameters
/// extracted from CloudCode <c>fetchAvailableModels</c>.
/// </summary>
public class ModelContextSpec
{
    public string ModelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TagTitle { get; set; } = string.Empty;
    public string TagDescription { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public bool Recommended { get; set; }
    public long MaxTokens { get; set; }
    public long MaxOutputTokens { get; set; }

    public string CheckpointerStrategy { get; set; } = string.Empty;
    public long CheckpointerMaxTokens { get; set; }
    public long CheckpointerThreshold { get; set; }
    public long CheckpointerMaxOutputTokens { get; set; }
    public double? CheckpointerOverheadRatio { get; set; }
    public string CheckpointModel { get; set; } = string.Empty;

    public bool SupportsThinking { get; set; }
    public int? ThinkingLevel { get; set; }
    public int? MinThinkingBudget { get; set; }
    public bool SupportsImages { get; set; }
    public bool SupportsVideo { get; set; }

    public static string FormatK(long tokens)
    {
        if (tokens <= 0) return "—";
        if (tokens >= 1000)
        {
            double k = tokens / 1000.0;
            return tokens >= 100000
                ? $"{Math.Round(k):0}K"
                : $"{k:0.#}K";
        }
        return tokens.ToString();
    }
}
