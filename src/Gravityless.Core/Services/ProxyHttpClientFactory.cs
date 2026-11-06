using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

/// <summary>
/// Builds HttpClient instances that honour the app's own proxy setting.
///
/// The proxy config was previously applied only to launched Antigravity processes (via
/// HTTP_PROXY environment variables), so Gravityless's own network calls — core update
/// checks, app update checks and installer downloads — bypassed it entirely. A user who
/// needed a proxy to reach GitHub could see the setting work for Antigravity while the
/// app itself reported a network error.
///
/// A new client is created per call on purpose: the handler differs by configuration,
/// and the proxy setting can change at runtime without restarting. These requests are
/// infrequent (one manifest fetch, one download), so socket reuse has no value here.
/// Callers own and dispose the returned client.
/// </summary>
public static class ProxyHttpClientFactory
{
    private const string UserAgent = "Gravityless";

    /// <summary>Creates a client for the given proxy config and timeout.</summary>
    public static HttpClient Create(ProxyConfig? config, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        ApplyProxy(handler, config);

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// Applies the three proxy modes and the bypass list. Bypass entries are mapped to
    /// <see cref="WebProxy.BypassArrayList"/>: without this, "localhost;127.0.0.1" in the
    /// config would still be sent through the proxy, which breaks local endpoints.
    /// </summary>
    private static void ApplyProxy(SocketsHttpHandler handler, ProxyConfig? config)
    {
        var mode = config?.Mode ?? ProxyMode.System;

        switch (mode)
        {
            case ProxyMode.Direct:
                handler.UseProxy = false;
                return;

            case ProxyMode.Manual:
                var cfg = config!;
                var scheme = cfg.Protocol switch
                {
                    ProxyProtocol.Socks5 => "socks5",
                    ProxyProtocol.Https => "https",
                    _ => "http",
                };

                var uri = $"{scheme}://{cfg.Host}:{cfg.Port}";
                var webProxy = new WebProxy(uri);

                if (!string.IsNullOrWhiteSpace(cfg.Username))
                {
                    webProxy.Credentials = new NetworkCredential(cfg.Username, cfg.Password);
                }

                ApplyBypass(webProxy, cfg.BypassList);
                handler.Proxy = webProxy;
                handler.UseProxy = true;
                return;

            default:
                // System: inspect WinINet registry for explicit endpoint and apply credentials
                var sys = ProxyService.GetSystemProxyDetails();
                if (sys.Enabled && !string.IsNullOrWhiteSpace(sys.Host) && sys.Port > 0)
                {
                    var sysScheme = sys.Protocol switch
                    {
                        ProxyProtocol.Socks5 => "socks5",
                        ProxyProtocol.Https => "https",
                        _ => "http",
                    };
                    var sysUri = $"{sysScheme}://{sys.Host}:{sys.Port}";
                    var sysWebProxy = new WebProxy(sysUri);
                    if (!string.IsNullOrWhiteSpace(config?.Username))
                    {
                        sysWebProxy.Credentials = new NetworkCredential(config.Username, config.Password);
                    }
                    ApplyBypass(sysWebProxy, sys.Bypass);
                    if (!string.IsNullOrWhiteSpace(config?.BypassList))
                    {
                        ApplyBypass(sysWebProxy, config.BypassList);
                    }
                    handler.Proxy = sysWebProxy;
                    handler.UseProxy = true;
                    return;
                }
                handler.UseProxy = true;
                return;
        }
    }

    private static void ApplyBypass(WebProxy proxy, string? bypassList)
    {
        if (string.IsNullOrWhiteSpace(bypassList)) return;

        foreach (var entry in bypassList.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // WebProxy understands "<local>" as "no proxy for bare hostnames"; other entries
            // are treated as regex fragments. Escape them so dots and dashes stay literal.
            if (entry == "<local>")
            {
                proxy.BypassProxyOnLocal = true;
            }
            else
            {
                var pattern = "^" + Regex.Escape(entry).Replace(@"\*", ".*") + "$";
                proxy.BypassArrayList.Add(pattern);
            }
        }
    }
}
