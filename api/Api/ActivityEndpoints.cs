using Lifecycle.Data;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class ActivityEndpoints
{
    public static WebApplication MapActivityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId:int}/activity");

        group.MapGet("/", async (int projectId, LifecycleDbContext db, int page = 1, int pageSize = 20, string? entityType = null, int? entityId = null) =>
        {
            var query = db.ActivityLogs
                .Where(a => a.ProjectId == projectId)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(entityType))
                query = query.Where(a => a.EntityType == entityType);
            if (entityId.HasValue)
                query = query.Where(a => a.EntityId == entityId.Value);

            var total = await query.CountAsync();
            var activities = await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Results.Ok(new
            {
                Total = total,
                Page = page,
                PageSize = pageSize,
                Items = activities.Select(a => new
                {
                    a.Id,
                    Type = a.Type.ToString(),
                    Source = a.Source.ToString(),
                    a.EntityType, a.EntityId, a.Action, a.Changes,
                    a.Description, a.Actor, a.CreatedAt
                })
            });
        });

        return app;
    }
}
