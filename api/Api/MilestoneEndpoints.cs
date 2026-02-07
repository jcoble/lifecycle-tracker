using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class MilestoneEndpoints
{
    public static WebApplication MapMilestoneEndpoints(this WebApplication app)
    {
        var projectGroup = app.MapGroup("/api/projects/{projectId:int}/milestones");
        var directGroup = app.MapGroup("/api/milestones");

        projectGroup.MapGet("/", async (int projectId, LifecycleDbContext db) =>
        {
            var milestones = await db.Milestones
                .Where(m => m.ProjectId == projectId)
                .Include(m => m.Phases)
                .OrderBy(m => m.OrderIndex)
                .ToListAsync();

            return Results.Ok(milestones.Select(MapToDto));
        });

        projectGroup.MapPost("/", async (int projectId, CreateMilestoneRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var maxOrder = await db.Milestones
                .Where(m => m.ProjectId == projectId)
                .MaxAsync(m => (int?)m.OrderIndex) ?? -1;

            var milestone = new Milestone
            {
                ProjectId = projectId,
                Name = req.Name,
                Description = req.Description,
                Version = req.Version,
                Status = MilestoneStatus.Planning,
                OrderIndex = maxOrder + 1,
                TargetDate = req.TargetDate,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Milestones.Add(milestone);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(db, projectId, ActivityType.MilestoneCreated,
                TaskSource.Manual, "Milestone", milestone.Id, "Created", $"Milestone '{milestone.Name}' created");

            await sse.BroadcastAsync("milestone:updated", new { milestone.Id, milestone.Name });

            return Results.Created($"/api/milestones/{milestone.Id}", MapToDto(milestone));
        });

        directGroup.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var milestone = await db.Milestones
                .Include(m => m.Phases.OrderBy(p => p.OrderIndex))
                .FirstOrDefaultAsync(m => m.Id == id);
            if (milestone is null) return Results.NotFound();

            return Results.Ok(new
            {
                milestone.Id, milestone.ProjectId, milestone.Name, milestone.Description,
                milestone.Version, Status = milestone.Status.ToString(),
                milestone.OrderIndex, milestone.StartedAt, milestone.TargetDate,
                milestone.CompletedAt, milestone.CreatedAt, milestone.UpdatedAt,
                Phases = milestone.Phases.Select(p => new
                {
                    p.Id, p.Name, p.PhaseNumber,
                    Status = p.Status.ToString(),
                    p.OrderIndex
                })
            });
        });

        directGroup.MapPatch("/{id:int}", async (int id, UpdateMilestoneRequest req, LifecycleDbContext db) =>
        {
            var milestone = await db.Milestones.FindAsync(id);
            if (milestone is null) return Results.NotFound();

            if (req.Name is not null) milestone.Name = req.Name;
            if (req.Description is not null) milestone.Description = req.Description;
            if (req.Version is not null) milestone.Version = req.Version;
            if (req.Status.HasValue) milestone.Status = req.Status.Value;
            if (req.OrderIndex.HasValue) milestone.OrderIndex = req.OrderIndex.Value;
            if (req.TargetDate.HasValue) milestone.TargetDate = req.TargetDate.Value;

            milestone.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(MapToDto(milestone));
        });

        directGroup.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var milestone = await db.Milestones.FindAsync(id);
            if (milestone is null) return Results.NotFound();
            db.Milestones.Remove(milestone);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        directGroup.MapPost("/{id:int}/status", async (int id, ChangeMilestoneStatusRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var milestone = await db.Milestones.FindAsync(id);
            if (milestone is null) return Results.NotFound();

            var oldStatus = milestone.Status;
            milestone.Status = req.Status;
            milestone.UpdatedAt = DateTime.UtcNow;

            if (req.Status == MilestoneStatus.InProgress && milestone.StartedAt is null)
                milestone.StartedAt = DateTime.UtcNow;
            if (req.Status == MilestoneStatus.Completed)
                milestone.CompletedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            var activityType = req.Status == MilestoneStatus.Completed
                ? ActivityType.MilestoneCompleted : ActivityType.MilestoneCreated;
            await ActivityHelper.LogActivity(db, milestone.ProjectId, activityType,
                TaskSource.Manual, "Milestone", milestone.Id, "StatusChanged",
                $"Milestone '{milestone.Name}' status: {oldStatus} -> {req.Status}");

            await sse.BroadcastAsync("milestone:updated", new { milestone.Id, Status = milestone.Status.ToString() });

            return Results.Ok(MapToDto(milestone));
        });

        return app;
    }

    private static object MapToDto(Milestone m) => new
    {
        m.Id, m.ProjectId, m.Name, m.Description, m.Version,
        Status = m.Status.ToString(),
        m.OrderIndex, m.StartedAt, m.TargetDate, m.CompletedAt,
        m.CreatedAt, m.UpdatedAt,
        PhaseCount = m.Phases?.Count ?? 0
    };
}

public record CreateMilestoneRequest(string Name, string? Description = null, string? Version = null, DateTime? TargetDate = null);
public record UpdateMilestoneRequest(string? Name = null, string? Description = null, string? Version = null, MilestoneStatus? Status = null, int? OrderIndex = null, DateTime? TargetDate = null);
public record ChangeMilestoneStatusRequest(MilestoneStatus Status);
