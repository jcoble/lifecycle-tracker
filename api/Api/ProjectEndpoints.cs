using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Lifecycle.Data.Enums.TaskStatus;

namespace Lifecycle.Api;

public static class ProjectEndpoints
{
    public static WebApplication MapProjectEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapGet("/", async (LifecycleDbContext db) =>
        {
            var projects = await db.Projects
                .Include(p => p.Milestones)
                .OrderByDescending(p => p.UpdatedAt)
                .ToListAsync();

            return Results.Ok(projects.Select(p => new
            {
                p.Id, p.Name, p.Description, p.Repository,
                Status = p.Status.ToString(),
                p.Settings,
                MilestoneCount = p.Milestones.Count,
                p.CreatedAt, p.UpdatedAt
            }));
        });

        group.MapPost("/", async (CreateProjectRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var project = new Project
            {
                Name = req.Name,
                Description = req.Description,
                Repository = req.Repository,
                Status = ProjectStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            // Seed default labels
            var defaultLabels = new[]
            {
                new Label { ProjectId = project.Id, Name = "bug", Color = "#ef4444" },
                new Label { ProjectId = project.Id, Name = "feature", Color = "#3b82f6" },
                new Label { ProjectId = project.Id, Name = "refactor", Color = "#8b5cf6" },
                new Label { ProjectId = project.Id, Name = "docs", Color = "#22c55e" },
                new Label { ProjectId = project.Id, Name = "infra", Color = "#f59e0b" },
                new Label { ProjectId = project.Id, Name = "research", Color = "#06b6d4" },
                new Label { ProjectId = project.Id, Name = "lifecycle", Color = "#ec4899" }
            };
            db.Labels.AddRange(defaultLabels);
            await db.SaveChangesAsync();

            await ActivityHelper.LogActivity(db, project.Id, ActivityType.ProjectCreated,
                TaskSource.Manual, "Project", project.Id, "Created", $"Project '{project.Name}' created");

            await sse.BroadcastAsync("project:created", new { project.Id, project.Name });

            return Results.Created($"/api/projects/{project.Id}", new
            {
                project.Id, project.Name, project.Description, project.Repository,
                Status = project.Status.ToString(),
                project.Settings,
                MilestoneCount = 0,
                project.CreatedAt, project.UpdatedAt
            });
        });

        group.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var project = await db.Projects
                .Include(p => p.Milestones)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (project is null) return Results.NotFound();

            return Results.Ok(new
            {
                project.Id, project.Name, project.Description, project.Repository,
                Status = project.Status.ToString(),
                project.Settings,
                MilestoneCount = project.Milestones.Count,
                project.CreatedAt, project.UpdatedAt
            });
        });

        group.MapPatch("/{id:int}", async (int id, UpdateProjectRequest req, LifecycleDbContext db) =>
        {
            var project = await db.Projects.FindAsync(id);
            if (project is null) return Results.NotFound();

            if (req.Name is not null) project.Name = req.Name;
            if (req.Description is not null) project.Description = req.Description;
            if (req.Repository is not null) project.Repository = req.Repository;
            if (req.Status.HasValue) project.Status = req.Status.Value;
            if (req.Settings is not null) project.Settings = req.Settings;

            project.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                project.Id, project.Name, project.Description, project.Repository,
                Status = project.Status.ToString(),
                project.Settings,
                project.CreatedAt, project.UpdatedAt
            });
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var project = await db.Projects.FindAsync(id);
            if (project is null) return Results.NotFound();
            db.Projects.Remove(project);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        group.MapGet("/{id:int}/dashboard", async (int id, LifecycleDbContext db) =>
        {
            var project = await db.Projects
                .Include(p => p.Milestones).ThenInclude(m => m.Phases).ThenInclude(ph => ph.Tasks)
                .Include(p => p.Labels)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (project is null) return Results.NotFound();

            var activeMilestone = project.Milestones
                .Where(m => m.Status == MilestoneStatus.InProgress)
                .OrderBy(m => m.OrderIndex)
                .FirstOrDefault();

            var phaseTasks = project.Milestones
                .SelectMany(m => m.Phases)
                .SelectMany(p => p.Tasks)
                .ToList();

            var orphanTasks = await db.Tasks
                .Where(t => t.PhaseId == null)
                .ToListAsync();

            var allTasks = phaseTasks.Concat(orphanTasks).ToList();

            var recentActivity = await db.ActivityLogs
                .Where(a => a.ProjectId == id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(20)
                .ToListAsync();

            var taskIds = allTasks.Select(at => at.Id).ToList();
            var allTests = await db.Tests
                .Where(t => db.TestPlans.Where(tp => taskIds.Contains(tp.TaskId)).Select(tp => tp.Id).Contains(t.TestPlanId))
                .ToListAsync();

            return Results.Ok(new
            {
                Project = new
                {
                    project.Id, project.Name, project.Description, project.Repository,
                    Status = project.Status.ToString(),
                    project.CreatedAt, project.UpdatedAt
                },
                ActiveMilestone = activeMilestone is null ? null : new
                {
                    activeMilestone.Id, activeMilestone.Name, activeMilestone.Version,
                    Status = activeMilestone.Status.ToString(),
                    PhaseCount = activeMilestone.Phases.Count,
                    activeMilestone.TargetDate
                },
                Milestones = project.Milestones.OrderBy(m => m.OrderIndex).Select(m => new
                {
                    m.Id, m.Name, m.Version,
                    Status = m.Status.ToString(),
                    PhaseCount = m.Phases.Count,
                    m.OrderIndex
                }),
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
                RecentActivity = recentActivity.Select(a => new
                {
                    a.Id,
                    Type = a.Type.ToString(),
                    a.EntityType, a.EntityId, a.Action, a.Description, a.Actor, a.CreatedAt
                }),
                Labels = project.Labels.Select(l => new { l.Id, l.Name, l.Color })
            });
        });

        return app;
    }
}

public record CreateProjectRequest(string Name, string? Description = null, string? Repository = null);
public record UpdateProjectRequest(string? Name = null, string? Description = null, string? Repository = null, ProjectStatus? Status = null, string? Settings = null);
