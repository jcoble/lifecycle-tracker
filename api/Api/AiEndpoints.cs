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
            var protectedTypes = new HashSet<TaskType> { TaskType.Feature, TaskType.Bug, TaskType.Refactor };
            var created = new List<object>();
            foreach (var t in req.Tasks)
            {
                var targetStatus = t.Status ?? TaskStatus.Backlog;
                var taskType = t.Type ?? TaskType.Feature;

                // Block creating Feature/Bug/Refactor tasks directly in Done or Review
                if ((targetStatus == TaskStatus.Done || targetStatus == TaskStatus.Review) && protectedTypes.Contains(taskType))
                    return Results.BadRequest(new { Error = $"Cannot create {taskType} task '{t.Title}' directly in {targetStatus} status. Tasks must follow the workflow: Backlog → Todo → InProgress → Review → Done." });

                var maxOrder = await db.Tasks
                    .Where(x => x.Status == targetStatus)
                    .MaxAsync(x => (int?)x.OrderInColumn) ?? -1;

                var task = new LifecycleTask
                {
                    ProjectId = req.ProjectId ?? 1,
                    PhaseId = t.PhaseId ?? req.PhaseId,
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
                    ProjectId = milestone.ProjectId,
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

            var (isValid, reason) = TaskTransitionValidator.IsValid(task.Status, req.Status);
            if (!isValid) return Results.BadRequest(new { Error = reason });

            // API-level completion enforcement — cannot be bypassed
            if (req.Status == TaskStatus.Done)
            {
                var (canComplete, doneReason) = await TaskTransitionValidator.CanCompleteDone(task, db);
                if (!canComplete) return Results.BadRequest(new { Error = doneReason });
            }

            var oldStatus = task.Status;
            task.Status = req.Status;
            task.UpdatedAt = DateTime.UtcNow;

            if (req.Status == TaskStatus.InProgress && task.StartedAt is null)
                task.StartedAt = DateTime.UtcNow;
            if (req.Status == TaskStatus.Done)
                task.CompletedAt = DateTime.UtcNow;
            else if (oldStatus == TaskStatus.Done)
                task.CompletedAt = null;

            if (req.GitCommitSha is not null) task.GitCommitSha = req.GitCommitSha;
            if (req.GitBranch is not null) task.GitBranch = req.GitBranch;
            if (req.PullRequestUrl is not null) task.PullRequestUrl = req.PullRequestUrl;

            await db.SaveChangesAsync();

            // Log activity
            if (task.Phase?.Milestone is not null)
            {
                await ActivityHelper.LogActivity(db, task.Phase.Milestone.ProjectId,
                    ActivityType.TaskMoved, TaskSource.Claude, "Task", task.Id, "Transitioned",
                    $"Task '{task.Title}' {oldStatus} -> {req.Status}");
            }

            await sse.BroadcastAsync("task:moved", new { task.Id, OldStatus = oldStatus.ToString(), NewStatus = req.Status.ToString() });

            // Find triggered team members for this status change
            var newStatusStr = req.Status.ToString();
            int? projectId = task.Phase?.Milestone?.ProjectId;
            object[]? triggeredAgents = null;
            if (projectId.HasValue)
            {
                var project = await db.Projects.FindAsync(projectId.Value);
                var settings = project is not null
                    ? TeamEndpoints.ParseSettings(project)
                    : new Dictionary<string, string>();

                var members = await db.TeamMembers
                    .Where(tm => tm.ProjectId == projectId.Value && tm.TriggerStatuses != null)
                    .ToListAsync();
                triggeredAgents = members
                    .Where(tm => tm.TriggerStatuses != null && tm.TriggerStatuses.Contains($"\"{newStatusStr}\""))
                    .Select(tm => (object)new
                    {
                        tm.Id, tm.AgentName, tm.Role, tm.ModelName,
                        tm.SpawnPromptTemplate,
                        ResolvedPrompt = TeamEndpoints.ResolvePromptVariables(tm.SpawnPromptTemplate, settings),
                        tm.TriggerStatuses
                    })
                    .ToArray();
            }

            return Results.Ok(new
            {
                task.Id, task.Title, Status = task.Status.ToString(),
                task.StartedAt, task.CompletedAt,
                task.RequiredTestLevel,
                TriggeredAgents = triggeredAgents ?? Array.Empty<object>()
            });
        });

        // Full project context dump for Claude
        group.MapGet("/context", async (LifecycleDbContext db, int? projectId) =>
        {
            var query = db.Projects
                .Include(p => p.Milestones).ThenInclude(m => m.Phases).ThenInclude(ph => ph.Tasks.Where(t => !t.IsArchived)).ThenInclude(t => t.TestPlans).ThenInclude(tp => tp.Tests)
                .Include(p => p.Milestones).ThenInclude(m => m.Phases).ThenInclude(ph => ph.Tasks.Where(t => !t.IsArchived)).ThenInclude(t => t.TaskLabels).ThenInclude(tl => tl.Label)
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

            var allTests = allTasks.SelectMany(t => t.TestPlans).SelectMany(tp => tp.Tests).ToList();

            var recentActivity = await db.ActivityLogs
                .Where(a => a.ProjectId == project.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(10)
                .ToListAsync();

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
                            Tests = t.TestPlans.SelectMany(tp => tp.Tests).Select(tr => new
                            {
                                tr.Id, tr.Name,
                                Status = tr.Status.ToString()
                            }),
                            Labels = t.TaskLabels.Select(tl => tl.Label.Name)
                        })
                    })
                },
                Phases = (activeMilestone?.Phases ?? []).OrderBy(p => p.OrderIndex).Select(p => new
                {
                    p.Id, p.Name, p.PhaseNumber,
                    Status = p.Status.ToString(),
                    TaskCount = p.Tasks.Count,
                    DoneCount = p.Tasks.Count(t => t.Status == TaskStatus.Done)
                }),
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

        // Get active project settings (for MCP test enforcement)
        group.MapGet("/settings", async (LifecycleDbContext db, int? projectId) =>
        {
            Project? project;
            if (projectId.HasValue)
                project = await db.Projects.FindAsync(projectId.Value);
            else
                project = await db.Projects.Where(p => p.Status == ProjectStatus.Active).FirstOrDefaultAsync();

            if (project is null) return Results.NotFound();
            var settings = TeamEndpoints.ParseSettings(project);
            return Results.Ok(new { project.Id, Settings = settings });
        });

        // Get tests for a task (from all test plans)
        group.MapGet("/tasks/{id:int}/tests", async (int id, LifecycleDbContext db) =>
        {
            var tests = await db.Tests
                .Include(t => t.TestPlan)
                .Where(t => t.TestPlan.TaskId == id)
                .Select(t => new
                {
                    t.Id, TaskId = t.TestPlan.TaskId,
                    TestType = t.Type.ToString(),
                    Status = t.Status.ToString(),
                    t.Name, t.TestFile,
                    t.LastRunAt, t.LastRunOutput
                })
                .ToListAsync();
            return Results.Ok(tests);
        });

        // Generate a test task from a source task (with optional test plan + tests + steps)
        group.MapPost("/tasks/{id:int}/generate-test-task", async (int id, GenerateTestTaskRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var sourceTask = await db.Tasks.Include(t => t.Phase).ThenInclude(p => p!.Milestone).FirstOrDefaultAsync(t => t.Id == id);
            if (sourceTask is null) return Results.NotFound("Source task not found");

            var maxOrder = await db.Tasks
                .Where(x => x.Status == TaskStatus.Todo)
                .MaxAsync(x => (int?)x.OrderInColumn) ?? -1;

            var testTask = new LifecycleTask
            {
                ProjectId = sourceTask.ProjectId,
                PhaseId = sourceTask.PhaseId,
                Title = $"Test: {sourceTask.Title}",
                Description = $"UI test task auto-generated from task #{id}",
                Status = TaskStatus.Todo,
                Priority = TaskPriority.P2,
                Type = TaskType.Test,
                Source = TaskSource.Claude,
                SourceTaskId = id,
                OrderInColumn = maxOrder + 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Tasks.Add(testTask);
            await db.SaveChangesAsync();

            // Create test plan with nested tests + steps
            TestPlan? testPlan = null;
            if (req.Tests is { Count: > 0 })
            {
                testPlan = new TestPlan
                {
                    TaskId = testTask.Id,
                    Name = req.TestPlanName ?? $"Test plan for: {sourceTask.Title}",
                    RequiredLevel = Enum.TryParse<TestLevel>(req.TestLevel, out var level) ? level : TestLevel.Smoke,
                    Status = TestPlanStatus.Draft,
                    Source = TestPlanSource.AI_Generated,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                for (int ti = 0; ti < req.Tests.Count; ti++)
                {
                    var t = req.Tests[ti];
                    var test = new Test
                    {
                        OrderIndex = ti,
                        Name = t.Name,
                        Description = t.Description,
                        Type = Enum.TryParse<TestType>(t.Type, out var tt) ? tt : TestType.UI,
                        Status = TestStatus.Created,
                        Framework = t.Framework,
                        CreatedAt = DateTime.UtcNow
                    };

                    if (t.Steps is { Count: > 0 })
                    {
                        for (int si = 0; si < t.Steps.Count; si++)
                        {
                            var s = t.Steps[si];
                            test.Steps.Add(new TestStep
                            {
                                OrderIndex = si,
                                StepType = Enum.TryParse<TestStepType>(s.StepType, out var st) ? st : TestStepType.Action,
                                Description = s.Description,
                                ExpectedResult = s.ExpectedResult,
                                AutomationCommand = s.AutomationCommand,
                                CreatedAt = DateTime.UtcNow
                            });
                        }
                    }
                    testPlan.Tests.Add(test);
                }
                db.TestPlans.Add(testPlan);
                await db.SaveChangesAsync();
            }

            // Log activity
            var projectId = sourceTask.Phase?.Milestone?.ProjectId ?? sourceTask.ProjectId;
            await ActivityHelper.LogActivity(db, projectId, ActivityType.TestTaskGenerated,
                TaskSource.Claude, "Task", testTask.Id, "TestTaskGenerated",
                $"Test task '{testTask.Title}' auto-generated from task #{id}");

            await sse.BroadcastAsync("task:created", new { testTask.Id, testTask.Title, Status = testTask.Status.ToString(), SourceTaskId = id });

            return Results.Created($"/api/tasks/{testTask.Id}", new
            {
                TestTaskId = testTask.Id,
                TestPlanId = testPlan?.Id,
                Title = testTask.Title
            });
        });

        // Request review - move to Review + optional test task generation
        group.MapPost("/tasks/{id:int}/request-review", async (int id, RequestReviewRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks.Include(t => t.Phase).ThenInclude(p => p!.Milestone)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (task is null) return Results.NotFound();

            // Must be InProgress or Blocked
            if (task.Status != TaskStatus.InProgress && task.Status != TaskStatus.Blocked)
                return Results.BadRequest(new { Error = $"Task must be InProgress or Blocked to request review. Current: {task.Status}" });

            var (isValid, reason) = TaskTransitionValidator.IsValid(task.Status, TaskStatus.Review);
            if (!isValid) return Results.BadRequest(new { Error = reason });

            var oldStatus = task.Status;
            task.Status = TaskStatus.Review;
            task.UpdatedAt = DateTime.UtcNow;
            if (req.GitCommitSha is not null) task.GitCommitSha = req.GitCommitSha;
            if (req.GitBranch is not null) task.GitBranch = req.GitBranch;
            if (req.PullRequestUrl is not null) task.PullRequestUrl = req.PullRequestUrl;
            await db.SaveChangesAsync();

            // Always create a linked test task for Feature/Bug/Refactor tasks
            // If testPlan is provided, use it; otherwise create a placeholder
            object? testTaskInfo = null;
            var requiresTestTask = task.Type == TaskType.Feature || task.Type == TaskType.Bug || task.Type == TaskType.Refactor;
            if (requiresTestTask && !task.SkipUiTesting)
            {
                var maxOrder = await db.Tasks
                    .Where(x => x.Status == TaskStatus.Todo)
                    .MaxAsync(x => (int?)x.OrderInColumn) ?? -1;
                var testTask = new LifecycleTask
                {
                    ProjectId = task.ProjectId,
                    PhaseId = task.PhaseId,
                    Title = $"Test: {task.Title}",
                    Description = $"UI test task auto-generated from task #{id}",
                    Status = TaskStatus.Todo,
                    Priority = TaskPriority.P2,
                    Type = TaskType.Test,
                    Source = TaskSource.Claude,
                    SourceTaskId = id,
                    OrderInColumn = maxOrder + 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.Tasks.Add(testTask);
                await db.SaveChangesAsync();

                TestPlan? testPlan = null;
                if (req.TestPlan?.Tests is { Count: > 0 })
                {
                    testPlan = new TestPlan
                    {
                        TaskId = testTask.Id,
                        Name = req.TestPlan.TestPlanName ?? $"Test plan for: {task.Title}",
                        RequiredLevel = Enum.TryParse<TestLevel>(req.TestPlan.TestLevel, out var level) ? level : TestLevel.Smoke,
                        Status = TestPlanStatus.Draft,
                        Source = TestPlanSource.AI_Generated,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    for (int ti = 0; ti < req.TestPlan.Tests.Count; ti++)
                    {
                        var t = req.TestPlan.Tests[ti];
                        var test = new Test
                        {
                            OrderIndex = ti,
                            Name = t.Name,
                            Description = t.Description,
                            Type = Enum.TryParse<TestType>(t.Type, out var tt) ? tt : TestType.UI,
                            Status = TestStatus.Created,
                            Framework = t.Framework,
                            CreatedAt = DateTime.UtcNow
                        };
                        if (t.Steps is { Count: > 0 })
                        {
                            for (int si = 0; si < t.Steps.Count; si++)
                            {
                                var s = t.Steps[si];
                                test.Steps.Add(new TestStep
                                {
                                    OrderIndex = si,
                                    StepType = Enum.TryParse<TestStepType>(s.StepType, out var st) ? st : TestStepType.Action,
                                    Description = s.Description,
                                    ExpectedResult = s.ExpectedResult,
                                    AutomationCommand = s.AutomationCommand,
                                    CreatedAt = DateTime.UtcNow
                                });
                            }
                        }
                        testPlan.Tests.Add(test);
                    }
                    db.TestPlans.Add(testPlan);
                    await db.SaveChangesAsync();
                }
                else
                {
                    // Create a placeholder smoke test plan so the test agent has something to work with
                    testPlan = new TestPlan
                    {
                        TaskId = testTask.Id,
                        Name = $"Smoke test: {task.Title}",
                        RequiredLevel = TestLevel.Smoke,
                        Status = TestPlanStatus.Draft,
                        Source = TestPlanSource.AI_Generated,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    var smokeTest = new Test
                    {
                        OrderIndex = 0,
                        Name = $"Verify: {task.Title}",
                        Description = $"Smoke test auto-generated for task #{id}. Verify the feature works as described.",
                        Type = TestType.UI,
                        Status = TestStatus.Created,
                        CreatedAt = DateTime.UtcNow
                    };
                    smokeTest.Steps.Add(new TestStep
                    {
                        OrderIndex = 0,
                        StepType = TestStepType.Action,
                        Description = $"Verify the changes from task #{id} ({task.Title}) work correctly in the UI",
                        ExpectedResult = "Feature works as expected with no visual or functional regressions",
                        CreatedAt = DateTime.UtcNow
                    });
                    testPlan.Tests.Add(smokeTest);
                    db.TestPlans.Add(testPlan);
                    await db.SaveChangesAsync();
                }

                testTaskInfo = new { TestTaskId = testTask.Id, TestPlanId = testPlan?.Id, Title = testTask.Title };
            }
            else if (req.TestPlan is not null && !requiresTestTask)
            {
                // Non-required type but testPlan provided — still create it
                var maxOrder = await db.Tasks
                    .Where(x => x.Status == TaskStatus.Todo)
                    .MaxAsync(x => (int?)x.OrderInColumn) ?? -1;
                var testTask = new LifecycleTask
                {
                    ProjectId = task.ProjectId,
                    PhaseId = task.PhaseId,
                    Title = $"Test: {task.Title}",
                    Description = $"UI test task auto-generated from task #{id}",
                    Status = TaskStatus.Todo,
                    Priority = TaskPriority.P2,
                    Type = TaskType.Test,
                    Source = TaskSource.Claude,
                    SourceTaskId = id,
                    OrderInColumn = maxOrder + 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.Tasks.Add(testTask);
                await db.SaveChangesAsync();
                testTaskInfo = new { TestTaskId = testTask.Id, TestPlanId = (int?)null, Title = testTask.Title };
            }

            // Create backend test plan on the SOURCE task (not the linked test task)
            int? backendTestPlanId = null;
            var backendTestCount = 0;
            if (req.BackendTests is { Count: > 0 })
            {
                var backendPlan = new TestPlan
                {
                    TaskId = task.Id,
                    Name = $"Backend tests: {task.Title}",
                    RequiredLevel = TestLevel.Smoke,
                    Status = TestPlanStatus.Draft,
                    Source = TestPlanSource.AI_Generated,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                for (int i = 0; i < req.BackendTests.Count; i++)
                {
                    var bt = req.BackendTests[i];
                    backendPlan.Tests.Add(new Test
                    {
                        OrderIndex = i,
                        Name = bt.Name,
                        TestFile = bt.TestFile,
                        Framework = bt.Framework ?? "xUnit",
                        Type = bt.Type == "Integration" ? TestType.Integration : TestType.Unit,
                        Status = TestStatus.Created,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                db.TestPlans.Add(backendPlan);
                await db.SaveChangesAsync();
                backendTestPlanId = backendPlan.Id;
                backendTestCount = req.BackendTests.Count;
            }

            // Log activity
            if (task.Phase?.Milestone is not null)
            {
                await ActivityHelper.LogActivity(db, task.Phase.Milestone.ProjectId,
                    ActivityType.TaskMoved, TaskSource.Claude, "Task", task.Id, "RequestedReview",
                    $"Task '{task.Title}' submitted for review ({oldStatus} -> Review)");
            }

            await sse.BroadcastAsync("task:moved", new { task.Id, OldStatus = oldStatus.ToString(), NewStatus = "Review" });

            // Find triggered agents for Review status
            int? projectId = task.Phase?.Milestone?.ProjectId;
            object[]? triggeredAgents = null;
            if (projectId.HasValue)
            {
                var project = await db.Projects.FindAsync(projectId.Value);
                var settings = project is not null ? TeamEndpoints.ParseSettings(project) : new Dictionary<string, string>();
                var members = await db.TeamMembers
                    .Where(tm => tm.ProjectId == projectId.Value && tm.TriggerStatuses != null)
                    .ToListAsync();
                triggeredAgents = members
                    .Where(tm => tm.TriggerStatuses != null && tm.TriggerStatuses.Contains("\"Review\""))
                    .Select(tm => (object)new
                    {
                        tm.Id, tm.AgentName, tm.Role, tm.ModelName,
                        tm.SpawnPromptTemplate,
                        ResolvedPrompt = TeamEndpoints.ResolvePromptVariables(tm.SpawnPromptTemplate, settings),
                        tm.TriggerStatuses
                    })
                    .ToArray();
            }

            return Results.Ok(new
            {
                task.Id, task.Title, Status = task.Status.ToString(),
                TestTask = testTaskInfo,
                BackendTestPlanId = backendTestPlanId,
                BackendTestCount = backendTestCount,
                TriggeredAgents = triggeredAgents ?? Array.Empty<object>()
            });
        });

        // Get linked test tasks for a source task
        group.MapGet("/tasks/{id:int}/linked-test-tasks", async (int id, LifecycleDbContext db) =>
        {
            var linkedTests = await db.Tasks
                .Where(t => t.SourceTaskId == id && t.Type == TaskType.Test)
                .Select(t => new
                {
                    t.Id, t.Title, Status = t.Status.ToString(),
                    Type = t.Type.ToString(), t.SourceTaskId,
                    t.CreatedAt, t.CompletedAt
                })
                .ToListAsync();
            return Results.Ok(linkedTests);
        });

        // Report test failure - add comment and move source task back to InProgress
        group.MapPost("/tasks/{id:int}/report-test-failure", async (int id, ReportTestFailureRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var sourceTask = await db.Tasks.Include(t => t.Phase).ThenInclude(p => p!.Milestone)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (sourceTask is null) return Results.NotFound("Source task not found");

            // Add failure comment
            var comment = new Comment
            {
                TaskId = id,
                Content = $"Test Failure Report:\n\n{req.FailureDescription}",
                Source = CommentSource.System,
                Author = "Test Agent",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Comments.Add(comment);

            // Move source task back to InProgress if it's in Review
            var oldStatus = sourceTask.Status;
            if (sourceTask.Status == TaskStatus.Review)
            {
                sourceTask.Status = TaskStatus.InProgress;
                sourceTask.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();

            // Log activity
            if (sourceTask.Phase?.Milestone is not null)
            {
                await ActivityHelper.LogActivity(db, sourceTask.Phase.Milestone.ProjectId,
                    ActivityType.TaskMoved, TaskSource.Claude, "Task", sourceTask.Id, "TestFailure",
                    $"Task '{sourceTask.Title}' returned to InProgress due to test failure");
            }

            await sse.BroadcastAsync("task:moved", new { sourceTask.Id, OldStatus = oldStatus.ToString(), NewStatus = sourceTask.Status.ToString() });

            return Results.Ok(new
            {
                sourceTask.Id, sourceTask.Title, Status = sourceTask.Status.ToString(),
                CommentId = comment.Id,
                Message = "Task returned to InProgress with failure report"
            });
        });

        return app;
    }
}

public record BulkCreateTasksRequest(List<BulkTaskItem> Tasks, int? ProjectId = null, int? PhaseId = null);
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

public record TaskTransitionRequest(TaskStatus Status, string? GitCommitSha = null, string? GitBranch = null, string? PullRequestUrl = null);


public record PasteAttachmentRequest(int TaskId, string Base64Data, string ContentType, string? OriginalFileName = null, int? Width = null, int? Height = null);

public record GenerateTestTaskRequest(
    string? TestPlanName = null,
    string? TestLevel = null,
    List<GenerateTestRequest>? Tests = null);
public record GenerateTestRequest(
    string Name,
    string? Description = null,
    string? Type = null,
    string? Framework = null,
    List<GenerateTestStepRequest>? Steps = null);
public record GenerateTestStepRequest(
    string Description,
    string? ExpectedResult = null,
    string? StepType = null,
    string? AutomationCommand = null);

public record RequestReviewRequest(
    string? GitCommitSha = null,
    string? GitBranch = null,
    string? PullRequestUrl = null,
    GenerateTestTaskRequest? TestPlan = null,
    List<BackendTestEntry>? BackendTests = null);

public record BackendTestEntry(
    string Name,
    string TestFile,
    string? Type = null,
    string? Framework = null);

public record ReportTestFailureRequest(string FailureDescription, int? TestTaskId = null);
