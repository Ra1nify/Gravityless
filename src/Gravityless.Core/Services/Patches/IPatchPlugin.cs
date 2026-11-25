namespace Gravityless.Core.Services.Patches;

/// <summary>
/// Immutable build parameters passed to each <see cref="IPatchPlugin"/> when generating runtime scripts.
/// </summary>
public sealed record PatchContext(
    string Material,
    string SafeOpacity,
    int PatchVersion,
    string PatchStamp
);

/// <summary>
/// Contract for a modular runtime patch plugin loaded by <see cref="PatchLoader"/>.
/// </summary>
public interface IPatchPlugin
{
    /// <summary>Unique identifier of the plugin.</summary>
    string Id { get; }

    /// <summary>Output script filename under dist/gravityless-plugins/.</summary>
    string FileName { get; }

    /// <summary>Execution order during bootstrap initialization (ascending).</summary>
    int Order { get; }

    /// <summary>Builds the CommonJS plugin module exporting <c>init(ctx)</c>.</summary>
    string BuildScript(PatchContext context);
}
