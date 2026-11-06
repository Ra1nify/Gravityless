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
        yield return Path.Combine(coreDir, "resources", "app", "dist", "main.js");
        yield return Path.Combine(coreDir, "resources", "app", "dist", "utils.js");
        yield return Path.Combine(coreDir, "Antigravity.exe");
    }

    private sealed record CandidateMatch(string ClientId, string ClientSecret, int Distance, int Tier);

    /// <summary>
    /// Scans candidate binaries and returns (clientId, clientSecret) combinations.
    /// Uses contextual proximity (思路 C): candidates found in the same chunk and closest
    /// to each other in bytecode/text offset are sorted first, ensuring the genuine pair
    /// is verified on the very first refresh attempt.
    /// </summary>
    public static List<(string ClientId, string ClientSecret)> FindCandidates(string coreDir)
    {
        var matchedPairs = new List<CandidateMatch>();
        var allIds = new HashSet<string>();
        var allSecrets = new HashSet<string>();

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

                // Tier 0: Same chunk proximity (the real pair is usually < 256 bytes apart)
                foreach (var id in chunkIds)
                {
                    foreach (var sec in chunkSecrets)
                    {
                        var dist = Math.Abs(id.Pos - sec.Pos);
                        int tier = dist <= 4096 ? 0 : 1;
                        matchedPairs.Add(new CandidateMatch(id.Value, sec.Value, dist + binaryIndex * 10000, tier));
                    }
                }

                chunkBaseOffset += text.Length;
            }

            // Tier 2: Same file, across different chunks
            if (fileIds.Count > 0 && fileSecrets.Count > 0)
            {
                foreach (var id in fileIds)
                {
                    foreach (var sec in fileSecrets)
                    {
                        var dist = Math.Abs(id.Pos - sec.Pos);
                        matchedPairs.Add(new CandidateMatch(id.Value, sec.Value, dist + binaryIndex * 50000, 2));
                    }
                }
            }

            binaryIndex++;
        }

        // Tier 3: Cross-binary fallback Cartesian product
        foreach (var id in allIds)
        {
            foreach (var sec in allSecrets)
            {
                matchedPairs.Add(new CandidateMatch(id, sec, int.MaxValue / 2, 3));
            }
        }

        // Order by Tier ASC, Distance ASC, and deduplicate
        var result = new List<(string ClientId, string ClientSecret)>();
        var seen = new HashSet<(string, string)>();

        foreach (var match in matchedPairs.OrderBy(m => m.Tier).ThenBy(m => m.Distance))
        {
            var key = (match.ClientId, match.ClientSecret);
            if (seen.Add(key))
            {
                result.Add(key);
            }
        }

        return result;
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
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;

        var candidates = FindCandidates(coreDir);

        using var client = AccountPoolService.CreateHttpClientForOAuth(proxy);

        foreach (var (clientId, clientSecret) in candidates)
        {
            if (await VerifyAsync(client, clientId, clientSecret, refreshToken, cancellationToken))
            {
                return new OAuthClientConfig
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret,
                    ExtractedAt = DateTime.UtcNow
                };
            }
        }

        return null;
    }

    private static async Task<bool> VerifyAsync(
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
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Contains("access_token", StringComparison.Ordinal);
        }
        catch
        {
            return false;
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
