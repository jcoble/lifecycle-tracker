using System.Text.Json;
using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Lifecycle.Data.Enums.TaskStatus;

namespace Lifecycle.Api;

public static class AiEndpoints
{
    public static WebApplication MapAiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/ai");

        // Bulk create tasks
        group.MapPost("/tasks/bulk-create", async (BulkCreateTasksRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var created = new List<object>();
            foreach (var t in req.Tasks)
            {
                var targetStatus = t.Status ?? TaskStatus.Backlog;
                var maxOrder = await db.Tasks
                    .Where(x => x.Status == targetStatus)
                    .MaxAsync(x => (int?)x.OrderInColumn) ?? -1;

                var task = new LifecycleTask
                {
                    PhaseId = t.PhaseId,
                    Title = t.Title,
                    Description = t.Description,
                    Status = targetStatus,
                    Priority = t.Priority ?? TaskPriority.P3,
                    Type = t.Type ?? TaskType.Feature,
                    Source = TaskSource.Claude,
                    OrderInColumn = maxOrder + 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.Tasks.Add(task);
                await db.SaveChangesAsync();

                if (t.LabelIds is { Count: > 0 })
                {
                    foreach (var labelId in t.LabelIds)
                        db.TaskLabels.Add(new TaskLabel { TaskId = task.Id, LabelId = labelId });
                    await db.SaveChangesAsync();
                }

                created.Add(new { task.Id, task.Title, Status = task.Status.ToString() });
            }

            // Log activity if projectId provided
            if (req.ProjectId.HasValue)
            {
                await ActivityHelper.LogActivity(db, req.ProjectId.Value, ActivityType.BulkTasksCreated,
                    TaskSource.Claude, "Task", 0, "BulkCreated",
                    $"{created.Count} tasks created via AI");
            }

            await sse.BroadcastAsync("task:created", new { Count = created.Count, Tasks = created });

            return Results.Ok(new { Count = created.Count, Tasks = created });
        });

        // Phase breakdown - create phase + tasks
        group.MapPost("/phases/breakdown", async (PhaseBreakdownRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var milestone = await db.Milestones.FindAsync(req.MilestoneId);
            if (milestone is null) return Results.NotFound("Milestone not found");

            var maxOrder = await db.Phases
                .Where(p => p.MilestoneId == req.MilestoneId)
                .MaxAsync(p => (int?)p.OrderIndex) ?? -1;

            var phase = new Phase
            {
                MilestoneId = req.MilestoneId,
                Name = req.PhaseName,
                Description = req.PhaseDescription,
                Goal = req.Goal,
                SuccessCriteria = req.SuccessCriteria,
                Status = PhaseStatus.NotStarted,
                PhaseNumber = req.PhaseNumber ?? (maxOrder + 2),
                OrderIndex = maxOrder + 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Phases.Add(phase);
            await db.SaveChangesAsync();

            var createdTasks = new List<object>();
            var orderIdx = 0;
            foreach (var t in req.Tasks)
            {
                var task = new LifecycleTask
                {
                    PhaseId = phase.Id,
                    Title = t.Title,
                    Description = t.Description,
                    Status = TaskStatus.Backlog,
                    Priority = t.Priority ?? TaskPriority.P3,
                    Type = t.Type ?? TaskType.Feature,
                    Source = TaskSource.Claude,
                    OrderInColumn = orderIdx++,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.Tasks.Add(task);
                createdTasks.Add(new { Title = task.Title, Type = task.Type.ToString() });
            }
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(db, milestone.ProjectId, ActivityType.PhaseBreakdown,
                TaskSource.Claude, "Phase", phase.Id, "Breakdown",
                $"Phase '{phase.Name}' created with {createdTasks.Count} tasks via AI");

            await sse.BroadcastAsync("phase:created", new { phase.Id, phase.Name, TaskCount = createdTasks.Count });

            return Results.Created($"/api/phases/{phase.Id}", new
            {
                Phase = new { phase.Id, phase.Name, Status = phase.Status.ToString() },
                Tasks = createdTasks
            });
        });

        // Task transition with auto timestamps + activity logging
        group.MapPost("/tasks/{id:int}/transition", async (int id, TaskTransitionRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks.Include(t => t.Phase).ThenInclude(p => p!.Milestone)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();

            var oldStatus = task.Status;
            task.Status = req.Status;
            task.UpdatedAt = DateTime.UtcNow;

            if (req.Status == TaskStatus.InProgress && task.StartedAt is null)
                task.StartedAt = DateTime.UtcNow;
            if (req.Status == TaskStatus.Done)
                task.CompletedAt = DateTime.UtcNow;
            else if (oldStatus == TaskStatus.Done)
                task.CompletedAt = null;

            await db.SaveChangesAsync();

            // Log activity
            if (task.Phase?.Milestone is not null)
            {
                await ActivityHelper.LogActivity(db, task.Phase.Milestone.ProjectId,
                    ActivityType.TaskMoved, TaskSource.Claude, "Task", task.Id, "Transitioned",
                    $"Task '{task.Title}' {oldStatus} -> {req.Status}");
            }

            await sse.BroadcastAsync("task:moved", new { task.Id, OldStatus = oldStatus.ToString(), NewStatus = req.Status.ToString() });

            return Results.Ok(new
            {
                task.Id, task.Title, Status = task.Status.ToString(),
                task.StartedAt, task.CompletedAt
            });
        });

        // Record test file creation
        group.MapPost("/tests/record", async (AiRecordTestRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks.FindAsync(req.TaskId);
            if (task is null) return Results.NotFound();

            var test = new TestRecord
            {
                TaskId = req.TaskId,
                TestType = req.TestType,
                Status = TestStatus.Created,
                TestName = req.TestName,
                TestFile = req.TestFile,
                Framework = req.Framework,
                CreatedAt = DateTime.UtcNow
            };
            db.TestRecords.Add(test);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("test:updated", new { test.Id, test.TaskId, Status = test.Status.ToString() });

            return Results.Created($"/api/tests/{test.Id}", new
            {
                test.Id, test.TaskId, TestType = test.TestType.ToString(),
                Status = test.Status.ToString(), test.TestName, test.TestFile
            });
        });

        // Record test result
        group.MapPost("/tests/result", async (AiTestResultRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var test = await db.TestRecords.FindAsync(req.TestId);
            if (test is null) return Results.NotFound();

            test.LastRunAt = DateTime.UtcNow;
            test.LastRunResult = req.Result;
            test.LastRunOutput = req.Output;
            test.TotalRuns++;

            if (req.Passed)
            {
                test.PassedRuns++;
                test.Status = TestStatus.Passing;
            }
            else
            {
                test.FailedRuns++;
                test.Status = TestStatus.Failing;
            }

            await db.SaveChangesAsync();

            await sse.BroadcastAsync("test:updated", new { test.Id, test.TaskId, Status = test.Status.ToString() });

            return Results.Ok(new
            {
                test.Id, test.TaskId, Status = test.Status.ToString(),
                test.TotalRuns, test.PassedRuns, test.FailedRuns
            });
        });

        // Full project context dump for Claude
        group.MapGet("/context", async (LifecycleDbContext db, int? projectId) =>
        {
            var query = db.Projects
                .Include(p => p.Milestones).ThenInclude(m => m.Phases).ThenInclude(ph => ph.Tasks).ThenInclude(t => t.Tests)
                .Include(p => p.Milestones).ThenInclude(m => m.Phases).ThenInclude(ph => ph.Tasks).ThenInclude(t => t.TaskLabels).ThenInclude(tl => tl.Label)
                .Include(p => p.Labels)
                .AsQueryable();

            Project? project;
            if (projectId.HasValue)
                project = await query.FirstOrDefaultAsync(p => p.Id == projectId.Value);
            else
                project = await query.Where(p => p.Status == ProjectStatus.Active).FirstOrDefaultAsync();

            if (project is null) return Results.NotFound("No active project found");

            var activeMilestone = project.Milestones
                .Where(m => m.Status == MilestoneStatus.InProgress)
                .OrderBy(m => m.OrderIndex)
                .FirstOrDefault();

            var allTasks = project.Milestones
                .SelectMany(m => m.Phases)
                .SelectMany(p => p.Tasks)
                .ToList();

            var allTests = allTasks.SelectMany(t => t.Tests).ToList();

            var recentActivity = await db.ActivityLogs
                .Where(a => a.ProjectId == project.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(10)
                .ToListAsync();

            var phases = activeMilestone?.Phases.OrderBy(p => p.OrderIndex).Select(p => new
            {
                p.Id, p.Name, p.PhaseNumber,
                Status = p.Status.ToString(),
                TaskCount = p.Tasks.Count,
                DoneCount = p.Tasks.Count(t => t.Status == TaskStatus.Done)
            });

            return Results.Ok(new
            {
                Project = new
                {
                    project.Id, project.Name, project.Description,
                    Status = project.Status.ToString()
                },
                ActiveMilestone = activeMilestone is null ? null : new
                {
                    activeMilestone.Id, activeMilestone.Name, activeMilestone.Version,
                    Status = activeMilestone.Status.ToString(),
                    Phases = activeMilestone.Phases.OrderBy(p => p.OrderIndex).Select(p => new
                    {
                        p.Id, p.Name, p.PhaseNumber,
                        Status = p.Status.ToString(),
                        Tasks = p.Tasks.OrderBy(t => t.OrderInColumn).Select(t => new
                        {
                            t.Id, t.Title,
                            Status = t.Status.ToString(),
                            Priority = t.Priority.ToString(),
                            Type = t.Type.ToString(),
                            Tests = t.Tests.Select(tr => new
                            {
                                tr.Id, tr.TestName,
                                Status = tr.Status.ToString()
                            }),
                            Labels = t.TaskLabels.Select(tl => tl.Label.Name)
                        })
                    })
                },
                Phases = phases,
                TaskSummary = new
                {
                    Total = allTasks.Count,
                    ByStatus = allTasks.GroupBy(t => t.Status.ToString())
                        .ToDictionary(g => g.Key, g => g.Count()),
                    BySource = allTasks.GroupBy(t => t.Source.ToString())
                        .ToDictionary(g => g.Key, g => g.Count())
                },
                TestSummary = new
                {
                    Total = allTests.Count,
                    Passing = allTests.Count(t => t.Status == TestStatus.Passing),
                    Failing = allTests.Count(t => t.Status == TestStatus.Failing),
                    NotCreated = allTests.Count(t => t.Status == TestStatus.NotCreated)
                },
                Labels = project.Labels.Select(l => new { l.Id, l.Name, l.Color }),
                RecentActivity = recentActivity.Select(a => new
                {
                    a.Id, Type = a.Type.ToString(), a.Description, a.CreatedAt
                })
            });
        });

        // Paste base64 image -> save to uploads
        group.MapPost("/attachments/paste", async (PasteAttachmentRequest req, LifecycleDbContext db, SseService sse, IWebHostEnvironment env) =>
        {
            var task = await db.Tasks.FindAsync(req.TaskId);
            if (task is null) return Results.NotFound();

            var bytes = Convert.FromBase64String(req.Base64Data);
            var uploadsDir = Path.Combine(env.ContentRootPath, "uploads");
            Directory.CreateDirectory(uploadsDir);

            var ext = req.ContentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".bin"
            };

            var fileName = $"{Guid.NewGuid()}{ext}";
            var filePath = Path.Combine(uploadsDir, fileName);
            await File.WriteAllBytesAsync(filePath, bytes);

            var attachment = new Attachment
            {
                TaskId = req.TaskId,
                FileName = fileName,
                OriginalFileName = req.OriginalFileName ?? fileName,
                ContentType = req.ContentType,
                FileSize = bytes.Length,
                StoragePath = filePath,
                Width = req.Width,
                Height = req.Height,
                UploadedBy = "Claude",
                UploadedAt = DateTime.UtcNow
            };
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("attachment:added", new { attachment.Id, attachment.TaskId });

            return Results.Created($"/api/attachments/{attachment.Id}", new
            {
                attachment.Id, attachment.TaskId, attachment.FileName,
                attachment.OriginalFileName, attachment.ContentType, attachment.FileSize
            });
        });

        return app;
    }
}

public record BulkCreateTasksRequest(List<BulkTaskItem> Tasks, int? ProjectId = null);
public record BulkTaskItem(string Title, string? Description = null, int? PhaseId = null, TaskStatus? Status = null, TaskPriority? Priority = null, TaskType? Type = null, List<int>? LabelIds = null);

public record PhaseBreakdownRequest(
    int MilestoneId,
    string PhaseName,
    string? PhaseDescription = null,
    string? Goal = null,
    string? SuccessCriteria = null,
    int? PhaseNumber = null,
    List<PhaseBreakdownTask> Tasks = null!);
public record PhaseBreakdownTask(string Title, string? Description = null, TaskPriority? Priority = null, TaskType? Type = null);

public record TaskTransitionRequest(TaskStatus Status);

public record AiRecordTestRequest(int TaskId, TestType TestType, string? TestName = null, string? TestFile = null, string? Framework = null);
public record AiTestResultRequest(int TestId, bool Passed, string? Result = null, string? Output = null);

public record PasteAttachmentRequest(int TaskId, string Base64Data, string ContentType, string? OriginalFileName = null, int? Width = null, int? Height = null);
