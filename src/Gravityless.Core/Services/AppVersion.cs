using System.Globalization;
using System.Reflection;
using System.Text;

namespace Gravityless.Core.Services;

/// <summary>
/// Version handling for the app's own releases.
///
/// Two forms describe the same build:
///
///     Version:      Display version string, e.g. 2.29.0.01
///     VersionCode:  Numeric version code, e.g. 229001
///
/// The code is what gets compared for updates (monotonically increasing integer);
/// the display form is shown in the UI.
/// </summary>
public static class AppVersion
{
    /// <summary>Assembly metadata key carrying the compiled version code.</summary>
    private const string VersionCodeKey = "VersionCode";

    /// <summary>
    /// Reads the version code baked in at build time. Returns 0 only when the assembly carries
    /// no metadata at all, which callers treat as "cannot compare".
    /// </summary>
    public static long ReadLocalVersionCode()
    {
        foreach (var asm in CandidateAssemblies())
        {
            try
            {
                var attr = asm
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == VersionCodeKey);

                if (attr != null && TryParseCode(attr.Value, out var code))
                {
                    return code;
                }
            }
            catch
            {
                // A dynamic or partially loaded assembly is not a reason to fail the check.
            }
        }

        return 0;
    }

    /// <summary>Entry assembly first, then this library.</summary>
    private static IEnumerable<Assembly> CandidateAssemblies()
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry != null) yield return entry;

        var self = typeof(AppVersion).Assembly;
        if (entry == null || entry != self) yield return self;
    }

    /// <summary>Reads the informational version, e.g. <c>2.29.0.01</c>.</summary>
    public static string ReadLocalVersionName()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip "+<commit sha>" build metadata.
                var cut = info.IndexOf('+');
                return (cut > 0 ? info[..cut] : info).Trim();
            }

            var v = asm.GetName().Version;
            return v == null ? "0.0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            return "0.0.0.0";
        }
    }

    /// <summary>
    /// Parses a version code from a code string, tag or display version.
    /// Accepts <c>229001</c>, <c>v229001</c>, <c>2.29.0.01</c>, etc.
    /// Returns false for anything non-positive.
    /// </summary>
    public static bool TryParseCode(string? text, out long code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var raw = text.Trim().TrimStart('v', 'V');

        // Drop pre-release and build metadata; neither is part of the code.
        var cut = raw.IndexOfAny(['-', '+']);
        if (cut > 0) raw = raw[..cut];
        if (raw.Length == 0) return false;

        // Try direct numeric parse first (e.g. "229001")
        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out code) && code > 0)
        {
            return true;
        }

        // If dotted (e.g. "2.29.0.01"), normalize into numeric code
        if (raw.Contains('.'))
        {
            var normalized = Normalize(raw);
            if (long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out code) && code > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Flattens a dotted version string into a numeric representation.
    /// E.g. <c>2.29.0.01</c> -> <c>229001</c>.
    /// </summary>
    public static string Normalize(string displayVersion)
    {
        if (string.IsNullOrWhiteSpace(displayVersion)) return string.Empty;

        var parts = displayVersion.Trim().TrimStart('v', 'V').Split('.');
        var sb = new StringBuilder();

        foreach (var p in parts)
        {
            if (!IsDigits(p)) return string.Empty;
            sb.Append(p);
        }

        return sb.ToString();
    }

    /// <summary>True when <paramref name="remote"/> is a newer build than <paramref name="local"/>.</summary>
    public static bool IsNewer(long remote, long local) => remote > local && remote > 0;

    private static bool IsDigits(string value)
    {
        if (value.Length == 0) return false;
        foreach (var ch in value)
        {
            if (ch is < '0' or > '9') return false;
        }
        return true;
    }
}
