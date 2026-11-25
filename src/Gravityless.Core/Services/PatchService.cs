using Gravityless.Core.Services.Patches;

namespace Gravityless.Core.Services;

/// <summary>
/// Public facade for Antigravity ASAR unpacking and runtime bootstrap patching.
/// Delegates orchestration to <see cref="PatchLoader"/> and modular <see cref="IPatchPlugin"/> implementations.
/// </summary>
public class PatchService
{
    /// <summary>Unpacks an Electron ASAR archive into <paramref name="outputDir"/> using only managed code.</summary>
    public static bool ExtractAsar(string asarPath, string outputDir, out string errorMessage) =>
        AsarExtractor.ExtractAsar(asarPath, outputDir, out errorMessage);

    /// <summary>Applies the runtime bootstrap loader and modular plugins to <paramref name="antigravityExePath"/>.</summary>
    public static bool ApplyPatch(string antigravityExePath, string material, double opacity, out string message) =>
        PatchLoader.Default.ApplyPatch(antigravityExePath, material, opacity, out message);

    public static bool ApplyPatch(string antigravityExePath, out string message) =>
        PatchLoader.Default.ApplyPatch(antigravityExePath, "mica", 0.72, out message);

    /// <summary>True when the bootstrap loader, main.js entry hook, and all registered plugin scripts are current.</summary>
    public static bool IsPatchCurrent(string antigravityExePath) =>
        PatchLoader.Default.IsPatchCurrent(antigravityExePath);

    /// <summary>Removes runtime bootstrap loader and plugins, restores main.js.stock, and moves app.asar back.</summary>
    public static bool RevertPatch(string antigravityExePath, out string message) =>
        PatchLoader.Default.RevertPatch(antigravityExePath, out message);

    // Backward compatibility aliases
    public static bool ApplyMicaPatch(string antigravityExePath, string material, double opacity, out string message) =>
        ApplyPatch(antigravityExePath, material, opacity, out message);

    public static bool ApplyMicaPatch(string antigravityExePath, out string message) =>
        ApplyPatch(antigravityExePath, out message);

    public static bool IsMicaPatchCurrent(string antigravityExePath) =>
        IsPatchCurrent(antigravityExePath);

    public static bool RevertMicaPatch(string antigravityExePath, out string message) =>
        RevertPatch(antigravityExePath, out message);
}
