namespace Gravityless.Core.Models;

public class ProjectProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string ShortId => Id.Length >= 8 ? Id.Substring(0, 8) : Id;
    public string DisplayNameWithShortId => $"{Name} ({ShortId})";
    public string Name { get; set; } = "新建项目";
    public string Path { get; set; } = string.Empty;

    public int ConversationCount { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Runtime state; one instance at a time may mount the project
    public bool IsRunning { get; set; }
    public string? RunningInstanceId { get; set; }
    public int? ProcessId { get; set; }
    public DateTime? StartedAt { get; set; }
}
