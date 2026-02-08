using Lifecycle.Data.Entities;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Lifecycle.Data.Enums.TaskStatus;

namespace Lifecycle.Data;

public class LifecycleDbContext : DbContext
{
    public LifecycleDbContext(DbContextOptions<LifecycleDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<Phase> Phases => Set<Phase>();
    public DbSet<LifecycleTask> Tasks => Set<LifecycleTask>();
    public DbSet<TestRecord> TestRecords => Set<TestRecord>();
    public DbSet<TestPlan> TestPlans => Set<TestPlan>();
    public DbSet<TestStep> TestSteps => Set<TestStep>();
    public DbSet<TestExecution> TestExecutions => Set<TestExecution>();
    public DbSet<TestStepResult> TestStepResults => Set<TestStepResult>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<TaskLabel> TaskLabels => Set<TaskLabel>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<TaskAssignment> TaskAssignments => Set<TaskAssignment>();
    public DbSet<AgentEscalation> AgentEscalations => Set<AgentEscalation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Repository).HasMaxLength(500);
            entity.Property(e => e.Settings).HasMaxLength(10000);
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<Milestone>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Version).HasMaxLength(50);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Project)
                .WithMany(p => p.Milestones)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Phase>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Goal).HasMaxLength(1000);
            entity.HasIndex(e => e.MilestoneId);
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Milestone)
                .WithMany(m => m.Phases)
                .HasForeignKey(e => e.MilestoneId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LifecycleTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.GitCommitSha).HasMaxLength(40);
            entity.Property(e => e.GitBranch).HasMaxLength(200);
            entity.Property(e => e.PullRequestUrl).HasMaxLength(500);
            entity.Property(e => e.ConversationRef).HasMaxLength(500);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.PhaseId);
            entity.HasIndex(e => new { e.Status, e.OrderInColumn });
            entity.HasOne(e => e.Project)
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Phase)
                .WithMany(p => p.Tasks)
                .HasForeignKey(e => e.PhaseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TestRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TestName).HasMaxLength(500);
            entity.Property(e => e.TestFile).HasMaxLength(500);
            entity.Property(e => e.Framework).HasMaxLength(100);
            entity.HasIndex(e => e.TaskId);
            entity.HasOne(e => e.Task)
                .WithMany(t => t.Tests)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TestPlan>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.HasIndex(e => e.TaskId);
            entity.HasOne(e => e.Task)
                .WithMany(t => t.TestPlans)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TestStep>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.ExpectedResult).HasMaxLength(2000);
            entity.Property(e => e.AutomationCommand).HasMaxLength(1000);
            entity.HasIndex(e => e.TestPlanId);
            entity.HasOne(e => e.TestPlan)
                .WithMany(tp => tp.Steps)
                .HasForeignKey(e => e.TestPlanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TestExecution>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FailureReason).HasMaxLength(2000);
            entity.Property(e => e.ExecutedBy).HasMaxLength(200);
            entity.HasIndex(e => e.TestPlanId);
            entity.HasOne(e => e.TestPlan)
                .WithMany(tp => tp.Executions)
                .HasForeignKey(e => e.TestPlanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TestStepResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ActualResult).HasMaxLength(2000);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.Screenshot).HasMaxLength(500);
            entity.HasIndex(e => e.TestExecutionId);
            entity.HasOne(e => e.Execution)
                .WithMany(te => te.StepResults)
                .HasForeignKey(e => e.TestExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Step)
                .WithMany(ts => ts.Results)
                .HasForeignKey(e => e.TestStepId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Attachment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.OriginalFileName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.StoragePath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.UploadedBy).HasMaxLength(200);
            entity.HasIndex(e => e.TaskId);
            entity.HasOne(e => e.Task)
                .WithMany(t => t.Attachments)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Comment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.Author).HasMaxLength(200);
            entity.HasIndex(e => e.TaskId);
            entity.HasOne(e => e.Task)
                .WithMany(t => t.Comments)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Label>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Color).IsRequired().HasMaxLength(7);
            entity.Property(e => e.Description).HasMaxLength(200);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne(e => e.Project)
                .WithMany(p => p.Labels)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskLabel>(entity =>
        {
            entity.HasKey(e => new { e.TaskId, e.LabelId });
            entity.HasOne(e => e.Task)
                .WithMany(t => t.TaskLabels)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Label)
                .WithMany(l => l.TaskLabels)
                .HasForeignKey(e => e.LabelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ActivityLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EntityType).HasMaxLength(50);
            entity.Property(e => e.Action).HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Actor).HasMaxLength(200);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne(e => e.Project)
                .WithMany(p => p.Activities)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(100);
            entity.Property(e => e.AgentName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ModelName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.TriggerStatuses).HasMaxLength(500);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne(e => e.Project)
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentSession>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SessionId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CurrentActivity).HasMaxLength(500);
            entity.HasIndex(e => e.TeamMemberId);
            entity.HasOne(e => e.TeamMember)
                .WithMany(tm => tm.Sessions)
                .HasForeignKey(e => e.TeamMemberId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskAssignment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AssignedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.TaskId);
            entity.HasIndex(e => e.TeamMemberId);
            entity.HasOne(e => e.Task)
                .WithMany(t => t.Assignments)
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.TeamMember)
                .WithMany(tm => tm.Assignments)
                .HasForeignKey(e => e.TeamMemberId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AgentEscalation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.ProjectId);
            entity.HasOne(e => e.Project)
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Task)
                .WithMany()
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
