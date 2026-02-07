using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class CommentEndpoints
{
    public static WebApplication MapCommentEndpoints(this WebApplication app)
    {
        var taskGroup = app.MapGroup("/api/tasks/{taskId:int}/comments");
        var directGroup = app.MapGroup("/api/comments");

        taskGroup.MapGet("/", async (int taskId, LifecycleDbContext db) =>
        {
            var comments = await db.Comments
                .Where(c => c.TaskId == taskId)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            return Results.Ok(comments.Select(MapToDto));
        });

        taskGroup.MapPost("/", async (int taskId, CreateCommentRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks.FindAsync(taskId);
            if (task is null) return Results.NotFound();

            var comment = new Comment
            {
                TaskId = taskId,
                Content = req.Content,
                Source = req.Source ?? CommentSource.Manual,
                Author = req.Author,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Comments.Add(comment);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("comment:added", new { comment.Id, comment.TaskId });

            return Results.Created($"/api/comments/{comment.Id}", MapToDto(comment));
        });

        directGroup.MapPatch("/{id:int}", async (int id, UpdateCommentRequest req, LifecycleDbContext db) =>
        {
            var comment = await db.Comments.FindAsync(id);
            if (comment is null) return Results.NotFound();

            if (req.Content is not null) comment.Content = req.Content;

            comment.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(MapToDto(comment));
        });

        directGroup.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var comment = await db.Comments.FindAsync(id);
            if (comment is null) return Results.NotFound();
            db.Comments.Remove(comment);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }

    private static object MapToDto(Comment c) => new
    {
        c.Id, c.TaskId, c.Content,
        Source = c.Source.ToString(),
        c.Author, c.CreatedAt, c.UpdatedAt
    };
}

public record CreateCommentRequest(string Content, CommentSource? Source = null, string? Author = null);
public record UpdateCommentRequest(string? Content = null);
