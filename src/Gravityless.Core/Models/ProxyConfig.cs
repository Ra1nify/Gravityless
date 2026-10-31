namespace Gravityless.Core.Models;

public enum ProxyMode
{
    Direct,     // No proxy
    System,     // Follow the Windows system proxy
    Manual      // Explicit proxy (HTTP/SOCKS5)
}

public enum ProxyProtocol
{
    Socks5,
    Http,
    Https
}

public class ProxyConfig
{
    public ProxyMode Mode { get; set; } = ProxyMode.System;
    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.Socks5;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3993;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string BypassList { get; set; } = "localhost;127.0.0.1;<local>";
}
