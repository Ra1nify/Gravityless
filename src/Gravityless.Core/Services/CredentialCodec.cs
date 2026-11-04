using System.Text.Json;
using System.Text.Json.Nodes;
using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

/// <summary>
/// A credential as it appears in a Windows credential blob or a <c>oauth_creds.json</c> file.
/// </summary>
public sealed class CapturedCredential
{
    public string? Email { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? IdToken { get; init; }
    public string? TokenType { get; init; }
    public DateTimeOffset? Expiry { get; init; }

    /// <summary>True when the identity half is present; without it the credential is unusable.</summary>
    public bool HasIdentity => !string.IsNullOrWhiteSpace(RefreshToken) && !string.IsNullOrWhiteSpace(Email);
}

/// <summary>
/// The one place that understands the on-disk and in-store credential formats.
///
/// Accounts are held as typed fields, so nothing outside this class needs to parse JSON to read
/// a token. Parsing used to happen on every quota probe and every refresh; the cost was small
/// per call but the duplication meant each caller could disagree about field names.
///
/// Two shapes are produced on demand:
///   - a Windows credential blob, for the global slot Antigravity reads at startup;
///   - <c>oauth_creds.json</c>, for an instance's own sandbox.
/// </summary>
public static class CredentialCodec
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Reads a credential from either shape. Accepts both the nested <c>token</c> object used by
    /// the Windows blob and the flat layout of <c>oauth_creds.json</c>.
    /// </summary>
    public static CapturedCredential? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var node = JsonNode.Parse(raw);
            if (node is not JsonObject root) return null;

            // The blob nests tokens under "token"; the file keeps them at the top level.
            var tokens = root["token"] as JsonObject ?? root;

            var accessToken = tokens["access_token"]?.ToString();
            var refreshToken = tokens["refresh_token"]?.ToString();
            var idToken = root["id_token"]?.ToString() ?? tokens["id_token"]?.ToString();
            var tokenType = tokens["token_type"]?.ToString();

            DateTimeOffset? expiry = null;

            // oauth_creds.json stores expiry as epoch milliseconds; the blob stores an ISO string.
            var expiryText = tokens["expiry"]?.ToString();
            if (!string.IsNullOrEmpty(expiryText) && DateTimeOffset.TryParse(expiryText, out var iso))
            {
                expiry = iso;
            }
            else if (long.TryParse(tokens["expiry_date"]?.ToString(), out var epochMs) && epochMs > 0)
            {
                expiry = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
            }

            var email = root["id_token_email"]?.ToString();
            if (string.IsNullOrEmpty(email)) email = ReadEmailFromIdToken(idToken);

            return new CapturedCredential
            {
                Email = email,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                IdToken = idToken,
                TokenType = tokenType,
                Expiry = expiry,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads just the identity from a blob; null when it carries none.</summary>
    public static string? ReadEmail(string? raw) => Parse(raw)?.Email;

    /// <summary>
    /// Builds the Windows credential blob Antigravity reads at startup.
    ///
    /// The identity fields come from the profile, which is immutable, so a rendered blob can
    /// never describe a different account than the one asked for.
    /// </summary>
    public static string RenderBlob(AccountProfile account)
    {
        var tokens = new JsonObject
        {
            ["access_token"] = account.AccessToken,
            ["refresh_token"] = account.RefreshToken,
            ["token_type"] = account.TokenType,
            ["expiry"] = account.AccessTokenExpiresAt?.ToString("o"),
        };

        var root = new JsonObject
        {
            ["token"] = tokens,
            ["auth_method"] = "oauth2",
            ["id_token_email"] = account.Email,
        };

        if (!string.IsNullOrEmpty(account.IdToken))
        {
            root["id_token"] = account.IdToken;
        }

        return root.ToJsonString();
    }

    /// <summary>Builds <c>oauth_creds.json</c> for an instance sandbox.</summary>
    public static string RenderOauthCreds(AccountProfile account)
    {
        var obj = new JsonObject
        {
            ["access_token"] = account.AccessToken,
            ["token_type"] = account.TokenType,
            ["refresh_token"] = account.RefreshToken,
            // Stored as epoch milliseconds, which is what the language server expects.
            ["expiry_date"] = account.AccessTokenExpiresAt is { } expires
                ? new DateTimeOffset(expires).ToUnixTimeMilliseconds()
                : 0,
            ["id_token"] = account.IdToken,
        };

        return obj.ToJsonString(Indented);
    }

    /// <summary>Extracts the email claim from an OpenID id token without validating the signature.</summary>
    public static string? ReadEmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            var node = JsonNode.Parse(json);
            return node?["email"]?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
