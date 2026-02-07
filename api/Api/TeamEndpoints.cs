using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class TeamEndpoints
{
    public static WebApplication MapTeamEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/teams");
        var escalationGroup = app.MapGroup("/api/agents/escalations");

        // List team members for a project
        group.MapGet("/", async (int? projectId, LifecycleDbContext db) =>
        {
            var query = db.TeamMembers
                .Include(tm => tm.Sessions.OrderByDescending(s => s.SpawnedAt).Take(1))
                .Include(tm => tm.Assignments.Where(a => a.Status != "Completed" && a.Status != "Abandoned"))
                .AsQueryable();

            if (projectId.HasValue)
                query = query.Where(tm => tm.ProjectId == projectId.Value);

            var members = await query.OrderBy(tm => tm.Role).ThenBy(tm => tm.AgentName).ToListAsync();
            return Results.Ok(members.Select(MapMemberToDto));
        });

        // Create team member
        group.MapPost("/", async (CreateTeamMemberRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var project = await db.Projects.FindAsync(req.ProjectId);
            if (project is null) return Results.NotFound("Project not found");

            var member = new TeamMember
            {
                ProjectId = req.ProjectId,
                Role = req.Role,
                AgentName = req.AgentName,
                ModelName = req.ModelName,
                IsPersistent = req.IsPersistent,
                Status = "Idle",
                ConfigJson = req.ConfigJson,
                SpawnPromptTemplate = req.SpawnPromptTemplate,
                CreatedAt = DateTime.UtcNow
            };

            db.TeamMembers.Add(member);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("team:updated", new { member.Id, member.AgentName, member.Role });

            return Results.Created($"/api/teams/{member.Id}", MapMemberToDto(member));
        });

        // Get team member details
        group.MapGet("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var member = await db.TeamMembers
                .Include(tm => tm.Sessions.OrderByDescending(s => s.SpawnedAt))
                .Include(tm => tm.Assignments)
                    .ThenInclude(a => a.Task)
                .FirstOrDefaultAsync(tm => tm.Id == id);

            if (member is null) return Results.NotFound();
            return Results.Ok(MapMemberDetailDto(member));
        });

        // Update team member
        group.MapPatch("/{id:int}", async (int id, UpdateTeamMemberRequest req, LifecycleDbContext db) =>
        {
            var member = await db.TeamMembers.FindAsync(id);
            if (member is null) return Results.NotFound();

            if (req.Status is not null) member.Status = req.Status;
            if (req.ModelName is not null) member.ModelName = req.ModelName;
            if (req.ConfigJson is not null) member.ConfigJson = req.ConfigJson;
            if (req.SpawnPromptTemplate is not null) member.SpawnPromptTemplate = req.SpawnPromptTemplate;

            await db.SaveChangesAsync();
            return Results.Ok(MapMemberToDto(member));
        });

        // Delete team member
        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var member = await db.TeamMembers.FindAsync(id);
            if (member is null) return Results.NotFound();
            db.TeamMembers.Remove(member);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Spawn agent session
        group.MapPost("/{id:int}/spawn", async (int id, SpawnSessionRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var member = await db.TeamMembers.FindAsync(id);
            if (member is null) return Results.NotFound();

            var session = new AgentSession
            {
                TeamMemberId = id,
                SessionId = req.SessionId ?? Guid.NewGuid().ToString(),
                Status = "Active",
                SpawnedAt = DateTime.UtcNow
            };

            db.AgentSessions.Add(session);
            member.Status = "Active";
            member.LastActiveAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("agent:spawned", new { session.Id, member.AgentName, member.Role });

            return Results.Created($"/api/agents/sessions/{session.Id}", new
            {
                session.Id, session.SessionId, session.Status, session.SpawnedAt,
                TeamMemberId = member.Id, member.AgentName, member.Role
            });
        });

        // Shutdown agent
        group.MapPost("/{id:int}/shutdown", async (int id, ShutdownRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var member = await db.TeamMembers
                .Include(tm => tm.Sessions.Where(s => s.Status == "Active"))
                .FirstOrDefaultAsync(tm => tm.Id == id);

            if (member is null) return Results.NotFound();

            foreach (var session in member.Sessions)
            {
                session.Status = "Completed";
                session.CompletedAt = DateTime.UtcNow;
                session.TokensUsed = req.TokensUsed;
            }

            member.Status = "Idle";
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("agent:shutdown", new { member.Id, member.AgentName });

            return Results.Ok(new { Success = true });
        });

        // Assign task to team member
        app.MapPost("/api/tasks/{taskId:int}/assign", async (int taskId, AssignTaskRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var task = await db.Tasks.FindAsync(taskId);
            if (task is null) return Results.NotFound("Task not found");

            if (req.TeamMemberId.HasValue)
            {
                var member = await db.TeamMembers.FindAsync(req.TeamMemberId.Value);
                if (member is null) return Results.NotFound("Team member not found");
            }

            var assignment = new TaskAssignment
            {
                TaskId = taskId,
                TeamMemberId = req.TeamMemberId,
                AssignedBy = req.AssignedBy ?? "Manual",
                Status = "Assigned",
                AssignedAt = DateTime.UtcNow
            };

            db.TaskAssignments.Add(assignment);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("task:assigned", new { assignment.Id, assignment.TaskId, assignment.TeamMemberId });

            return Results.Created($"/api/tasks/{taskId}/assignments/{assignment.Id}", new
            {
                assignment.Id, assignment.TaskId, assignment.TeamMemberId,
                assignment.AssignedBy, assignment.Status, assignment.AssignedAt
            });
        });

        // Claim task (agent claims from pool)
        app.MapPost("/api/tasks/{taskId:int}/claim", async (int taskId, ClaimTaskRequest req, LifecycleDbContext db) =>
        {
            var task = await db.Tasks.FindAsync(taskId);
            if (task is null) return Results.NotFound("Task not found");

            var member = await db.TeamMembers.FirstOrDefaultAsync(tm => tm.AgentName == req.AgentName);
            if (member is null) return Results.NotFound("Agent not found");

            // Check if already assigned
            var existing = await db.TaskAssignments
                .FirstOrDefaultAsync(ta => ta.TaskId == taskId && ta.Status != "Completed" && ta.Status != "Abandoned");
            if (existing is not null) return Results.Conflict("Task already assigned");

            var assignment = new TaskAssignment
            {
                TaskId = taskId,
                TeamMemberId = member.Id,
                AssignedBy = "Self-Claimed",
                Status = "Claimed",
                AssignedAt = DateTime.UtcNow
            };

            db.TaskAssignments.Add(assignment);
            await db.SaveChangesAsync();

            return Results.Ok(new { assignment.Id, assignment.TaskId, assignment.TeamMemberId, assignment.Status });
        });

        // Create escalation
        escalationGroup.MapPost("/", async (CreateEscalationRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var escalation = new AgentEscalation
            {
                ProjectId = req.ProjectId,
                TaskId = req.TaskId,
                Description = req.Description,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow
            };

            db.AgentEscalations.Add(escalation);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("agent:escalation", new { escalation.Id, escalation.Description });

            return Results.Created($"/api/agents/escalations/{escalation.Id}", new
            {
                escalation.Id, escalation.ProjectId, escalation.TaskId,
                escalation.Description, escalation.Status, escalation.CreatedAt
            });
        });

        // List escalations
        escalationGroup.MapGet("/", async (int? projectId, string? status, LifecycleDbContext db) =>
        {
            var query = db.AgentEscalations
                .Include(e => e.Task)
                .AsQueryable();

            if (projectId.HasValue) query = query.Where(e => e.ProjectId == projectId.Value);
            if (!string.IsNullOrEmpty(status)) query = query.Where(e => e.Status == status);

            var escalations = await query.OrderByDescending(e => e.CreatedAt).ToListAsync();
            return Results.Ok(escalations.Select(e => new
            {
                e.Id, e.ProjectId, e.TaskId,
                TaskTitle = e.Task?.Title,
                e.Description, e.Status, e.Resolution,
                e.CreatedAt, e.ResolvedAt
            }));
        });

        // Resolve escalation
        escalationGroup.MapPatch("/{id:int}", async (int id, ResolveEscalationRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var escalation = await db.AgentEscalations.FindAsync(id);
            if (escalation is null) return Results.NotFound();

            escalation.Status = req.Status ?? "Resolved";
            escalation.Resolution = req.Resolution;
            escalation.ResolvedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            await sse.BroadcastAsync("agent:escalation_resolved", new { escalation.Id });

            return Results.Ok(new
            {
                escalation.Id, escalation.Status, escalation.Resolution, escalation.ResolvedAt
            });
        });

        // Agent context (full project context for spawning)
        app.MapGet("/api/agents/context", async (int projectId, string? role, LifecycleDbContext db) =>
        {
            var project = await db.Projects.FindAsync(projectId);
            if (project is null) return Results.NotFound();

            var team = await db.TeamMembers
                .Where(tm => tm.ProjectId == projectId)
                .Select(tm => new { tm.Id, tm.AgentName, tm.Role, tm.Status })
                .ToListAsync();

            var activeTasks = await db.Tasks
                .Where(t => t.Status != Data.Enums.TaskStatus.Done && t.Status != Data.Enums.TaskStatus.Cancelled)
                .Select(t => new { t.Id, t.Title, Status = t.Status.ToString(), Priority = t.Priority.ToString(), t.PhaseId })
                .ToListAsync();

            var pendingEscalations = await db.AgentEscalations
                .Where(e => e.ProjectId == projectId && e.Status == "Pending")
                .Select(e => new { e.Id, e.Description, e.TaskId })
                .ToListAsync();

            return Results.Ok(new
            {
                Project = new { project.Id, project.Name, project.Description },
                Team = team,
                ActiveTasks = activeTasks,
                PendingEscalations = pendingEscalations
            });
        });

        return app;
    }

    private static object MapMemberToDto(TeamMember tm)
    {
        var latestSession = tm.Sessions?.OrderByDescending(s => s.SpawnedAt).FirstOrDefault();
        var activeAssignment = tm.Assignments?.FirstOrDefault(a => a.Status != "Completed" && a.Status != "Abandoned");
        return new
        {
            tm.Id, tm.ProjectId, tm.Role, tm.AgentName, tm.ModelName,
            tm.IsPersistent, tm.Status, tm.ConfigJson,
            tm.CreatedAt, tm.LastActiveAt,
            CurrentSession = latestSession is null ? null : new
            {
                latestSession.Id, latestSession.SessionId, latestSession.Status, latestSession.SpawnedAt
            },
            CurrentTask = activeAssignment is null ? null : new
            {
                activeAssignment.TaskId, activeAssignment.Status
            }
        };
    }

    private static object MapMemberDetailDto(TeamMember tm) => new
    {
        tm.Id, tm.ProjectId, tm.Role, tm.AgentName, tm.ModelName,
        tm.IsPersistent, tm.Status, tm.ConfigJson, tm.SpawnPromptTemplate,
        tm.CreatedAt, tm.LastActiveAt,
        Sessions = tm.Sessions.Select(s => new
        {
            s.Id, s.SessionId, s.Status, s.SpawnedAt, s.CompletedAt, s.TokensUsed
        }),
        Assignments = tm.Assignments.Select(a => new
        {
            a.Id, a.TaskId, TaskTitle = a.Task?.Title,
            a.AssignedBy, a.Status, a.AssignedAt, a.CompletedAt
        })
    };
}

public record CreateTeamMemberRequest(
    int ProjectId,
    string Role,
    string AgentName,
    string ModelName,
    bool IsPersistent = false,
    string? ConfigJson = null,
    string? SpawnPromptTemplate = null);

public record UpdateTeamMemberRequest(
    string? Status = null,
    string? ModelName = null,
    string? ConfigJson = null,
    string? SpawnPromptTemplate = null);

public record SpawnSessionRequest(string? SessionId = null);
public record ShutdownRequest(int? TokensUsed = null);

public record AssignTaskRequest(int? TeamMemberId = null, string? AssignedBy = null);
public record ClaimTaskRequest(string AgentName);

public record CreateEscalationRequest(
    int ProjectId,
    string Description,
    int? TaskId = null);

public record ResolveEscalationRequest(
    string? Status = null,
    string? Resolution = null);
