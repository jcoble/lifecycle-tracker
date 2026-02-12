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

            var logFile = $"/tmp/test-agent-{taskId}.log";
            var pidFile = $"/tmp/test-agent-{taskId}.pid";

            // Spawn claude in background with stream-json for real-time log output
            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"claude -p \\\"{prompt}\\\" --system-prompt file:{promptFile} --dangerously-skip-permissions --model sonnet --output-format stream-json --add-dir {repoRoot} >> {logFile} 2>&1 & echo $! > {pidFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                var process = Process.Start(psi);
                process?.WaitForExit(5000); // Wait for bash to fork and write PID
                await sse.BroadcastAsync("agent:spawned", new { TaskId = taskId, Type = "test-runner" });

                return Results.Ok(new
                {
                    Message = $"Test runner spawned for task #{taskId}",
                    TaskId = taskId,
                    TaskType = taskType,
                    LogFile = logFile
                });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to spawn test agent: {ex.Message}");
            }
        });

        // Resolve task — spawn an orchestrator agent to implement the task end-to-end
        group.MapPost("/{taskId:int}/resolve", async (int taskId, LifecycleDbContext db, SseService sse) =>
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
            var promptFile = Path.Combine(repoRoot, ".claude/prompts/resolve-agent-system.md");

            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
                return Results.BadRequest(new { Error = "Repository root not configured or not found. Set 'repositoryRoot' in project settings." });

            // Use resolve prompt if it exists, otherwise fall back to the slash command
            var systemPromptArg = File.Exists(promptFile)
                ? $"--system-prompt file:{promptFile}"
                : "";

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

            var prompt = $"/resolve-task {taskId}";
            var logFile = $"/tmp/resolve-agent-{taskId}.log";
            var pidFile = $"/tmp/resolve-agent-{taskId}.pid";

            // Spawn claude in background
            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"claude -p \\\"{prompt}\\\" {systemPromptArg} --dangerously-skip-permissions --model sonnet --output-format stream-json --add-dir {repoRoot} >> {logFile} 2>&1 & echo $! > {pidFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                var process = Process.Start(psi);
                process?.WaitForExit(5000);
                await sse.BroadcastAsync("agent:spawned", new { TaskId = taskId, Type = "resolve" });

                return Results.Ok(new
                {
                    Message = $"Resolve agent spawned for task #{taskId}",
                    TaskId = taskId,
                    LogFile = logFile
                });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to spawn resolve agent: {ex.Message}");
            }
        });

        // Read agent log output for a task (polling endpoint, parses stream-json)
        // Checks both test-agent and resolve-agent logs, using whichever is newer
        group.MapGet("/{taskId:int}/agent-log", (int taskId, int? offset) =>
        {
            var testLog = $"/tmp/test-agent-{taskId}.log";
            var resolveLog = $"/tmp/resolve-agent-{taskId}.log";
            // Pick the most recently modified log file
            var testExists = File.Exists(testLog);
            var resolveExists = File.Exists(resolveLog);
            string logFile;
            if (testExists && resolveExists)
                logFile = File.GetLastWriteTimeUtc(resolveLog) > File.GetLastWriteTimeUtc(testLog) ? resolveLog : testLog;
            else if (resolveExists)
                logFile = resolveLog;
            else
                logFile = testLog;

            if (!File.Exists(logFile))
                return Results.Ok(new { lines = Array.Empty<string>(), totalLines = 0, running = false, sessionId = (string?)null });

            var allLines = File.ReadAllLines(logFile);
            var skip = Math.Min(offset ?? 0, allLines.Length);
            var rawNewLines = allLines.Skip(skip).ToArray();
            var parsedLines = ParseStreamJsonLines(rawNewLines);

            // Extract session_id from first few lines of log
            string? sessionId = null;
            foreach (var rawLine in allLines.Take(20))
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawLine);
                    if (doc.RootElement.TryGetProperty("session_id", out var sid))
                    { sessionId = sid.GetString(); break; }
                }
                catch { }
            }

            // Check if agent is still running via PID file
            var pidFile = $"/tmp/test-agent-{taskId}.pid";
            var running = false;
            if (File.Exists(pidFile))
            {
                var pidText = File.ReadAllText(pidFile).Trim();
                if (int.TryParse(pidText, out var pid))
                {
                    try { Process.GetProcessById(pid); running = true; }
                    catch { /* process exited — clean up PID file */ try { File.Delete(pidFile); } catch { } }
                }
            }

            return Results.Ok(new { lines = parsedLines, totalLines = allLines.Length, running, sessionId });
        });

        // Stop a running test agent for a task
        group.MapPost("/{taskId:int}/stop-agent", async (int taskId, SseService sse) =>
        {
            // Check both test-agent and resolve-agent PID files
            var pidFiles = new[] { $"/tmp/test-agent-{taskId}.pid", $"/tmp/resolve-agent-{taskId}.pid" };
            var found = false;

            foreach (var pidFile in pidFiles)
            {
                if (!File.Exists(pidFile)) continue;
                found = true;
                var pidText = File.ReadAllText(pidFile).Trim();
                if (int.TryParse(pidText, out var pid))
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        proc.Kill(entireProcessTree: true);
                    }
                    catch { /* process already exited */ }
                }
                try { File.Delete(pidFile); } catch { }
            }

            if (!found)
                return Results.NotFound(new { error = "No agent PID found for this task" });

            // Append stop message to whichever log exists
            var logFiles = new[] { $"/tmp/test-agent-{taskId}.log", $"/tmp/resolve-agent-{taskId}.log" };
            foreach (var logFile in logFiles)
            {
                if (File.Exists(logFile))
                    try { File.AppendAllText(logFile, $"\n[{DateTime.UtcNow:o}] Agent stopped by user\n"); } catch { }
            }

            await sse.BroadcastAsync("agent:stopped", new { TaskId = taskId });

            return Results.Ok(new { message = $"Agent for task #{taskId} stopped" });
        });

        // Send a message to a finished agent (resume session with user feedback)
        group.MapPost("/{taskId:int}/message-agent", async (int taskId, MessageAgentRequest req, LifecycleDbContext db, SseService sse) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new { Error = "Message is required" });

            // Kill any running agent first so we can resume cleanly
            var pidFile = $"/tmp/test-agent-{taskId}.pid";
            if (File.Exists(pidFile))
            {
                var pidText = File.ReadAllText(pidFile).Trim();
                if (int.TryParse(pidText, out var existingPid))
                {
                    try { var proc = Process.GetProcessById(existingPid); proc.Kill(entireProcessTree: true); }
                    catch { /* already exited */ }
                }
                try { File.Delete(pidFile); } catch { }
                // Brief pause to let process exit
                await Task.Delay(500);
            }

            // Find session_id from log file
            var logFile = $"/tmp/test-agent-{taskId}.log";
            if (!File.Exists(logFile))
                return Results.BadRequest(new { Error = "No agent log found for this task. Run the agent first." });

            string? sessionId = null;
            var logLines = File.ReadAllLines(logFile);
            foreach (var rawLine in logLines.Take(20))
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawLine);
                    if (doc.RootElement.TryGetProperty("session_id", out var sid))
                    { sessionId = sid.GetString(); break; }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(sessionId))
                return Results.BadRequest(new { Error = "Could not find session_id in agent log. The agent may not have produced stream-json output." });

            // Write user message to a temp file to avoid shell injection
            var msgFile = $"/tmp/test-agent-{taskId}.msg";
            await File.WriteAllTextAsync(msgFile, req.Message);

            // Inject a visible marker into the log file
            var userMsgJson = JsonSerializer.Serialize(new { type = "user_message", content = req.Message });
            await File.AppendAllTextAsync(logFile, userMsgJson + "\n");

            // Get project settings for repository root
            var task = await db.Tasks.FindAsync(taskId);
            var project = await db.Projects
                .FirstOrDefaultAsync(p => p.Id == (task != null && task.ProjectId != 0 ? task.ProjectId : 1));
            var settings = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(project?.Settings))
            {
                try { settings = JsonSerializer.Deserialize<Dictionary<string, string>>(project.Settings) ?? settings; }
                catch { }
            }
            var repoRoot = settings.GetValueOrDefault("repositoryRoot", "");

            // Spawn: claude --resume <sessionId> with message from file
            var addDir = !string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot) ? $"--add-dir {repoRoot}" : "";
            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"claude --resume {sessionId} -p \\\"$(cat {msgFile})\\\" --dangerously-skip-permissions --output-format stream-json {addDir} >> {logFile} 2>&1 & echo $! > {pidFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                var process = Process.Start(psi);
                process?.WaitForExit(5000);
                await sse.BroadcastAsync("agent:resumed", new { TaskId = taskId });

                return Results.Ok(new { message = $"Agent resumed for task #{taskId} with your instructions", taskId, sessionId });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to resume agent: {ex.Message}");
            }
        });

        return app;
    }

    /// <summary>
    /// Parses stream-json lines from claude CLI into human-readable log entries.
    /// Handles both stream-json format and plain text (e.g. timestamps from hook scripts).
    /// </summary>
    private static string[] ParseStreamJsonLines(string[] rawLines)
    {
        var result = new List<string>();
        foreach (var line in rawLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) { result.Add(line); continue; }
                var type = typeEl.GetString();

                if (type == "assistant" && root.TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        var btype = block.TryGetProperty("type", out var bt) ? bt.GetString() : "";
                        if (btype == "text" && block.TryGetProperty("text", out var text))
                        {
                            var t = text.GetString()?.Trim();
                            if (!string.IsNullOrEmpty(t))
                            {
                                // Split multi-line text into separate log lines
                                foreach (var tl in t.Split('\n'))
                                {
                                    var trimmed = tl.TrimEnd();
                                    if (!string.IsNullOrEmpty(trimmed))
                                        result.Add(trimmed.Length > 300 ? trimmed[..300] + "..." : trimmed);
                                }
                            }
                        }
                        else if (btype == "tool_use" && block.TryGetProperty("name", out var name))
                        {
                            var toolName = name.GetString() ?? "";
                            var summary = "";
                            if (block.TryGetProperty("input", out var input))
                            {
                                if (input.TryGetProperty("command", out var cmd))
                                {
                                    var c = cmd.GetString() ?? "";
                                    summary = c.Length > 120 ? $": {c[..120]}..." : $": {c}";
                                }
                                else if (input.TryGetProperty("activity", out var act))
                                    summary = $": {act.GetString()}";
                                else if (input.TryGetProperty("file_path", out var fp))
                                    summary = $": {fp.GetString()}";
                                else if (input.TryGetProperty("pattern", out var pat))
                                    summary = $": {pat.GetString()}";
                                else if (input.TryGetProperty("url", out var url))
                                    summary = $": {url.GetString()}";
                                else if (input.TryGetProperty("taskId", out var tid))
                                    summary = $": task #{tid}";
                                else if (input.TryGetProperty("status", out var st))
                                    summary = $": {st.GetString()}";
                            }
                            result.Add($"» {toolName}{summary}");
                        }
                        // Skip tool_result blocks — too verbose
                    }
                }
                else if (type == "user_message")
                {
                    var userMsg = root.TryGetProperty("content", out var um) ? um.GetString() : "";
                    result.Add($"[You] {userMsg}");
                }
                else if (type == "result")
                {
                    var cost = root.TryGetProperty("cost_usd", out var c) ? c.GetDouble() : 0;
                    var turns = root.TryGetProperty("num_turns", out var nt) ? nt.GetInt32() : 0;
                    var isError = root.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
                    result.Add(isError
                        ? $"Agent failed after {turns} turns (${cost:F4})"
                        : $"Agent finished ({turns} turns, ${cost:F4})");
                }
                // Skip system/init and user (tool_result) messages
            }
            catch (JsonException)
            {
                // Not JSON — plain text from hook script or stderr
                result.Add(line);
            }
        }
        return result.ToArray();
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
public record MessageAgentRequest(string Message);

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
    /// all tests on THIS task passing (no linked Test task requirement).
    /// Test tasks still need a passed execution.
    /// </summary>
    public static async Task<(bool canComplete, string? reason)> CanCompleteDone(
        LifecycleTask task, LifecycleDbContext db)
    {
        // Test tasks have their own enforcement — must have a passed execution
        if (task.Type == TaskType.Test)
            return await CanCompleteTestTask(task, db);

        // Only enforce test checks on Feature/Bug/Refactor
        if (!ReviewRequiredTypes.Contains(task.Type))
            return (true, null);

        // Check all tests on THIS task (from all test plans)
        var tests = await db.Tests
            .Where(t => t.TestPlan.TaskId == task.Id)
            .Select(t => new { t.Id, t.Name, t.Status, t.Type })
            .ToListAsync();

        // If no tests exist, allow completion (no test requirement by default)
        if (tests.Count == 0)
            return (true, null);

        // Skip UI-type test checks if skipUiTesting is set
        var testsToCheck = task.SkipUiTesting
            ? tests.Where(t => t.Type != TestType.UI).ToList()
            : tests;

        var failing = testsToCheck.Where(t => t.Status == TestStatus.Failing).ToList();
        if (failing.Count > 0)
        {
            var names = string.Join(", ", failing.Select(t => t.Name));
            return (false, $"Task #{task.Id} has {failing.Count} failing test(s): {names}. Fix them before completing.");
        }

        var notRun = testsToCheck.Where(t => t.Status == TestStatus.Created).ToList();
        if (notRun.Count > 0)
        {
            var names = string.Join(", ", notRun.Select(t => t.Name));
            return (false, $"Task #{task.Id} has {notRun.Count} test(s) not yet run: {names}. Run all tests before completing.");
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
