using System.Globalization;
using System.Reflection;

namespace Gravityless.Core.Services;

/// <summary>
/// Version handling for the app's own releases.
///
/// Two forms describe the same build:
///
///     version code   yyMM + major + minor   261102
///     display        yy.MM.major.minor      26.11.0.2
///
/// The code is what gets compared; the display form is only ever shown. Comparison is
/// numeric on purpose: ordinal string ordering happens to match while every code has the
/// same width, and silently breaks the first time a component gains a digit.
/// </summary>
public static class AppVersion
{
    /// <summary>Assembly metadata key carrying the compiled version code.</summary>
    private const string VersionCodeKey = "VersionCode";

    /// <summary>
    /// Reads the version code baked in at build time. Returns 0 only when the assembly carries
    /// no metadata at all, which callers treat as "cannot compare".
    ///
    /// The attribute is emitted on the entry assembly (where the csproj declares it), not on
    /// this library, so the lookup starts there and falls back to the calling assembly for
    /// hosts that load Core on its own.
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

    /// <summary>Reads the informational version, e.g. <c>26.11.0.2</c>.</summary>
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
    /// Parses a version code from a tag, a code string or a display version.
    /// Accepts <c>2611002</c>, <c>v2611002</c>, <c>26.11.0.2</c> and a trailing
    /// <c>-suffix</c> or <c>+metadata</c>. Returns false for anything non-positive.
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

        // A dotted form is the display version; flatten it.
        if (raw.Contains('.'))
        {
            raw = Normalize(raw);
        }

        // NumberStyles.None rejects signs, separators and culture-specific shapes, so
        // "26,110,02" and "-2611002" cannot slip through as valid codes.
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out code)
            && code > 0;
    }

    /// <summary>
    /// Flattens <c>yy.MM.major.minor</c> into <c>yyMM + major + minor</c>, mirroring the
    /// build-time concatenation. Returns an empty string when the shape does not match.
    /// </summary>
    public static string Normalize(string displayVersion)
    {
        if (string.IsNullOrWhiteSpace(displayVersion)) return string.Empty;

        var parts = displayVersion.Trim().TrimStart('v', 'V').Split('.');
        if (parts.Length != 4) return string.Empty;

        // The four segments are the year, month and the two counters; only the counters may
        // widen, and they concatenate without padding — the same rule the build uses.
        if (!IsDigits(parts[0]) || !IsDigits(parts[1]) || !IsDigits(parts[2]) || !IsDigits(parts[3]))
        {
            return string.Empty;
        }

        if (parts[0].Length != 2 || parts[1].Length != 2) return string.Empty;

        return parts[0] + parts[1] + parts[2] + parts[3];
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
