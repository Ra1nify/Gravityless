namespace Gravityless.Core.Models;

public class ThemeConfig
{
    public string Material { get; set; } = "mica"; // "mica" | "tabbed" | "acrylic" | "none"
    public double Opacity { get; set; } = 0.72;
    public double PopupOpacity { get; set; } = 0.60;
    public string AccentColor { get; set; } = "#4285f4";

    /// <summary>"auto" (follow system) / "light" / "dark".</summary>
    public string ThemeMode { get; set; } = "auto";

    /// <summary>New UX preset: "" (off) / alabaster / tahoe / atelier / studio / planes.</summary>
    public string NewUxTheme { get; set; } = string.Empty;

    /// <summary>Tint hover/selection states with the system accent color.</summary>
    public bool AccentInteraction { get; set; } = true;

    public double NoiseAlpha { get; set; } = 0.55;
    public double NoiseFrequency { get; set; } = 0.85;
    public double NoiseTintRatio { get; set; } = 0.28;
    public double NoiseBaseAlpha { get; set; } = 0.35;
}
