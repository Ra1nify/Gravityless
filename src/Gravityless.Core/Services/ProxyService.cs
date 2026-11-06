using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
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

    /// <summary>
    /// Short label of the sampled account (its display name, falling back to the local part of
    /// the address). Shown in the UI so a full email is not exposed in a status report.
    /// </summary>
    public string? TestedAccountAlias { get; set; }

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
                // System proxy: inspect WinINet registry for explicit endpoint and apply credentials
                var sys = GetSystemProxyDetails();
                if (sys.Enabled && !string.IsNullOrWhiteSpace(sys.Host) && sys.Port > 0)
                {
                    var proxyUri = $"{sys.Protocol.ToString().ToLowerInvariant()}://{sys.Host}:{sys.Port}";
                    var webProxy = new WebProxy(proxyUri);
                    if (!string.IsNullOrEmpty(config.Username))
                    {
                        webProxy.Credentials = new NetworkCredential(config.Username, config.Password);
                    }
                    handler.Proxy = webProxy;
                    handler.UseProxy = true;
                }
                else
                {
                    handler.UseProxy = true;
                    if (!string.IsNullOrEmpty(config.Username))
                    {
                        handler.DefaultProxyCredentials = new NetworkCredential(config.Username, config.Password);
                    }
                }
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
                .Where(a => !string.IsNullOrWhiteSpace(a.RefreshToken))
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
                result.TestedAccountAlias = AccountAlias(chosenAccount);
                var accessToken = chosenAccount.AccessToken;
                var refreshToken = chosenAccount.RefreshToken;
                var expiry = chosenAccount.AccessTokenExpiresAt;

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
                                    var newAt = refreshedAcc.AccessToken;
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
                    result.AntigravityMessage = $"抽测账号 [{result.TestedAccountAlias}] 凭据损坏或缺少 Access Token";
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
                                    refreshedToken = refreshedAcc.AccessToken;
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
                            var evaluated = EvaluateAntigravityResponse(result, sw, retryResp, retryBody, result.TestedAccountAlias ?? chosenAccount.Email);
                            if (evaluated.AntigravityStatus == ConnectivityStatus.Success || evaluated.AntigravityMessage.Contains("地区受阻"))
                            {
                                return evaluated;
                            }
                            lastAgResult = evaluated;
                            continue;
                        }
                    }

                    var eval = EvaluateAntigravityResponse(result, sw, agResp, agBody, result.TestedAccountAlias ?? chosenAccount.Email);
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
    /// <summary>
    /// Short label for an account: its display name, or the local part of the address when the
    /// name is empty. Keeps reports readable without printing a full email.
    /// </summary>
    private static string AccountAlias(AccountProfile account)
    {
        if (!string.IsNullOrWhiteSpace(account.DisplayName)) return account.DisplayName.Trim();

        var at = account.Email.IndexOf('@');
        return at > 0 ? account.Email[..at] : account.Email;
    }

    private static ProxyTestResult EvaluateAntigravityResponse(
        ProxyTestResult result,
        Stopwatch totalSw,
        HttpResponseMessage agResp,
        string agBody,
        string accountAlias)
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
            result.AntigravityMessage = $"凭据失效 (401 Unauthorized · 账号 [{accountAlias}] 需要重新登录授权)";
            return result;
        }

        if (agResp.IsSuccessStatusCode)
        {
            totalSw.Stop();
            result.AntigravityStatus = ConnectivityStatus.Success;
            result.AntigravityMessage = $"服务正常 (200 OK · 抽测账号: {accountAlias})";
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
        else if (config.Mode == ProxyMode.System)
        {
            var sys = GetSystemProxyDetails();
            if (sys.Enabled && !string.IsNullOrWhiteSpace(sys.Host) && sys.Port > 0)
            {
                var proxyObj = new JsonObject
                {
                    ["type"] = sys.Protocol.ToString().ToLowerInvariant(),
                    ["host"] = sys.Host,
                    ["port"] = sys.Port
                };
                if (!string.IsNullOrEmpty(config.Username))
                {
                    proxyObj["username"] = config.Username;
                    proxyObj["password"] = config.Password;
                }
                root["proxy"] = proxyObj;
            }
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(configPath, root.ToJsonString(options));
    }

    /// <summary>
    /// Reads Windows system proxy configuration from the WinINet registry.
    /// </summary>
    public static SystemProxyDetails GetSystemProxyDetails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SystemProxyDetails(false, null, null, 0, ProxyProtocol.Http, null);
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key == null) return new SystemProxyDetails(false, null, null, 0, ProxyProtocol.Http, null);

            var proxyEnable = key.GetValue("ProxyEnable");
            var enabled = proxyEnable is int val && val == 1;
            var proxyServer = key.GetValue("ProxyServer") as string;
            var proxyOverride = key.GetValue("ProxyOverride") as string;

            if (!enabled || string.IsNullOrWhiteSpace(proxyServer))
            {
                return new SystemProxyDetails(false, proxyServer, null, 0, ProxyProtocol.Http, proxyOverride);
            }

            string? targetEndpoint = null;
            var protocol = ProxyProtocol.Http;

            if (proxyServer.Contains('='))
            {
                var parts = proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var httpPart = parts.FirstOrDefault(p => p.StartsWith("http=", StringComparison.OrdinalIgnoreCase));
                var httpsPart = parts.FirstOrDefault(p => p.StartsWith("https=", StringComparison.OrdinalIgnoreCase));
                var socksPart = parts.FirstOrDefault(p => p.StartsWith("socks=", StringComparison.OrdinalIgnoreCase));

                if (httpPart != null)
                {
                    targetEndpoint = httpPart["http=".Length..].Trim();
                    protocol = ProxyProtocol.Http;
                }
                else if (httpsPart != null)
                {
                    targetEndpoint = httpsPart["https=".Length..].Trim();
                    protocol = ProxyProtocol.Https;
                }
                else if (socksPart != null)
                {
                    targetEndpoint = socksPart["socks=".Length..].Trim();
                    protocol = ProxyProtocol.Socks5;
                }
                else if (parts.Length > 0)
                {
                    var kv = parts[0].Split('=', 2);
                    targetEndpoint = kv.Length == 2 ? kv[1] : kv[0];
                }
            }
            else
            {
                targetEndpoint = proxyServer.Trim();
            }

            if (string.IsNullOrWhiteSpace(targetEndpoint))
            {
                return new SystemProxyDetails(false, proxyServer, null, 0, ProxyProtocol.Http, proxyOverride);
            }

            if (targetEndpoint.Contains("://"))
            {
                if (Uri.TryCreate(targetEndpoint, UriKind.Absolute, out var uri))
                {
                    var detectedProto = uri.Scheme.ToLowerInvariant() switch
                    {
                        "socks5" => ProxyProtocol.Socks5,
                        "https" => ProxyProtocol.Https,
                        _ => ProxyProtocol.Http
                    };
                    return new SystemProxyDetails(true, proxyServer, uri.Host, uri.Port, detectedProto, proxyOverride);
                }
            }

            var lastColon = targetEndpoint.LastIndexOf(':');
            if (lastColon > 0 && int.TryParse(targetEndpoint[(lastColon + 1)..], out var port))
            {
                var host = targetEndpoint[..lastColon].Trim('[', ']');
                return new SystemProxyDetails(true, proxyServer, host, port, protocol, proxyOverride);
            }

            return new SystemProxyDetails(true, proxyServer, targetEndpoint, 80, protocol, proxyOverride);
        }
        catch
        {
            return new SystemProxyDetails(false, null, null, 0, ProxyProtocol.Http, null);
        }
    }

    /// <summary>
    /// Configures environment variables on a ProcessStartInfo for Antigravity and language_server.exe.
    /// Handles Direct (clean isolation), Manual, and System proxy modes with optional credentials.
    /// </summary>
    public static void ApplyProxyToProcess(ProcessStartInfo psi, ProxyConfig? proxy)
    {
        if (proxy == null || proxy.Mode == ProxyMode.Direct)
        {
            // Direct mode: Clean direct connection matching stock Antigravity.
            ClearProxyEnvironmentVariables(psi);
            return;
        }

        string? proxyUrl = null;
        string? bypassList = proxy.BypassList;

        if (proxy.Mode == ProxyMode.Manual)
        {
            var auth = (!string.IsNullOrEmpty(proxy.Username))
                ? $"{Uri.EscapeDataString(proxy.Username)}:{Uri.EscapeDataString(proxy.Password)}@"
                : "";
            var scheme = proxy.Protocol.ToString().ToLowerInvariant();
            proxyUrl = $"{scheme}://{auth}{proxy.Host}:{proxy.Port}";
        }
        else if (proxy.Mode == ProxyMode.System)
        {
            var sys = GetSystemProxyDetails();
            if (sys.Enabled && !string.IsNullOrWhiteSpace(sys.Host) && sys.Port > 0)
            {
                var auth = (!string.IsNullOrEmpty(proxy.Username))
                    ? $"{Uri.EscapeDataString(proxy.Username)}:{Uri.EscapeDataString(proxy.Password)}@"
                    : "";
                var scheme = sys.Protocol.ToString().ToLowerInvariant();
                proxyUrl = $"{scheme}://{auth}{sys.Host}:{sys.Port}";
                if (!string.IsNullOrWhiteSpace(sys.Bypass))
                {
                    bypassList = string.IsNullOrWhiteSpace(bypassList)
                        ? sys.Bypass
                        : $"{sys.Bypass};{bypassList}";
                }
            }
        }

        if (!string.IsNullOrEmpty(proxyUrl))
        {
            psi.EnvironmentVariables["ALL_PROXY"] = proxyUrl;
            psi.EnvironmentVariables["HTTP_PROXY"] = proxyUrl;
            psi.EnvironmentVariables["HTTPS_PROXY"] = proxyUrl;
            psi.EnvironmentVariables["all_proxy"] = proxyUrl;
            psi.EnvironmentVariables["http_proxy"] = proxyUrl;
            psi.EnvironmentVariables["https_proxy"] = proxyUrl;

            // Loopback must always be excluded from proxy to prevent local IPC routing failure
            var finalBypass = string.IsNullOrWhiteSpace(bypassList)
                ? "localhost,127.0.0.1,::1,<-loopback>"
                : $"localhost,127.0.0.1,::1,<-loopback>,{bypassList.Replace(';', ',')}";

            psi.EnvironmentVariables["NO_PROXY"] = finalBypass;
            psi.EnvironmentVariables["no_proxy"] = finalBypass;
        }
        else
        {
            ClearProxyEnvironmentVariables(psi);
        }
    }

    private static void ClearProxyEnvironmentVariables(ProcessStartInfo psi)
    {
        psi.EnvironmentVariables.Remove("ALL_PROXY");
        psi.EnvironmentVariables.Remove("all_proxy");
        psi.EnvironmentVariables.Remove("HTTP_PROXY");
        psi.EnvironmentVariables.Remove("http_proxy");
        psi.EnvironmentVariables.Remove("HTTPS_PROXY");
        psi.EnvironmentVariables.Remove("https_proxy");
        psi.EnvironmentVariables.Remove("NO_PROXY");
        psi.EnvironmentVariables.Remove("no_proxy");
    }
}

public record SystemProxyDetails(
    bool Enabled,
    string? Server,
    string? Host,
    int Port,
    ProxyProtocol Protocol,
    string? Bypass
);
