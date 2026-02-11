using System.Text.Json;
using System.Text.RegularExpressions;
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
                TriggerStatuses = req.TriggerStatuses,
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
            if (req.TriggerStatuses is not null) member.TriggerStatuses = req.TriggerStatuses;

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
                session.CurrentActivity = null;
            }

            // Ephemeral (auto-registered) members get deleted on shutdown
            if (!member.IsPersistent)
            {
                db.TeamMembers.Remove(member);
            }
            else
            {
                member.Status = "Idle";
            }
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("agent:shutdown", new { member.Id, member.AgentName });

            return Results.Ok(new { Success = true });
        });

        // Agent heartbeat / activity report
        group.MapPost("/{id:int}/heartbeat", async (int id, HeartbeatRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var member = await db.TeamMembers
                .Include(tm => tm.Sessions.Where(s => s.Status == "Active"))
                .FirstOrDefaultAsync(tm => tm.Id == id);

            if (member is null) return Results.NotFound();

            var activeSession = member.Sessions.FirstOrDefault();
            if (activeSession is null) return Results.BadRequest("No active session");

            var activityChanged = req.Activity is not null && req.Activity != activeSession.CurrentActivity;

            activeSession.LastHeartbeatAt = DateTime.UtcNow;
            if (req.Activity is not null)
                activeSession.CurrentActivity = req.Activity;
            if (req.TokensUsed.HasValue)
                activeSession.TokensUsed = req.TokensUsed;

            if (req.PlanContent is not null)
            {
                activeSession.LatestPlanContent = req.PlanContent;
                activeSession.LatestPlanFileName = req.PlanFileName;
                activeSession.LatestPlanUpdatedAt = DateTime.UtcNow;
            }

            member.LastActiveAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            if (activityChanged)
            {
                await sse.BroadcastAsync("agent:activity", new
                {
                    TeamMemberId = member.Id,
                    member.AgentName,
                    member.Role,
                    Activity = req.Activity,
                    Timestamp = DateTime.UtcNow
                });
            }

            if (req.PlanContent is not null)
            {
                await sse.BroadcastAsync("agent:plan_updated", new
                {
                    TeamMemberId = member.Id,
                    member.AgentName,
                    PlanFileName = req.PlanFileName
                });
            }

            return Results.Ok(new { Success = true, activeSession.SessionId });
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

            // Look up agent name for enhanced SSE event
            string? agentName = null;
            if (req.TeamMemberId.HasValue)
            {
                var assignedMember = await db.TeamMembers.FindAsync(req.TeamMemberId.Value);
                agentName = assignedMember?.AgentName;
            }

            await sse.BroadcastAsync("task:assigned", new
            {
                assignment.Id, assignment.TaskId, assignment.TeamMemberId,
                AgentName = agentName, TaskTitle = task.Title
            });

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

        // Get agents triggered by a task status change
        group.MapGet("/triggered", async (int projectId, string status, LifecycleDbContext db) =>
        {
            var project = await db.Projects.FindAsync(projectId);
            var settings = project is not null ? ParseSettings(project) : new Dictionary<string, string>();

            var members = await db.TeamMembers
                .Where(tm => tm.ProjectId == projectId && tm.TriggerStatuses != null)
                .ToListAsync();

            // Filter in memory since TriggerStatuses is a JSON array string
            var triggered = members
                .Where(tm => tm.TriggerStatuses != null && tm.TriggerStatuses.Contains($"\"{status}\""))
                .Select(tm => new
                {
                    tm.Id, tm.AgentName, tm.Role, tm.ModelName,
                    tm.SpawnPromptTemplate,
                    ResolvedPrompt = ResolvePromptVariables(tm.SpawnPromptTemplate, settings),
                    tm.TriggerStatuses,
                    tm.IsPersistent, tm.Status
                })
                .ToList();

            return Results.Ok(triggered);
        });

        // Role templates for quick setup
        group.MapGet("/role-templates", () =>
        {
            var templates = new[]
            {
                new {
                    Role = "Researcher",
                    TriggerStatuses = "[\"Todo\"]",
                    ModelName = "claude-sonnet-4-5",
                    SpawnPromptTemplate = "You are a Researcher agent for the {{projectName}} project ({{language}}/{{framework}}).\n\nWhen a task enters Todo status, your job is to:\n1. Analyze the task requirements and scope\n2. Explore the relevant codebase at {{repositoryRoot}}\n3. Research any unfamiliar technologies or patterns needed\n4. Document your findings as a comment on the task\n5. Identify potential challenges or blockers\n\nProject dev URL: {{devUrl}}\nAPI URL: {{apiUrl}}\n\nBe thorough but concise. Focus on actionable insights that help the developer implement the task efficiently."
                },
                new {
                    Role = "TestPlanner",
                    TriggerStatuses = "[\"InProgress\"]",
                    ModelName = "claude-sonnet-4-5",
                    SpawnPromptTemplate = "You are a Test Planner agent for the {{projectName}} project ({{language}}/{{framework}}).\n\nWhen a task moves to InProgress, create a test plan covering:\n1. Unit tests — run with: {{unitTestCommand}}\n2. Integration tests — run with: {{integrationTestCommand}}\n3. Web/E2E tests — use {{webTestTool}} against {{devUrl}}\n4. Edge cases and error conditions\n5. Test data requirements\n\nTest depth: Use the task's requiredTestLevel if set, otherwise default to {{defaultTestLevel}}.\nAutonomy mode: {{testAutonomyLevel}}\n- Manual: create the plan only, human runs tests\n- SemiAuto: create plan + test files, human triggers execution\n- AutoCreate: create plan + test files + record in lifecycle\n- FullAuto: create plan + test files + execute + report results\n\nRecord the test plan using lifecycle tools. Prioritize: P1 = must have, P2 = should have."
                },
                new {
                    Role = "TestRunner",
                    TriggerStatuses = "[\"Review\"]",
                    ModelName = "claude-sonnet-4-5",
                    SpawnPromptTemplate = "You are a Test Runner agent for the {{projectName}} project.\n\nAutonomy mode: {{testAutonomyLevel}}\nWhen a task enters Review status:\n1. Get the test plan for this task\n2. Execute unit tests: {{unitTestCommand}}\n3. Execute integration tests: {{integrationTestCommand}}\n4. Execute web tests using {{webTestTool}} against {{devUrl}}\n5. Record results using lifecycle tools\n6. If tests fail: create an escalation with details\n7. If all tests pass: confirm the task is ready for completion\n\nProject root: {{repositoryRoot}}\nBe precise about failures — include error messages, expected vs actual results."
                },
                new {
                    Role = "Reviewer",
                    TriggerStatuses = "[\"Review\"]",
                    ModelName = "claude-opus-4-6",
                    SpawnPromptTemplate = "You are a Devil's Advocate Reviewer for the {{projectName}} project ({{language}}/{{framework}}).\n\nWhen a task enters Review:\n1. Review the implementation critically at {{repositoryRoot}}\n2. Challenge assumptions and look for:\n   - Edge cases not handled\n   - Security vulnerabilities (OWASP top 10)\n   - Performance concerns\n   - Missing error handling\n   - Code that could break in production\n3. If you find issues: create escalations describing each concern\n4. If the implementation is solid: confirm it passes review\n\nBe constructive but thorough. Better to catch issues now than in production."
                },
                new {
                    Role = "Custom",
                    TriggerStatuses = "[]",
                    ModelName = "claude-sonnet-4-5",
                    SpawnPromptTemplate = ""
                }
            };

            return Results.Ok(templates);
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

            // Parse settings for variable resolution
            var settingsDict = ParseSettings(project);

            return Results.Ok(new
            {
                Project = new { project.Id, project.Name, project.Description, project.Settings },
                ProjectSettings = settingsDict,
                Team = team,
                ActiveTasks = activeTasks,
                PendingEscalations = pendingEscalations
            });
        });

        // Get/update project settings (convenience endpoint)
        app.MapGet("/api/projects/{id:int}/settings", async (int id, LifecycleDbContext db) =>
        {
            var project = await db.Projects.FindAsync(id);
            if (project is null) return Results.NotFound();

            var settings = ParseSettings(project);
            return Results.Ok(new { project.Id, Settings = settings });
        });

        app.MapPut("/api/projects/{id:int}/settings", async (int id, ProjectSettingsRequest req, LifecycleDbContext db) =>
        {
            var project = await db.Projects.FindAsync(id);
            if (project is null) return Results.NotFound();

            project.Settings = JsonSerializer.Serialize(req.Settings);
            project.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { project.Id, Settings = req.Settings });
        });

        // Cleanup stale agent sessions
        group.MapPost("/cleanup-stale", async (LifecycleDbContext db, SseService sse) =>
        {
            var cleaned = await CleanupStaleSessions(db, sse);
            return Results.Ok(new { CleanedCount = cleaned.Count, Agents = cleaned });
        });

        // Monitor all agents for a project (auto-cleans stale sessions on each poll)
        group.MapGet("/monitor", async (int? projectId, LifecycleDbContext db, SseService sse) =>
        {
            await CleanupStaleSessions(db, sse);

            var pid = projectId ?? 1;
            var members = await db.TeamMembers
                .Include(tm => tm.Sessions.Where(s => s.Status == "Active").OrderByDescending(s => s.SpawnedAt).Take(1))
                .Include(tm => tm.Assignments.Where(a => a.Status != "Completed" && a.Status != "Abandoned"))
                    .ThenInclude(a => a.Task)
                .Where(tm => tm.ProjectId == pid)
                .ToListAsync();

            var now = DateTime.UtcNow;
            var staleThreshold = now.AddMinutes(-5);

            var agents = members.Select(m =>
            {
                var session = m.Sessions.FirstOrDefault();
                var assignment = m.Assignments.FirstOrDefault();
                var lastHb = session?.LastHeartbeatAt;
                return new
                {
                    m.Id, m.AgentName, m.Role, m.ModelName, m.Status,
                    CurrentActivity = session?.CurrentActivity,
                    CurrentTask = assignment?.Task is null ? null : new
                    {
                        Id = assignment.Task.Id,
                        Title = assignment.Task.Title,
                        Status = assignment.Task.Status.ToString()
                    },
                    SessionStarted = session?.SpawnedAt,
                    LastHeartbeat = lastHb,
                    TokensUsed = session?.TokensUsed,
                    LatestPlan = session?.LatestPlanContent is null ? null : new
                    {
                        FileName = session.LatestPlanFileName,
                        UpdatedAt = session.LatestPlanUpdatedAt
                    },
                    IsStale = m.Status == "Active" && lastHb.HasValue && lastHb < staleThreshold
                };
            }).ToList();

            return Results.Ok(new { Agents = agents });
        });

        // Auto-register: find-or-create TeamMember + spawn session in one call
        // Supports multiple concurrent sessions by using sessionId to find existing registrations
        group.MapPost("/auto-register", async (AutoRegisterRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var pid = req.ProjectId ?? 1;
            var sessionId = req.SessionId ?? Guid.NewGuid().ToString();

            // Check if this exact session already registered (idempotent re-register)
            var existingSession = await db.AgentSessions
                .Include(s => s.TeamMember)
                .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.Status == "Active");

            if (existingSession is not null)
            {
                existingSession.LastHeartbeatAt = DateTime.UtcNow;
                existingSession.TeamMember.LastActiveAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return Results.Ok(new
                {
                    TeamMemberId = existingSession.TeamMemberId,
                    SessionId = existingSession.SessionId,
                    existingSession.TeamMember.AgentName,
                    existingSession.TeamMember.Role,
                    IsNew = false
                });
            }

            // Count active sessions for this base agent name to generate unique display name
            var baseName = req.AgentName;
            var activeCount = await db.TeamMembers
                .Where(tm => tm.ProjectId == pid && tm.Status == "Active"
                    && (tm.AgentName == baseName || tm.AgentName.StartsWith(baseName + "-")))
                .CountAsync();

            var displayName = activeCount == 0 ? baseName : $"{baseName}-{activeCount + 1}";

            // Create a new TeamMember for this session
            var member = new TeamMember
            {
                ProjectId = pid,
                Role = req.Role ?? "ClaudeCode",
                AgentName = displayName,
                ModelName = req.ModelName ?? "unknown",
                IsPersistent = false, // auto-registered sessions are ephemeral
                Status = "Active",
                CreatedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow
            };
            db.TeamMembers.Add(member);
            await db.SaveChangesAsync();

            // Spawn session
            var session = new AgentSession
            {
                TeamMemberId = member.Id,
                SessionId = sessionId,
                Status = "Active",
                SpawnedAt = DateTime.UtcNow,
                LastHeartbeatAt = DateTime.UtcNow
            };
            db.AgentSessions.Add(session);
            await db.SaveChangesAsync();

            await sse.BroadcastAsync("agent:spawned", new { member.Id, member.AgentName, member.Role });

            return Results.Ok(new
            {
                TeamMemberId = member.Id,
                SessionId = session.SessionId,
                member.AgentName,
                member.Role,
                IsNew = true
            });
        });

        // Get agent's latest plan
        group.MapGet("/{id:int}/plan", async (int id, LifecycleDbContext db) =>
        {
            var session = await db.AgentSessions
                .Where(s => s.TeamMemberId == id && s.Status == "Active")
                .OrderByDescending(s => s.SpawnedAt)
                .FirstOrDefaultAsync();

            if (session?.LatestPlanContent is null)
                return Results.Ok(new { PlanFileName = (string?)null, PlanContent = (string?)null, UpdatedAt = (DateTime?)null });

            return Results.Ok(new
            {
                session.LatestPlanFileName,
                session.LatestPlanContent,
                UpdatedAt = session.LatestPlanUpdatedAt
            });
        });

        return app;
    }

    /// <summary>Cleanup stale sessions: 2min for ephemeral, 10min for persistent</summary>
    private static async Task<List<object>> CleanupStaleSessions(LifecycleDbContext db, SseService sse)
    {
        var ephemeralCutoff = DateTime.UtcNow.AddMinutes(-2);
        var persistentCutoff = DateTime.UtcNow.AddMinutes(-10);

        var staleSessions = await db.AgentSessions
            .Include(s => s.TeamMember)
            .Where(s => s.Status == "Active" && (
                (!s.TeamMember.IsPersistent && s.LastHeartbeatAt < ephemeralCutoff) ||
                (s.TeamMember.IsPersistent && s.LastHeartbeatAt < persistentCutoff)))
            .ToListAsync();

        var cleaned = new List<object>();
        foreach (var session in staleSessions)
        {
            session.Status = "Terminated";
            session.CompletedAt = DateTime.UtcNow;
            cleaned.Add(new { session.TeamMember.Id, session.TeamMember.AgentName });
            if (!session.TeamMember.IsPersistent)
                db.TeamMembers.Remove(session.TeamMember);
            else
                session.TeamMember.Status = "Idle";
            await sse.BroadcastAsync("agent:shutdown", new { session.TeamMember.Id, session.TeamMember.AgentName });
        }
        if (cleaned.Count > 0) await db.SaveChangesAsync();
        return cleaned;
    }

    internal static string ResolvePromptVariables(string? template, Dictionary<string, string> settings)
    {
        if (string.IsNullOrEmpty(template)) return template ?? "";
        return Regex.Replace(template, @"\{\{(\w+)\}\}", match =>
        {
            var key = match.Groups[1].Value;
            return settings.TryGetValue(key, out var val) ? val : match.Value;
        });
    }

    internal static Dictionary<string, string> ParseSettings(Project project)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["projectName"] = project.Name
        };

        if (!string.IsNullOrEmpty(project.Settings))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(project.Settings);
                if (parsed is not null)
                {
                    foreach (var kv in parsed)
                        dict[kv.Key] = kv.Value;
                }
            }
            catch { /* ignore parse errors */ }
        }

        return dict;
    }

    private static object MapMemberToDto(TeamMember tm)
    {
        var latestSession = tm.Sessions?.OrderByDescending(s => s.SpawnedAt).FirstOrDefault();
        var activeAssignment = tm.Assignments?.FirstOrDefault(a => a.Status != "Completed" && a.Status != "Abandoned");
        return new
        {
            tm.Id, tm.ProjectId, tm.Role, tm.AgentName, tm.ModelName,
            tm.IsPersistent, tm.Status, tm.ConfigJson, tm.TriggerStatuses,
            tm.CreatedAt, tm.LastActiveAt,
            CurrentActivity = latestSession?.CurrentActivity,
            CurrentSession = latestSession is null ? null : new
            {
                latestSession.Id, latestSession.SessionId, latestSession.Status,
                latestSession.SpawnedAt, latestSession.LastHeartbeatAt
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
        tm.TriggerStatuses, tm.CreatedAt, tm.LastActiveAt,
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
    string? SpawnPromptTemplate = null,
    string? TriggerStatuses = null);

public record UpdateTeamMemberRequest(
    string? Status = null,
    string? ModelName = null,
    string? ConfigJson = null,
    string? SpawnPromptTemplate = null,
    string? TriggerStatuses = null);

public record SpawnSessionRequest(string? SessionId = null);
public record ShutdownRequest(int? TokensUsed = null);
public record HeartbeatRequest(string? Activity = null, int? TokensUsed = null, string? PlanFileName = null, string? PlanContent = null);

public record AssignTaskRequest(int? TeamMemberId = null, string? AssignedBy = null);
public record ClaimTaskRequest(string AgentName);

public record CreateEscalationRequest(
    int ProjectId,
    string Description,
    int? TaskId = null);

public record ResolveEscalationRequest(
    string? Status = null,
    string? Resolution = null);

public record ProjectSettingsRequest(Dictionary<string, string> Settings);

public record AutoRegisterRequest(
    string AgentName,
    int? ProjectId = null,
    string? Role = null,
    string? ModelName = null,
    string? SessionId = null);
