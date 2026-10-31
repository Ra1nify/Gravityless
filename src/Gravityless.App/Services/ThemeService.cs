using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Gravityless.App.Services;

/// <summary>Applies native window backdrop and surface opacity across all FluentWindow instances.</summary>
public static class ThemeService
{
    private static string _currentMaterial = "mica";
    private static double _currentOpacity = 0.72;
    private static double _currentPopupOpacity = 0.60;

    public static string CurrentMaterial => _currentMaterial;
    public static double CurrentOpacity => _currentOpacity;
    public static double CurrentPopupOpacity => _currentPopupOpacity;

    /// <summary>Mica requires Windows 11 build 22000+.</summary>
    public static bool SupportsMica { get; } = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Tabbed requires Windows 11 22H2 build 22621+.</summary>
    public static bool SupportsTabbed { get; } = Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Acrylic requires Windows 11 22H2 build 22621+.</summary>
    public static bool SupportsAcrylic { get; } = Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Resolves the requested material to a supported backdrop type.</summary>
    public static WindowBackdropType CurrentBackdropType =>
        _currentMaterial.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? WindowBackdropType.None
            : _currentMaterial.Equals("tabbed", StringComparison.OrdinalIgnoreCase) && SupportsTabbed
            ? WindowBackdropType.Tabbed
            : _currentMaterial.Equals("acrylic", StringComparison.OrdinalIgnoreCase) && SupportsAcrylic
                ? WindowBackdropType.Acrylic
                : SupportsMica
                    ? WindowBackdropType.Mica
                    : WindowBackdropType.None;

    /// <summary>Maps opacity to overlay alpha; floors at 20, or 235 when no backdrop is available.</summary>
    public static byte SurfaceAlpha(double opacity)
    {
        var alpha = (byte)Math.Clamp((int)(Math.Clamp(opacity, 0.0, 1.0) * 255), 20, 255);
        return CurrentBackdropType == WindowBackdropType.None ? Math.Max(alpha, (byte)235) : alpha;
    }

    public static event Action<string, double>? ThemeChanged;

    public static void Update(string material, double opacity, double popupOpacity = 0.60)
    {
        _currentMaterial = material?.Trim().ToLowerInvariant() switch
        {
            "tabbed" => "tabbed",
            "acrylic" => "acrylic",
            "none" => "none",
            _ => "mica"
        };
        _currentOpacity = Math.Clamp(opacity, 0.00, 1.00);
        _currentPopupOpacity = Math.Clamp(popupOpacity, 0.05, 1.00);

        ThemeChanged?.Invoke(_currentMaterial, _currentOpacity);

        if (Application.Current != null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var win in Application.Current.Windows.OfType<FluentWindow>())
                {
                    ApplyTheme(win);
                }
            });
        }
    }

    public static void ApplyTheme(FluentWindow window)
    {
        if (window == null) return;

        window.WindowBackdropType = CurrentBackdropType;
        window.Background = Brushes.Transparent;
        window.WindowCornerPreference = WindowCornerPreference.Round;

        // The main window manages its own surface overlay.
        if (window is not MainWindow)
        {
            ApplySurfaceOverlay(window, _currentPopupOpacity);
        }
    }

    private static void ApplySurfaceOverlay(FluentWindow window, double opacity)
    {
        var alpha = SurfaceAlpha(opacity);

        var baseColor = Application.Current?.TryFindResource("WindowOverlayBaseColor") as Color?
                        ?? Color.FromRgb(0x20, 0x20, 0x20);

        var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));

        switch (window.Content)
        {
            case System.Windows.Controls.Panel panel:
                panel.Background = brush;
                break;
            case System.Windows.Controls.Border border:
                border.Background = brush;
                break;
        }
    }
}
