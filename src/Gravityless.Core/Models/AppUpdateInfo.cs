namespace Gravityless.Core.Models;

/// <summary>
/// The manifest published at <c>update.json</c> on the <c>info</c> branch.
///
/// Only <see cref="VersionCode"/> decides whether an update exists; the display fields are
/// for the UI. Downloads are listed per architecture so one manifest serves both builds.
/// </summary>
public class AppUpdateInfo
{
    /// <summary>yyMM + major + minor. Compared as a number against the local build.</summary>
    public long VersionCode { get; set; }

    /// <summary>Display version, e.g. <c>26.11.0.2</c>.</summary>
    public string VersionName { get; set; } = string.Empty;

    /// <summary>Per-architecture download entries.</summary>
    public List<AppUpdateAsset> Assets { get; set; } = [];

    /// <summary>Changelog for this version.</summary>
    public string ChangelogUrl { get; set; } = string.Empty;

    /// <summary>Same document as <see cref="ChangelogUrl"/>; preferred when present.</summary>
    public string? LatestChangelog { get; set; }
}

/// <summary>One downloadable installer.</summary>
public class AppUpdateAsset
{
    /// <summary><c>x64</c> or <c>arm64</c>.</summary>
    public string Arch { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-512 of the installer. Verified before it is executed.</summary>
    public string Sha512 { get; set; } = string.Empty;

    public long Size { get; set; }
}

/// <summary>
/// A single release's changes, forming a doubly linked list across versions.
///
/// The link is <c>null</c> at both ends. Clients walk it to page through history instead of
/// fetching every changelog up front.
/// </summary>
public class AppChangelog
{
    public long VersionCode { get; set; }

    public string VersionName { get; set; } = string.Empty;

    /// <summary>Release date, <c>YYYY-MM-DD</c>.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Absolute URL of the previous version, or null for the first release.</summary>
    public string? PreviousChangelog { get; set; }

    /// <summary>Absolute URL of the next version, or null for the newest release.</summary>
    public string? NextChangelog { get; set; }

    public List<AppChangelogEntry> Entries { get; set; } = [];
}

/// <summary>One change line.</summary>
public class AppChangelogEntry
{
    /// <summary>Commit type prefix, e.g. <c>feat</c>, <c>fix</c>. Empty when unsorted.</summary>
    public string Type { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;
}

/// <summary>
/// Why an update check produced nothing. Each value maps to a distinct user-facing message;
/// collapsing them would tell the user to fix their network when the server is at fault.
/// </summary>
public enum AppUpdateFailure
{
    /// <summary>No endpoint configured.</summary>
    NoUrl,

    /// <summary>The request never completed.</summary>
    Network,

    /// <summary>Blocked by policy or a local firewall.</summary>
    Blocked,

    /// <summary>HTTP 404: the manifest or a referenced document is missing.</summary>
    NotFound,

    /// <summary>Any other non-success status.</summary>
    Http,

    /// <summary>Response body was not the expected JSON shape.</summary>
    Malformed,

    /// <summary>Parsed, but a required field was unusable (version code ≤ 0, no asset for this arch).</summary>
    Invalid,
}

/// <summary>Result of a check: either the remote manifest, or the reason there is none.</summary>
public class AppUpdateResult
{
    public AppUpdateInfo? Info { get; init; }

    public AppUpdateFailure? Failure { get; init; }

    /// <summary>Detail for logs and error text; never localized.</summary>
    public string? Error { get; init; }

    public bool IsSuccess => Info != null;
}
