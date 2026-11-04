namespace Gravityless.Core.Services;

/// <summary>
/// Download progress: the ratio for a progress bar, plus the byte counts for a readable
/// "17.8 / 42.0 MB" label. A bare ratio cannot tell the user whether a stalled bar is near
/// the end of a small file or the start of a large one.
/// </summary>
public readonly record struct DownloadProgress(double Ratio, long Received, long Total)
{
    /// <summary>Percent in [0, 100], clamped so an over-reporting stream cannot overflow a bar.</summary>
    public double Percent => Math.Clamp(Ratio * 100.0, 0.0, 100.0);

    /// <summary>Human-readable "x.x / y.y MB", or an empty string when the total is unknown.</summary>
    public string BytesLabel => Total > 0
        ? $"{Received / 1024.0 / 1024.0:F1} / {Total / 1024.0 / 1024.0:F1} MB"
        : string.Empty;
}
