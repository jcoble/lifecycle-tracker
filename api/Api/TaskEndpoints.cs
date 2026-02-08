using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Lifecycle.Data.Enums.TaskStatus;

namespace Lifecycle.Api;

public static class TaskEndpoints
{
    public static WebApplication MapTaskEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tasks");

        group.MapGet("/", async (
            LifecycleDbContext db,
            int? phaseId, int? milestoneId, int? projectId,
            TaskStatus? status, TaskPriority? priority, TaskType? type,
            TaskSource? source, string? search, string? label) =>
        {
            var query = db.Tasks
                .Include(t => t.TaskLabels).ThenInclude(tl => tl.Label)
                .Include(t => t.Phase)
                .Include(t => t.Assignments.Where(a => a.Status != "Completed" && a.Status != "Abandoned"))
                    .ThenInclude(a => a.TeamMember)
                .AsQueryable();

            if (phaseId.HasValue)
                query = query.Where(t => t.PhaseId == phaseId.Value);
            if (milestoneId.HasValue)
                query = query.Where(t => t.Phase != null && t.Phase.MilestoneId == milestoneId.Value);
            if (projectId.HasValue)
                query = query.Where(t => t.ProjectId == projectId.Value);
            if (status.HasValue)
                query = query.Where(t => t.Status == status.Value);
            if (priority.HasValue)
                query = query.Where(t => t.Priority == priority.Value);
            if (type.HasValue)
                query = query.Where(t => t.Type == type.Value);
            if (source.HasValue)
                query = query.Where(t => t.Source == source.Value);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.ToLower();
                query = query.Where(t => t.Title.ToLower().Contains(term) ||
                    (t.Description != null && t.Description.ToLower().Contains(term)));
            }
            if (!string.IsNullOrWhiteSpace(label))
                query = query.Where(t => t.TaskLabels.Any(tl => tl.Label.Name == label));

            var tasks = await query
                .OrderBy(t => t.Status)
                .ThenBy(t => t.OrderInColumn)
                .ToListAsync();

            return Results.Ok(tasks.Select(MapToListDto));
        });

        group.MapPost("/", async (CreateTaskRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var targetStatus = req.Status ?? TaskStatus.Backlog;
            var maxOrder = await db.Tasks
                .Where(t => t.Status == targetStatus)
                .MaxAsync(t => (int?)t.OrderInColumn) ?? -1;

            var task = new LifecycleTask
            {
                ProjectId = req.ProjectId ?? 1,
                PhaseId = req.PhaseId,
                Title = req.Title,
                Description = req.Description,
                Status = targetStatus,
                Priority = req.Priority ?? TaskPriority.P3,
                Type = req.Type ?? TaskType.Feature,
                Source = req.Source ?? TaskSource.Manual,
                OrderInColumn = maxOrder + 1,
                DueDate = req.DueDate,
                RequiredTestLevel = req.RequiredTestLevel,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            if (targetStatus == TaskStatus.Done)
                task.CompletedAt = DateTime.UtcNow;
            if (targetStatus == TaskStatus.InProgress)
                task.StartedAt = DateTime.UtcNow;

            db.Tasks.Add(task);
            await db.SaveChangesAsync();

            if (req.LabelIds is { Count: > 0 })
            {
                foreach (var labelId in req.LabelIds)
                    db.TaskLabels.Add(new TaskLabel { TaskId = task.Id, LabelId = labelId });
                await db.SaveChangesAsync();
            }

            await db.Entry(task).Collection(t => t.TaskLabels).LoadAsync();
            foreach (var tl in task.TaskLabels)
                await db.Entry(tl).Reference(x => x.Label).LoadAsync();

            // Log activity if we can determine the project
            if (req.PhaseId.HasValue)
            {
                var phase = await db.Phases.Include(p => p.Milestone).FirstOrDefaultAsync(p => p.Id == req.PhaseId.Value);
                if (phase is not null)
                {
                    await ActivityHelper.LogActivity(db, phase.Milestone.ProjectId, ActivityType.TaskCreated,
                        task.Source, "Task", task.Id, "Created", $"Task '{task.Title}' created");
                }
            }

            await sse.BroadcastAsync("task:created", new { task.Id, task.Title, Status = task.Status.ToString() });

            return Results.Created($"/api/tasks/{task.Id}", MapToListDto(task));
        });

        group.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var task = await db.Tasks
                .Include(t => t.TaskLabels).ThenInclude(tl => tl.Label)
                .Include(t => t.TestPlans).ThenInclude(tp => tp.Tests)
                .Include(t => t.Attachments)
                .Include(t => t.Comments.OrderByDescending(c => c.CreatedAt))
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();

            return Results.Ok(MapToDetailDto(task));
        });

        group.MapPatch("/{id:int}", async (int id, UpdateTaskRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks
                .Include(t => t.TaskLabels).ThenInclude(tl => tl.Label)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();

            if (req.Title is not null) task.Title = req.Title;
            if (req.Description is not null) task.Description = req.Description;
            if (req.Priority.HasValue) task.Priority = req.Priority.Value;
            if (req.Type.HasValue) task.Type = req.Type.Value;
            if (req.DueDate.HasValue) task.DueDate = req.DueDate.Value;
            if (req.Source.HasValue) task.Source = req.Source.Value;
            if (req.PhaseId.HasValue) task.PhaseId = req.PhaseId.Value;
            if (req.GitCommitSha is not null) task.GitCommitSha = req.GitCommitSha;
            if (req.GitBranch is not null) task.GitBranch = req.GitBranch;
            if (req.PullRequestUrl is not null) task.PullRequestUrl = req.PullRequestUrl;
            if (req.ConversationRef is not null) task.ConversationRef = req.ConversationRef;
            if (req.RequiredTestLevel.HasValue) task.RequiredTestLevel = req.RequiredTestLevel.Value;

            if (req.Status.HasValue && req.Status.Value != task.Status)
            {
                if (req.Status.Value == TaskStatus.InProgress && task.StartedAt is null)
                    task.StartedAt = DateTime.UtcNow;
                if (req.Status.Value == TaskStatus.Done)
                    task.CompletedAt = DateTime.UtcNow;
                else if (task.Status == TaskStatus.Done)
                    task.CompletedAt = null;
                task.Status = req.Status.Value;
            }

            if (req.LabelIds is not null)
            {
                db.TaskLabels.RemoveRange(task.TaskLabels);
                foreach (var labelId in req.LabelIds)
                    db.TaskLabels.Add(new TaskLabel { TaskId = task.Id, LabelId = labelId });
            }

            task.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await db.Entry(task).Collection(t => t.TaskLabels).Query()
                .Include(tl => tl.Label).LoadAsync();

            await sse.BroadcastAsync("task:updated", new { task.Id, task.Title, Status = task.Status.ToString() });

            return Results.Ok(MapToListDto(task));
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks.FindAsync(id);
            if (task is null) return Results.NotFound();
            db.Tasks.Remove(task);
            await db.SaveChangesAsync();
            await sse.BroadcastAsync("task:deleted", new { Id = id });
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/move", async (int id, MoveTaskRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks
                .Include(t => t.TaskLabels).ThenInclude(tl => tl.Label)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();

            if (req.Status == TaskStatus.InProgress && task.StartedAt is null)
                task.StartedAt = DateTime.UtcNow;
            if (req.Status == TaskStatus.Done && task.Status != TaskStatus.Done)
                task.CompletedAt = DateTime.UtcNow;
            else if (req.Status != TaskStatus.Done && task.Status == TaskStatus.Done)
                task.CompletedAt = null;

            task.Status = req.Status;
            task.OrderInColumn = req.OrderInColumn;
            task.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("task:moved", new { task.Id, Status = task.Status.ToString(), task.OrderInColumn });

            return Results.Ok(MapToListDto(task));
        });

        group.MapPatch("/reorder", async (ReorderRequest req, LifecycleDbContext db) =>
        {
            var taskIds = req.Items.Select(i => i.Id).ToList();
            var tasks = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync();

            foreach (var item in req.Items)
            {
                var task = tasks.FirstOrDefault(t => t.Id == item.Id);
                if (task is not null)
                {
                    task.Status = req.Status;
                    task.OrderInColumn = item.Order;
                    task.UpdatedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }

    private static object MapToListDto(LifecycleTask t)
    {
        var activeAssignment = t.Assignments?.FirstOrDefault(a => a.Status != "Completed" && a.Status != "Abandoned");
        return new
        {
            t.Id, t.ProjectId, t.PhaseId, t.Title, t.Description,
            Status = t.Status.ToString(),
            Priority = t.Priority.ToString(),
            Type = t.Type.ToString(),
            Source = t.Source.ToString(),
            t.OrderInColumn, t.DueDate, t.StartedAt, t.CompletedAt,
            t.GitCommitSha, t.GitBranch, t.PullRequestUrl, t.ConversationRef,
            RequiredTestLevel = t.RequiredTestLevel?.ToString(),
            t.CreatedAt, t.UpdatedAt,
            Labels = t.TaskLabels.Select(tl => new { tl.Label.Id, tl.Label.Name, tl.Label.Color }),
            AssignedTo = activeAssignment is null ? null : new
            {
                TeamMemberId = activeAssignment.TeamMemberId,
                AgentName = activeAssignment.TeamMember?.AgentName
            }
        };
    }

    private static object MapToDetailDto(LifecycleTask t) => new
    {
        t.Id, t.ProjectId, t.PhaseId, t.Title, t.Description,
        Status = t.Status.ToString(),
        Priority = t.Priority.ToString(),
        Type = t.Type.ToString(),
        Source = t.Source.ToString(),
        t.OrderInColumn, t.DueDate, t.StartedAt, t.CompletedAt,
        t.GitCommitSha, t.GitBranch, t.PullRequestUrl, t.ConversationRef,
        RequiredTestLevel = t.RequiredTestLevel?.ToString(),
        t.CreatedAt, t.UpdatedAt,
        Labels = t.TaskLabels.Select(tl => new { tl.Label.Id, tl.Label.Name, tl.Label.Color }),
        Tests = t.TestPlans.SelectMany(tp => tp.Tests).Select(test => new
        {
            test.Id, TestType = test.Type.ToString(), Status = test.Status.ToString(),
            test.Name, test.TestFile, test.Framework, test.LastRunAt,
            test.TotalRuns, test.PassedRuns, test.FailedRuns, test.TestPlanId
        }),
        Attachments = t.Attachments.Select(a => new
        {
            a.Id, a.FileName, a.OriginalFileName, a.ContentType,
            a.FileSize, a.Width, a.Height, a.UploadedBy, a.UploadedAt
        }),
        Comments = t.Comments.Select(c => new
        {
            c.Id, c.Content, Source = c.Source.ToString(),
            c.Author, c.CreatedAt, c.UpdatedAt
        })
    };
}

public record CreateTaskRequest(
    string Title,
    string? Description = null,
    int? ProjectId = null,
    int? PhaseId = null,
    TaskStatus? Status = null,
    TaskPriority? Priority = null,
    TaskType? Type = null,
    TaskSource? Source = null,
    DateTime? DueDate = null,
    List<int>? LabelIds = null,
    TestLevel? RequiredTestLevel = null);

public record UpdateTaskRequest(
    string? Title = null,
    string? Description = null,
    int? PhaseId = null,
    TaskStatus? Status = null,
    TaskPriority? Priority = null,
    TaskType? Type = null,
    TaskSource? Source = null,
    DateTime? DueDate = null,
    string? GitCommitSha = null,
    string? GitBranch = null,
    string? PullRequestUrl = null,
    string? ConversationRef = null,
    List<int>? LabelIds = null,
    TestLevel? RequiredTestLevel = null);

public record MoveTaskRequest(TaskStatus Status, int OrderInColumn);
public record ReorderRequest(TaskStatus Status, List<ReorderItem> Items);
public record ReorderItem(int Id, int Order);
