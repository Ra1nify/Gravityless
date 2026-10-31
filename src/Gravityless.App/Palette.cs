using System.Windows;
using System.Windows.Media;

namespace Gravityless.App;

/// <summary>
/// Resolves named brushes from application resources.
/// All colors are defined in App.xaml; views and code carry no color literals.
/// </summary>
internal static class Palette
{
    public static Brush Accent => Get("BrandAccentBrush");

    public static Brush AccentText => Get("BrandAccentTextBrush");

    public static Brush Success => Get("StatusSuccessBrush");

    public static Brush Caution => Get("StatusCautionBrush");

    public static Brush Critical => Get("StatusCriticalBrush");

    public static Brush Neutral => Get("StatusNeutralBrush");

    /// <summary>Returns a fallback brush when the key is missing; never null.</summary>
    public static Brush Get(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>Solid copy of <paramref name="brush"/> at the given alpha.</summary>
    public static Brush WithAlpha(Brush brush, byte alpha)
    {
        var color = (brush as SolidColorBrush)?.Color ?? Colors.Gray;
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}
