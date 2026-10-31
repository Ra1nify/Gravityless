using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Gravityless.App.Dialogs;
using Gravityless.App.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace Gravityless.App;

public partial class App : Application
{
    /// <summary>Sandbox routing table written by AccountPoolService before a capture starts.</summary>
    private const string RouteFileName = "capture_route.json";

    /// <summary>Dark/light palettes; identical key sets, so either can replace the other wholesale.</summary>
    private const string PaletteDarkSource = "Resources/Palette.Dark.xaml";
    private const string PaletteLightSource = "Resources/Palette.Light.xaml";

    /// <summary>"auto" | "light" | "dark", set from the appearance page.</summary>
    public static string ThemeMode { get; private set; } = "auto";

    protected override void OnStartup(StartupEventArgs e)
    {
        // Protocol activation: a single "antigravity:" argument means the process is only a callback carrier.
        var url = e.Args.FirstOrDefault(a => a.StartsWith("antigravity:", StringComparison.OrdinalIgnoreCase));
        if (url != null)
        {
            try
            {
                HandleProtocolCallback(url);
            }
            catch
            {
                // System-initiated launch: never surface errors to the user.
            }

            // Exit without creating any window.
            Shutdown(0);
            return;
        }

        // Apply the persisted mode before subscribing to system changes.
        ThemeMode = ReadThemeModeFromDisk();
        ApplySystemTheme(ResolveTheme(ThemeMode));
        ApplicationThemeManager.Changed += OnSystemThemeChanged;

        LocalizationService.Initialize();

        // Last-resort handler: an exception escaping an async event handler terminates the
        // process by default. Report it and keep running instead.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    /// <summary>
    /// Handles exceptions that escaped a UI-thread handler. These are almost always a failed
    /// I/O or network call inside an <c>async void</c> handler, which has no caller to observe it.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            LogUnhandled(e.Exception, "dispatcher");
            ModernDialog.ShowError(
                LocalizationService.GetString("Common_Error", "错误"),
                e.Exception.Message);
        }
        catch { }

        // Keep the app alive; state files are written atomically, so a half-finished
        // operation leaves the previous state intact.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            LogUnhandled(e.ExceptionObject as Exception, "domain");
        }
        catch { }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            LogUnhandled(e.Exception, "task");
            e.SetObserved();
        }
        catch { }
    }

    /// <summary>Appends an unhandled exception to <c>data\logs\unhandled.log</c>.</summary>
    private static void LogUnhandled(Exception? ex, string source)
    {
        if (ex == null) return;

        try
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "logs");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(dir, "unhandled.log"), line);
        }
        catch { }
    }

    /// <summary>Switches theme mode and applies it immediately.</summary>
    public static void SetThemeMode(string mode)
    {
        ThemeMode = NormalizeThemeMode(mode);
        ApplySystemTheme(ResolveTheme(ThemeMode));
    }

    private static ApplicationTheme ResolveTheme(string mode) => mode switch
    {
        "light" => ApplicationTheme.Light,
        "dark" => ApplicationTheme.Dark,
        _ => MapSystemTheme(ApplicationThemeManager.GetSystemTheme())
    };

    /// <summary>GetSystemTheme returns SystemTheme, theme resources need ApplicationTheme.</summary>
    private static ApplicationTheme MapSystemTheme(SystemTheme theme) => theme switch
    {
        SystemTheme.Light => ApplicationTheme.Light,
        SystemTheme.Dark => ApplicationTheme.Dark,
        SystemTheme.HCWhite or SystemTheme.HCBlack or SystemTheme.HC1 or SystemTheme.HC2
            => ApplicationTheme.HighContrast,
        _ => ApplicationTheme.Dark
    };

    private static string NormalizeThemeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "auto"
    };

    /// <summary>Reads ThemeMode from data/theme_config.json; runs before any window exists.</summary>
    private static string ReadThemeModeFromDisk()
    {
        try
        {
            var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "theme_config.json");
            if (!File.Exists(file))
            {
                return "auto";
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty("ThemeMode", out var value)
                ? NormalizeThemeMode(value.GetString())
                : "auto";
        }
        catch
        {
            return "auto";
        }
    }

    /// <summary>Re-entrancy guard: ApplicationThemeManager.Apply raises Changed, whose handler re-enters ApplySystemTheme.</summary>
    private static bool _isApplyingTheme;

    /// <summary>Syncs WPF-UI theme, accent, and our palette. HighContrast falls back to the dark palette.</summary>
    private static void ApplySystemTheme(ApplicationTheme theme)
    {
        if (_isApplyingTheme)
        {
            return;
        }

        _isApplyingTheme = true;
        try
        {
            var wpfUiTheme = theme == ApplicationTheme.Unknown ? ApplicationTheme.Dark : theme;

            foreach (var dictionary in Current.Resources.MergedDictionaries.OfType<ThemesDictionary>())
            {
                dictionary.Theme = wpfUiTheme;
            }

            // Pass the current backdrop explicitly; the Apply default (Mica) would reset a Tabbed choice.
            ApplicationThemeManager.Apply(wpfUiTheme, ThemeService.CurrentBackdropType, true);

            ApplyPalette(theme == ApplicationTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark);

            foreach (var window in Current.Windows.OfType<Wpf.Ui.Controls.FluentWindow>())
            {
                ThemeService.ApplyTheme(window);          // Backdrop + dialog overlay
                if (window is MainWindow mainWindow)
                {
                    mainWindow.RefreshWindowSurface();    // Main window overlay
                }
            }
        }
        finally
        {
            _isApplyingTheme = false;
        }
    }

    /// <summary>Replaces the palette dictionary; DynamicResource references refresh in place.</summary>
    private static void ApplyPalette(ApplicationTheme theme)
    {
        var target = theme == ApplicationTheme.Light ? PaletteLightSource : PaletteDarkSource;
        var merged = Current.Resources.MergedDictionaries;

        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var source = merged[i].Source?.OriginalString;
            if (source != null
                && (source.EndsWith("Palette.Dark.xaml", StringComparison.OrdinalIgnoreCase)
                    || source.EndsWith("Palette.Light.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                merged.RemoveAt(i);
            }
        }

        // Absolute pack URI avoids relative-path resolution differences between hosts.
        merged.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{target}", UriKind.Absolute)
        });
    }

    /// <summary>Raised by WPF-UI on system theme or accent changes.</summary>
    private static void OnSystemThemeChanged(ApplicationTheme currentApplicationTheme, System.Windows.Media.Color systemAccent)
    {
        // Only "auto" follows the system theme.
        if (ThemeMode == "auto")
        {
            ApplySystemTheme(currentApplicationTheme);
        }

        // WPF-UI already supplies the accent color in the event.
        if (Current is App app)
        {
            app.ForwardSystemAccent(systemAccent);
        }
    }

    /// <summary>Pushes the accent color to the daemon, which broadcasts it to the injector.</summary>
    private void ForwardSystemAccent(System.Windows.Media.Color accent)
    {
        var hex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        try
        {
            Daemon?.PushSystemAccent(hex);
        }
        catch
        {
            // Accent sync failure must not break the theme switch.
        }
    }

    /// <summary>Injected by MainWindow for theme/accent broadcast.</summary>
    public Gravityless.Core.Services.GravitylessDaemon? Daemon { get; set; }

    /// <summary>
    /// Handles an antigravity:// callback: forward it to the capture sandbox, which completes the OAuth
    /// exchange with the PKCE code_verifier held in that instance's process memory.
    /// </summary>
    private static void HandleProtocolCallback(string url)
    {
        if (TryForwardToCaptureSandbox(url))
        {
            return;
        }

        // No capture in progress: keep the on-disk diagnostic only.
        try
        {
            var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "temp");
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "protocol_callback.txt"), url);
        }
        catch { }
    }

    /// <summary>
    /// Relaunches Antigravity with the sandbox's --user-data-dir. Electron's requestSingleInstanceLock is
    /// keyed on userData, so the new process exits at once and the sandbox receives the URL via second-instance.
    /// </summary>
    private static bool TryForwardToCaptureSandbox(string url)
    {
        try
        {
            var routeFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "temp", RouteFileName);
            if (!File.Exists(routeFile))
            {
                return false;
            }

            string? exePath;
            string? userDataDir;
            using (var doc = JsonDocument.Parse(File.ReadAllText(routeFile)))
            {
                exePath = doc.RootElement.TryGetProperty("exePath", out var exe) ? exe.GetString() : null;
                userDataDir = doc.RootElement.TryGetProperty("userDataDir", out var ud) ? ud.GetString() : null;
            }

            if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(userDataDir))
            {
                return false;
            }

            // Forwarding into a removed sandbox would just open a blank window.
            if (!File.Exists(exePath) || !Directory.Exists(userDataDir))
            {
                return false;
            }

            // Strip quotes so the URL cannot break argument quoting.
            var safeUrl = url.Replace("\"", string.Empty);

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--user-data-dir=\"{userDataDir}\" \"{safeUrl}\"",
                UseShellExecute = false,
                CreateNoWindow = false
            };

            using var forwarder = Process.Start(psi);
            return forwarder != null;
        }
        catch
        {
            return false;
        }
    }
}
