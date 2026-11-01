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

    /// <summary>
    /// Scans the given binaries and returns every (clientId, clientSecret) combination found.
    /// Combinations are unverified; callers must confirm one against a live refresh token.
    /// </summary>
    public static List<(string ClientId, string ClientSecret)> FindCandidates(string coreDir)
    {
        var ids = new List<string>();
        var secrets = new List<string>();

        foreach (var binary in CandidateBinaries(coreDir))
        {
            if (!File.Exists(binary)) continue;

            foreach (var text in ReadTextChunks(binary))
            {
                foreach (Match m in ClientIdPattern.Matches(text))
                {
                    if (!ids.Contains(m.Value)) ids.Add(m.Value);
                }

                foreach (Match m in ExtractSecrets(text))
                {
                    if (!secrets.Contains(m.Value)) secrets.Add(m.Value);
                }
            }
        }

        var result = new List<(string, string)>();
        foreach (var id in ids)
        {
            foreach (var secret in secrets)
            {
                result.Add((id, secret));
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

        // The correct pair is normally the first secret next to the first client id,
        // so the full cross product is rarely walked.
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
    /// Extracts fixed-length secrets. A trailing identifier usually follows the secret in
    /// the binary, so the match is bounded by length rather than by a word boundary.
    /// </summary>
    private static IEnumerable<Match> ExtractSecrets(string text)
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
                    yield return Regex.Match(candidate, Regex.Escape(candidate));
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
