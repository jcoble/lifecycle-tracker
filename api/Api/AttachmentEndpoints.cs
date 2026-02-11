using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class AttachmentEndpoints
{
    public static WebApplication MapAttachmentEndpoints(this WebApplication app)
    {
        var taskGroup = app.MapGroup("/api/tasks/{taskId:int}/attachments");
        var directGroup = app.MapGroup("/api/attachments");

        taskGroup.MapPost("/", async (int taskId, HttpRequest request, LifecycleDbContext db, SseService sse, IWebHostEnvironment env) =>
        {
            var task = await db.Tasks.FindAsync(taskId);
            if (task is null) return Results.NotFound();

            if (!request.HasFormContentType) return Results.BadRequest("Expected multipart/form-data");

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null) return Results.BadRequest("No file provided");

            var uploadsDir = Path.Combine(env.ContentRootPath, "uploads");
            Directory.CreateDirectory(uploadsDir);

            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            var filePath = Path.Combine(uploadsDir, fileName);

            await using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var attachment = new Attachment
            {
                TaskId = taskId,
                FileName = fileName,
                OriginalFileName = file.FileName,
                ContentType = file.ContentType,
                FileSize = file.Length,
                StoragePath = filePath,
                UploadedBy = form["uploadedBy"].FirstOrDefault(),
                UploadedAt = DateTime.UtcNow
            };
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("attachment:added", new { attachment.Id, attachment.TaskId, attachment.OriginalFileName });

            return Results.Created($"/api/attachments/{attachment.Id}", new
            {
                attachment.Id, attachment.TaskId, attachment.FileName, attachment.OriginalFileName,
                attachment.ContentType, attachment.FileSize, attachment.Width, attachment.Height,
                attachment.UploadedBy, attachment.UploadedAt
            });
        }).DisableAntiforgery();

        // List attachments for a task
        taskGroup.MapGet("/", async (int taskId, LifecycleDbContext db) =>
        {
            var attachments = await db.Attachments
                .Where(a => a.TaskId == taskId)
                .OrderByDescending(a => a.UploadedAt)
                .Select(a => new
                {
                    a.Id, a.TaskId, a.FileName, a.OriginalFileName,
                    a.ContentType, a.FileSize, a.Width, a.Height,
                    a.UploadedBy, a.UploadedAt
                })
                .ToListAsync();
            return Results.Ok(attachments);
        });

        // Get attachment file
        directGroup.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var attachment = await db.Attachments.FindAsync(id);
            if (attachment is null) return Results.NotFound();

            if (!File.Exists(attachment.StoragePath))
                return Results.NotFound();

            return Results.File(attachment.StoragePath, attachment.ContentType, attachment.OriginalFileName);
        });

        // Get attachment as base64 (for MCP/AI consumption)
        directGroup.MapGet("/{id:int}/base64", async (int id, LifecycleDbContext db) =>
        {
            var attachment = await db.Attachments.FindAsync(id);
            if (attachment is null) return Results.NotFound();

            if (!File.Exists(attachment.StoragePath))
                return Results.NotFound();

            var bytes = await File.ReadAllBytesAsync(attachment.StoragePath);
            return Results.Ok(new
            {
                attachment.Id, attachment.TaskId, attachment.OriginalFileName,
                attachment.ContentType, attachment.Width, attachment.Height,
                Base64Data = Convert.ToBase64String(bytes)
            });
        });

        directGroup.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var attachment = await db.Attachments.FindAsync(id);
            if (attachment is null) return Results.NotFound();

            if (File.Exists(attachment.StoragePath))
                File.Delete(attachment.StoragePath);

            db.Attachments.Remove(attachment);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}
