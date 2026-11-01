using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

public enum ConnectivityStatus
{
    NotTested, // - (not tested or skipped)
    Success,   // ✓
    Failed     // ✗
}

public class ProxyTestResult
{
    public ConnectivityStatus GeminiStatus { get; set; } = ConnectivityStatus.NotTested;
    public string GeminiMessage { get; set; } = string.Empty;
    public long GeminiLatencyMs { get; set; }

    /// <summary>
    /// True when the probe actually completed a round trip. A failure that never connected
    /// leaves this false, which is how the UI tells "blocked" apart from "slow".
    /// </summary>
    public bool GeminiLatencyMeasured { get; set; }

    public ConnectivityStatus AntigravityStatus { get; set; } = ConnectivityStatus.NotTested;
    public string AntigravityMessage { get; set; } = string.Empty;
    public long AntigravityLatencyMs { get; set; }
    public bool AntigravityLatencyMeasured { get; set; }
    public string? TestedAccountEmail { get; set; }

    public long LatencyMs { get; set; }

    // Egress IP of the proxy node, kept internally and not shown in the UI
    public string EgressIp { get; set; } = string.Empty;

    /// <summary>Egress country code, e.g. "HK", "SG", "US".</summary>
    public string EgressLocation { get; set; } = string.Empty;

    /// <summary>Flag emoji plus localized name, e.g. "🇺🇸 US (US)".</summary>
    public string FormattedLocation => FormatLocationName(EgressLocation);

    /// <summary>True when Gemini succeeded and Antigravity was not blocked.</summary>
    public bool Success => GeminiStatus == ConnectivityStatus.Success &&
                           (AntigravityStatus == ConnectivityStatus.Success || AntigravityStatus == ConnectivityStatus.NotTested);

    /// <summary>Compact badge, e.g. "Gemini: ✓ / Antigravity: ✓".</summary>
    public string StatusBadge => $"Gemini: {FormatStatus(GeminiStatus)} / Antigravity: {FormatStatus(AntigravityStatus)}";

    private static string FormatStatus(ConnectivityStatus status) => status switch
    {
        ConnectivityStatus.Success => "✓",
        ConnectivityStatus.Failed => "✗",
        _ => "-"
    };

    private static readonly Dictionary<string, string> KnownLocationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "美国",
        ["JP"] = "日本",
        ["SG"] = "新加坡",
        ["HK"] = "中国香港",
        ["TW"] = "中国台湾",
        ["GB"] = "英国",
        ["UK"] = "英国",
        ["DE"] = "德国",
        ["FR"] = "法国",
        ["CA"] = "加拿大",
        ["AU"] = "澳大利亚",
        ["KR"] = "韩国",
        ["NL"] = "荷兰",
        ["IN"] = "印度",
        ["MY"] = "马来西亚",
        ["TH"] = "泰国",
        ["VN"] = "越南",
        ["PH"] = "菲律宾",
        ["ID"] = "印度尼西亚",
        ["CN"] = "中国大陆",
        ["MO"] = "中国澳门",
        ["RU"] = "俄罗斯",
        ["BR"] = "巴西",
        ["ZA"] = "南非",
        ["SE"] = "瑞典",
        ["CH"] = "瑞士",
        ["IE"] = "爱尔兰",
        ["FI"] = "芬兰",
        ["NO"] = "挪威"
    };

    public static string FormatLocationName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "未知地区";
        var upper = code.Trim().ToUpperInvariant();
        string flag = "";
        // Two ASCII letters map to regional indicator symbols (U+1F1E6 + letter offset)
        if (upper.Length == 2 && upper[0] >= 'A' && upper[0] <= 'Z' && upper[1] >= 'A' && upper[1] <= 'Z')
        {
            flag = char.ConvertFromUtf32(0x1F1E6 + (upper[0] - 'A')) +
                   char.ConvertFromUtf32(0x1F1E6 + (upper[1] - 'A')) + " ";
        }

        if (KnownLocationNames.TryGetValue(upper, out var name))
        {
            return $"{flag}{name} ({upper})";
        }

        return $"{flag}{upper}";
    }
}

public class ProxyService
{
    private const string GeminiProbeEndpoint = "https://generativelanguage.googleapis.com/v1beta/models?key=probe";
    private const string AntigravityProbeEndpoint = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";

    /// <summary>Probes egress IP, Gemini reachability, then Antigravity with a real account credential.</summary>
    public async Task<ProxyTestResult> TestConnectionAsync(
        ProxyConfig config,
        IEnumerable<AccountProfile>? candidateAccounts = null,
        AccountPoolService? accountPoolService = null,
        string? customGeminiUrl = null,
        string? customAntigravityUrl = null)
    {
        var result = new ProxyTestResult();
        var sw = Stopwatch.StartNew();

        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(6)
            };

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
                handler.UseProxy = true; // System proxy
            }

            // Built per probe so each run uses its own proxy handler; disposed below.
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

            // Egress IP and country from the Cloudflare trace endpoint
            try
            {
                using var traceReq = new HttpRequestMessage(HttpMethod.Get, "https://cloudflare.com/cdn-cgi/trace");
                using var traceResp = await client.SendAsync(traceReq);
                if (traceResp.IsSuccessStatusCode)
                {
                    var traceText = await traceResp.Content.ReadAsStringAsync();
                    foreach (var line in traceText.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("ip=", StringComparison.OrdinalIgnoreCase)) result.EgressIp = trimmed.Substring(3);
                        else if (trimmed.StartsWith("loc=", StringComparison.OrdinalIgnoreCase)) result.EgressLocation = trimmed.Substring(4).ToUpperInvariant();
                    }
                }
            }
            catch
            {
                // Egress diagnostics are best-effort
            }

            var geminiUrl = customGeminiUrl ?? GeminiProbeEndpoint;
            var geminiSw = Stopwatch.StartNew();
            try
            {
                using var geminiReq = new HttpRequestMessage(HttpMethod.Get, geminiUrl);
                using var geminiResp = await client.SendAsync(geminiReq);
                geminiSw.Stop();
                result.GeminiLatencyMs = geminiSw.ElapsedMilliseconds;
                result.GeminiLatencyMeasured = true;

                var geminiBody = await geminiResp.Content.ReadAsStringAsync();

                // Regions that Gemini rejects outright: CN, RU, IR, KP, CU, SY
                bool isGeminiBlockedRegion = result.EgressLocation is "CN" or "RU" or "IR" or "KP" or "CU" or "SY";

                if (isGeminiBlockedRegion)
                {
                    sw.Stop();
                    result.LatencyMs = sw.ElapsedMilliseconds;
                    result.GeminiStatus = ConnectivityStatus.Failed;
                    result.AntigravityStatus = ConnectivityStatus.NotTested;
                    result.GeminiMessage = $"出口地区 [{result.FormattedLocation}] 不受 Google Gemini 支持";
                    result.AntigravityMessage = "已跳过（前置 Gemini 服务受阻）";
                    return result;
                }

                result.GeminiStatus = ConnectivityStatus.Success;
                result.GeminiMessage = "基础链路通畅，网关响应正常";
            }
            catch (Exception ex)
            {
                geminiSw.Stop();
                result.GeminiLatencyMs = geminiSw.ElapsedMilliseconds;
                sw.Stop();
                result.LatencyMs = sw.ElapsedMilliseconds;
                result.GeminiStatus = ConnectivityStatus.Failed;
                result.AntigravityStatus = ConnectivityStatus.NotTested;
                result.GeminiMessage = $"连接失败: {ex.Message}";
                result.AntigravityMessage = "已跳过（前置 Gemini 服务不可达）";
                return result;
            }

            var accountList = (candidateAccounts ?? accountPoolService?.Accounts)?
                .Where(a => !string.IsNullOrWhiteSpace(a.CredentialBlob))
                .ToList();

            if (accountList == null || accountList.Count == 0)
            {
                sw.Stop();
                result.LatencyMs = sw.ElapsedMilliseconds;
                result.AntigravityStatus = ConnectivityStatus.NotTested;
                result.AntigravityMessage = "号池未添加账号，已跳过（Antigravity 探针需授权凭据，未授权永久返回 401）";
                return result;
            }

            var agUrl = customAntigravityUrl ?? AntigravityProbeEndpoint;
            ProxyTestResult? lastAgResult = null;

            // Try accounts not currently running first; fall through to the rest when a credential fails
            var orderedAccounts = accountList.OrderBy(a => a.IsRunning ? 1 : 0).ToList();

            foreach (var chosenAccount in orderedAccounts)
            {
                result.TestedAccountEmail = chosenAccount.Email;
                var (accessToken, refreshToken, expiry) = ExtractTokens(chosenAccount.CredentialBlob);

                // Refresh when the token is missing or expires within two minutes
                if ((string.IsNullOrWhiteSpace(accessToken) || (expiry.HasValue && expiry.Value <= DateTimeOffset.UtcNow.AddMinutes(2))) && !string.IsNullOrWhiteSpace(refreshToken))
                {
                    if (accountPoolService != null && !string.IsNullOrWhiteSpace(chosenAccount.Id))
                    {
                        try
                        {
                            var (refreshed, _) = await accountPoolService.RefreshTokenAsync(chosenAccount.Id, config, autoRetry: false);
                            if (refreshed)
                            {
                                var refreshedAcc = accountPoolService.Accounts.FirstOrDefault(a => a.Id == chosenAccount.Id);
                                if (refreshedAcc != null)
                                {
                                    var (newAt, _, _) = ExtractTokens(refreshedAcc.CredentialBlob);
                                    if (!string.IsNullOrWhiteSpace(newAt)) accessToken = newAt;
                                }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        var directToken = await RefreshAccessTokenDirectAsync(client, refreshToken);
                        if (!string.IsNullOrWhiteSpace(directToken)) accessToken = directToken;
                    }
                }

                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    result.AntigravityStatus = ConnectivityStatus.Failed;
                    result.AntigravityMessage = $"抽测账号 [{chosenAccount.Email}] 凭据损坏或缺少 Access Token";
                    lastAgResult = result;
                    continue;
                }

                var agSw = Stopwatch.StartNew();
                try
                {
                    using var agRequest = new HttpRequestMessage(HttpMethod.Post, agUrl)
                    {
                        Content = new StringContent("{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}", System.Text.Encoding.UTF8, "application/json")
                    };
                    agRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    agRequest.Headers.TryAddWithoutValidation("User-Agent", "antigravity/2.26.0");

                    using var agResp = await client.SendAsync(agRequest);
                    agSw.Stop();
                    result.AntigravityLatencyMs = agSw.ElapsedMilliseconds;
                    result.AntigravityLatencyMeasured = true;

                    var agBody = await agResp.Content.ReadAsStringAsync();

                    // On 401/UNAUTHENTICATED, refresh once and retry the same account
                    if ((agResp.StatusCode == HttpStatusCode.Unauthorized || agBody.Contains("UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(refreshToken))
                    {
                        string? refreshedToken = null;
                        if (accountPoolService != null && !string.IsNullOrWhiteSpace(chosenAccount.Id))
                        {
                            var (refreshed, _) = await accountPoolService.RefreshTokenAsync(chosenAccount.Id, config, autoRetry: false);
                            if (refreshed)
                            {
                                var refreshedAcc = accountPoolService.Accounts.FirstOrDefault(a => a.Id == chosenAccount.Id);
                                if (refreshedAcc != null)
                                {
                                    var (newAt, _, _) = ExtractTokens(refreshedAcc.CredentialBlob);
                                    refreshedToken = newAt;
                                }
                            }
                        }
                        else
                        {
                            refreshedToken = await RefreshAccessTokenDirectAsync(client, refreshToken);
                        }

                        if (!string.IsNullOrWhiteSpace(refreshedToken))
                        {
                            accessToken = refreshedToken;
                            agSw.Restart();
                            using var retryAgReq = new HttpRequestMessage(HttpMethod.Post, agUrl)
                            {
                                Content = new StringContent("{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}", System.Text.Encoding.UTF8, "application/json")
                            };
                            retryAgReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                            retryAgReq.Headers.TryAddWithoutValidation("User-Agent", "antigravity/2.26.0");
                            using var retryResp = await client.SendAsync(retryAgReq);
                            agSw.Stop();
                            result.AntigravityLatencyMs = agSw.ElapsedMilliseconds;

                            var retryBody = await retryResp.Content.ReadAsStringAsync();
                            var evaluated = EvaluateAntigravityResponse(result, sw, retryResp, retryBody, chosenAccount.Email);
                            if (evaluated.AntigravityStatus == ConnectivityStatus.Success || evaluated.AntigravityMessage.Contains("地区受阻"))
                            {
                                return evaluated;
                            }
                            lastAgResult = evaluated;
                            continue;
                        }
                    }

                    var eval = EvaluateAntigravityResponse(result, sw, agResp, agBody, chosenAccount.Email);
                    if (eval.AntigravityStatus == ConnectivityStatus.Success || eval.AntigravityMessage.Contains("地区受阻"))
                    {
                        return eval;
                    }
                    lastAgResult = eval;
                }
                catch (Exception ex)
                {
                    agSw.Stop();
                    result.AntigravityLatencyMs = agSw.ElapsedMilliseconds;
                    sw.Stop();
                    result.LatencyMs = sw.ElapsedMilliseconds;
                    result.AntigravityStatus = ConnectivityStatus.Failed;
                    result.AntigravityMessage = $"连接异常: {ex.Message}";
                    return result;
                }
            }

            sw.Stop();
            result.LatencyMs = sw.ElapsedMilliseconds;
            return lastAgResult ?? result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.LatencyMs = sw.ElapsedMilliseconds;
            result.GeminiStatus = ConnectivityStatus.Failed;
            result.AntigravityStatus = ConnectivityStatus.NotTested;
            result.GeminiMessage = $"代理套接字创建失败: {ex.Message}";
            result.AntigravityMessage = "已跳过";
            return result;
        }
    }

    /// <summary>Classifies the Antigravity probe response: location block, credential failure, success, or other.</summary>
    private static ProxyTestResult EvaluateAntigravityResponse(
        ProxyTestResult result,
        Stopwatch totalSw,
        HttpResponseMessage agResp,
        string agBody,
        string accountEmail)
    {
        result.LatencyMs = totalSw.ElapsedMilliseconds;

        // Region block: 400/403 with location markers, or HK/MO/CN egress
        bool isLocationBlocked = (agResp.StatusCode == HttpStatusCode.BadRequest || agResp.StatusCode == HttpStatusCode.Forbidden) &&
                                 (agBody.Contains("User location is not supported", StringComparison.OrdinalIgnoreCase) ||
                                  agBody.Contains("FAILED_PRECONDITION", StringComparison.OrdinalIgnoreCase)) ||
                                 agBody.Contains("User location is not supported", StringComparison.OrdinalIgnoreCase) ||
                                 result.EgressLocation is "HK" or "MO" or "CN";

        if (isLocationBlocked)
        {
            totalSw.Stop();
            result.AntigravityStatus = ConnectivityStatus.Failed;
            result.AntigravityMessage = $"地区受阻 (400): 当前出口 [{result.FormattedLocation}] 不受 Antigravity 官方支持 (建议切换美/日/新/台原生节点)";
            return result;
        }

        if (agResp.StatusCode == HttpStatusCode.Unauthorized ||
            agBody.Contains("invalid_token", StringComparison.OrdinalIgnoreCase) ||
            agBody.Contains("UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase))
        {
            result.AntigravityStatus = ConnectivityStatus.Failed;
            result.AntigravityMessage = $"凭据失效 (401 Unauthorized · 账号 [{accountEmail}] 需要重新登录授权)";
            return result;
        }

        if (agResp.IsSuccessStatusCode)
        {
            totalSw.Stop();
            result.AntigravityStatus = ConnectivityStatus.Success;
            result.AntigravityMessage = $"服务正常 (200 OK · 抽测账号: {accountEmail})";
            return result;
        }

        totalSw.Stop();
        result.AntigravityStatus = ConnectivityStatus.Failed;
        result.AntigravityMessage = $"响应异常 (HTTP {(int)agResp.StatusCode})：{agBody.Trim()}";
        return result;
    }

    private static async Task<string?> RefreshAccessTokenDirectAsync(HttpClient client, string refreshToken)
    {
        try
        {
            var oauthClient = AccountPoolService.LoadOAuthClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, AccountPoolService.OAuthTokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = oauthClient.ClientId,
                    ["client_secret"] = oauthClient.ClientSecret,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken
                })
            };
            using var resp = await client.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                var node = JsonNode.Parse(body);
                return node?["access_token"]?.ToString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Extracts access token, refresh token and expiry; accepts them nested under "token" or at the root.</summary>
    public static (string? accessToken, string? refreshToken, DateTimeOffset? expiry) ExtractTokens(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) return (null, null, null);
        try
        {
            var node = JsonNode.Parse(blob);
            var tokenNode = node?["token"];
            var accessToken = tokenNode?["access_token"]?.ToString() ?? node?["access_token"]?.ToString();
            var refreshToken = tokenNode?["refresh_token"]?.ToString() ?? node?["refresh_token"]?.ToString();
            var expiryStr = tokenNode?["expiry"]?.ToString() ?? node?["expiry"]?.ToString();
            DateTimeOffset? expiry = null;
            if (!string.IsNullOrEmpty(expiryStr) && DateTimeOffset.TryParse(expiryStr, out var dto))
            {
                expiry = dto;
            }
            return (accessToken, refreshToken, expiry);
        }
        catch
        {
            return (null, null, null);
        }
    }

    /// <summary>Writes Antigravity-Proxy config.json into <paramref name="targetDir"/>.</summary>
    public static void WriteProxyConfigFile(string targetDir, ProxyConfig config)
    {
        var configPath = Path.Combine(targetDir, "config.json");

        var root = new JsonObject
        {
            ["_version"] = "2.4",
            ["_comment"] = "Gravityless 托管代理配置",
            ["child_injection"] = true,
            ["child_injection_mode"] = "filtered",
            ["log_level"] = "info",
            ["traffic_logging"] = false
        };

        if (config.Mode == ProxyMode.Manual)
        {
            var proxyObj = new JsonObject
            {
                ["type"] = config.Protocol.ToString().ToLower(),
                ["host"] = config.Host,
                ["port"] = config.Port
            };
            if (!string.IsNullOrEmpty(config.Username))
            {
                proxyObj["username"] = config.Username;
                proxyObj["password"] = config.Password;
            }
            root["proxy"] = proxyObj;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(configPath, root.ToJsonString(options));
    }
}
