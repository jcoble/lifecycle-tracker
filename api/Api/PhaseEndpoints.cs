using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Lifecycle.Data.Enums.TaskStatus;

namespace Lifecycle.Api;

public static class PhaseEndpoints
{
    public static WebApplication MapPhaseEndpoints(this WebApplication app)
    {
        var milestoneGroup = app.MapGroup("/api/milestones/{milestoneId:int}/phases");
        var directGroup = app.MapGroup("/api/phases");

        milestoneGroup.MapGet("/", async (int milestoneId, LifecycleDbContext db) =>
        {
            var phases = await db.Phases
                .Where(p => p.MilestoneId == milestoneId)
                .Include(p => p.Tasks)
                .OrderBy(p => p.OrderIndex)
                .ToListAsync();

            return Results.Ok(phases.Select(MapToListDto));
        });

        milestoneGroup.MapPost("/", async (int milestoneId, CreatePhaseRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var milestone = await db.Milestones.FindAsync(milestoneId);
            if (milestone is null) return Results.NotFound();

            var maxOrder = await db.Phases
                .Where(p => p.MilestoneId == milestoneId)
                .MaxAsync(p => (int?)p.OrderIndex) ?? -1;

            var phase = new Phase
            {
                MilestoneId = milestoneId,
                Name = req.Name,
                Description = req.Description,
                Goal = req.Goal,
                SuccessCriteria = req.SuccessCriteria,
                Status = PhaseStatus.NotStarted,
                PhaseNumber = req.PhaseNumber ?? (maxOrder + 2),
                OrderIndex = maxOrder + 1,
                DependsOnPhaseIds = req.DependsOnPhaseIds,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Phases.Add(phase);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(db, milestone.ProjectId, ActivityType.PhaseCreated,
                TaskSource.Manual, "Phase", phase.Id, "Created", $"Phase '{phase.Name}' created");

            await sse.BroadcastAsync("phase:created", new { phase.Id, phase.Name });

            return Results.Created($"/api/phases/{phase.Id}", MapToListDto(phase));
        });

        directGroup.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var phase = await db.Phases
                .Include(p => p.Tasks).ThenInclude(t => t.TaskLabels).ThenInclude(tl => tl.Label)
                .Include(p => p.Tasks).ThenInclude(t => t.Tests)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (phase is null) return Results.NotFound();

            var testRecords = phase.Tasks.SelectMany(t => t.Tests).ToList();

            return Results.Ok(new
            {
                phase.Id, phase.MilestoneId, phase.Name, phase.Description, phase.Goal,
                phase.SuccessCriteria, Status = phase.Status.ToString(),
                phase.PhaseNumber, phase.OrderIndex, phase.DependsOnPhaseIds,
                phase.StartedAt, phase.CompletedAt, phase.CreatedAt, phase.UpdatedAt,
                Tasks = phase.Tasks.OrderBy(t => t.Status).ThenBy(t => t.OrderInColumn).Select(t => new
                {
                    t.Id, t.Title,
                    Status = t.Status.ToString(),
                    Priority = t.Priority.ToString(),
                    Type = t.Type.ToString(),
                    t.OrderInColumn,
                    Labels = t.TaskLabels.Select(tl => new { tl.Label.Id, tl.Label.Name, tl.Label.Color })
                }),
                TestSummary = new
                {
                    Total = testRecords.Count,
                    Passing = testRecords.Count(t => t.Status == TestStatus.Passing),
                    Failing = testRecords.Count(t => t.Status == TestStatus.Failing)
                }
            });
        });

        directGroup.MapPatch("/{id:int}", async (int id, UpdatePhaseRequest req, LifecycleDbContext db) =>
        {
            var phase = await db.Phases.FindAsync(id);
            if (phase is null) return Results.NotFound();

            if (req.Name is not null) phase.Name = req.Name;
            if (req.Description is not null) phase.Description = req.Description;
            if (req.Goal is not null) phase.Goal = req.Goal;
            if (req.SuccessCriteria is not null) phase.SuccessCriteria = req.SuccessCriteria;
            if (req.Status.HasValue) phase.Status = req.Status.Value;
            if (req.PhaseNumber.HasValue) phase.PhaseNumber = req.PhaseNumber.Value;
            if (req.OrderIndex.HasValue) phase.OrderIndex = req.OrderIndex.Value;
            if (req.DependsOnPhaseIds is not null) phase.DependsOnPhaseIds = req.DependsOnPhaseIds;

            phase.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(MapToListDto(phase));
        });

        directGroup.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var phase = await db.Phases.Include(p => p.Tasks).FirstOrDefaultAsync(p => p.Id == id);
            if (phase is null) return Results.NotFound();

            await using var transaction = await db.Database.BeginTransactionAsync();

            // Unassign tasks from this phase
            foreach (var task in phase.Tasks)
                task.PhaseId = null;

            // Remove this phase ID from DependsOnPhaseIds of other phases in the same milestone
            var siblingPhases = await db.Phases
                .Where(p => p.MilestoneId == phase.MilestoneId && p.Id != id && p.DependsOnPhaseIds != null)
                .ToListAsync();
            foreach (var sibling in siblingPhases)
            {
                if (sibling.DependsOnPhaseIds is not null && sibling.DependsOnPhaseIds.Contains(id.ToString()))
                {
                    var ids = sibling.DependsOnPhaseIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Where(x => x.Trim() != id.ToString())
                        .ToList();
                    sibling.DependsOnPhaseIds = ids.Count > 0 ? string.Join(",", ids) : null;
                }
            }

            db.Phases.Remove(phase);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            return Results.NoContent();
        });

        directGroup.MapPost("/{id:int}/status", async (int id, ChangePhaseStatusRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var phase = await db.Phases.Include(p => p.Milestone).FirstOrDefaultAsync(p => p.Id == id);
            if (phase is null) return Results.NotFound();

            var oldStatus = phase.Status;
            phase.Status = req.Status;
            phase.UpdatedAt = DateTime.UtcNow;

            if (req.Status == PhaseStatus.InProgress && phase.StartedAt is null)
                phase.StartedAt = DateTime.UtcNow;
            if (req.Status == PhaseStatus.Completed)
                phase.CompletedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            var activityType = req.Status == PhaseStatus.Completed
                ? ActivityType.PhaseCompleted : ActivityType.PhaseUpdated;
            await ActivityHelper.LogActivity(db, phase.Milestone.ProjectId, activityType,
                TaskSource.Manual, "Phase", phase.Id, "StatusChanged",
                $"Phase '{phase.Name}' status: {oldStatus} -> {req.Status}");

            await sse.BroadcastAsync(req.Status == PhaseStatus.Completed ? "phase:completed" : "phase:updated",
                new { phase.Id, Status = phase.Status.ToString() });

            return Results.Ok(MapToListDto(phase));
        });

        return app;
    }

    private static object MapToListDto(Phase p) => new
    {
        p.Id, p.MilestoneId, p.Name, p.Description, p.Goal, p.SuccessCriteria,
        Status = p.Status.ToString(),
        p.PhaseNumber, p.OrderIndex, p.DependsOnPhaseIds,
        p.StartedAt, p.CompletedAt, p.CreatedAt, p.UpdatedAt,
        TaskCount = p.Tasks?.Count ?? 0,
        DoneCount = p.Tasks?.Count(t => t.Status == TaskStatus.Done) ?? 0
    };
}

public record CreatePhaseRequest(string Name, string? Description = null, string? Goal = null, string? SuccessCriteria = null, int? PhaseNumber = null, string? DependsOnPhaseIds = null);
public record UpdatePhaseRequest(string? Name = null, string? Description = null, string? Goal = null, string? SuccessCriteria = null, PhaseStatus? Status = null, int? PhaseNumber = null, int? OrderIndex = null, string? DependsOnPhaseIds = null);
public record ChangePhaseStatusRequest(PhaseStatus Status);
