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
        var execGroup = app.MapGroup("/api/test-executions");

        // List test plans for a task
        taskGroup.MapGet("/", async (int taskId, LifecycleDbContext db) =>
        {
            var plans = await db.TestPlans
                .Where(tp => tp.TaskId == taskId)
                .Include(tp => tp.Steps.OrderBy(s => s.OrderIndex))
                .Include(tp => tp.Executions.OrderByDescending(e => e.StartedAt).Take(1))
                .OrderBy(tp => tp.RequiredLevel)
                .ToListAsync();

            return Results.Ok(plans.Select(MapPlanToDto));
        });

        // Create test plan for a task
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

            if (req.Steps is { Count: > 0 })
            {
                for (int i = 0; i < req.Steps.Count; i++)
                {
                    var s = req.Steps[i];
                    plan.Steps.Add(new TestStep
                    {
                        OrderIndex = i,
                        StepType = s.StepType,
                        Description = s.Description,
                        ExpectedResult = s.ExpectedResult,
                        AutomationCommand = s.AutomationCommand,
                        RequiresManualVerification = s.RequiresManualVerification,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            db.TestPlans.Add(plan);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("test:updated", new { PlanId = plan.Id, plan.TaskId });

            return Results.Created($"/api/test-plans/{plan.Id}", MapPlanToDto(plan));
        });

        // Get test plan with steps
        planGroup.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var plan = await db.TestPlans
                .Include(tp => tp.Steps.OrderBy(s => s.OrderIndex))
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

        // Add step to plan
        planGroup.MapPost("/{id:int}/steps", async (int id, CreateTestStepRequest req, LifecycleDbContext db) =>
        {
            var plan = await db.TestPlans.Include(tp => tp.Steps).FirstOrDefaultAsync(tp => tp.Id == id);
            if (plan is null) return Results.NotFound();

            var maxOrder = plan.Steps.Any() ? plan.Steps.Max(s => s.OrderIndex) : -1;
            var step = new TestStep
            {
                TestPlanId = id,
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

            return Results.Created($"/api/test-plans/{id}/steps/{step.Id}", MapStepToDto(step));
        });

        // Reorder steps
        planGroup.MapPatch("/{id:int}/steps/reorder", async (int id, ReorderStepsRequest req, LifecycleDbContext db) =>
        {
            var steps = await db.TestSteps.Where(s => s.TestPlanId == id).ToListAsync();
            foreach (var item in req.Items)
            {
                var step = steps.FirstOrDefault(s => s.Id == item.Id);
                if (step is not null) step.OrderIndex = item.Order;
            }
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Start execution of a test plan
        planGroup.MapPost("/{id:int}/execute", async (int id, StartExecutionRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var plan = await db.TestPlans
                .Include(tp => tp.Steps)
                .FirstOrDefaultAsync(tp => tp.Id == id);
            if (plan is null) return Results.NotFound();

            var execution = new TestExecution
            {
                TestPlanId = id,
                ExecutionMode = req.ExecutionMode,
                Status = TestExecutionStatus.Running,
                StartedAt = DateTime.UtcNow,
                TotalSteps = plan.Steps.Count,
                ExecutedBy = req.ExecutedBy
            };

            db.TestExecutions.Add(execution);
            plan.Status = TestPlanStatus.InProgress;
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("test:updated", new { PlanId = plan.Id, plan.TaskId, ExecutionId = execution.Id });

            return Results.Created($"/api/test-executions/{execution.Id}", MapExecutionToDto(execution));
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

            exec.Status = req.Status;
            exec.CompletedAt = DateTime.UtcNow;
            exec.FailureReason = req.FailureReason;

            // Update plan status based on execution result
            exec.TestPlan.Status = req.Status == TestExecutionStatus.Passed
                ? TestPlanStatus.Passing
                : TestPlanStatus.Failing;
            exec.TestPlan.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            await sse.BroadcastAsync("test:updated", new { ExecutionId = id, PlanId = exec.TestPlanId, exec.TestPlan.TaskId, Status = req.Status.ToString() });

            return Results.Ok(MapExecutionToDto(exec));
        });

        // Check if task can complete (testing requirements met)
        app.MapGet("/api/tasks/{taskId:int}/can-complete", async (int taskId, LifecycleDbContext db) =>
        {
            var task = await db.Tasks
                .Include(t => t.TestPlans)
                    .ThenInclude(tp => tp.Executions)
                .FirstOrDefaultAsync(t => t.Id == taskId);

            if (task is null) return Results.NotFound();

            if (!task.RequiredTestLevel.HasValue)
                return Results.Ok(new { CanComplete = true, Reason = (string?)null });

            var qualifyingPlan = task.TestPlans
                .FirstOrDefault(tp => tp.RequiredLevel >= task.RequiredTestLevel.Value
                    && tp.Executions.Any(e => e.Status == TestExecutionStatus.Passed));

            if (qualifyingPlan is not null)
                return Results.Ok(new { CanComplete = true, Reason = (string?)null });

            var hasAnyPlan = task.TestPlans.Any(tp => tp.RequiredLevel >= task.RequiredTestLevel.Value);
            var reason = hasAnyPlan
                ? "Test plan exists but no passing execution yet"
                : $"No test plan at level {task.RequiredTestLevel.Value} or above";

            return Results.Ok(new { CanComplete = false, Reason = reason });
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
            StepCount = tp.Steps?.Count ?? 0,
            tp.CreatedAt, tp.UpdatedAt,
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
        Steps = tp.Steps.Select(MapStepToDto),
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

    private static object MapStepToDto(TestStep s) => new
    {
        s.Id, s.TestPlanId, s.OrderIndex,
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
    List<CreateTestStepRequest>? Steps = null);

public record UpdateTestPlanRequest(
    string? Name = null,
    string? Description = null,
    TestLevel? RequiredLevel = null,
    TestPlanStatus? Status = null);

public record CreateTestStepRequest(
    TestStepType StepType,
    string Description,
    string? ExpectedResult = null,
    string? AutomationCommand = null,
    bool RequiresManualVerification = false);

public record ReorderStepsRequest(List<ReorderStepItem> Items);
public record ReorderStepItem(int Id, int Order);

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
