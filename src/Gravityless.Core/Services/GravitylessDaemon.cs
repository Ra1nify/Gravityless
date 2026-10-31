using System.Net;
using System.Text;
using System.Text.Json;
using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

public class GravitylessDaemon : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<HttpListenerResponse> _sseClients = [];

    /// <summary>
    /// Upper bound on concurrent event streams. Each running instance opens one; this keeps a
    /// leaked connection from growing the list without limit.
    /// </summary>
    private const int MaxSseClients = 64;
    private readonly object _lock = new();

    public int Port { get; private set; }
    public bool IsRunning { get; private set; }

    public string MicaMaterial { get; set; } = "mica"; // "mica" | "tabbed" | "acrylic" | "none"
    /// <summary>"auto" (follow system) / "light" / "dark".</summary>
    public string ThemeMode { get; set; } = "auto";
    /// <summary>New UX preset: "" (off) / alabaster / tahoe / atelier / studio / planes.</summary>
    public string NewUxTheme { get; set; } = string.Empty;
    public bool AccentInteraction { get; set; } = true;

    public double NoiseAlpha { get; set; } = 0.55;
    public double NoiseFrequency { get; set; } = 0.85;
    public double NoiseTintRatio { get; set; } = 0.28;
    public double NoiseBaseAlpha { get; set; } = 0.35;
    public double MicaOpacity { get; set; } = 0.72;
    public double PopupOpacity { get; set; } = 0.60;
    public string AccentColor { get; set; } = "#4285f4";
    public string Language { get; set; } = "zh-CN";
    private readonly string _baseDir;
    private readonly string _themeConfigFile;

    /// <summary>Maps unknown material names to "mica".</summary>
    public static string NormalizeMaterial(string? material) =>
        material?.Trim().ToLowerInvariant() switch
        {
            "tabbed" => "tabbed",
            "acrylic" => "acrylic",
            "none" => "none",
            _ => "mica"
        };

    private static readonly string[] NewUxThemes = ["alabaster", "tahoe", "atelier", "studio", "planes"];

    /// <summary>Returns "" for anything outside the preset whitelist.</summary>
    public static string NormalizeNewUxTheme(string? theme)
    {
        var value = theme?.Trim().ToLowerInvariant() ?? string.Empty;
        return NewUxThemes.Contains(value) ? value : string.Empty;
    }

    public GravitylessDaemon(string? baseDir = null)
    {
        _baseDir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
        _themeConfigFile = Path.Combine(_baseDir, "data", "theme_config.json");
        LoadThemeConfig();
        _listener = new HttpListener();
    }

    public void Start()
    {
        if (IsRunning) return;

        // Try up to 20 random ports in 28790-28990, then fall back to an OS-assigned port
        var rand = new Random();
        for (int i = 0; i < 20; i++)
        {
            var testPort = rand.Next(28790, 28990);
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{testPort}/");
                _listener.Start();
                Port = testPort;
                IsRunning = true;
                break;
            }
            catch
            {
                // Port in use
            }
        }

        if (!IsRunning)
        {
            // Probe the OS for a free ephemeral port, then bind HttpListener to it
            var fallbackListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            fallbackListener.Start();
            Port = ((IPEndPoint)fallbackListener.LocalEndpoint).Port;
            fallbackListener.Stop();

            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            IsRunning = true;
        }

        Task.Run(ListenLoop);
    }

    private async Task ListenLoop()
    {
        while (!_cts.Token.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch
            {
                if (_cts.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        // CORS for the local Electron renderer
        res.Headers.Add("Access-Control-Allow-Origin", "*");
        res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        res.Headers.Add("Access-Control-Allow-Headers", "*");

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        var path = req.Url?.AbsolutePath ?? "/";

        try
        {
            switch (path)
            {
                case "/api/v1/theme.json":
                    await ServeThemeJson(res);
                    break;

                case "/api/v1/events":
                    await HandleSseConnection(context);
                    break;

                case "/api/v1/heartbeat":
                    res.StatusCode = 200;
                    await RespondString(res, "{\"status\":\"ok\"}", "application/json");
                    break;

                default:
                    res.StatusCode = 404;
                    res.Close();
                    break;
            }
        }
        catch
        {
            // Client disconnected mid-response
            try { res.Close(); } catch { }
        }
    }

    private async Task ServeThemeJson(HttpListenerResponse res)
    {
        var alpha = Math.Clamp(MicaOpacity, 0.00, 1.00);
        var popupAlpha = Math.Clamp(PopupOpacity, 0.05, 1.00);
        var data = new
        {
            material = MicaMaterial,
            opacity = alpha,
            popupOpacity = popupAlpha,
            accent = AccentColor,
            theme = ResolveEffectiveTheme(),
            newUxTheme = NewUxTheme,
            accentInteraction = AccentInteraction,
            noiseAlpha = Math.Clamp(NoiseAlpha, 0.0, 1.0),
            noiseFrequency = Math.Clamp(NoiseFrequency, 0.5, 2.0),
            noiseTintRatio = Math.Clamp(NoiseTintRatio, 0.0, 1.0),
            noiseBaseAlpha = Math.Clamp(NoiseBaseAlpha, 0.0, 1.0)
        };
        var json = JsonSerializer.Serialize(data);
        res.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
        await RespondString(res, json, "application/json");
    }

    /// <summary>Resolves "light"/"dark"; "auto" reads AppsUseLightTheme from the registry.</summary>
    private string ResolveEffectiveTheme()
    {
        if (ThemeMode == "light") return "light";
        if (ThemeMode == "dark") return "dark";

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int flag && flag != 0 ? "light" : "dark";
        }
        catch
        {
            return "dark";
        }
    }

    private async Task HandleSseConnection(HttpListenerContext context)
    {
        var res = context.Response;
        res.ContentType = "text/event-stream";
        res.Headers.Add("Cache-Control", "no-cache");
        res.Headers.Add("Connection", "keep-alive");
        res.SendChunked = true;

        lock (_lock)
        {
            // Drop the oldest stream when full: it is the most likely to be stale.
            while (_sseClients.Count >= MaxSseClients)
            {
                var oldest = _sseClients[0];
                _sseClients.RemoveAt(0);
                try { oldest.Close(); } catch { }
            }
            _sseClients.Add(res);
        }

        // Initial frame; the connection stays open until BroadcastEvent or Dispose drops it
        var initBytes = Encoding.UTF8.GetBytes("event: connected\ndata: ready\n\n");
        await res.OutputStream.WriteAsync(initBytes);
        await res.OutputStream.FlushAsync();
    }

    public void BroadcastEvent(string eventName, string data = "")
    {
        var payload = $"event: {eventName}\ndata: {data}\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);

        // Snapshot under the lock, then write outside it. A stalled client must not block
        // other callers while its socket is being written to.
        HttpListenerResponse[] snapshot;
        lock (_lock)
        {
            snapshot = _sseClients.ToArray();
        }

        var deadClients = new List<HttpListenerResponse>();
        foreach (var client in snapshot)
        {
            try
            {
                client.OutputStream.Write(bytes);
                client.OutputStream.Flush();
            }
            catch
            {
                deadClients.Add(client);
            }
        }

        if (deadClients.Count == 0) return;

        lock (_lock)
        {
            foreach (var dead in deadClients)
            {
                _sseClients.Remove(dead);
                try { dead.Close(); } catch { }
            }
        }
    }

    private void LoadThemeConfig()
    {
        try
        {
            if (File.Exists(_themeConfigFile))
            {
                var json = File.ReadAllText(_themeConfigFile, Encoding.UTF8);
                var config = JsonSerializer.Deserialize<ThemeConfig>(json);
                if (config != null)
                {
                    MicaMaterial = NormalizeMaterial(config.Material);
                    MicaOpacity = Math.Clamp(config.Opacity, 0.00, 1.00);
                    if (config.PopupOpacity > 0.00)
                        PopupOpacity = Math.Clamp(config.PopupOpacity, 0.05, 1.00);
                    if (!string.IsNullOrEmpty(config.AccentColor))
                        AccentColor = config.AccentColor;
                    ThemeMode = NormalizeThemeMode(config.ThemeMode);
                    NewUxTheme = NormalizeNewUxTheme(config.NewUxTheme);
                    AccentInteraction = config.AccentInteraction;
                    NoiseAlpha = Math.Clamp(config.NoiseAlpha, 0.0, 1.0);
                    NoiseFrequency = Math.Clamp(config.NoiseFrequency, 0.5, 2.0);
                    NoiseTintRatio = Math.Clamp(config.NoiseTintRatio, 0.0, 1.0);
                    NoiseBaseAlpha = Math.Clamp(config.NoiseBaseAlpha, 0.0, 1.0);
                }
            }
        }
        catch { }
    }

    public void SaveThemeConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(_themeConfigFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var config = new ThemeConfig
            {
                Material = MicaMaterial,
                Opacity = MicaOpacity,
                PopupOpacity = PopupOpacity,
                AccentColor = AccentColor,
                ThemeMode = ThemeMode,
                NewUxTheme = NewUxTheme,
                AccentInteraction = AccentInteraction,
                NoiseAlpha = NoiseAlpha,
                NoiseFrequency = NoiseFrequency,
                NoiseTintRatio = NoiseTintRatio,
                NoiseBaseAlpha = NoiseBaseAlpha
            };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_themeConfigFile, json, Encoding.UTF8);
        }
        catch (Exception ex) { DiagLog.Swallowed("daemon.theme-save", ex); }
    }

    /// <summary>Maps anything other than light/dark to "auto".</summary>
    public static string NormalizeThemeMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "auto"
        };

    public void UpdateThemeMode(string mode)
    {
        ThemeMode = NormalizeThemeMode(mode);
        SaveThemeConfig();
    }
    public void UpdateTheme(string material, double opacity, double? popupOpacity = null, string? accent = null, string? newUxTheme = null)
    {
        MicaMaterial = NormalizeMaterial(material);
        MicaOpacity = opacity;
        if (popupOpacity.HasValue)
            PopupOpacity = Math.Clamp(popupOpacity.Value, 0.05, 1.00);
        if (!string.IsNullOrEmpty(accent))
            AccentColor = accent;
        // null keeps the current preset; "" disables it
        if (newUxTheme != null)
            NewUxTheme = NormalizeNewUxTheme(newUxTheme);

        SaveThemeConfig();

        BroadcastThemeReload();
    }

    /// <summary>Null arguments keep the current value.</summary>
    public void UpdateNoise(double? alpha = null, double? frequency = null,
                            double? tintRatio = null, double? baseAlpha = null)
    {
        if (alpha.HasValue) NoiseAlpha = Math.Clamp(alpha.Value, 0.0, 1.0);
        if (frequency.HasValue) NoiseFrequency = Math.Clamp(frequency.Value, 0.5, 2.0);
        if (tintRatio.HasValue) NoiseTintRatio = Math.Clamp(tintRatio.Value, 0.0, 1.0);
        if (baseAlpha.HasValue) NoiseBaseAlpha = Math.Clamp(baseAlpha.Value, 0.0, 1.0);
        SaveThemeConfig();
        BroadcastThemeReload();
    }

    public void UpdateAccentInteraction(bool enabled)
    {
        AccentInteraction = enabled;
        SaveThemeConfig();
        BroadcastThemeReload();
    }

    /// <summary>Stores the system accent color, persists it, then broadcasts.</summary>
    public void PushSystemAccent(string? accentHex)
    {
        if (string.IsNullOrWhiteSpace(accentHex)) return;
        AccentColor = accentHex.Trim();
        SaveThemeConfig();
        BroadcastThemeReload();
    }

    private void BroadcastThemeReload(string? accentOverride = null)
    {
        BroadcastEvent("theme_reload", JsonSerializer.Serialize(new
        {
            material = MicaMaterial,
            opacity = MicaOpacity,
            popupOpacity = PopupOpacity,
            newUxTheme = NewUxTheme,
            accent = accentOverride ?? AccentColor,
            accentInteraction = AccentInteraction,
            noiseAlpha = Math.Clamp(NoiseAlpha, 0.0, 1.0),
            noiseFrequency = Math.Clamp(NoiseFrequency, 0.5, 2.0),
            noiseTintRatio = Math.Clamp(NoiseTintRatio, 0.0, 1.0),
            noiseBaseAlpha = Math.Clamp(NoiseBaseAlpha, 0.0, 1.0)
        }));
    }

    public void UpdateTheme(double opacity, double? popupOpacity = null, string? accent = null, string? newUxTheme = null)
    {
        UpdateTheme(MicaMaterial, opacity, popupOpacity, accent, newUxTheme);
    }

    public void UpdateLanguage(string lang)
    {
        Language = lang;
        BroadcastEvent("i18n_reload", lang);
    }

    private static async Task RespondString(HttpListenerResponse res, string content, string contentType)
    {
        res.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(content);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
        {
            foreach (var client in _sseClients)
            {
                try { client.Close(); } catch { }
            }
            _sseClients.Clear();
        }

        if (_listener.IsListening)
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
        IsRunning = false;
        GC.SuppressFinalize(this);
    }
}
