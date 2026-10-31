namespace Gravityless.Core.Models;

public enum EngineType
{
    Stock,    // Installation found on the system (%LOCALAPPDATA%\Programs\antigravity)
    Managed   // Portable core managed by Gravityless (<root>\core)
}

public class AntigravityEngine
{
    public EngineType Type { get; set; }
    public string Version { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public string StatusDescription { get; set; } = string.Empty;
}
