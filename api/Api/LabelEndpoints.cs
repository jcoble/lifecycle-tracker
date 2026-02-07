using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class LabelEndpoints
{
    public static WebApplication MapLabelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId:int}/labels");

        group.MapGet("/", async (int projectId, LifecycleDbContext db) =>
        {
            var labels = await db.Labels
                .Where(l => l.ProjectId == projectId)
                .OrderBy(l => l.Name)
                .ToListAsync();

            return Results.Ok(labels.Select(l => new { l.Id, l.Name, l.Color, l.Description }));
        });

        group.MapPost("/", async (int projectId, CreateLabelRequest req, LifecycleDbContext db) =>
        {
            var label = new Label
            {
                ProjectId = projectId,
                Name = req.Name,
                Color = req.Color,
                Description = req.Description
            };
            db.Labels.Add(label);
            await db.SaveChangesAsync();
            return Results.Created($"/api/projects/{projectId}/labels/{label.Id}",
                new { label.Id, label.Name, label.Color, label.Description });
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var label = await db.Labels.FindAsync(id);
            if (label is null) return Results.NotFound();
            db.Labels.Remove(label);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateLabelRequest(string Name, string Color, string? Description = null);
