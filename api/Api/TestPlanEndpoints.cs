using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class TestPlanEndpoints
{
    public static WebApplication MapTestPlanEndpoints(this WebApplication app)
    {
        var taskGroup = app.MapGroup("/api/tasks/{taskId:int}/test-plans");
        var planGroup = app.MapGroup("/api/test-plans");
        var testGroup = app.MapGroup("/api/tests");
        var execGroup = app.MapGroup("/api/test-executions");

        // List test plans for a task
        taskGroup.MapGet("/", async (int taskId, LifecycleDbContext db) =>
        {
            var plans = await db.TestPlans
                .Where(tp => tp.TaskId == taskId)
                .Include(tp => tp.Tests.OrderBy(t => t.OrderIndex))
                    .ThenInclude(t => t.Steps.OrderBy(s => s.OrderIndex))
                .Include(tp => tp.Executions.OrderByDescending(e => e.StartedAt).Take(1))
                .OrderBy(tp => tp.RequiredLevel)
                .ToListAsync();

            return Results.Ok(plans.Select(MapPlanToDto));
        });

        // Create test plan for a task (with nested tests + steps)
        taskGroup.MapPost("/", async (int taskId, CreateTestPlanRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks.FindAsync(taskId);
            if (task is null) return Results.NotFound();

            var plan = new TestPlan
            {
                TaskId = taskId,
                Name = req.Name,
                Description = req.Description,
                RequiredLevel = req.RequiredLevel,
                Status = TestPlanStatus.Draft,
                Source = req.Source ?? TestPlanSource.Manual,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            if (req.Tests is { Count: > 0 })
            {
                for (int ti = 0; ti < req.Tests.Count; ti++)
                {
                    var t = req.Tests[ti];
                    var test = new Test
                    {
                        OrderIndex = ti,
                        Name = t.Name,
                        Description = t.Description,
                        Type = t.Type,
                        Status = TestStatus.Created,
                        TestFile = t.TestFile,
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
                                StepType = s.StepType,
                                Description = s.Description,
                                ExpectedResult = s.ExpectedResult,
                                AutomationCommand = s.AutomationCommand,
                                RequiresManualVerification = s.RequiresManualVerification,
                                CreatedAt = DateTime.UtcNow
                            });
                        }
                    }

                    plan.Tests.Add(test);
                }
            }

            db.TestPlans.Add(plan);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("test:updated", new { PlanId = plan.Id, plan.TaskId });

            return Results.Created($"/api/test-plans/{plan.Id}", MapPlanToDto(plan));
        });

        // Get test plan with tests + steps
        planGroup.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var plan = await db.TestPlans
                .Include(tp => tp.Tests.OrderBy(t => t.OrderIndex))
                    .ThenInclude(t => t.Steps.OrderBy(s => s.OrderIndex))
                .Include(tp => tp.Executions.OrderByDescending(e => e.StartedAt))
                    .ThenInclude(e => e.StepResults)
                .FirstOrDefaultAsync(tp => tp.Id == id);

            if (plan is null) return Results.NotFound();
            return Results.Ok(MapPlanDetailDto(plan));
        });

        // Update test plan
        planGroup.MapPatch("/{id:int}", async (int id, UpdateTestPlanRequest req, LifecycleDbContext db) =>
        {
            var plan = await db.TestPlans.FindAsync(id);
            if (plan is null) return Results.NotFound();

            if (req.Name is not null) plan.Name = req.Name;
            if (req.Description is not null) plan.Description = req.Description;
            if (req.RequiredLevel.HasValue) plan.RequiredLevel = req.RequiredLevel.Value;
            if (req.Status.HasValue) plan.Status = req.Status.Value;

            plan.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(MapPlanToDto(plan));
        });

        // Delete test plan
        planGroup.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var plan = await db.TestPlans.FindAsync(id);
            if (plan is null) return Results.NotFound();
            db.TestPlans.Remove(plan);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Add test to plan
        planGroup.MapPost("/{id:int}/tests", async (int id, CreateTestRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var plan = await db.TestPlans.Include(tp => tp.Tests).FirstOrDefaultAsync(tp => tp.Id == id);
            if (plan is null) return Results.NotFound();

            var maxOrder = plan.Tests.Any() ? plan.Tests.Max(t => t.OrderIndex) : -1;
            var test = new Test
            {
                TestPlanId = id,
                OrderIndex = maxOrder + 1,
                Name = req.Name,
                Description = req.Description,
                Type = req.Type,
                Status = TestStatus.Created,
                TestFile = req.TestFile,
                Framework = req.Framework,
                CreatedAt = DateTime.UtcNow
            };

            db.Tests.Add(test);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("test:updated", new { PlanId = id, plan.TaskId });

            return Results.Created($"/api/tests/{test.Id}", MapTestToDto(test));
        });

        // Update test metadata/status
        testGroup.MapPatch("/{id:int}", async (int id, UpdateTestRequest req, LifecycleDbContext db) =>
        {
            var test = await db.Tests.FindAsync(id);
            if (test is null) return Results.NotFound();

            if (req.Name is not null) test.Name = req.Name;
            if (req.Description is not null) test.Description = req.Description;
            if (req.TestFile is not null) test.TestFile = req.TestFile;
            if (req.Framework is not null) test.Framework = req.Framework;
            if (req.Status.HasValue) test.Status = req.Status.Value;

            await db.SaveChangesAsync();
            return Results.Ok(MapTestToDto(test));
        });

        // Record a pass/fail run for a test
        testGroup.MapPost("/{id:int}/run", async (int id, RunTestRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var test = await db.Tests.Include(t => t.TestPlan).FirstOrDefaultAsync(t => t.Id == id);
            if (test is null) return Results.NotFound();

            test.LastRunAt = DateTime.UtcNow;
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

            await sse.BroadcastAsync("test:updated", new { test.Id, PlanId = test.TestPlanId, test.TestPlan.TaskId, Status = test.Status.ToString() });

            return Results.Ok(MapTestToDto(test));
        });

        // Add step to a test
        testGroup.MapPost("/{id:int}/steps", async (int id, CreateTestStepRequest req, LifecycleDbContext db) =>
        {
            var test = await db.Tests.Include(t => t.Steps).FirstOrDefaultAsync(t => t.Id == id);
            if (test is null) return Results.NotFound();

            var maxOrder = test.Steps.Any() ? test.Steps.Max(s => s.OrderIndex) : -1;
            var step = new TestStep
            {
                TestId = id,
                OrderIndex = maxOrder + 1,
                StepType = req.StepType,
                Description = req.Description,
                ExpectedResult = req.ExpectedResult,
                AutomationCommand = req.AutomationCommand,
                RequiresManualVerification = req.RequiresManualVerification,
                CreatedAt = DateTime.UtcNow
            };

            db.TestSteps.Add(step);
            await db.SaveChangesAsync();

            return Results.Created($"/api/tests/{id}/steps/{step.Id}", MapStepToDto(step));
        });

        // Start execution of a test plan
        planGroup.MapPost("/{id:int}/execute", async (int id, StartExecutionRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var plan = await db.TestPlans
                .Include(tp => tp.Tests).ThenInclude(t => t.Steps)
                .FirstOrDefaultAsync(tp => tp.Id == id);
            if (plan is null) return Results.NotFound();

            var totalSteps = plan.Tests.Sum(t => t.Steps.Count);
            var execution = new TestExecution
            {
                TestPlanId = id,
                ExecutionMode = req.ExecutionMode,
                Status = TestExecutionStatus.Running,
                StartedAt = DateTime.UtcNow,
                TotalSteps = totalSteps,
                ExecutedBy = req.ExecutedBy
            };

            db.TestExecutions.Add(execution);
            plan.Status = TestPlanStatus.InProgress;
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("test:updated", new { PlanId = plan.Id, plan.TaskId, ExecutionId = execution.Id });

            return Results.Created($"/api/test-executions/{execution.Id}", new
            {
                execution.Id,
                execution.TestPlanId,
                ExecutionMode = execution.ExecutionMode.ToString(),
                Status = execution.Status.ToString(),
                execution.TotalSteps,
                execution.PassedSteps,
                execution.FailedSteps,
                execution.SkippedSteps,
                execution.ExecutedBy,
                execution.StartedAt,
                execution.CompletedAt,
                execution.FailureReason,
                Tests = plan.Tests.OrderBy(t => t.OrderIndex).Select(t => new
                {
                    t.Id,
                    t.Name,
                    Steps = t.Steps.OrderBy(s => s.OrderIndex).Select(s => new
                    {
                        s.Id,
                        s.OrderIndex,
                        StepType = s.StepType.ToString(),
                        s.Description,
                        s.ExpectedResult
                    })
                })
            });
        });

        // Get execution details
        execGroup.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var exec = await db.TestExecutions
                .Include(e => e.StepResults.OrderBy(sr => sr.ExecutedAt))
                    .ThenInclude(sr => sr.Step)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (exec is null) return Results.NotFound();
            return Results.Ok(MapExecutionDetailDto(exec));
        });

        // Record step result
        execGroup.MapPost("/{id:int}/step-results", async (int id, RecordStepResultRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var exec = await db.TestExecutions
                .Include(e => e.TestPlan)
                .FirstOrDefaultAsync(e => e.Id == id);
            if (exec is null) return Results.NotFound();

            // Require screenshot evidence for Assertion steps with Passed/Failed status
            if (req.Status is TestStepStatus.Passed or TestStepStatus.Failed)
            {
                var step = await db.TestSteps.FindAsync(req.TestStepId);
                if (step is not null && step.StepType == TestStepType.Assertion && string.IsNullOrWhiteSpace(req.Screenshot))
                {
                    return Results.BadRequest(new
                    {
                        Error = "Screenshot required for Assertion steps. Use agent-browser to take a screenshot as evidence.",
                        StepId = req.TestStepId,
                        StepType = "Assertion",
                        Status = req.Status.ToString()
                    });
                }
            }

            var result = new TestStepResult
            {
                TestExecutionId = id,
                TestStepId = req.TestStepId,
                Status = req.Status,
                ActualResult = req.ActualResult,
                ErrorMessage = req.ErrorMessage,
                Screenshot = req.Screenshot,
                DurationMs = req.DurationMs,
                ExecutedAt = DateTime.UtcNow
            };

            db.TestStepResults.Add(result);

            // Update execution counts
            switch (req.Status)
            {
                case TestStepStatus.Passed: exec.PassedSteps++; break;
                case TestStepStatus.Failed: exec.FailedSteps++; break;
                case TestStepStatus.Skipped: exec.SkippedSteps++; break;
            }

            await db.SaveChangesAsync();

            await sse.BroadcastAsync("test:updated", new { ExecutionId = id, PlanId = exec.TestPlanId, exec.TestPlan.TaskId });

            return Results.Created($"/api/test-executions/{id}/step-results/{result.Id}", MapStepResultToDto(result));
        });

        // Complete execution
        execGroup.MapPatch("/{id:int}/complete", async (int id, CompleteExecutionRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var exec = await db.TestExecutions
                .Include(e => e.TestPlan)
                .FirstOrDefaultAsync(e => e.Id == id);
            if (exec is null) return Results.NotFound();

            // When completing as Passed, verify all Assertion steps have results with screenshots
            if (req.Status == TestExecutionStatus.Passed)
            {
                var assertionSteps = await db.TestSteps
                    .Where(s => s.Test.TestPlanId == exec.TestPlanId && s.StepType == TestStepType.Assertion)
                    .Select(s => new { s.Id, s.Description })
                    .ToListAsync();

                var evidenceResults = await db.TestStepResults
                    .Where(r => r.TestExecutionId == id)
                    .ToListAsync();

                var stepResultsByStepId = evidenceResults.ToDictionary(r => r.TestStepId);

                var missing = new List<string>();
                foreach (var step in assertionSteps)
                {
                    if (!stepResultsByStepId.TryGetValue(step.Id, out var result))
                    {
                        missing.Add($"Step #{step.Id} '{step.Description}': no result recorded");
                    }
                    else if (string.IsNullOrWhiteSpace(result.Screenshot))
                    {
                        missing.Add($"Step #{step.Id} '{step.Description}': result recorded but no screenshot evidence");
                    }
                }

                if (missing.Count > 0)
                {
                    return Results.BadRequest(new
                    {
                        Error = "Cannot mark execution as Passed — Assertion steps missing evidence. Use agent-browser to take screenshots.",
                        MissingEvidence = missing
                    });
                }
            }

            exec.Status = req.Status;
            exec.CompletedAt = DateTime.UtcNow;
            exec.FailureReason = req.FailureReason;

            // Update plan status based on execution result
            exec.TestPlan.Status = req.Status == TestExecutionStatus.Passed
                ? TestPlanStatus.Passing
                : TestPlanStatus.Failing;
            exec.TestPlan.UpdatedAt = DateTime.UtcNow;

            // Update individual test statuses based on their step results
            var tests = await db.Tests
                .Where(t => t.TestPlanId == exec.TestPlanId)
                .ToListAsync();
            var stepResults = await db.TestStepResults
                .Where(r => r.TestExecutionId == exec.Id)
                .ToListAsync();
            foreach (var test in tests)
            {
                var testStepIds = await db.TestSteps
                    .Where(s => s.TestId == test.Id)
                    .Select(s => s.Id)
                    .ToListAsync();
                var results = stepResults.Where(r => testStepIds.Contains(r.TestStepId)).ToList();
                if (results.Count == 0) continue;
                test.Status = results.All(r => r.Status == TestStepStatus.Passed)
                    ? TestStatus.Passing
                    : TestStatus.Failing;
                test.LastRunAt = DateTime.UtcNow;
                test.TotalRuns++;
                if (test.Status == TestStatus.Passing) test.PassedRuns++;
                else test.FailedRuns++;
            }

            await db.SaveChangesAsync();

            // Resolve source task ID from the test plan's task
            var testPlanTask = await db.Tasks.FirstOrDefaultAsync(t => t.Id == exec.TestPlan.TaskId);
            int? sourceTaskId = testPlanTask?.SourceTaskId;

            await sse.BroadcastAsync("test:updated", new { ExecutionId = id, PlanId = exec.TestPlanId, exec.TestPlan.TaskId, Status = req.Status.ToString() });

            var guidance = req.Status == TestExecutionStatus.Passed
                ? sourceTaskId.HasValue ? $"All tests passed. The source task #{sourceTaskId} can now be completed via complete_task." : "All tests passed."
                : sourceTaskId.HasValue ? $"Tests failed. Use report_test_failure on task #{sourceTaskId} to send it back for rework." : "Tests failed.";

            return Results.Ok(new
            {
                Id = exec.Id, TestPlanId = exec.TestPlanId,
                ExecutionMode = exec.ExecutionMode.ToString(),
                Status = exec.Status.ToString(),
                exec.TotalSteps, exec.PassedSteps, exec.FailedSteps, exec.SkippedSteps,
                exec.ExecutedBy, exec.StartedAt, exec.CompletedAt, exec.FailureReason,
                SourceTaskId = sourceTaskId,
                Guidance = guidance
            });
        });

        // Serve screenshot image for a step result
        execGroup.MapGet("/{id:int}/step-results/{resultId:int}/screenshot", async (int id, int resultId, LifecycleDbContext db, IWebHostEnvironment env) =>
        {
            var result = await db.TestStepResults
                .FirstOrDefaultAsync(r => r.Id == resultId && r.TestExecutionId == id);
            if (result is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(result.Screenshot)) return Results.NotFound();

            var filePath = Path.Combine(env.ContentRootPath, "uploads", "screenshots", result.Screenshot);
            if (!File.Exists(filePath)) return Results.NotFound();

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var contentType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png"
            };
            return Results.File(filePath, contentType);
        });

        // Check if task can complete (testing requirements met)
        app.MapGet("/api/tasks/{taskId:int}/can-complete", async (int taskId, bool? skipUiCheck, LifecycleDbContext db) =>
        {
            var task = await db.Tasks
                .Include(t => t.TestPlans)
                    .ThenInclude(tp => tp.Tests)
                .Include(t => t.TestPlans)
                    .ThenInclude(tp => tp.Executions)
                .FirstOrDefaultAsync(t => t.Id == taskId);

            if (task is null) return Results.NotFound();

            if (!task.RequiredTestLevel.HasValue)
                return Results.Ok(new { CanComplete = true, Reason = (string?)null });

            var allTests = task.TestPlans.SelectMany(tp => tp.Tests).ToList();
            var checksTests = skipUiCheck == true
                ? allTests.Where(t => t.Type != TestType.UI).ToList()
                : allTests;

            if (checksTests.Count == 0 && allTests.Count == 0)
                return Results.Ok(new { CanComplete = false, Reason = "No tests created. Create tests within a test plan first." });

            // If skipUiCheck and we have no non-UI tests but have UI tests, allow completion
            if (skipUiCheck == true && checksTests.Count == 0 && allTests.Count > 0)
                return Results.Ok(new { CanComplete = true, Reason = (string?)null });

            var hasUnit = checksTests.Any(t => t.Type == TestType.Unit);
            var hasIntegration = checksTests.Any(t => t.Type == TestType.Integration);
            var anyFailing = checksTests.Any(t => t.Status == TestStatus.Failing);
            var anyNotRun = checksTests.Any(t => t.Status == TestStatus.Created || t.Status == TestStatus.NotCreated);

            var issues = new List<string>();
            if (!hasUnit) issues.Add("No Unit test found");
            if (!hasIntegration) issues.Add("No Integration test found");
            if (anyFailing) issues.Add("Some tests are failing");
            if (anyNotRun) issues.Add("Some tests have not been run yet");

            if (skipUiCheck != true)
            {
                var qualifyingPlan = task.TestPlans
                    .FirstOrDefault(tp => tp.RequiredLevel >= task.RequiredTestLevel.Value
                        && tp.Executions.Any(e => e.Status == TestExecutionStatus.Passed));

                if (qualifyingPlan is null)
                {
                    var hasAnyPlan = task.TestPlans.Any(tp => tp.RequiredLevel >= task.RequiredTestLevel.Value);
                    issues.Add(hasAnyPlan
                        ? "Test plan exists but no passing execution yet"
                        : $"No test plan at level {task.RequiredTestLevel.Value} or above");
                }
            }

            if (issues.Count > 0)
                return Results.Ok(new { CanComplete = false, Reason = string.Join("; ", issues) });

            return Results.Ok(new { CanComplete = true, Reason = (string?)null });
        });

        return app;
    }

    private static object MapPlanToDto(TestPlan tp)
    {
        var latestExec = tp.Executions?.OrderByDescending(e => e.StartedAt).FirstOrDefault();
        return new
        {
            tp.Id, tp.TaskId, tp.Name, tp.Description,
            RequiredLevel = tp.RequiredLevel.ToString(),
            Status = tp.Status.ToString(),
            Source = tp.Source.ToString(),
            TestCount = tp.Tests?.Count ?? 0,
            StepCount = tp.Tests?.Sum(t => t.Steps?.Count ?? 0) ?? 0,
            tp.CreatedAt, tp.UpdatedAt,
            Tests = tp.Tests?.Select(MapTestToDto),
            LatestExecution = latestExec is null ? null : new
            {
                latestExec.Id,
                Status = latestExec.Status.ToString(),
                latestExec.PassedSteps, latestExec.FailedSteps, latestExec.TotalSteps,
                latestExec.StartedAt, latestExec.CompletedAt
            }
        };
    }

    private static object MapPlanDetailDto(TestPlan tp) => new
    {
        tp.Id, tp.TaskId, tp.Name, tp.Description,
        RequiredLevel = tp.RequiredLevel.ToString(),
        Status = tp.Status.ToString(),
        Source = tp.Source.ToString(),
        tp.CreatedAt, tp.UpdatedAt,
        Tests = tp.Tests.Select(t => new
        {
            t.Id, t.TestPlanId, t.OrderIndex, t.Name, t.Description,
            Type = t.Type.ToString(),
            Status = t.Status.ToString(),
            t.TestFile, t.Framework,
            t.LastRunAt, t.LastRunOutput,
            t.TotalRuns, t.PassedRuns, t.FailedRuns,
            t.CreatedAt,
            Steps = t.Steps.Select(MapStepToDto)
        }),
        Executions = tp.Executions.Select(e => new
        {
            e.Id,
            ExecutionMode = e.ExecutionMode.ToString(),
            Status = e.Status.ToString(),
            e.TotalSteps, e.PassedSteps, e.FailedSteps, e.SkippedSteps,
            e.ExecutedBy, e.StartedAt, e.CompletedAt, e.FailureReason,
            StepResults = e.StepResults.Select(MapStepResultToDto)
        })
    };

    private static object MapTestToDto(Test t) => new
    {
        t.Id, t.TestPlanId, t.OrderIndex, t.Name, t.Description,
        Type = t.Type.ToString(),
        Status = t.Status.ToString(),
        t.TestFile, t.Framework,
        t.LastRunAt, t.LastRunOutput,
        t.TotalRuns, t.PassedRuns, t.FailedRuns,
        t.CreatedAt,
        StepCount = t.Steps?.Count ?? 0,
        Steps = t.Steps?.Select(MapStepToDto)
    };

    private static object MapStepToDto(TestStep s) => new
    {
        s.Id, s.TestId, s.OrderIndex,
        StepType = s.StepType.ToString(),
        s.Description, s.ExpectedResult, s.AutomationCommand,
        s.RequiresManualVerification, s.CreatedAt
    };

    private static object MapExecutionToDto(TestExecution e) => new
    {
        e.Id, e.TestPlanId,
        ExecutionMode = e.ExecutionMode.ToString(),
        Status = e.Status.ToString(),
        e.TotalSteps, e.PassedSteps, e.FailedSteps, e.SkippedSteps,
        e.ExecutedBy, e.StartedAt, e.CompletedAt, e.FailureReason
    };

    private static object MapExecutionDetailDto(TestExecution e) => new
    {
        e.Id, e.TestPlanId,
        ExecutionMode = e.ExecutionMode.ToString(),
        Status = e.Status.ToString(),
        e.TotalSteps, e.PassedSteps, e.FailedSteps, e.SkippedSteps,
        e.ExecutedBy, e.StartedAt, e.CompletedAt, e.FailureReason,
        StepResults = e.StepResults.Select(sr => new
        {
            sr.Id, sr.TestStepId,
            Status = sr.Status.ToString(),
            sr.ActualResult, sr.ErrorMessage, sr.Screenshot, sr.DurationMs, sr.ExecutedAt,
            StepDescription = sr.Step?.Description
        })
    };

    private static object MapStepResultToDto(TestStepResult sr) => new
    {
        sr.Id, sr.TestStepId,
        Status = sr.Status.ToString(),
        sr.ActualResult, sr.ErrorMessage, sr.Screenshot, sr.DurationMs, sr.ExecutedAt
    };
}

public record CreateTestPlanRequest(
    string Name,
    TestLevel RequiredLevel,
    string? Description = null,
    TestPlanSource? Source = null,
    List<CreateTestInPlanRequest>? Tests = null);

public record CreateTestInPlanRequest(
    string Name,
    TestType Type,
    string? Description = null,
    string? TestFile = null,
    string? Framework = null,
    List<CreateTestStepRequest>? Steps = null);

public record UpdateTestPlanRequest(
    string? Name = null,
    string? Description = null,
    TestLevel? RequiredLevel = null,
    TestPlanStatus? Status = null);

public record CreateTestRequest(
    string Name,
    TestType Type,
    string? Description = null,
    string? TestFile = null,
    string? Framework = null);

public record UpdateTestRequest(
    string? Name = null,
    string? Description = null,
    string? TestFile = null,
    string? Framework = null,
    TestStatus? Status = null);

public record RunTestRequest(bool Passed, string? Output = null);

public record CreateTestStepRequest(
    TestStepType StepType,
    string Description,
    string? ExpectedResult = null,
    string? AutomationCommand = null,
    bool RequiresManualVerification = false);

public record StartExecutionRequest(
    TestExecutionMode ExecutionMode,
    string? ExecutedBy = null);

public record RecordStepResultRequest(
    int TestStepId,
    TestStepStatus Status,
    string? ActualResult = null,
    string? ErrorMessage = null,
    string? Screenshot = null,
    int DurationMs = 0);

public record CompleteExecutionRequest(
    TestExecutionStatus Status,
    string? FailureReason = null);
