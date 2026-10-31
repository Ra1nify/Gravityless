namespace Gravityless.Core.Models;

/// <summary>
/// A project that is mounted by another running instance, so it cannot be opened twice.
/// </summary>
public class ProjectConflict
{
    public string ProjectId { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Instance currently holding the project.</summary>
    public string InstanceId { get; set; } = string.Empty;

    public string InstanceName { get; set; } = string.Empty;

    /// <summary>Process time of the holding instance, when known.</summary>
    public DateTime? StartedAt { get; set; }
}
