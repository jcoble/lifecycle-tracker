using System.Diagnostics;
using System.Text.Json;
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
            TaskSource? source, string? search, string? label,
            bool includeArchived = false) =>
        {
            var query = db.Tasks
                .Include(t => t.TaskLabels).ThenInclude(tl => tl.Label)
                .Include(t => t.Phase)
                .Include(t => t.Assignments.Where(a => a.Status != "Completed" && a.Status != "Abandoned"))
                    .ThenInclude(a => a.TeamMember)
                .AsQueryable();

            if (!includeArchived)
                query = query.Where(t => !t.IsArchived);

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
            var taskType = req.Type ?? TaskType.Feature;

            // Block creating Feature/Bug/Refactor tasks directly as Done or Review
            if (targetStatus == TaskStatus.Done || targetStatus == TaskStatus.Review)
            {
                var protectedTypes = new HashSet<TaskType> { TaskType.Feature, TaskType.Bug, TaskType.Refactor };
                if (protectedTypes.Contains(taskType))
                    return Results.BadRequest(new { Error = $"Cannot create {taskType} tasks directly in {targetStatus} status. Tasks must follow the workflow: Backlog → Todo → InProgress → Review → Done." });
            }

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
                Type = taskType,
                Source = req.Source ?? TaskSource.Manual,
                OrderInColumn = maxOrder + 1,
                DueDate = req.DueDate,
                IsArchived = req.IsArchived ?? false,
                ArchivedAt = req.IsArchived == true ? DateTime.UtcNow : null,
                RequiredTestLevel = req.RequiredTestLevel,
                SkipUiTesting = req.SkipUiTesting ?? false,
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

            // SECURITY: Block type changes on tasks that have left Backlog
            // Prevents type mutation attack (changing Feature -> Docs to bypass test enforcement)
            if (req.Type.HasValue && req.Type.Value != task.Type)
            {
                var protectedFromTypes = new HashSet<TaskType> { TaskType.Feature, TaskType.Bug, TaskType.Refactor };
                if (protectedFromTypes.Contains(task.Type) && task.Status != TaskStatus.Backlog)
                    return Results.BadRequest(new { Error = $"Cannot change type of {task.Type} task from {task.Status} status. Type changes are only allowed while in Backlog." });
            }

            // SECURITY: Block skipUiTesting changes via API for protected types
            // Only the task creator or admin should set this at creation time
            if (req.SkipUiTesting.HasValue && req.SkipUiTesting.Value && !task.SkipUiTesting)
            {
                var protectedTypes = new HashSet<TaskType> { TaskType.Feature, TaskType.Bug, TaskType.Refactor };
                if (protectedTypes.Contains(task.Type))
                    return Results.BadRequest(new { Error = $"Cannot enable skipUiTesting on {task.Type} tasks via API. This flag must be set at task creation time." });
            }

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
            if (req.SkipUiTesting.HasValue) task.SkipUiTesting = req.SkipUiTesting.Value;
            if (req.IsArchived.HasValue)
            {
                task.IsArchived = req.IsArchived.Value;
                task.ArchivedAt = req.IsArchived.Value ? DateTime.UtcNow : null;
            }

            if (req.Status.HasValue && req.Status.Value != task.Status)
            {
                var (isValid, reason) = TaskTransitionValidator.IsValid(task.Status, req.Status.Value);
                if (!isValid) return Results.BadRequest(new { Error = reason });

                // API-level completion enforcement — cannot be bypassed
                if (req.Status.Value == TaskStatus.Done)
                {
                    var (canComplete, doneReason) = await TaskTransitionValidator.CanCompleteDone(task, db);
                    if (!canComplete) return Results.BadRequest(new { Error = doneReason });
                }

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

            // SECURITY: Prevent deletion of tasks that have started work
            // This blocks the delete-and-recreate-as-exempt-type bypass
            var protectedStatuses = new HashSet<TaskStatus>
            {
                TaskStatus.InProgress, TaskStatus.Review, TaskStatus.Done, TaskStatus.Blocked
            };
            if (protectedStatuses.Contains(task.Status))
                return Results.BadRequest(new { Error = $"Cannot delete task in {task.Status} status. Move it to Cancelled first, or archive it." });

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

            if (req.Status != task.Status)
            {
                var (isValid, reason) = TaskTransitionValidator.IsValid(task.Status, req.Status);
                if (!isValid) return Results.BadRequest(new { Error = reason });

                // API-level completion enforcement — cannot be bypassed
                if (req.Status == TaskStatus.Done)
                {
                    var (canComplete, doneReason) = await TaskTransitionValidator.CanCompleteDone(task, db);
                    if (!canComplete) return Results.BadRequest(new { Error = doneReason });
                }
            }

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

        group.MapPost("/archive-completed", async (ArchiveCompletedTasksRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var now = DateTime.UtcNow;

            var query = db.Tasks
                .Include(t => t.Phase)
                .Where(t => !t.IsArchived && t.Status == TaskStatus.Done);

            if (req.ProjectId.HasValue)
                query = query.Where(t => t.ProjectId == req.ProjectId.Value);

            if (req.CompletedPhasesOnly)
                query = query.Where(t => t.Phase != null && t.Phase.Status == PhaseStatus.Completed);

            if (req.OlderThanDays.HasValue && req.OlderThanDays.Value > 0)
            {
                var cutoff = now.AddDays(-req.OlderThanDays.Value);
                query = query.Where(t => t.CompletedAt.HasValue && t.CompletedAt.Value <= cutoff);
            }

            var tasksToArchive = await query.ToListAsync();
            foreach (var task in tasksToArchive)
            {
                task.IsArchived = true;
                task.ArchivedAt = now;
                task.UpdatedAt = now;
            }

            if (tasksToArchive.Count > 0)
            {
                await db.SaveChangesAsync();
                await sse.BroadcastAsync("task:updated", new
                {
                    Count = tasksToArchive.Count,
                    TaskIds = tasksToArchive.Select(t => t.Id).ToArray(),
                    Archived = true
                });
            }

            return Results.Ok(new
            {
                ArchivedCount = tasksToArchive.Count,
                ProjectId = req.ProjectId,
                req.CompletedPhasesOnly,
                req.OlderThanDays
            });
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
                    // Validate transition if status is changing
                    if (req.Status != task.Status)
                    {
                        var (isValid, reason) = TaskTransitionValidator.IsValid(task.Status, req.Status);
                        if (!isValid) return Results.BadRequest(new { Error = reason, TaskId = task.Id });

                        if (req.Status == TaskStatus.Done)
                        {
                            var (canComplete, doneReason) = await TaskTransitionValidator.CanCompleteDone(task, db);
                            if (!canComplete) return Results.BadRequest(new { Error = doneReason, TaskId = task.Id });
                        }
                    }

                    task.Status = req.Status;
                    task.OrderInColumn = item.Order;
                    task.UpdatedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Spawn a test runner agent for a task (runs claude CLI in background)
        group.MapPost("/{taskId:int}/spawn-test", async (int taskId, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks.FindAsync(taskId);
            if (task is null) return Results.NotFound(new { Error = $"Task #{taskId} not found" });

            // Get project settings for repository root
            var project = await db.Projects
                .FirstOrDefaultAsync(p => p.Id == (task.ProjectId != 0 ? task.ProjectId : 1));
            var settings = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(project?.Settings))
            {
                try { settings = JsonSerializer.Deserialize<Dictionary<string, string>>(project.Settings) ?? settings; }
                catch { /* ignore malformed settings */ }
            }
            var repoRoot = settings.GetValueOrDefault("repositoryRoot", "");
            var promptFile = Path.Combine(repoRoot, ".claude/prompts/test-agent-system.md");

            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                return Results.BadRequest(new { Error = "Repository root not configured or not found. Set 'repositoryRoot' in project settings." });

            if (!File.Exists(promptFile))
                return Results.BadRequest(new { Error = $"Test agent system prompt not found at {promptFile}" });

            // Check if claude CLI is available
            try
            {
                var which = Process.Start(new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "claude",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                which?.WaitForExit(3000);
                if (which?.ExitCode != 0)
                    return Results.BadRequest(new { Error = "claude CLI not found on this machine" });
            }
            catch
            {
                return Results.BadRequest(new { Error = "Unable to check for claude CLI" });
            }

            var taskType = task.Type.ToString();
            var gitBranch = task.GitBranch;
            var gitSync = !string.IsNullOrEmpty(gitBranch)
                ? $"First, sync code: run 'git fetch origin && git checkout {gitBranch} && git pull origin {gitBranch}' in {repoRoot}. Then 'dotnet build EdiPlatform.sln --no-restore'. "
                : $"First, make sure the code is up to date: run 'git pull' in {repoRoot}. Then 'dotnet build EdiPlatform.sln --no-restore'. ";

            var prompt = $"You are a test runner agent for task #{taskId} ({taskType}: {task.Title}). " +
                gitSync +
                "Then find and execute all existing tests for this task. Steps: " +
                "1) Find test plans via mcp__lifecycle__get_project_context or search for task #{taskId}. " +
                "2) Run backend tests with 'dotnet test --filter' for any test files linked to this task. " +
                "3) Run UI tests using agent-browser — call start_test_execution, walk through each step visually, record results with record_step_result. " +
                "4) Complete the test execution and report all results back via the lifecycle API. " +
                "Follow the system prompt for test patterns and conventions.";

            // Spawn claude in background
            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"claude -p \\\"{prompt}\\\" --system-prompt file:{promptFile} --dangerously-skip-permissions --model sonnet --add-dir {repoRoot} >> /tmp/test-agent-spawns.log 2>&1 &\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                var process = Process.Start(psi);
                await sse.BroadcastAsync("agent:spawned", new { TaskId = taskId, Type = "test-runner" });

                return Results.Ok(new
                {
                    Message = $"Test runner spawned for task #{taskId}",
                    TaskId = taskId,
                    TaskType = taskType,
                    LogFile = "/tmp/test-agent-spawns.log"
                });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to spawn test agent: {ex.Message}");
            }
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
            t.IsArchived, t.ArchivedAt,
            t.GitCommitSha, t.GitBranch, t.PullRequestUrl, t.ConversationRef,
            RequiredTestLevel = t.RequiredTestLevel?.ToString(),
            t.SkipUiTesting,
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
        t.IsArchived, t.ArchivedAt,
        t.GitCommitSha, t.GitBranch, t.PullRequestUrl, t.ConversationRef,
        RequiredTestLevel = t.RequiredTestLevel?.ToString(),
        t.SkipUiTesting,
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
    bool? IsArchived = null,
    TestLevel? RequiredTestLevel = null,
    bool? SkipUiTesting = null);

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
    bool? IsArchived = null,
    TestLevel? RequiredTestLevel = null,
    bool? SkipUiTesting = null);

public record MoveTaskRequest(TaskStatus Status, int OrderInColumn);
public record ReorderRequest(TaskStatus Status, List<ReorderItem> Items);
public record ReorderItem(int Id, int Order);
public record ArchiveCompletedTasksRequest(int? ProjectId = null, int? OlderThanDays = null, bool CompletedPhasesOnly = true);

public static class TaskTransitionValidator
{
    private static readonly HashSet<TaskType> ReviewRequiredTypes = new()
    {
        TaskType.Feature, TaskType.Bug, TaskType.Refactor
    };

    private static readonly Dictionary<TaskStatus, HashSet<TaskStatus>> AllowedTransitions = new()
    {
        [TaskStatus.Backlog] = new() { TaskStatus.Todo, TaskStatus.Cancelled },
        [TaskStatus.Todo] = new() { TaskStatus.InProgress, TaskStatus.Backlog, TaskStatus.Cancelled },
        [TaskStatus.InProgress] = new() { TaskStatus.Review, TaskStatus.Blocked, TaskStatus.Cancelled },
        [TaskStatus.Review] = new() { TaskStatus.Done, TaskStatus.InProgress, TaskStatus.Blocked, TaskStatus.Cancelled },
        [TaskStatus.Blocked] = new() { TaskStatus.InProgress, TaskStatus.Review, TaskStatus.Cancelled },
        [TaskStatus.Done] = new() { TaskStatus.InProgress },
        [TaskStatus.Cancelled] = new() { TaskStatus.Backlog }
    };

    public static (bool isValid, string? reason) IsValid(TaskStatus from, TaskStatus to)
    {
        if (from == to)
            return (true, null);

        if (AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to))
            return (true, null);

        var allowedList = AllowedTransitions.TryGetValue(from, out var a)
            ? string.Join(", ", a.Select(s => s.ToString()))
            : "none";
        return (false, $"Cannot move from {from} to {to}. Allowed: {allowedList}");
    }

    /// <summary>
    /// API-level enforcement: Feature/Bug/Refactor tasks moving to Done must have
    /// a linked Test task that is Done (unless skipUiTesting is set).
    /// This runs at the API layer so it cannot be bypassed by skipping MCP tools or hooks.
    /// </summary>
    public static async Task<(bool canComplete, string? reason)> CanCompleteDone(
        LifecycleTask task, LifecycleDbContext db)
    {
        // Test tasks have their own enforcement — must have a passed execution
        if (task.Type == TaskType.Test)
            return await CanCompleteTestTask(task, db);

        // Only enforce linked-test-task requirement on Feature/Bug/Refactor
        if (!ReviewRequiredTypes.Contains(task.Type))
            return (true, null);

        // Skip if task has skipUiTesting flag
        if (task.SkipUiTesting)
            return (true, null);

        // Must have at least one linked Test task that is Done
        var linkedTestTasks = await db.Tasks
            .Where(t => t.SourceTaskId == task.Id && t.Type == TaskType.Test)
            .Select(t => new { t.Id, t.Status })
            .ToListAsync();

        if (linkedTestTasks.Count == 0)
            return (false, $"Task #{task.Id} requires a linked Test task before completion. Use request_review to create one.");

        if (!linkedTestTasks.Any(t => t.Status == TaskStatus.Done))
        {
            var statuses = string.Join(", ", linkedTestTasks.Select(t => $"#{t.Id}: {t.Status}"));
            return (false, $"Task #{task.Id} has linked Test task(s) but none are Done ({statuses}). The Test task must be completed first.");
        }

        return (true, null);
    }

    /// <summary>
    /// Test tasks cannot be completed unless they have at least one Passed test execution.
    /// This prevents agents from rushing Test tasks through the workflow without actually running tests.
    /// </summary>
    private static async Task<(bool canComplete, string? reason)> CanCompleteTestTask(
        LifecycleTask task, LifecycleDbContext db)
    {
        // Check if this test task has any test plans with passed executions
        var hasPassedExecution = await db.TestExecutions
            .AnyAsync(e => e.TestPlan.TaskId == task.Id &&
                           e.Status == TestExecutionStatus.Passed);

        if (!hasPassedExecution)
            return (false, $"Test task #{task.Id} cannot be completed without a passing test execution. " +
                "DO NOT retry this call — it will keep failing. You must actually run UI tests first. Steps: " +
                "1) Use start_test_execution to begin the test plan. " +
                "2) Use agent-browser to open the app and verify each test step visually. " +
                "3) Use record_step_result for each step with Passed/Failed status. " +
                "4) Use complete_test_execution with status Passed/Failed. " +
                "5) ONLY THEN call complete_task again.");

        return (true, null);
    }
}
