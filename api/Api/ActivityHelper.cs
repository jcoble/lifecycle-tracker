using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;

namespace Lifecycle.Api;

public static class ActivityHelper
{
    public static async Task LogActivity(
        LifecycleDbContext db,
        int projectId,
        ActivityType type,
        TaskSource source,
        string entityType,
        int entityId,
        string action,
        string description,
        string? changes = null,
        string? actor = null)
    {
        db.ActivityLogs.Add(new ActivityLog
        {
            ProjectId = projectId,
            Type = type,
            Source = source,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Description = description,
            Changes = changes,
            Actor = actor,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
