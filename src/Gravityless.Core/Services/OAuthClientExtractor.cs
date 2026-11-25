using System.Text;
using System.Text.RegularExpressions;
using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

/// <summary>
/// Recovers the OAuth client pair used for token refresh from the installed Antigravity
/// binaries. The pair belongs to Google's desktop client and is not redistributed with
/// Gravityless, so it is read from the copy the user already has on disk.
/// </summary>
public static class OAuthClientExtractor
{
    private const string ClientSecretPrefix = "GOCSPX-";
    private const int ClientSecretLength = 35;

    private static readonly Regex ClientIdPattern = new(
        @"\d{6,}-[a-z0-9]{20,40}\.apps\.googleusercontent\.com",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Candidate binaries, in the order they are most likely to carry the client pair.
    /// The language server embeds the OAuth constants; main.js only forwards them.
    /// </summary>
    private static IEnumerable<string> CandidateBinaries(string coreDir)
    {
        yield return Path.Combine(coreDir, "resources", "bin", "language_server.exe");
        yield return Path.Combine(coreDir, "resources", "app", "dist", "remote", "signInUrls.js");
        yield return Path.Combine(coreDir, "resources", "app", "dist", "main.js");
        yield return Path.Combine(coreDir, "resources", "app", "dist", "utils.js");
        yield return Path.Combine(coreDir, "Antigravity.exe");
    }

    private sealed record CandidateMatch(string ClientId, string ClientSecret, int Distance, int Tier, int LoginPriority);

    /// <summary>
    /// Scans candidate binaries and returns (clientId, clientSecret) combinations.
    /// Prioritizes OAuth client IDs declared in <c>signInUrls.js</c> (Personal and Business
    /// login clients, excluding unrelated internal clients such as MCP), then ranks pairs
    /// by proximity and cross-binary fallback.
    /// </summary>
    public static List<(string ClientId, string ClientSecret)> FindCandidates(string coreDir)
    {
        var matchedPairs = new List<CandidateMatch>();
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        var allSecrets = new HashSet<string>(StringComparer.Ordinal);
        var signInClientIds = ExtractSignInClientIds(coreDir);

        int binaryIndex = 0;
        foreach (var binary in CandidateBinaries(coreDir))
        {
            if (!File.Exists(binary)) continue;

            var fileIds = new List<(string Value, int Pos)>();
            var fileSecrets = new List<(string Value, int Pos)>();

            int chunkBaseOffset = 0;
            foreach (var text in ReadTextChunks(binary))
            {
                var chunkIds = new List<(string Value, int Pos)>();
                var chunkSecrets = new List<(string Value, int Pos)>();

                foreach (Match m in ClientIdPattern.Matches(text))
                {
                    chunkIds.Add((m.Value, m.Index));
                    allIds.Add(m.Value);
                    fileIds.Add((m.Value, chunkBaseOffset + m.Index));
                }

                foreach (var s in ExtractSecretsWithPos(text))
                {
                    chunkSecrets.Add((s.Value, s.Pos));
                    allSecrets.Add(s.Value);
                    fileSecrets.Add((s.Value, chunkBaseOffset + s.Pos));
                }

                // Tier 0: Same chunk proximity (where JS files place pairs together)
                foreach (var id in chunkIds)
                {
                    int loginPriority = signInClientIds.Count == 0 || signInClientIds.Contains(id.Value) ? 0 : 1;
                    foreach (var sec in chunkSecrets)
                    {
                        var dist = Math.Abs(id.Pos - sec.Pos);
                        int tier = dist <= 4096 ? 0 : 1;
                        matchedPairs.Add(new CandidateMatch(id.Value, sec.Value, dist + binaryIndex * 10000, tier, loginPriority));
                    }
                }

                chunkBaseOffset += text.Length;
            }

            // Tier 2: Same file, across different chunks (Go binaries pack literals by length)
            if (fileIds.Count > 0 && fileSecrets.Count > 0)
            {
                foreach (var id in fileIds)
                {
                    int loginPriority = signInClientIds.Count == 0 || signInClientIds.Contains(id.Value) ? 0 : 1;
                    foreach (var sec in fileSecrets)
                    {
                        var dist = Math.Abs(id.Pos - sec.Pos);
                        matchedPairs.Add(new CandidateMatch(id.Value, sec.Value, dist + binaryIndex * 50000, 2, loginPriority));
                    }
                }
            }

            binaryIndex++;
        }

        // Tier 3: Cross-binary fallback Cartesian product (e.g., ID in signInUrls.js, secret in language_server.exe)
        foreach (var id in allIds)
        {
            int loginPriority = signInClientIds.Count == 0 || signInClientIds.Contains(id) ? 0 : 1;
            foreach (var sec in allSecrets)
            {
                matchedPairs.Add(new CandidateMatch(id, sec, int.MaxValue / 2, 3, loginPriority));
            }
        }

        // Prefer known login client IDs first, then Tier ASC, Distance ASC, and deduplicate
        var result = new List<(string ClientId, string ClientSecret)>();
        var seen = new HashSet<(string, string)>();

        foreach (var match in matchedPairs
                     .OrderBy(m => m.LoginPriority)
                     .ThenBy(m => m.Tier)
                     .ThenBy(m => m.Distance))
        {
            var key = (match.ClientId, match.ClientSecret);
            if (seen.Add(key))
            {
                result.Add(key);
            }
        }

        return result;
    }

    private static HashSet<string> ExtractSignInClientIds(string coreDir)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var signInUrlsPath = Path.Combine(coreDir, "resources", "app", "dist", "remote", "signInUrls.js");
            if (!File.Exists(signInUrlsPath)) return ids;

            var content = File.ReadAllText(signInUrlsPath);
            foreach (Match m in ClientIdPattern.Matches(content))
            {
                ids.Add(m.Value);
            }
        }
        catch
        {
        }
        return ids;
    }

    /// <summary>
    /// Picks the working pair by attempting a refresh with each candidate.
    /// Returns null when the core is missing or no combination is accepted.
    /// </summary>
    public static async Task<OAuthClientConfig?> ResolveAsync(
        string coreDir,
        string refreshToken,
        ProxyConfig? proxy,
        CancellationToken cancellationToken = default)
    {
        var (config, _) = await ResolveWithTokenResponseAsync(coreDir, refreshToken, proxy, cancellationToken)
            .ConfigureAwait(false);
        return config;
    }

    /// <summary>
    /// Resolves the matching OAuth client pair for <paramref name="refreshToken"/> and also
    /// returns the raw OAuth token JSON from the successful exchange so callers avoid a second
    /// redundant token round-trip.
    /// </summary>
    public static async Task<(OAuthClientConfig? Config, string? TokenResponseBody)> ResolveWithTokenResponseAsync(
        string coreDir,
        string refreshToken,
        ProxyConfig? proxy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return (null, null);

        var candidates = FindCandidates(coreDir);
        if (candidates.Count == 0) return (null, null);

        using var client = AccountPoolService.CreateHttpClientForOAuth(proxy);

        foreach (var (clientId, clientSecret) in candidates)
        {
            var (ok, body, isInvalidGrant) = await VerifyAsync(client, clientId, clientSecret, refreshToken, cancellationToken)
                .ConfigureAwait(false);
            if (ok)
            {
                return (new OAuthClientConfig
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret,
                    ExtractedAt = DateTime.UtcNow,
                    Verified = true
                }, body);
            }

            // If Google specifically returned invalid_grant, the client auth succeeded and the
            // refresh token itself is revoked/expired — retrying other clients won't help.
            if (isInvalidGrant)
            {
                return (null, body);
            }
        }

        return (null, null);
    }

    private static async Task<(bool Ok, string? Body, bool IsInvalidGrant)> VerifyAsync(
        HttpClient client,
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AccountPoolService.OAuthTokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken
                })
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                bool invalidGrant = body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
                return (false, body, invalidGrant);
            }

            bool ok = body.Contains("access_token", StringComparison.Ordinal);
            return (ok, body, false);
        }
        catch
        {
            return (false, null, false);
        }
    }

    /// <summary>
    /// Extracts fixed-length secrets along with their character offset in the chunk.
    /// </summary>
    private static IEnumerable<(string Value, int Pos)> ExtractSecretsWithPos(string text)
    {
        var index = 0;
        while (true)
        {
            index = text.IndexOf(ClientSecretPrefix, index, StringComparison.Ordinal);
            if (index < 0) yield break;

            if (index + ClientSecretLength <= text.Length)
            {
                var candidate = text.Substring(index, ClientSecretLength);
                if (candidate.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
                {
                    yield return (candidate, index);
                }
            }

            index += ClientSecretPrefix.Length;
        }
    }

    /// <summary>
    /// Reads a binary in overlapping chunks and returns each chunk as ASCII. The client
    /// constants are short, so a chunk boundary can only split one, never corrupt a match.
    /// </summary>
    private static IEnumerable<string> ReadTextChunks(string path)
    {
        const int chunkSize = 8 * 1024 * 1024;

        using var stream = File.OpenRead(path);
        var buffer = new byte[chunkSize + 256];
        var carry = 0;

        while (true)
        {
            var read = stream.Read(buffer, carry, chunkSize);
            if (read <= 0) break;

            var total = carry + read;
            yield return Encoding.ASCII.GetString(buffer, 0, total);

            // Keep the tail so a pattern straddling two reads is still seen once.
            carry = Math.Min(256, total);
            Array.Copy(buffer, total - carry, buffer, 0, carry);

            if (read < chunkSize) break;
        }
    }
}
