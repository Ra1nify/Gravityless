using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Gravityless.Core.Models;
using Gravityless.Core.Native;

namespace Gravityless.Core.Services;

public class AccountPoolService
{
    /// <summary>
    /// How many token refreshes may be in flight at once. Chosen to hide latency on a normal
    /// pool without tripping the token endpoint's rate limit.
    /// </summary>
    private const int RefreshConcurrency = 4;

    private readonly string _baseDir;
    private readonly string _accountsJsonPath;
    private readonly string _accountsStorageDir;
    private readonly List<AccountProfile> _accounts = [];

    public IReadOnlyList<AccountProfile> Accounts => _accounts.AsReadOnly();

    /// <summary>
    /// Email currently stored in the global credential slot, or null when the slot is empty.
    /// The slot is shared by every instance, so this identifies which account would be
    /// displaced by the next launch.
    /// </summary>
    public string? GetSlotOwnerEmail()
    {
        var blob = CredentialManager.ReadCredential();
        return string.IsNullOrEmpty(blob) ? null : CredentialCodec.ReadEmail(blob);
    }

    public AccountPoolService(string? baseDir = null)
    {
        _baseDir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
        var dataDir = Path.Combine(_baseDir, "data");
        Directory.CreateDirectory(dataDir);

        _accountsJsonPath = Path.Combine(dataDir, "accounts.json");
        _accountsStorageDir = Path.Combine(dataDir, "accounts");
        Directory.CreateDirectory(_accountsStorageDir);

        LoadAccounts();
    }

    private void LoadAccounts()
    {
        _accounts.Clear();
        if (!File.Exists(_accountsJsonPath)) return;

        try
        {
            var json = File.ReadAllText(_accountsJsonPath);
            var list = JsonSerializer.Deserialize<List<AccountProfile>>(json);
            if (list == null) return;

            foreach (var acc in list)
            {
                // Accounts saved before credentials became typed carry an opaque JSON blob instead
                // of identity fields. There is nothing to migrate: the blob cannot be trusted to
                // describe the account it is filed under, which is exactly the bug this model
                // removes. Such records are dropped and the account must be captured again.
                if (string.IsNullOrEmpty(acc.RefreshToken))
                {
                    DiagLog.Swallowed("account.load-dropped", new InvalidOperationException(
                        $"account {acc.Email} has no refresh token; capture it again"));
                    continue;
                }

                _accounts.Add(acc);
            }
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("account.load", ex);
        }
    }

    /// <summary>Persists account state atomically. Throws on failure so callers can roll back.</summary>
    public void SaveAccounts() => JsonStateStore.Write(_accountsJsonPath, _accounts);

    /// <summary>Save that reports failure instead of throwing.</summary>
    public bool TrySaveAccounts() => JsonStateStore.TryWrite(_accountsJsonPath, _accounts);

    /// <summary>Leases an account to an instance; force breaks an existing binding held by another instance.</summary>
    public bool TryCheckOutAccount(string accountId, string instanceId, out string errorMessage, bool force = false)
    {
        errorMessage = string.Empty;
        var acc = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc == null)
        {
            errorMessage = "指定账号在号池中不存在";
            return false;
        }

        if (acc.IsRunning && acc.BoundInstanceId != instanceId)
        {
            if (force)
            {
                // Take over the lease from the previous owner.
                acc.IsRunning = false;
                acc.BoundInstanceId = null;
            }
            else
            {
                errorMessage = $"该账号正在被 [实例 ID: {acc.BoundInstanceId}] 运行中，严禁在多实例并发使用同一个账号！";
                return false;
            }
        }

        acc.IsRunning = true;
        acc.BoundInstanceId = instanceId;
        acc.LastActiveAt = DateTime.UtcNow;
        SaveAccounts();
        return true;
    }

    /// <summary>Releases the lease on instance exit and stores back the freshest token.</summary>
    public void CheckInAccount(string accountId, string instanceGeminiDir)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc == null) return;

        // Prefer the instance-local oauth_creds.json, refreshed by language_server while the instance ran.
        var instOauthCredsPath = Path.Combine(instanceGeminiDir, "oauth_creds.json");
        if (File.Exists(instOauthCredsPath))
        {
            try
            {
                // CredentialCodec accepts both the flat file and the nested blob layout, so the
                // file can be handed over as-is.
                var credJson = File.ReadAllText(instOauthCredsPath);
                AdoptTokensIfOwned(acc, credJson, "account.checkin-instance");
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("account.checkin-read", ex);
            }
        }
        else
        {
            // Copy the latest Windows Credential Manager token back into the account.
            // The credential slot is global, so only adopt a blob whose email matches this account.
            var latestCred = CredentialManager.ReadCredential();
            if (!string.IsNullOrEmpty(latestCred))
            {
                AdoptTokensIfOwned(acc, latestCred, "account.checkin-slot");
            }
        }

        acc.IsRunning = false;
        acc.BoundInstanceId = null;
        acc.LastActiveAt = DateTime.UtcNow;
        SaveAccounts();

        // Archive into the per-account storage directory.
        var accountDir = GetAccountStorageDir(acc.Id);
        InjectAccountToGeminiDir(accountDir, acc);
    }

    /// <summary>
    /// Takes the access token from a freshly observed credential, but only when that credential
    /// belongs to <paramref name="account"/>.
    ///
    /// The refresh token is never copied: it is the account's identity and is fixed at capture.
    /// A credential whose identity cannot be read is refused outright, because accepting it is
    /// how one account's tokens end up filed under another.
    /// </summary>
    private static bool AdoptTokensIfOwned(AccountProfile account, string? candidate, string context)
    {
        var cred = CredentialCodec.Parse(candidate);

        if (cred == null || string.IsNullOrEmpty(cred.Email))
        {
            DiagLog.Swallowed(context, new InvalidOperationException(
                $"credential has no identity; not adopted for {account.Email}"));
            return false;
        }

        if (!cred.Email.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
        {
            DiagLog.Swallowed(context, new InvalidOperationException(
                $"credential belongs to {cred.Email}; not adopted for {account.Email}"));
            return false;
        }

        if (!string.IsNullOrEmpty(cred.AccessToken))
        {
            account.AccessToken = cred.AccessToken;
            account.AccessTokenExpiresAt = cred.Expiry?.UtcDateTime;
        }

        return true;
    }

    /// <summary>Turns a freshly captured credential into a profile. Identity is set once, here.</summary>
    private static AccountProfile NewProfileFrom(CapturedCredential cred) => new()
    {
        Email = cred.Email!,
        DisplayName = cred.Email!.Split('@')[0],
        RefreshToken = cred.RefreshToken!,
        IdToken = cred.IdToken,
        TokenType = cred.TokenType ?? "Bearer",
        AccessToken = cred.AccessToken,
        AccessTokenExpiresAt = cred.Expiry?.UtcDateTime,
        InstallationId = Guid.NewGuid().ToString(),
    };

    /// <summary>Returns (and creates) the per-account credential directory.</summary>
    public string GetAccountStorageDir(string accountId)
    {
        var dir = Path.Combine(_accountsStorageDir, accountId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void RemoveAccount(string accountId)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc != null)
        {
            _accounts.Remove(acc);
            SaveAccounts();
        }
    }

    public void UpdateAccountDisplayName(string accountId, string newDisplayName)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc != null)
        {
            acc.DisplayName = newDisplayName.Trim();
            SaveAccounts();
        }
    }

    /// <summary>Clears the exclusive lock of one account.</summary>
    public void ForceUnlockAccount(string accountId)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc != null)
        {
            acc.IsRunning = false;
            acc.BoundInstanceId = null;
            SaveAccounts();
        }
    }

    /// <summary>Clears the exclusive lock of every account.</summary>
    public void ForceUnlockAllAccounts()
    {
        foreach (var acc in _accounts)
        {
            acc.IsRunning = false;
            acc.BoundInstanceId = null;
        }
        SaveAccounts();
    }

    public const string OAuthTokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string CloudCodeQuotaSummaryEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
    public const string CloudCodeQuotaEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    public const string CloudCodeAvailableModelsEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels";
    public const string CloudCodeAssistEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string FallbackAntigravityVersion = "2.35.1";
    public static string DefaultUserAgent => $"antigravity/{ResolveAntigravityVersion(null)}";

    /// <summary>
    /// OAuth client pair used for token refresh, read from <c>data/oauth_client.json</c>.
    /// The values belong to the Antigravity desktop client and are not redistributed here.
    /// Static so <see cref="ProxyService"/> can share it.
    /// </summary>
    public static (string ClientId, string ClientSecret) LoadOAuthClient(string? baseDir = null)
    {
        try
        {
            var root = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
            var path = Path.Combine(root, "data", "oauth_client.json");
            if (File.Exists(path))
            {
                var node = JsonNode.Parse(File.ReadAllText(path));
                var id = node?["client_id"]?.ToString();
                var secret = node?["client_secret"]?.ToString();
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret))
                {
                    return (id, secret);
                }
            }
        }
        catch { }

        return (string.Empty, string.Empty);
    }

    public static void SaveOAuthClient(OAuthClientConfig config, string? baseDir = null)
    {
        try
        {
            var root = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(dataDir);

            var node = new JsonObject
            {
                ["client_id"] = config.ClientId,
                ["client_secret"] = config.ClientSecret,
                ["extracted_at"] = config.ExtractedAt.ToString("o")
            };

            File.WriteAllText(
                Path.Combine(dataDir, "oauth_client.json"),
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("account.oauth-client-save", ex);
        }
    }

    /// <summary>
    /// Returns the cached OAuth pair, extracting it from the installed Antigravity binaries
    /// when the cache is missing. Verification needs a live refresh token, so it is skipped
    /// when none is supplied; the first candidate pair is cached unverified in that case.
    /// </summary>
    public async Task<OAuthClientConfig?> EnsureOAuthClientAsync(
        ProxyConfig? proxy = null,
        CancellationToken cancellationToken = default)
    {
        var cached = LoadOAuthClient(_baseDir);
        if (!string.IsNullOrEmpty(cached.ClientId) && !string.IsNullOrEmpty(cached.ClientSecret))
        {
            return new OAuthClientConfig { ClientId = cached.ClientId, ClientSecret = cached.ClientSecret };
        }

        var coreDir = Path.Combine(_baseDir, "core");
        if (!Directory.Exists(coreDir)) return null;

        // Any stored refresh token can certify a candidate pair.
        var refreshToken = _accounts
            .Select(a => a.RefreshToken)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        OAuthClientConfig? resolved = null;

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            resolved = await OAuthClientExtractor.ResolveAsync(coreDir, refreshToken, proxy, cancellationToken);
        }

        if (resolved == null)
        {
            // Nothing to verify against: cache the first candidate so a later refresh has
            // something to try. The refresh path reports failure honestly if it is wrong.
            var candidates = OAuthClientExtractor.FindCandidates(coreDir);
            if (candidates.Count > 0)
            {
                resolved = new OAuthClientConfig
                {
                    ClientId = candidates[0].ClientId,
                    ClientSecret = candidates[0].ClientSecret
                };
            }
        }

        if (resolved != null)
        {
            SaveOAuthClient(resolved, _baseDir);
        }

        return resolved;
    }

    /// <summary>Shared by the refresh path and the OAuth client extractor.</summary>
    public static HttpClient CreateHttpClientForOAuth(ProxyConfig? config) => CreateHttpClient(config);

    /// <summary>
    /// Delegates to the common factory so the bypass list and the Socks5 URI scheme are
    /// handled the same way for every request the app makes, not just the update checks.
    /// </summary>
    private static HttpClient CreateHttpClient(ProxyConfig? config) =>
        ProxyHttpClientFactory.Create(config, TimeSpan.FromSeconds(12));

    /// <summary>
    /// Guarantees a usable access token before the account is written to the credential slot.
    ///
    /// The slot write validates the rendered blob, and a profile with a null access token renders
    /// <c>"access_token": null</c>, which is refused — the launch would then run against whatever
    /// the slot already held, or against nothing. Refreshing here makes the write deterministic.
    /// </summary>
    public async Task<(bool ok, string message)> EnsureAccessTokenAsync(
        string accountId,
        ProxyConfig? proxyConfig = null,
        CancellationToken cancellationToken = default)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc == null) return (false, "找不到指定的账号");

        if (string.IsNullOrEmpty(acc.RefreshToken))
        {
            return (false, "账号缺少 Refresh Token，请重新捕获该账号");
        }

        // Already fresh: nothing to do, and no network call to wait on.
        if (!acc.NeedsRefresh && !string.IsNullOrEmpty(acc.AccessToken))
        {
            return (true, string.Empty);
        }

        return await RefreshTokenAsync(accountId, proxyConfig, autoRetry: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Refreshes the account access token; up to 3 attempts, 10s apart, when autoRetry is set.</summary>
    public async Task<(bool success, string message)> RefreshTokenAsync(
        string accountId,
        ProxyConfig? proxyConfig = null,
        bool autoRetry = true,
        CancellationToken cancellationToken = default)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc == null)
        {
            return (false, "找不到指定的账号");
        }

        var refreshToken = acc.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return (false, "账号未包含 Refresh Token 凭据，无法自动刷新");
        }

        int maxAttempts = autoRetry ? 3 : 1;

        // The file path is a cache populated ahead of time (startup and after each capture).
        // Reading it here keeps the refresh path free of binary scanning, which takes seconds.
        var oauthClient = LoadOAuthClient(_baseDir);
        if (string.IsNullOrEmpty(oauthClient.ClientId) || string.IsNullOrEmpty(oauthClient.ClientSecret))
        {
            return (false, "缺少 OAuth 客户端配置，请先在概览页同步核心后重启应用");
        }

        using var client = CreateHttpClient(proxyConfig);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, OAuthTokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = oauthClient.ClientId,
                        ["client_secret"] = oauthClient.ClientSecret,
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refreshToken
                    })
                };

                using var response = await client.SendAsync(request, cancellationToken);
                var respBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var respNode = JsonNode.Parse(respBody);
                    var newAccessToken = respNode?["access_token"]?.ToString();
                    var expiresIn = respNode?["expires_in"]?.GetValue<int>() ?? 3599;

                    if (!string.IsNullOrEmpty(newAccessToken))
                    {
                        // The refresh token identifies the account, so a token minted from it
                        // cannot belong anywhere else; storing it is a plain field assignment.
                        acc.AccessToken = newAccessToken;
                        acc.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
                        acc.FailedRefreshAttempts = 0;
                        acc.LastActiveAt = DateTime.UtcNow;

                        // Also refresh the 5h/weekly model quota groups and reset times.
                        bool quotaOk = false;
                        string quotaMsg = string.Empty;
                        try
                        {
                            (quotaOk, quotaMsg) = await FetchAndSaveAccountQuotaInternalAsync(acc, newAccessToken, client, cancellationToken);
                        }
                        catch (Exception qEx)
                        {
                            quotaMsg = qEx.Message;
                            acc.QuotaLastError = qEx.Message;
                        }

                        SaveAccounts();

                        if (!string.IsNullOrEmpty(acc.BoundInstanceId))
                        {
                            var instDir = Path.Combine(_baseDir, "data", "instances", acc.BoundInstanceId);
                            InjectAccountToGeminiDir(instDir, acc);
                        }

                        var accDir = GetAccountStorageDir(acc.Id);
                        InjectAccountToGeminiDir(accDir, acc);

                        return quotaOk
                            ? (true, "Token 刷新成功！模型配额已同步就绪。")
                            : (true, $"Token 刷新成功，但配额查询失败: {quotaMsg}");
                    }
                }
                else
                {
                    if (respBody.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
                    {
                        acc.FailedRefreshAttempts = 3;
                        SaveAccounts();
                        return (false, "凭据已失效或被撤销 (invalid_grant)，需重新登录");
                    }

                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(10000, cancellationToken);
                        continue;
                    }

                    acc.FailedRefreshAttempts = Math.Min(3, acc.FailedRefreshAttempts + 1);
                    SaveAccounts();
                    return (false, $"刷新失败 (HTTP {(int)response.StatusCode}): {respBody}");
                }
            }
            catch (Exception ex)
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(10000, cancellationToken);
                    continue;
                }

                acc.FailedRefreshAttempts = Math.Min(3, acc.FailedRefreshAttempts + 1);
                SaveAccounts();
                return (false, $"网络请求异常: {ex.Message}");
            }
        }

        return (false, "刷新失败（已重试 3 次仍未成功）");
    }

    /// <summary>Refreshes the token of every account in the pool (single attempt each).</summary>
    /// <summary>
    /// Refreshes every account in turn. Each account exchanges its refresh token for a fresh
    /// access token and then re-reads its quota, so a completed run leaves both current.
    /// </summary>
    /// <param name="onProgress">Receives (completed, total, email) after each account finishes.</param>
    public async Task<(int successCount, int failedCount)> RefreshAllTokensAsync(
        ProxyConfig? proxyConfig = null,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var accounts = _accounts.ToList();
        if (accounts.Count == 0) return (0, 0);

        int success = 0;
        int failed = 0;
        int completed = 0;
        var counterLock = new object();

        // A few refreshes run at once so a long list does not spend its whole time waiting on
        // latency, but the degree is capped: the token endpoint rate-limits, and firing twenty
        // requests together would turn a working batch into a run of 429s.
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = RefreshConcurrency,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(accounts, options, async (acc, ct) =>
        {
            // autoRetry stays on: a transient network fault should not mark an account dead
            // for the rest of the batch.
            var res = await RefreshTokenAsync(acc.Id, proxyConfig, autoRetry: true, ct)
                .ConfigureAwait(false);

            lock (counterLock)
            {
                if (res.success) success++;
                else failed++;
                completed++;
                onProgress?.Invoke(completed, accounts.Count, acc.Email);
            }
        }).ConfigureAwait(false);

        return (success, failed);
    }

    /// <summary>One account whose stored credentials do not belong to it.</summary>
    public class CredentialMismatch
    {
        public string AccountId { get; set; } = string.Empty;
        public string AccountEmail { get; set; } = string.Empty;

        /// <summary>Address the refresh token actually belongs to, when it could be read.</summary>
        public string? ActualEmail { get; set; }

        /// <summary>Short machine-readable reason: no-identity, email-mismatch, refresh-failed.</summary>
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Checks every account's stored identity without changing anything.
    ///
    /// Two accounts can end up sharing one token when a blob from one is adopted by the other.
    /// The damage is not repairable in place — the refresh token is the identity — so this only
    /// reports which accounts need to be captured again.
    /// </summary>
    /// <param name="onProgress">Receives (checked, total, email) as each account is verified.</param>
    public async Task<List<CredentialMismatch>> VerifyCredentialsAsync(
        ProxyConfig? proxyConfig = null,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var mismatches = new List<CredentialMismatch>();
        var accounts = _accounts.ToList();
        if (accounts.Count == 0) return mismatches;

        var oauthClient = LoadOAuthClient(_baseDir);
        var counterLock = new object();
        int checkedCount = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = RefreshConcurrency,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(accounts, options, async (acc, ct) =>
        {
            var mismatch = await VerifyOneAsync(acc, oauthClient, proxyConfig, ct).ConfigureAwait(false);

            lock (counterLock)
            {
                if (mismatch != null) mismatches.Add(mismatch);
                checkedCount++;
                onProgress?.Invoke(checkedCount, accounts.Count, acc.Email);
            }
        }).ConfigureAwait(false);

        // Stable order so the report does not shuffle between runs.
        mismatches.Sort((a, b) => string.CompareOrdinal(a.AccountEmail, b.AccountEmail));
        return mismatches;
    }

    private async Task<CredentialMismatch?> VerifyOneAsync(
        AccountProfile acc,
        (string ClientId, string ClientSecret) oauthClient,
        ProxyConfig? proxyConfig,
        CancellationToken cancellationToken)
    {
        CredentialMismatch Flag(string reason, string? actual) => new()
        {
            AccountId = acc.Id,
            AccountEmail = acc.Email,
            ActualEmail = actual,
            Reason = reason,
        };

        var refreshToken = acc.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Flag("no-identity", null);
        }

        // A stored id token is a claim about who this is; it must agree with the account record.
        var idTokenEmail = CredentialCodec.ReadEmailFromIdToken(acc.IdToken);
        if (!string.IsNullOrEmpty(idTokenEmail)
            && !idTokenEmail.Equals(acc.Email, StringComparison.OrdinalIgnoreCase))
        {
            return Flag("email-mismatch", idTokenEmail);
        }

        // The stored identity matches, but the refresh token itself may still belong elsewhere.
        // Exchanging it and reading the returned id_token is the only way to tell.
        if (string.IsNullOrEmpty(oauthClient.ClientId) || string.IsNullOrEmpty(oauthClient.ClientSecret))
        {
            return null;
        }

        try
        {
            using var client = CreateHttpClient(proxyConfig);
            using var request = new HttpRequestMessage(HttpMethod.Post, OAuthTokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = oauthClient.ClientId,
                    ["client_secret"] = oauthClient.ClientSecret,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                }),
            };

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;   // Network or revocation: not identity damage.

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(body);

            var tokenEmail = node?["id_token_email"]?.ToString();
            if (string.IsNullOrEmpty(tokenEmail))
            {
                tokenEmail = CredentialCodec.ReadEmailFromIdToken(node?["id_token"]?.ToString());
            }

            if (string.IsNullOrEmpty(tokenEmail)) return null;

            return tokenEmail.Equals(acc.Email, StringComparison.OrdinalIgnoreCase)
                ? null
                : Flag("email-mismatch", tokenEmail);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the Antigravity client version sent in the <c>User-Agent</c> header.
    /// Reads <c>core/resources/app/package.json</c> when present and compares against the
    /// compiled <see cref="AppVersion"/>, ensuring Google CloudCode never strips newer quota
    /// groups (such as Gemini or Argon) due to an outdated or test fallback version string.
    /// </summary>
    public static string ResolveAntigravityVersion(string? baseDir = null)
    {
        Version? bestVer = null;
        string? bestStr = null;

        void Consider(string? raw)
        {
            if (TryParseThreePartVersion(raw, out var ver, out var normalized) && ver!.Major >= 2)
            {
                if (bestVer == null || ver > bestVer)
                {
                    bestVer = ver;
                    bestStr = normalized;
                }
            }
        }

        Consider(AppVersion.ReadOfficialVersion());

        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            try
            {
                var pkgPath = Path.Combine(baseDir, "core", "resources", "app", "package.json");
                if (File.Exists(pkgPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pkgPath));
                    if (doc.RootElement.TryGetProperty("version", out var vProp))
                    {
                        Consider(vProp.GetString());
                    }
                }
            }
            catch { }
        }

        Consider(FallbackAntigravityVersion);
        return bestStr ?? FallbackAntigravityVersion;
    }

    private string GetUserAgent() => $"antigravity/{ResolveAntigravityVersion(_baseDir)}";

    private static bool TryParseThreePartVersion(string? raw, out Version? parsed, out string normalized)
    {
        parsed = null;
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Trim().TrimStart('v', 'V').Split('.');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch))
        {
            return false;
        }

        parsed = new Version(major, minor, patch);
        normalized = $"{major}.{minor}.{patch}";
        return true;
    }

    /// <summary>Queries the quota gateway for structured pool quotas, windows, and experimental Argon status.</summary>
    public async Task<(bool success, string message)> FetchAccountQuotaAsync(
        string accountId,
        ProxyConfig? proxyConfig = null,
        CancellationToken cancellationToken = default)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc == null) return (false, "找不到指定的账号");

        bool justRefreshed = false;
        // Ensure token is fresh before querying quota (Google OAuth access tokens expire after 1h).
        if (string.IsNullOrWhiteSpace(acc.AccessToken) ||
            (acc.AccessTokenExpiresAt.HasValue && acc.AccessTokenExpiresAt.Value <= DateTime.UtcNow.AddMinutes(2)))
        {
            var (refreshOk, refreshMsg) = await RefreshTokenAsync(accountId, proxyConfig, autoRetry: true, cancellationToken);
            if (!refreshOk) return (false, $"刷新凭据失败: {refreshMsg}");
            justRefreshed = true;
        }

        // RefreshTokenAsync already fetched quota; avoid duplicate HTTP round-trips when it succeeded cleanly.
        if (justRefreshed && string.IsNullOrEmpty(acc.QuotaLastError))
        {
            return (true, acc.HasArgonEntitlement ? "模型配额同步成功（含 Argon）" : "模型配额同步成功");
        }

        var accessToken = acc.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return (false, "账号未包含有效的 Access Token，请先刷新 Token");
        }

        using var client = CreateHttpClient(proxyConfig);
        var result = await FetchAndSaveAccountQuotaInternalAsync(acc, accessToken, client, cancellationToken);

        // If unauthorized (401), access token might have been invalidated; refresh and retry once.
        if (!result.success && result.message.Contains("401", StringComparison.OrdinalIgnoreCase))
        {
            var (refreshOk, refreshMsg) = await RefreshTokenAsync(accountId, proxyConfig, autoRetry: true, cancellationToken);
            if (!refreshOk) return (false, $"刷新凭据失败: {refreshMsg}");
            if (string.IsNullOrEmpty(acc.QuotaLastError))
            {
                return (true, acc.HasArgonEntitlement ? "模型配额同步成功（含 Argon）" : "模型配额同步成功");
            }

            if (!string.IsNullOrWhiteSpace(acc.AccessToken))
            {
                result = await FetchAndSaveAccountQuotaInternalAsync(acc, acc.AccessToken, client, cancellationToken);
            }
        }

        return result;
    }

    private async Task<(bool success, string message)> FetchAndSaveAccountQuotaInternalAsync(
        AccountProfile acc,
        string accessToken,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            // 1. Discover companion project ID and tier metadata (helps summary + exposes tier).
            var assistInfo = await DiscoverCloudCodeInfoAsync(client, accessToken, cancellationToken);
            var projectId = assistInfo.ProjectId ?? string.Empty;
            var projectPayload = string.IsNullOrEmpty(projectId)
                ? "{}"
                : JsonSerializer.Serialize(new { project = projectId });

            // 2. Primary query: retrieveUserQuotaSummary returns grouped 5h / weekly / daily limits.
            var (success, statusCode, body, errDetail) = await SendCloudCodePostAsync(
                client, CloudCodeQuotaSummaryEndpoint, accessToken, projectPayload, cancellationToken);

            // If project payload failed unexpectedly, retry with empty payload (or discover if omitted).
            if (!success && !string.IsNullOrEmpty(projectId) && statusCode == 400)
            {
                (success, statusCode, body, errDetail) = await SendCloudCodePostAsync(
                    client, CloudCodeQuotaSummaryEndpoint, accessToken, "{}", cancellationToken);
            }

            AccountQuotaParsedResult? parsed = null;
            if (success && !string.IsNullOrWhiteSpace(body))
            {
                parsed = ParseQuotaResponse(body, sourceEndpoint: "retrieveUserQuotaSummary");
            }

            // 3. Fallback to retrieveUserQuota or fetchAvailableModels if summary failed or yielded no pools.
            if (!success || parsed == null || parsed.Groups.Count == 0)
            {
                var (qOk, _, qBody, qErr) = await SendCloudCodePostAsync(
                    client, CloudCodeQuotaEndpoint, accessToken, projectPayload, cancellationToken);
                if (qOk && !string.IsNullOrWhiteSpace(qBody))
                {
                    var qParsed = ParseQuotaResponse(qBody, sourceEndpoint: "retrieveUserQuota");
                    if (qParsed.Groups.Count > 0)
                    {
                        success = true;
                        body = qBody;
                        parsed = qParsed;
                    }
                }
                else if (!success && string.IsNullOrEmpty(errDetail))
                {
                    errDetail = qErr;
                }

                if (!success || parsed == null || parsed.Groups.Count == 0)
                {
                    var (mOk, _, mBody, mErr) = await SendCloudCodePostAsync(
                        client, CloudCodeAvailableModelsEndpoint, accessToken, projectPayload, cancellationToken);
                    if (mOk && !string.IsNullOrWhiteSpace(mBody))
                    {
                        var mParsed = ParseQuotaResponse(mBody, sourceEndpoint: "fetchAvailableModels");
                        if (mParsed.Groups.Count > 0)
                        {
                            success = true;
                            body = mBody;
                            parsed = mParsed;
                        }
                    }
                    else if (!success && string.IsNullOrEmpty(errDetail))
                    {
                        errDetail = mErr;
                    }
                }
            }

            if (!success || parsed == null)
            {
                var failureReason = string.IsNullOrWhiteSpace(errDetail) ? body : errDetail;
                acc.QuotaLastError = failureReason;
                SaveAccounts();
                return (false, $"配额查询未就绪: {failureReason}");
            }

            // 4. Experimental Argon probe: when summary lacks an Argon pool, inspect fetchAvailableModels
            //    (non-fatal; accounts without Argon entitlement keep Argon* = null cleanly).
            if (parsed.ArgonGroup == null)
            {
                var (mOk, _, mBody, _) = await SendCloudCodePostAsync(
                    client, CloudCodeAvailableModelsEndpoint, accessToken, projectPayload, cancellationToken, maxAttempts: 1);
                if (mOk && !string.IsNullOrWhiteSpace(mBody))
                {
                    MergeArgonQuotaFromPayload(parsed, mBody, "fetchAvailableModels");
                }

                if (parsed.ArgonGroup == null)
                {
                    var (qOk, _, qBody, _) = await SendCloudCodePostAsync(
                        client, CloudCodeQuotaEndpoint, accessToken, projectPayload, cancellationToken, maxAttempts: 1);
                    if (qOk && !string.IsNullOrWhiteSpace(qBody))
                    {
                        MergeArgonQuotaFromPayload(parsed, qBody, "retrieveUserQuota");
                    }
                }
            }

            // 5. Apply structured pools + backward-compatible scalar fields (strictly isolated per pool).
            parsed.SyncLegacyScalarProperties();
            acc.QuotaGroups = parsed.Groups;

            acc.Gemini5hFraction = parsed.Gemini5hFraction;
            acc.Gemini5hResetTime = parsed.Gemini5hReset;
            acc.Gemini7dFraction = parsed.Gemini7dFraction;
            acc.Gemini7dResetTime = parsed.Gemini7dReset;

            acc.Claude5hFraction = parsed.Claude5hFraction;
            acc.Claude5hResetTime = parsed.Claude5hReset;
            acc.Claude7dFraction = parsed.Claude7dFraction;
            acc.Claude7dResetTime = parsed.Claude7dReset;

            acc.Argon5hFraction = parsed.Argon5hFraction;
            acc.Argon5hResetTime = parsed.Argon5hReset;
            acc.Argon7dFraction = parsed.Argon7dFraction;
            acc.Argon7dResetTime = parsed.Argon7dReset;

            if (!string.IsNullOrWhiteSpace(assistInfo.TierId))
            {
                acc.AccountTierId = assistInfo.TierId;
            }
            if (!string.IsNullOrWhiteSpace(assistInfo.TierName))
            {
                acc.AccountTierName = assistInfo.TierName;
            }
            if (!string.IsNullOrWhiteSpace(parsed.Description))
            {
                acc.QuotaSummaryDescription = parsed.Description;
            }

            acc.QuotaLastError = null;
            acc.QuotaLastFetchedAt = DateTime.UtcNow;
            acc.QuotaRawText = body;

            SaveAccounts();
            return (true, parsed.ArgonGroup != null ? "模型配额同步成功（含 Argon）" : "模型配额同步成功");
        }
        catch (Exception ex)
        {
            acc.QuotaLastError = ex.Message;
            SaveAccounts();
            return (false, $"配额网络请求异常: {ex.Message}");
        }
    }

    private async Task<(bool success, int statusCode, string body, string errorDetail)> SendCloudCodePostAsync(
        HttpClient client,
        string endpoint,
        string accessToken,
        string payloadJson,
        CancellationToken cancellationToken,
        int maxAttempts = 2)
    {
        var userAgent = GetUserAgent();
        string lastError = string.Empty;
        int lastStatus = 0;
        string lastBody = string.Empty;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                // Clear factory default ("Gravityless") so CloudCode only receives antigravity/<ver>.
                req.Headers.UserAgent.Clear();
                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                req.Content = new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json");

                using var resp = await client.SendAsync(req, cancellationToken);
                lastStatus = (int)resp.StatusCode;
                lastBody = await resp.Content.ReadAsStringAsync(cancellationToken);

                if (resp.IsSuccessStatusCode)
                {
                    return (true, lastStatus, lastBody, string.Empty);
                }

                lastError = $"HTTP {lastStatus}: {(lastBody.Length > 200 ? lastBody[..200] : lastBody)}";
                bool transientHttp = lastStatus == 429 || lastStatus >= 500;
                if (!transientHttp || attempt >= maxAttempts)
                {
                    return (false, lastStatus, lastBody, lastError);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                if (attempt >= maxAttempts)
                {
                    return (false, lastStatus, lastBody, lastError);
                }
            }

            await Task.Delay(250 * attempt, cancellationToken);
        }

        return (false, lastStatus, lastBody, lastError);
    }

    private sealed record CloudCodeAssistInfo(string? ProjectId, string? TierId, string? TierName);

    private async Task<CloudCodeAssistInfo> DiscoverCloudCodeInfoAsync(
        HttpClient client,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var (ok, _, body, _) = await SendCloudCodePostAsync(
                client,
                CloudCodeAssistEndpoint,
                accessToken,
                "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}",
                cancellationToken);
            if (!ok || string.IsNullOrWhiteSpace(body))
            {
                return new CloudCodeAssistInfo(null, null, null);
            }

            var node = JsonNode.Parse(body);
            var projectId = node?["cloudaicompanionProject"]?.ToString()
                ?? node?["project"]?.ToString()
                ?? node?["projectId"]?.ToString();

            var tierNode = node?["paidTier"] ?? node?["currentTier"];
            var tierId = tierNode?["id"]?.ToString();
            var tierName = tierNode?["name"]?.ToString();
            return new CloudCodeAssistInfo(projectId, tierId, tierName);
        }
        catch
        {
            return new CloudCodeAssistInfo(null, null, null);
        }
    }

    /// <summary>
    /// Parses CloudCode quota JSON into field-matched <see cref="QuotaGroupInfo"/> entries
    /// (<c>Name</c>, <c>5h</c>, <c>Weekly</c>, <c>Daily</c>, extra windows) and populates
    /// backward-compatible scalar properties.
    /// </summary>
    public static AccountQuotaParsedResult ParseQuotaResponse(string json, string sourceEndpoint = "retrieveUserQuotaSummary")
    {
        var result = new AccountQuotaParsedResult();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            var root = JsonNode.Parse(json);
            if (root == null) return result;

            var utcNow = DateTime.UtcNow;
            result.Description = root["description"]?.ToString();

            // Path A: grouped summary mode (retrieveUserQuotaSummary).
            var groupsNode = root["groups"] as JsonArray
                ?? root["command"]?["data"]?["groups"] as JsonArray
                ?? root["data"]?["groups"] as JsonArray;

            if (groupsNode != null && groupsNode.Count > 0)
            {
                foreach (var grp in groupsNode)
                {
                    if (grp == null) continue;
                    var rawName = grp["displayName"]?.ToString()
                        ?? grp["name"]?.ToString()
                        ?? grp["groupId"]?.ToString()
                        ?? grp["id"]?.ToString()
                        ?? string.Empty;
                    var rawDesc = grp["description"]?.ToString() ?? string.Empty;

                    var buckets = grp["buckets"] as JsonArray;
                    var bucketHint = buckets == null
                        ? string.Empty
                        : string.Join(" ", buckets
                            .OfType<JsonNode>()
                            .Select(b => b["bucketId"]?.ToString() ?? b["modelId"]?.ToString() ?? b["id"]?.ToString() ?? string.Empty));

                    var (poolKey, canonicalName) = ClassifyPool(rawName, rawDesc, bucketHint);
                    var group = GetOrCreateGroup(result, poolKey, canonicalName, rawDesc, sourceEndpoint);

                    if (buckets != null)
                    {
                        foreach (var b in buckets)
                        {
                            if (b == null) continue;
                            var (bucketInfo, windowKind) = ExtractBucketInfo(b, utcNow);
                            AddBucketToGroup(group, bucketInfo, windowKind);
                        }
                    }
                }
            }

            // Optional flat top-level buckets[] inside summary or retrieveUserQuota response.
            var bucketsNode = root["buckets"] as JsonArray
                ?? root["userQuota"]?["buckets"] as JsonArray;

            if (bucketsNode != null && bucketsNode.Count > 0)
            {
                foreach (var item in bucketsNode)
                {
                    if (item == null) continue;
                    var modelId = (item["modelId"]?.ToString()
                        ?? item["model_id"]?.ToString()
                        ?? item["bucketId"]?.ToString()
                        ?? item["id"]?.ToString()
                        ?? string.Empty).Trim();

                    // Skip internal completion (tab_*) and probe (chat_*) buckets.
                    if (ShouldSkipInternalModel(modelId)) continue;

                    var itemDisplayName = item["displayName"]?.ToString() ?? string.Empty;
                    var (poolKey, canonicalName) = ClassifyPool(modelId, itemDisplayName, modelId);
                    var group = GetOrCreateGroup(result, poolKey, canonicalName, string.Empty, sourceEndpoint);

                    var (bucketInfo, windowKind) = ExtractBucketInfo(item, utcNow);
                    AddBucketToGroup(group, bucketInfo, windowKind);
                }
            }

            // Path C: fetchAvailableModels response (models map -> per-model quotaInfo).
            if (root["models"] is JsonObject modelsObj)
            {
                ParseModelsObjectIntoResult(result, modelsObj, utcNow, argonOnly: false, sourceEndpoint: sourceEndpoint);
            }
        }
        catch { }

        result.SyncLegacyScalarProperties();
        return result;
    }

    /// <summary>
    /// Extracts only experimental Argon entries from a fallback <c>fetchAvailableModels</c> or
    /// <c>retrieveUserQuota</c> payload without disturbing existing summary groups.
    /// </summary>
    public static void MergeArgonQuotaFromPayload(
        AccountQuotaParsedResult result,
        string payloadJson,
        string sourceEndpoint)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return;
        try
        {
            var root = JsonNode.Parse(payloadJson);
            if (root == null) return;

            var utcNow = DateTime.UtcNow;

            if (root["groups"] is JsonArray groupsNode)
            {
                foreach (var grp in groupsNode)
                {
                    if (grp == null) continue;
                    var rawName = grp["displayName"]?.ToString() ?? grp["name"]?.ToString() ?? string.Empty;
                    var rawDesc = grp["description"]?.ToString() ?? string.Empty;
                    var buckets = grp["buckets"] as JsonArray;
                    var hint = buckets == null
                        ? string.Empty
                        : string.Join(" ", buckets.OfType<JsonNode>().Select(b => b["bucketId"]?.ToString() ?? b["modelId"]?.ToString() ?? string.Empty));

                    var (poolKey, canonicalName) = ClassifyPool(rawName, rawDesc, hint);
                    if (poolKey != "argon") continue;

                    var group = GetOrCreateGroup(result, poolKey, canonicalName, rawDesc, sourceEndpoint);
                    if (buckets != null)
                    {
                        foreach (var b in buckets)
                        {
                            if (b == null) continue;
                            var (bucketInfo, windowKind) = ExtractBucketInfo(b, utcNow);
                            AddBucketToGroup(group, bucketInfo, windowKind);
                        }
                    }
                }
            }

            if (root["buckets"] is JsonArray bucketsNode)
            {
                foreach (var item in bucketsNode)
                {
                    if (item == null) continue;
                    var id = item["modelId"]?.ToString() ?? item["model_id"]?.ToString() ?? item["bucketId"]?.ToString() ?? string.Empty;
                    var dn = item["displayName"]?.ToString() ?? string.Empty;
                    if (ShouldSkipInternalModel(id)) continue;

                    var (poolKey, canonicalName) = ClassifyPool(id, dn, id);
                    if (poolKey != "argon") continue;

                    var group = GetOrCreateGroup(result, poolKey, canonicalName, string.Empty, sourceEndpoint);
                    var (bucketInfo, windowKind) = ExtractBucketInfo(item, utcNow);
                    AddBucketToGroup(group, bucketInfo, windowKind);
                }
            }

            if (root["models"] is JsonObject modelsObj)
            {
                ParseModelsObjectIntoResult(result, modelsObj, utcNow, argonOnly: true, sourceEndpoint: sourceEndpoint);
            }

            // Discard synthesized empty Argon group if no numeric quota values existed.
            result.Groups.RemoveAll(g => g.PoolKey == "argon" && !g.HasAnyQuota);
            result.SyncLegacyScalarProperties();
        }
        catch { }
    }

    private static void ParseModelsObjectIntoResult(
        AccountQuotaParsedResult result,
        JsonObject modelsObj,
        DateTime utcNow,
        bool argonOnly,
        string sourceEndpoint)
    {
        foreach (var kv in modelsObj)
        {
            if (kv.Value is not JsonObject modelNode) continue;
            var modelId = kv.Key;
            if (ShouldSkipInternalModel(modelId)) continue;

            var displayName = modelNode["displayName"]?.ToString() ?? string.Empty;
            var (poolKey, canonicalName) = ClassifyPool(modelId, displayName, modelId);
            if (argonOnly && poolKey != "argon") continue;

            var quotaNode = modelNode["quotaInfo"] ?? modelNode;
            if (quotaNode == null) continue;

            var (bucketInfo, windowKind) = ExtractBucketInfo(quotaNode, utcNow, overrideBucketId: modelId, overrideDisplayName: displayName);
            if (!bucketInfo.RemainingFraction.HasValue && !bucketInfo.RemainingAmount.HasValue) continue;

            var group = GetOrCreateGroup(result, poolKey, canonicalName, string.Empty, sourceEndpoint);
            AddBucketToGroup(group, bucketInfo, windowKind);
        }
    }

    private static bool ShouldSkipInternalModel(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        return lower.StartsWith("tab_", StringComparison.Ordinal)
            || lower.StartsWith("chat_", StringComparison.Ordinal);
    }

    private static (string poolKey, string canonicalName) ClassifyPool(string primaryName, string description, string bucketHint)
    {
        var combined = $"{primaryName} {description} {bucketHint}".ToLowerInvariant();

        // Check Argon BEFORE Gemini so "Gemini 4 Argon" / "gemini-4-argon" never overwrites Gemini Flash/Pro.
        if (combined.Contains("argon", StringComparison.Ordinal))
        {
            var displayName = !string.IsNullOrWhiteSpace(primaryName) &&
                              primaryName.Contains("argon", StringComparison.OrdinalIgnoreCase)
                ? primaryName.Trim()
                : "Gemini 4 Argon";
            return ("argon", displayName);
        }

        if (combined.Contains("claude", StringComparison.Ordinal)
            || combined.Contains("gpt", StringComparison.Ordinal)
            || combined.Contains("3p", StringComparison.Ordinal)
            || combined.Contains("sonnet", StringComparison.Ordinal)
            || combined.Contains("opus", StringComparison.Ordinal))
        {
            var displayName = string.IsNullOrWhiteSpace(primaryName) || primaryName.StartsWith("claude", StringComparison.OrdinalIgnoreCase) || primaryName.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
                ? "Claude and GPT models"
                : primaryName.Trim();
            return ("claude_gpt", displayName);
        }

        if (combined.Contains("gemini", StringComparison.Ordinal)
            || combined.Contains("flash", StringComparison.Ordinal)
            || combined.Contains("model", StringComparison.Ordinal)
            || combined.Contains("pro", StringComparison.Ordinal))
        {
            var displayName = string.IsNullOrWhiteSpace(primaryName) || primaryName.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
                ? "Gemini Models"
                : primaryName.Trim();
            return ("gemini", displayName);
        }

        // Preserve any unrecognized/new quota pool cleanly using its name.
        var cleanName = string.IsNullOrWhiteSpace(primaryName) ? "Unknown Pool" : primaryName.Trim();
        var slug = System.Text.RegularExpressions.Regex.Replace(cleanName.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        if (string.IsNullOrEmpty(slug)) slug = "other";
        return (slug, cleanName);
    }

    private static QuotaGroupInfo GetOrCreateGroup(
        AccountQuotaParsedResult result,
        string poolKey,
        string canonicalName,
        string description,
        string sourceEndpoint)
    {
        var existing = result.Groups.FirstOrDefault(g => string.Equals(g.PoolKey, poolKey, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (string.IsNullOrWhiteSpace(existing.Description) && !string.IsNullOrWhiteSpace(description))
            {
                existing.Description = description;
            }
            return existing;
        }

        var group = new QuotaGroupInfo
        {
            PoolKey = poolKey,
            Name = canonicalName,
            Description = description,
            Source = sourceEndpoint
        };
        result.Groups.Add(group);
        return group;
    }

    private static (QuotaBucketInfo bucket, string windowKind) ExtractBucketInfo(
        JsonNode b,
        DateTime utcNow,
        string? overrideBucketId = null,
        string? overrideDisplayName = null)
    {
        double? fraction = null;
        if (b["remainingFraction"] != null && double.TryParse(b["remainingFraction"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var f1))
        {
            fraction = f1;
        }
        else if (b["remaining_fraction"] != null && double.TryParse(b["remaining_fraction"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var f2))
        {
            fraction = f2;
        }
        else if (b["usedPercent"] != null && double.TryParse(b["usedPercent"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var u1))
        {
            fraction = Math.Max(0.0, 1.0 - (u1 > 1.0 ? u1 / 100.0 : u1));
        }

        long? amount = null;
        if (b["remainingAmount"] != null && long.TryParse(b["remainingAmount"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var a1))
        {
            amount = a1;
        }
        else if (b["remaining_amount"] != null && long.TryParse(b["remaining_amount"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var a2))
        {
            amount = a2;
        }

        DateTime? resetDt = null;
        var resetStr = b["resetTime"]?.ToString() ?? b["reset_time"]?.ToString();
        if (!string.IsNullOrEmpty(resetStr) && DateTime.TryParse(resetStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
        {
            resetDt = dt.ToUniversalTime();
        }

        bool disabled = false;
        if (b["disabled"] != null && bool.TryParse(b["disabled"]!.ToString(), out var dis))
        {
            disabled = dis;
        }

        var bucketId = overrideBucketId
            ?? b["bucketId"]?.ToString()
            ?? b["modelId"]?.ToString()
            ?? b["model_id"]?.ToString()
            ?? b["id"]?.ToString()
            ?? string.Empty;

        var displayName = overrideDisplayName ?? b["displayName"]?.ToString() ?? string.Empty;
        var rawWindow = b["window"]?.ToString() ?? b["windowDuration"]?.ToString() ?? b["duration"]?.ToString() ?? string.Empty;

        var windowKind = NormalizeWindow(rawWindow, bucketId, displayName, resetDt, utcNow);

        var info = new QuotaBucketInfo
        {
            BucketId = bucketId,
            DisplayName = displayName,
            Window = string.IsNullOrWhiteSpace(rawWindow) ? windowKind : rawWindow.Trim(),
            RemainingFraction = fraction,
            RemainingAmount = amount,
            ResetTime = resetDt,
            Disabled = disabled
        };
        return (info, windowKind);
    }

    private static string NormalizeWindow(
        string rawWindow,
        string bucketId,
        string displayName,
        DateTime? resetDt,
        DateTime utcNow)
    {
        var w = rawWindow.Trim().ToLowerInvariant();
        var id = bucketId.Trim().ToLowerInvariant();
        var dn = displayName.Trim().ToLowerInvariant();

        // Explicit duration tokens first so "5d" is never misclassified as "5h".
        if (w is "5h" or "5hr" or "5hrs" or "18000s" or "300m" ||
            w.Contains("five hour", StringComparison.Ordinal) ||
            w.Contains("5-hour", StringComparison.Ordinal) ||
            w.Contains("5 hour", StringComparison.Ordinal) ||
            id.Contains("-5h", StringComparison.Ordinal) ||
            id.EndsWith("5h", StringComparison.Ordinal) ||
            dn.Contains("five hour", StringComparison.Ordinal))
        {
            return "5h";
        }

        if (w is "daily" or "1d" or "24h" or "86400s" ||
            w.Contains("daily", StringComparison.Ordinal) ||
            id.Contains("-daily", StringComparison.Ordinal) ||
            id.EndsWith("daily", StringComparison.Ordinal) ||
            dn.Contains("daily", StringComparison.Ordinal))
        {
            return "daily";
        }

        if (w is "weekly" or "7d" or "604800s" or "1w" ||
            w.Contains("weekly", StringComparison.Ordinal) ||
            w.Contains("seven day", StringComparison.Ordinal) ||
            id.Contains("weekly", StringComparison.Ordinal) ||
            id.EndsWith("-7d", StringComparison.Ordinal) ||
            dn.Contains("weekly", StringComparison.Ordinal))
        {
            return "weekly";
        }

        // Preserve valid compact duration tokens like "5d", "12h", "30d" (matches official cra()).
        if (!string.IsNullOrEmpty(w) && System.Text.RegularExpressions.Regex.IsMatch(w, @"^\d+[smhd]$"))
        {
            return w;
        }

        // Unannotated model buckets (retrieveUserQuota / fetchAvailableModels): infer from reset horizon.
        if (resetDt.HasValue)
        {
            var diff = resetDt.Value - utcNow;
            if (diff.TotalHours <= 6.5) return "5h";
            if (diff.TotalHours <= 30.0) return "daily";
            return "weekly";
        }

        return string.Empty;
    }

    private static void AddBucketToGroup(QuotaGroupInfo group, QuotaBucketInfo bucket, string windowKind)
    {
        group.Buckets.Add(bucket);
        if (!bucket.RemainingFraction.HasValue) return;

        static double? MinFraction(double? current, double incoming) =>
            current.HasValue ? Math.Min(current.Value, incoming) : incoming;

        switch (windowKind)
        {
            case "5h":
                group.FiveHourFraction = MinFraction(group.FiveHourFraction, bucket.RemainingFraction.Value);
                group.FiveHourResetTime ??= bucket.ResetTime;
                break;
            case "daily":
                group.DailyFraction = MinFraction(group.DailyFraction, bucket.RemainingFraction.Value);
                group.DailyResetTime ??= bucket.ResetTime;
                break;
            case "weekly":
                group.WeeklyFraction = MinFraction(group.WeeklyFraction, bucket.RemainingFraction.Value);
                group.WeeklyResetTime ??= bucket.ResetTime;
                break;
        }
    }

    public class AccountQuotaParsedResult
    {
        public string? Description { get; set; }
        public List<QuotaGroupInfo> Groups { get; set; } = new();

        public QuotaGroupInfo? ArgonGroup =>
            Groups.FirstOrDefault(g => string.Equals(g.PoolKey, "argon", StringComparison.OrdinalIgnoreCase));

        public double? Gemini5hFraction { get; set; }
        public DateTime? Gemini5hReset { get; set; }
        public double? Gemini7dFraction { get; set; }
        public DateTime? Gemini7dReset { get; set; }

        public double? Claude5hFraction { get; set; }
        public DateTime? Claude5hReset { get; set; }
        public double? Claude7dFraction { get; set; }
        public DateTime? Claude7dReset { get; set; }

        public double? Argon5hFraction { get; set; }
        public DateTime? Argon5hReset { get; set; }
        public double? Argon7dFraction { get; set; }
        public DateTime? Argon7dReset { get; set; }

        public void SyncLegacyScalarProperties()
        {
            var gemini = Groups.FirstOrDefault(g => string.Equals(g.PoolKey, "gemini", StringComparison.OrdinalIgnoreCase));
            if (gemini != null)
            {
                Gemini5hFraction = gemini.FiveHourFraction;
                Gemini5hReset = gemini.FiveHourResetTime;
                Gemini7dFraction = gemini.WeeklyFraction;
                Gemini7dReset = gemini.WeeklyResetTime;
            }
            else
            {
                Gemini5hFraction = null;
                Gemini5hReset = null;
                Gemini7dFraction = null;
                Gemini7dReset = null;
            }

            var claude = Groups.FirstOrDefault(g => string.Equals(g.PoolKey, "claude_gpt", StringComparison.OrdinalIgnoreCase));
            if (claude != null)
            {
                Claude5hFraction = claude.FiveHourFraction;
                Claude5hReset = claude.FiveHourResetTime;
                Claude7dFraction = claude.WeeklyFraction;
                Claude7dReset = claude.WeeklyResetTime;
            }
            else
            {
                Claude5hFraction = null;
                Claude5hReset = null;
                Claude7dFraction = null;
                Claude7dReset = null;
            }

            var argon = ArgonGroup;
            if (argon != null)
            {
                Argon5hFraction = argon.FiveHourFraction;
                Argon5hReset = argon.FiveHourResetTime;
                Argon7dFraction = argon.WeeklyFraction;
                Argon7dReset = argon.WeeklyResetTime;
            }
            else
            {
                Argon5hFraction = null;
                Argon5hReset = null;
                Argon7dFraction = null;
                Argon7dReset = null;
            }
        }
    }
    /// <summary>Captures a new account by running the engine in an isolated sandbox with the protocol handler redirected.</summary>
    public async Task<AccountProfile?> CaptureNewAccountAsync(
        string antigravityExePath, 
        ProxyConfig? proxy = null,
        Action<int>? statusCallback = null,
        CancellationToken cancellationToken = default)
    {
        // Point the Windows antigravity:// protocol handler at Gravityless.App.exe.
        EnsureProtocolInterceptorRegistered();

        var callbackFile = Path.Combine(_baseDir, "data", "temp", "protocol_callback.txt");
        try { if (File.Exists(callbackFile)) File.Delete(callbackFile); } catch { }

        // Cross-process route: Gravityless.App.exe runs as its own process and reads this file
        // to forward antigravity:// URLs back to the sandbox being captured.
        var routeFile = Path.Combine(_baseDir, "data", "temp", "capture_route.json");
        try { if (File.Exists(routeFile)) File.Delete(routeFile); } catch { }

        // Back up and clear the host Windows credential so the new sandbox starts logged out.
        // The slot lock is acquired inside the main try so any throw below still releases it.
        string? backupCred = null;
        var slotLocked = false;

        var tempCaptureDir = Path.Combine(_baseDir, "temp", $"capture_{Guid.NewGuid():N}");
        var tempUserData = Path.Combine(tempCaptureDir, "UserData");
        var tempGemini = Path.Combine(tempCaptureDir, ".gemini");
        var tempRoaming = Path.Combine(tempCaptureDir, "AppData", "Roaming");
        var tempLocal = Path.Combine(tempCaptureDir, "AppData", "Local");

        Directory.CreateDirectory(tempUserData);
        Directory.CreateDirectory(tempGemini);
        Directory.CreateDirectory(tempRoaming);
        Directory.CreateDirectory(tempLocal);

        // Write the proxy config file.
        if (proxy != null)
        {
            ProxyService.WriteProxyConfigFile(tempCaptureDir, proxy);
        }

        var tcs = new TaskCompletionSource<AccountProfile?>();
        using var timer = new System.Timers.Timer(500);

        timer.Elapsed += (_, _) =>
        {
            // A. Watch oauth_creds.json inside the sandbox.
            var credFile = Path.Combine(tempGemini, "oauth_creds.json");
            if (File.Exists(credFile))
            {
                try
                {
                    var fileContent = File.ReadAllText(credFile);
                    var cred = CredentialCodec.Parse(fileContent);
                    if (cred?.HasIdentity == true)
                    {
                        tcs.TrySetResult(NewProfileFrom(cred));
                        return;
                    }
                }
                catch { }
            }

            // B. Watch for a new Windows Credential Manager entry.
            var currentCred = CredentialManager.ReadCredential();
            if (!string.IsNullOrEmpty(currentCred))
            {
                var cred = CredentialCodec.Parse(currentCred);
                if (cred?.HasIdentity == true)
                {
                    tcs.TrySetResult(NewProfileFrom(cred));
                    return;
                }
            }
        };

        timer.Start();

        Process? process = null;
        try
        {
            // Hold the global credential slot for the whole capture: it is one system-wide entry,
            // so a concurrent launch writing it would hand this sandbox another account's token.
            if (!CredentialManager.SlotLock.Wait(CredentialManager.SlotLockTtl))
            {
                statusCallback?.Invoke(-1);
                return null;
            }
            slotLocked = true;

            backupCred = CredentialManager.BackupAndClear();

            statusCallback?.Invoke(2); // Sandbox started; waiting for browser login

            var psi = new ProcessStartInfo
            {
                FileName = antigravityExePath,
                Arguments = $"--user-data-dir=\"{tempUserData}\" --no-sandbox",
                UseShellExecute = false,
                CreateNoWindow = false
            };

            var drive = Path.GetPathRoot(tempCaptureDir) ?? "C:\\";
            var pathWithoutDrive = tempCaptureDir.Substring(drive.Length - (drive.EndsWith('\\') ? 1 : 0));

            psi.EnvironmentVariables["USERPROFILE"] = tempCaptureDir;
            psi.EnvironmentVariables["HOME"] = tempCaptureDir;
            psi.EnvironmentVariables["HOMEDRIVE"] = drive.TrimEnd('\\');
            psi.EnvironmentVariables["HOMEPATH"] = pathWithoutDrive;
            psi.EnvironmentVariables["APPDATA"] = tempRoaming;
            psi.EnvironmentVariables["LOCALAPPDATA"] = tempLocal;

            ProxyService.ApplyProxyToProcess(psi, proxy);

            // Register the route before starting the process so callbacks always resolve.
            WriteCaptureRoute(routeFile, antigravityExePath, tempUserData);

            process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Antigravity 官方引擎进行登录捕获");

            using var reg = cancellationToken.Register(() =>
            {
                tcs.TrySetCanceled();
                ForceKillProcessTree(process);
            });

            // Wait for capture or exit, max 5 minutes.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await Task.WhenAny(tcs.Task, Task.Run(async () =>
            {
                await process.WaitForExitAsync(linkedCts.Token);
                await Task.Delay(1000, linkedCts.Token);
            }, linkedCts.Token));

            timer.Stop();

            // On success, allow 500ms for files to flush, then kill the sandbox process tree.
            if (tcs.Task.IsCompletedSuccessfully)
            {
                statusCallback?.Invoke(3); // Credentials captured; finishing up
                await Task.Delay(500, cancellationToken);
                ForceKillProcessTree(process);
            }

            AccountProfile? profile = null;
            if (tcs.Task.IsCompletedSuccessfully)
            {
                profile = await tcs.Task;
            }
            else
            {
                // One last check.
                var credFile = Path.Combine(tempGemini, "oauth_creds.json");
                if (File.Exists(credFile))
                {
                    try
                    {
                        var cred = CredentialCodec.Parse(File.ReadAllText(credFile));
                        if (cred?.HasIdentity == true)
                        {
                            profile = NewProfileFrom(cred);
                        }
                    }
                    catch { }
                }
            }

            if (profile != null)
            {
                var existing = _accounts.FirstOrDefault(a => a.Email.Equals(profile.Email, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    // Re-capturing the same address is an explicit re-login, so the identity
                    // fields are replaced along with the token cache.
                    existing.RefreshToken = profile.RefreshToken;
                    existing.IdToken = profile.IdToken;
                    existing.TokenType = profile.TokenType;
                    existing.AccessToken = profile.AccessToken;
                    existing.AccessTokenExpiresAt = profile.AccessTokenExpiresAt;
                    existing.LastActiveAt = DateTime.UtcNow;
                    profile = existing;
                }
                else
                {
                    _accounts.Add(profile);
                }
                SaveAccounts();

                // Archive the account credentials into data/accounts/{id}/.
                var accountDir = GetAccountStorageDir(profile.Id);
                InjectAccountToGeminiDir(accountDir, profile);

                // The captured refresh token is guaranteed to be live, so it is the most
                // reliable moment to certify an OAuth client pair. Fire and forget: capture
                // has already succeeded and must not block on a 2 s binary scan.
                _ = Task.Run(async () =>
                {
                    try { await EnsureOAuthClientAsync(null); }
                    catch (Exception ex) { DiagLog.Swallowed("account.oauth-client-post-capture", ex); }
                });
            }

            return profile;
        }
        finally
        {
            timer.Stop();

            // Drop the route first so late callbacks cannot wake the sandbox during cleanup.
            try { if (File.Exists(routeFile)) File.Delete(routeFile); } catch { }

            // Restore the host credentials (only if this call actually cleared) and free the slot.
            if (slotLocked)
            {
                slotLocked = false;
                CredentialManager.Restore(backupCred);
                CredentialManager.SlotLock.Release();
            }

            // Kill the whole capture process tree.
            ForceKillProcessTree(process);

            // Remove the temp directory and callback file.
            try
            {
                if (Directory.Exists(tempCaptureDir))
                {
                    await Task.Delay(500); // Wait for file handles to be released
                    Directory.Delete(tempCaptureDir, true);
                }
            }
            catch { }

            try
            {
                if (File.Exists(callbackFile)) File.Delete(callbackFile);
            }
            catch { }
        }
    }

    /// <summary>Writes the route table telling Gravityless.App.exe which sandbox receives antigravity:// callbacks.</summary>
    private static void WriteCaptureRoute(string routeFile, string exePath, string userDataDir)
    {
        try
        {
            var dir = Path.GetDirectoryName(routeFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var route = new JsonObject
            {
                ["exePath"] = exePath,
                ["userDataDir"] = userDataDir,
                ["startedAt"] = DateTime.UtcNow.ToString("o")
            };

            File.WriteAllText(routeFile, route.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Points the HKCU antigravity:// protocol handler at Gravityless.</summary>
    public static void EnsureProtocolInterceptorRegistered()
    {
        try
        {
            var gravitylessExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(gravitylessExe) || !File.Exists(gravitylessExe))
            {
                gravitylessExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Gravityless.App.exe");
            }

            using var cmdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\antigravity\shell\open\command");
            if (cmdKey != null)
            {
                var targetCmd = $"\"{gravitylessExe}\" \"%1\"";
                var current = cmdKey.GetValue("")?.ToString();
                if (current != targetCmd)
                {
                    cmdKey.SetValue("", targetCmd);
                }
            }
        }
        catch { }
    }
    private static void ForceKillProcessTree(Process? proc)
    {
        if (proc == null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch { }

        try
        {
            var killPsi = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/F /T /PID {proc.Id}",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var killProc = Process.Start(killPsi);
            killProc?.WaitForExit(2000);
        }
        catch { }
    }

    /// <summary>Writes google_accounts.json, oauth_creds.json and installation_id into a .gemini directory.</summary>
    public static void InjectAccountToGeminiDir(string geminiDir, AccountProfile account)
    {
        Directory.CreateDirectory(geminiDir);

        // 1. google_accounts.json
        var accountsObj = new JsonObject
        {
            ["active"] = account.Email,
            ["old"] = new JsonArray()
        };
        File.WriteAllText(Path.Combine(geminiDir, "google_accounts.json"), accountsObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // 2. installation_id (isolates the device fingerprint)
        //
        // oauth_creds.json is deliberately not written. Antigravity 2.29 no longer references
        // that file at all — the language server reads its token from the Windows credential
        // slot — so writing it only suggested a channel that no longer exists.
        if (!string.IsNullOrEmpty(account.InstallationId))
        {
            File.WriteAllText(Path.Combine(geminiDir, "installation_id"), account.InstallationId);
        }
    }
}
