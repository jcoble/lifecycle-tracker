using Lifecycle.Data.Enums;

namespace Lifecycle.Data.Entities;

public class ActivityLog
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public ActivityType Type { get; set; }
    public TaskSource Source { get; set; }
    public string? EntityType { get; set; }
    public int? EntityId { get; set; }
    public string? Action { get; set; }
    public string? Changes { get; set; }
    public string? Description { get; set; }
    public string? Actor { get; set; }
    public DateTime CreatedAt { get; set; }
    public Project Project { get; set; } = null!;
}
