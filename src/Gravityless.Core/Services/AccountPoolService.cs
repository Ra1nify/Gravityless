using System.Net;
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
        return string.IsNullOrEmpty(blob) ? null : ExtractEmailFromCredBlob(blob);
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
        if (File.Exists(_accountsJsonPath))
        {
            try
            {
                var json = File.ReadAllText(_accountsJsonPath);
                var list = JsonSerializer.Deserialize<List<AccountProfile>>(json);
                if (list != null)
                {
                    foreach (var acc in list)
                    {
                        if (acc.DisplayName?.Contains("(系统主号)") == true)
                        {
                            acc.DisplayName = acc.DisplayName.Replace(" (系统主号)", "").Replace("(系统主号)", "").Trim();
                        }
                    }
                    _accounts.AddRange(list);
                }
            }
            catch
            {
                // ignore
            }
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
                var credJson = File.ReadAllText(instOauthCredsPath);
                var synthesizedBlob = ConvertOauthCredsJsonToCredBlob(credJson);
                if (!string.IsNullOrEmpty(synthesizedBlob))
                {
                    acc.CredentialBlob = synthesizedBlob;
                }
            }
            catch { }
        }
        else
        {
            // Copy the latest Windows Credential Manager token back into the account.
            // The credential slot is global, so only adopt a blob whose email matches this account.
            var latestCred = CredentialManager.ReadCredential();
            if (!string.IsNullOrEmpty(latestCred))
            {
                var emailInCred = ExtractEmailFromCredBlob(latestCred);
                if (!string.IsNullOrEmpty(emailInCred)
                    && emailInCred.Equals(acc.Email, StringComparison.OrdinalIgnoreCase))
                {
                    acc.CredentialBlob = latestCred;
                }
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
    public const string CloudCodeAssistEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";

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
            .Select(a => ProxyService.ExtractTokens(a.CredentialBlob).refreshToken)
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

    private static HttpClient CreateHttpClient(ProxyConfig? config)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };

        if (config != null)
        {
            if (config.Mode == ProxyMode.Manual)
            {
                var proxyUri = $"{config.Protocol.ToString().ToLower()}://{config.Host}:{config.Port}";
                var webProxy = new WebProxy(proxyUri);
                if (!string.IsNullOrEmpty(config.Username))
                {
                    webProxy.Credentials = new NetworkCredential(config.Username, config.Password);
                }
                handler.Proxy = webProxy;
                handler.UseProxy = true;
            }
            else if (config.Mode == ProxyMode.Direct)
            {
                handler.UseProxy = false;
            }
            else
            {
                handler.UseProxy = true;
            }
        }
        else
        {
            handler.UseProxy = true;
        }

        // A new client per call is intentional: the proxy handler differs by configuration
        // and cannot be shared. Callers dispose it, so no socket is left behind.
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
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

        var (accessToken, refreshToken, expiry) = ProxyService.ExtractTokens(acc.CredentialBlob);
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
                        UpdateAccountAccessToken(acc, newAccessToken, expiresIn);
                        acc.FailedRefreshAttempts = 0;
                        acc.LastActiveAt = DateTime.UtcNow;

                        // Also refresh the 5h/7d model quota fractions and reset times.
                        try
                        {
                            await FetchAndSaveAccountQuotaInternalAsync(acc, newAccessToken, client, cancellationToken);
                        }
                        catch { }

                        SaveAccounts();

                        if (!string.IsNullOrEmpty(acc.BoundInstanceId))
                        {
                            var instDir = Path.Combine(_baseDir, "data", "instances", acc.BoundInstanceId);
                            InjectAccountToGeminiDir(instDir, acc);
                        }

                        var accDir = GetAccountStorageDir(acc.Id);
                        InjectAccountToGeminiDir(accDir, acc);

                        return (true, "Token 刷新成功！模型配额已同步就绪。");
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
    /// <param name="onProgress">Receives (index, total, email) before each account is processed.</param>
    public async Task<(int successCount, int failedCount)> RefreshAllTokensAsync(
        ProxyConfig? proxyConfig = null,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        int success = 0;
        int failed = 0;

        var accounts = _accounts.ToList();
        for (int i = 0; i < accounts.Count; i++)
        {
            var acc = accounts[i];
            cancellationToken.ThrowIfCancellationRequested();

            onProgress?.Invoke(i + 1, accounts.Count, acc.Email);

            // autoRetry stays on: a transient network fault should not mark an account dead
            // for the rest of the batch.
            var res = await RefreshTokenAsync(acc.Id, proxyConfig, autoRetry: true, cancellationToken);
            if (res.success) success++;
            else failed++;
        }

        return (success, failed);
    }

    /// <summary>Queries the quota gateway for the 5h/7d remaining fractions (0.0-1.0) and reset times.</summary>
    public async Task<(bool success, string message)> FetchAccountQuotaAsync(
        string accountId,
        ProxyConfig? proxyConfig = null,
        CancellationToken cancellationToken = default)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc == null) return (false, "找不到指定的账号");

        var (accessToken, _, _) = ProxyService.ExtractTokens(acc.CredentialBlob);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return (false, "账号未包含有效的 Access Token，请先刷新 Token");
        }

        using var client = CreateHttpClient(proxyConfig);
        return await FetchAndSaveAccountQuotaInternalAsync(acc, accessToken, client, cancellationToken);
    }

    private async Task<(bool success, string message)> FetchAndSaveAccountQuotaInternalAsync(
        AccountProfile acc,
        string accessToken,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            // Try retrieveUserQuotaSummary for the grouped 5h / 7d quota first.
            var (success, body) = await SendRetrieveUserQuotaSummaryRequestAsync(client, accessToken, cancellationToken);

            // Fall back to retrieveUserQuota.
            if (!success)
            {
                (success, body) = await SendRetrieveUserQuotaRequestAsync(client, accessToken, "", cancellationToken);

                // Missing project: discover the companion project ID via loadCodeAssist, then retry.
                if (!success && (body.Contains("project", StringComparison.OrdinalIgnoreCase) || body.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase)))
                {
                    var discoveredProject = await DiscoverCloudCodeProjectAsync(client, accessToken, cancellationToken);
                    if (!string.IsNullOrEmpty(discoveredProject))
                    {
                        (success, body) = await SendRetrieveUserQuotaRequestAsync(client, accessToken, discoveredProject, cancellationToken);
                    }
                }
            }

            if (!success)
            {
                return (false, $"配额查询未就绪: {body}");
            }

            // Parse the response (5h / 7d windows).
            var parsed = ParseQuotaResponse(body);
            acc.Gemini5hFraction = parsed.Gemini5hFraction;
            acc.Gemini5hResetTime = parsed.Gemini5hReset;
            acc.Gemini7dFraction = parsed.Gemini7dFraction;
            acc.Gemini7dResetTime = parsed.Gemini7dReset;

            acc.Claude5hFraction = parsed.Claude5hFraction;
            acc.Claude5hResetTime = parsed.Claude5hReset;
            acc.Claude7dFraction = parsed.Claude7dFraction;
            acc.Claude7dResetTime = parsed.Claude7dReset;

            acc.QuotaLastFetchedAt = DateTime.UtcNow;
            acc.QuotaRawText = body;

            SaveAccounts();
            return (true, "模型配额同步成功");
        }
        catch (Exception ex)
        {
            return (false, $"配额网络请求异常: {ex.Message}");
        }
    }

    private static async Task<(bool success, string body)> SendRetrieveUserQuotaSummaryRequestAsync(
        HttpClient client,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, CloudCodeQuotaSummaryEndpoint);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.TryAddWithoutValidation("User-Agent", "antigravity/2.26.0");
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var resp = await client.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            return (resp.IsSuccessStatusCode, body);
        }
        catch
        {
            return (false, "");
        }
    }

    private static async Task<(bool success, string body)> SendRetrieveUserQuotaRequestAsync(
        HttpClient client,
        string accessToken,
        string projectId,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, CloudCodeQuotaEndpoint);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("User-Agent", "antigravity/2.26.0");

        var payload = string.IsNullOrEmpty(projectId) 
            ? "{}" 
            : JsonSerializer.Serialize(new { project = projectId });
        req.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        return (resp.IsSuccessStatusCode, body);
    }

    private static async Task<string?> DiscoverCloudCodeProjectAsync(
        HttpClient client,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, CloudCodeAssistEndpoint);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.TryAddWithoutValidation("User-Agent", "antigravity/2.26.0");
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var resp = await client.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            var node = JsonNode.Parse(body);
            return node?["cloudaicompanionProject"]?.ToString() 
                ?? node?["project"]?.ToString() 
                ?? node?["projectId"]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    public static AccountQuotaParsedResult ParseQuotaResponse(string json)
    {
        var result = new AccountQuotaParsedResult();
        try
        {
            var root = JsonNode.Parse(json);
            if (root == null) return result;

            // Path A: summary mode (groups[]).
            var groupsNode = root["groups"] as JsonArray 
                ?? root["command"]?["data"]?["groups"] as JsonArray
                ?? root["data"]?["groups"] as JsonArray;

            if (groupsNode != null && groupsNode.Count > 0)
            {
                foreach (var grp in groupsNode)
                {
                    if (grp == null) continue;
                    var name = (grp["displayName"]?.ToString() 
                        ?? grp["name"]?.ToString() 
                        ?? grp["description"]?.ToString() 
                        ?? grp["groupId"]?.ToString() 
                        ?? grp["id"]?.ToString() ?? "").ToLowerInvariant();
                    bool isClaudeGpt = name.Contains("claude") || name.Contains("gpt") || name.Contains("3p") || name.Contains("sonnet");
                    bool isGemini = name.Contains("gemini") || (!isClaudeGpt && (name.Contains("model") || name.Contains("pro")));

                    var buckets = grp["buckets"] as JsonArray;
                    if (buckets != null)
                    {
                        foreach (var b in buckets)
                        {
                            if (b == null) continue;
                            var (fraction, resetDt, is5h) = ExtractBucket(b);
                            if (isClaudeGpt)
                            {
                                if (is5h)
                                {
                                    result.Claude5hFraction = fraction ?? result.Claude5hFraction;
                                    result.Claude5hReset = resetDt ?? result.Claude5hReset;
                                }
                                else
                                {
                                    result.Claude7dFraction = fraction ?? result.Claude7dFraction;
                                    result.Claude7dReset = resetDt ?? result.Claude7dReset;
                                }
                            }
                            else if (isGemini)
                            {
                                if (is5h)
                                {
                                    result.Gemini5hFraction = fraction ?? result.Gemini5hFraction;
                                    result.Gemini5hReset = resetDt ?? result.Gemini5hReset;
                                }
                                else
                                {
                                    result.Gemini7dFraction = fraction ?? result.Gemini7dFraction;
                                    result.Gemini7dReset = resetDt ?? result.Gemini7dReset;
                                }
                            }
                        }
                    }
                }

                return result;
            }

            // Path B: flat buckets[] mode.
            var bucketsNode = root["buckets"] as JsonArray 
                ?? root["userQuota"]?["buckets"] as JsonArray;

            if (bucketsNode != null)
            {
                foreach (var item in bucketsNode)
                {
                    if (item == null) continue;
                    var modelId = (item["modelId"]?.ToString() ?? item["model_id"]?.ToString() ?? "").ToLowerInvariant();

                    // Skip internal completion (tab_*) and probe (chat_*) buckets.
                    if (modelId.StartsWith("tab_") || modelId.StartsWith("chat_"))
                    {
                        continue;
                    }

                    bool isClaudeGpt = modelId.Contains("claude") || modelId.Contains("gpt") || modelId.Contains("sonnet") || modelId.Contains("opus");
                    bool isGemini = modelId.StartsWith("gemini") || modelId.Contains("gemini");

                    if (!isClaudeGpt && !isGemini)
                    {
                        continue;
                    }

                    var (fraction, resetDt, is5h) = ExtractBucket(item);

                    if (isClaudeGpt)
                    {
                        if (is5h)
                        {
                            result.Claude5hFraction = fraction ?? result.Claude5hFraction;
                            result.Claude5hReset = resetDt ?? result.Claude5hReset;
                        }
                        else
                        {
                            result.Claude7dFraction = fraction ?? result.Claude7dFraction;
                            result.Claude7dReset = resetDt ?? result.Claude7dReset;
                        }
                    }
                    else if (isGemini)
                    {
                        if (is5h)
                        {
                            result.Gemini5hFraction = fraction ?? result.Gemini5hFraction;
                            result.Gemini5hReset = resetDt ?? result.Gemini5hReset;
                        }
                        else
                        {
                            result.Gemini7dFraction = fraction ?? result.Gemini7dFraction;
                            result.Gemini7dReset = resetDt ?? result.Gemini7dReset;
                            // Weekly reset fired: mirror the value onto the 5h fields if not set.
                            if (!result.Gemini5hFraction.HasValue)
                            {
                                result.Gemini5hFraction = fraction;
                                result.Gemini5hReset = resetDt;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return result;
    }

    private static (double? fraction, DateTime? resetDt, bool is5h) ExtractBucket(JsonNode b)
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

        DateTime? resetDt = null;
        var resetStr = b["resetTime"]?.ToString() ?? b["reset_time"]?.ToString();
        if (!string.IsNullOrEmpty(resetStr) && DateTime.TryParse(resetStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
        {
            resetDt = dt.ToUniversalTime();
        }

        // Classify 5h vs 7d window.
        var bucketId = (b["bucketId"]?.ToString() ?? b["id"]?.ToString() ?? "").ToLowerInvariant();
        var winStr = (b["window"]?.ToString() ?? b["windowDuration"]?.ToString() ?? b["duration"]?.ToString() ?? b["displayName"]?.ToString() ?? "").ToLowerInvariant();
        bool is5h = false;
        if (winStr.Contains("5") || winStr.Contains("five") || winStr == "18000s" || winStr == "300" || bucketId.Contains("5h"))
        {
            is5h = true;
        }
        else if (winStr.Contains("7") || winStr.Contains("seven") || winStr.Contains("week") || winStr == "604800s" || winStr == "10080" || bucketId.Contains("weekly") || bucketId.Contains("7d"))
        {
            is5h = false;
        }
        else if (resetDt.HasValue)
        {
            var diff = resetDt.Value - DateTime.UtcNow;
            is5h = diff.TotalHours <= 6.0;
        }

        return (fraction, resetDt, is5h);
    }

    public class AccountQuotaParsedResult
    {
        public double? Gemini5hFraction { get; set; }
        public DateTime? Gemini5hReset { get; set; }
        public double? Gemini7dFraction { get; set; }
        public DateTime? Gemini7dReset { get; set; }

        public double? Claude5hFraction { get; set; }
        public DateTime? Claude5hReset { get; set; }
        public double? Claude7dFraction { get; set; }
        public DateTime? Claude7dReset { get; set; }
    }

    private static void UpdateAccountAccessToken(AccountProfile account, string newAccessToken, int expiresIn)
    {
        try
        {
            var node = JsonNode.Parse(account.CredentialBlob ?? "{}") ?? new JsonObject();
            var tokenNode = node["token"] as JsonObject;
            if (tokenNode == null)
            {
                tokenNode = new JsonObject();
                node["token"] = tokenNode;
            }

            tokenNode["access_token"] = newAccessToken;
            tokenNode["expires_in"] = expiresIn;
            tokenNode["expiry"] = DateTime.UtcNow.AddSeconds(expiresIn).ToString("o");

            account.CredentialBlob = node.ToJsonString();
        }
        catch { }
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
                    var blob = ConvertOauthCredsJsonToCredBlob(fileContent);
                    if (!string.IsNullOrEmpty(blob))
                    {
                        var email = ExtractEmailFromCredBlob(blob);
                        if (!string.IsNullOrEmpty(email))
                        {
                            tcs.TrySetResult(new AccountProfile
                            {
                                Email = email,
                                DisplayName = email.Split('@')[0],
                                CredentialBlob = blob,
                                InstallationId = Guid.NewGuid().ToString()
                            });
                            return;
                        }
                    }
                }
                catch { }
            }

            // B. Watch for a new Windows Credential Manager entry.
            var currentCred = CredentialManager.ReadCredential();
            if (!string.IsNullOrEmpty(currentCred))
            {
                var email = ExtractEmailFromCredBlob(currentCred);
                if (!string.IsNullOrEmpty(email))
                {
                    tcs.TrySetResult(new AccountProfile
                    {
                        Email = email,
                        DisplayName = email.Split('@')[0],
                        CredentialBlob = currentCred,
                        InstallationId = Guid.NewGuid().ToString()
                    });
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

            if (proxy != null && proxy.Mode == ProxyMode.Manual)
            {
                var proxyUrl = $"{proxy.Protocol.ToString().ToLower()}://{proxy.Host}:{proxy.Port}";
                psi.EnvironmentVariables["ALL_PROXY"] = proxyUrl;
                psi.EnvironmentVariables["HTTP_PROXY"] = proxyUrl;
                psi.EnvironmentVariables["HTTPS_PROXY"] = proxyUrl;
                if (!string.IsNullOrEmpty(proxy.BypassList))
                {
                    psi.EnvironmentVariables["NO_PROXY"] = proxy.BypassList;
                }
            }

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
                        var blob = ConvertOauthCredsJsonToCredBlob(File.ReadAllText(credFile));
                        if (!string.IsNullOrEmpty(blob))
                        {
                            var email = ExtractEmailFromCredBlob(blob);
                            if (!string.IsNullOrEmpty(email))
                            {
                                profile = new AccountProfile
                                {
                                    Email = email,
                                    DisplayName = email.Split('@')[0],
                                    CredentialBlob = blob,
                                    InstallationId = Guid.NewGuid().ToString()
                                };
                            }
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
                    existing.CredentialBlob = profile.CredentialBlob;
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

        // 2. oauth_creds.json
        if (!string.IsNullOrEmpty(account.CredentialBlob))
        {
            var oauthCredsJson = ConvertCredBlobToOauthCredsJson(account.CredentialBlob);
            if (!string.IsNullOrEmpty(oauthCredsJson))
            {
                File.WriteAllText(Path.Combine(geminiDir, "oauth_creds.json"), oauthCredsJson);
            }
        }

        // 3. installation_id (isolates the device fingerprint)
        if (!string.IsNullOrEmpty(account.InstallationId))
        {
            File.WriteAllText(Path.Combine(geminiDir, "installation_id"), account.InstallationId);
        }
    }

    /// <summary>Converts a CredentialBlob into .gemini/oauth_creds.json format.</summary>
    public static string? ConvertCredBlobToOauthCredsJson(string blob)
    {
        try
        {
            var node = JsonNode.Parse(blob);
            var tokenNode = node?["token"];
            var idToken = node?["id_token"]?.ToString();

            var accessToken = tokenNode?["access_token"]?.ToString();
            var tokenType = tokenNode?["token_type"]?.ToString() ?? "Bearer";
            var refreshToken = tokenNode?["refresh_token"]?.ToString();
            var expiryStr = tokenNode?["expiry"]?.ToString();

            long expiryDate = 0;
            if (!string.IsNullOrEmpty(expiryStr) && DateTimeOffset.TryParse(expiryStr, out var dto))
            {
                expiryDate = dto.ToUnixTimeMilliseconds();
            }

            var obj = new JsonObject
            {
                ["access_token"] = accessToken,
                ["token_type"] = tokenType,
                ["refresh_token"] = refreshToken,
                ["expiry_date"] = expiryDate,
                ["id_token"] = idToken
            };

            return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Converts .gemini/oauth_creds.json into the CredentialBlob shape used by Windows Credential Manager.</summary>
    public static string? ConvertOauthCredsJsonToCredBlob(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);

            // Accept both flat oauth_creds.json and the nested { token: {...} } blob form.
            var source = node?["token"] is JsonNode nested ? nested : node;
            if (source == null) return null;

            var accessToken = source["access_token"]?.ToString();
            var tokenType = source["token_type"]?.ToString() ?? "Bearer";
            var refreshToken = source["refresh_token"]?.ToString();
            var idToken = node?["id_token"]?.ToString() ?? source["id_token"]?.ToString();
            var expiryDate = source["expiry_date"]?.GetValue<long>() ?? 0;

            var expiry = expiryDate > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(expiryDate).ToString("o")
                : DateTimeOffset.UtcNow.AddHours(1).ToString("o");

            var tokenObj = new JsonObject
            {
                ["access_token"] = accessToken,
                ["token_type"] = tokenType,
                ["refresh_token"] = refreshToken,
                ["expiry"] = expiry,
                ["expires_in"] = 3599
            };

            var root = new JsonObject
            {
                ["token"] = tokenObj,
                ["auth_method"] = "consumer",
                ["id_token"] = idToken
            };

            // Newer builds resolve the account from id_token_email instead of decoding id_token.
            var email = ExtractEmailFromIdToken(idToken) ?? node?["id_token_email"]?.ToString();
            if (!string.IsNullOrEmpty(email))
            {
                root["id_token_email"] = email;
            }

            return root.ToJsonString();
        }
        catch
        {
            return null;
        }
    }

    public static string? ExtractEmailFromCredBlob(string blob)
    {
        try
        {
            var node = JsonNode.Parse(blob);

            // Newer builds store the account address directly and omit id_token.
            var email = node?["id_token_email"]?.ToString();
            if (!string.IsNullOrEmpty(email)) return email;

            var idToken = node?["id_token"]?.ToString();
            return ExtractEmailFromIdToken(idToken);
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static string? ExtractEmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return null;

        try
        {
            var parts = idToken.Split('.');
            if (parts.Length >= 2)
            {
                var payload = parts[1];
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var bytes = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'));
                var claimsJson = System.Text.Encoding.UTF8.GetString(bytes);
                var claimsNode = JsonNode.Parse(claimsJson);
                return claimsNode?["email"]?.ToString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
