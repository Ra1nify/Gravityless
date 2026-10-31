namespace Gravityless.Core.Models;

public class InstanceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ShortId => Id.Length >= 8 ? Id.Substring(0, 8) : Id;
    public string DisplayNameWithShortId => $"{Name} ({ShortId})";
    public string Name { get; set; } = "新建实例";
    public string? BoundAccountId { get; set; }

    // Project IDs mounted into this instance
    public List<string> BoundProjectIds { get; set; } = [];

    // Legacy single-project accessor; stored in BoundProjectIds
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BoundProjectId
    {
        get => BoundProjectIds.FirstOrDefault();
        set
        {
            BoundProjectIds.Clear();
            if (!string.IsNullOrEmpty(value)) BoundProjectIds.Add(value);
        }
    }

    public EngineType PreferredEngine { get; set; } = EngineType.Stock;

    // Share flags; false isolates that data tier inside the sandbox
    public bool ShareSkills { get; set; } = true;
    public bool SharePlugins { get; set; } = true;
    public bool ShareMcp { get; set; } = true;
    public bool ShareBrain { get; set; } = false;

    public bool UseCustomProxy { get; set; } = false;
    public ProxyConfig CustomProxy { get; set; } = new();

    /// <summary>
    /// True once this instance has taken its token and the global credential slot was
    /// cleared, freeing it for the next account's launch.
    /// </summary>
    public bool CredentialSlotReleased { get; set; }

    // Runtime state
    public bool IsRunning { get; set; }
    public int? ProcessId { get; set; }
    public int? ListeningPort { get; set; }
    public DateTime? StartedAt { get; set; }
}
