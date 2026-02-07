using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class TestEndpoints
{
    public static WebApplication MapTestEndpoints(this WebApplication app)
    {
        var taskGroup = app.MapGroup("/api/tasks/{taskId:int}/tests");
        var directGroup = app.MapGroup("/api/tests");

        taskGroup.MapGet("/", async (int taskId, LifecycleDbContext db) =>
        {
            var tests = await db.TestRecords
                .Where(t => t.TaskId == taskId)
                .OrderBy(t => t.TestName)
                .ToListAsync();

            return Results.Ok(tests.Select(MapToDto));
        });

        taskGroup.MapPost("/", async (int taskId, CreateTestRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks.FindAsync(taskId);
            if (task is null) return Results.NotFound();

            var test = new TestRecord
            {
                TaskId = taskId,
                TestType = req.TestType,
                Status = req.Status ?? TestStatus.Created,
                TestName = req.TestName,
                TestFile = req.TestFile,
                Framework = req.Framework,
                CreatedAt = DateTime.UtcNow
            };
            db.TestRecords.Add(test);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("test:updated", new { test.Id, test.TaskId, Status = test.Status.ToString() });

            return Results.Created($"/api/tests/{test.Id}", MapToDto(test));
        });

        directGroup.MapPatch("/{id:int}", async (int id, UpdateTestRequest req, LifecycleDbContext db) =>
        {
            var test = await db.TestRecords.FindAsync(id);
            if (test is null) return Results.NotFound();

            if (req.TestName is not null) test.TestName = req.TestName;
            if (req.TestFile is not null) test.TestFile = req.TestFile;
            if (req.Framework is not null) test.Framework = req.Framework;
            if (req.Status.HasValue) test.Status = req.Status.Value;

            await db.SaveChangesAsync();
            return Results.Ok(MapToDto(test));
        });

        directGroup.MapPost("/{id:int}/run", async (int id, RunTestRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var test = await db.TestRecords.FindAsync(id);
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

            return Results.Ok(MapToDto(test));
        });

        return app;
    }

    private static object MapToDto(TestRecord t) => new
    {
        t.Id, t.TaskId,
        TestType = t.TestType.ToString(),
        Status = t.Status.ToString(),
        t.TestName, t.TestFile, t.Framework,
        t.LastRunAt, t.LastRunResult, t.LastRunOutput,
        t.TotalRuns, t.PassedRuns, t.FailedRuns, t.CreatedAt
    };
}

public record CreateTestRequest(TestType TestType, string? TestName = null, string? TestFile = null, string? Framework = null, TestStatus? Status = null);
public record UpdateTestRequest(string? TestName = null, string? TestFile = null, string? Framework = null, TestStatus? Status = null);
public record RunTestRequest(bool Passed, string? Result = null, string? Output = null);
