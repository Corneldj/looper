using Looper.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Infrastructure;

public class LooperDbContext(DbContextOptions<LooperDbContext> options) : DbContext(options)
{
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<LoopAgent> Agents => Set<LoopAgent>();
    public DbSet<AgentRun> Runs => Set<AgentRun>();
    public DbSet<RunLogEntry> RunLogs => Set<RunLogEntry>();
    public DbSet<ResourceModuleRecord> ResourceModules => Set<ResourceModuleRecord>();
    public DbSet<AgentPullRequest> PullRequests => Set<AgentPullRequest>();
    public DbSet<ManagedWorkspace> Workspaces => Set<ManagedWorkspace>();
    public DbSet<UserActionRequest> UserActionRequests => Set<UserActionRequest>();
    public DbSet<LooperEvent> Events => Set<LooperEvent>();
    public DbSet<EventDelivery> EventDeliveries => Set<EventDelivery>();
    public DbSet<MetricValue> MetricValues => Set<MetricValue>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workflow>(workflow =>
        {
            workflow.Property(w => w.Name).HasMaxLength(200);
            workflow.HasMany(w => w.Resources).WithOne(r => r.Workflow).HasForeignKey(r => r.WorkflowId).OnDelete(DeleteBehavior.Cascade);
            workflow.HasMany(w => w.Agents).WithOne(a => a.Workflow).HasForeignKey(a => a.WorkflowId).OnDelete(DeleteBehavior.Cascade);
            // The default workflow is seeded with the schema: everything from before workflows
            // lands in it, and a fresh install has somewhere to put the first resource.
            workflow.HasData(new Workflow
            {
                Id = Workflow.DefaultId,
                Name = "Default",
                Description = "The original workbench. Rename it, or create more workflows for other loops.",
                CreatedAtUtc = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc)
            });
        });

        modelBuilder.Entity<Resource>(resource =>
        {
            resource.Property(r => r.Name).HasMaxLength(200);
            resource.Property(r => r.Type).HasConversion<string>().HasMaxLength(40);
            resource.Property(r => r.CustomTypeKey).HasMaxLength(100);
            resource.HasIndex(r => r.Type);
            resource.HasIndex(r => r.WorkflowId);
        });

        modelBuilder.Entity<AgentPullRequest>(pr =>
        {
            pr.Property(p => p.Title).HasMaxLength(300);
            pr.Property(p => p.Url).HasMaxLength(600);
            pr.Property(p => p.Repository).HasMaxLength(300);
            pr.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            pr.Property(p => p.MergeCommitSha).HasMaxLength(64);
            pr.HasIndex(p => p.Url);
            pr.HasIndex(p => p.Status);
            pr.HasOne(p => p.Agent).WithMany().HasForeignKey(p => p.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MetricValue>(value =>
        {
            value.Property(v => v.Note).HasMaxLength(500);
            value.Property(v => v.Source).HasConversion<string>().HasMaxLength(20);
            value.HasIndex(v => new { v.ResourceId, v.RecordedAtUtc });
            value.HasOne(v => v.Resource).WithMany().HasForeignKey(v => v.ResourceId).OnDelete(DeleteBehavior.Cascade);
            // Measurements outlive the agent that made them; the attribution just goes blank.
            value.HasOne<LoopAgent>().WithMany().HasForeignKey(v => v.AgentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<LooperEvent>(evt =>
        {
            evt.Property(e => e.Topic).HasMaxLength(200);
            evt.Property(e => e.Source).HasConversion<string>().HasMaxLength(20);
            evt.HasIndex(e => e.CreatedAtUtc);
        });

        modelBuilder.Entity<EventDelivery>(delivery =>
        {
            delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            delivery.HasIndex(d => new { d.AgentId, d.Status });
            delivery.HasOne(d => d.Event).WithMany().HasForeignKey(d => d.EventId).OnDelete(DeleteBehavior.Cascade);
            delivery.HasOne(d => d.Agent).WithMany().HasForeignKey(d => d.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserActionRequest>(request =>
        {
            request.Property(r => r.Title).HasMaxLength(300);
            request.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            request.HasIndex(r => new { r.AgentId, r.Status });
            request.HasOne(r => r.Agent).WithMany().HasForeignKey(r => r.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ManagedWorkspace>(workspace =>
        {
            workspace.Property(w => w.Unit).HasMaxLength(120);
            workspace.Property(w => w.Path).HasMaxLength(1024);
            workspace.Property(w => w.Status).HasConversion<string>().HasMaxLength(20);
            workspace.HasIndex(w => new { w.ResourceId, w.Status });
            workspace.HasOne(w => w.Resource).WithMany().HasForeignKey(w => w.ResourceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ResourceModuleRecord>(module =>
        {
            module.Property(m => m.TypeKey).HasMaxLength(100);
            module.Property(m => m.DisplayName).HasMaxLength(200);
            module.Property(m => m.Icon).HasMaxLength(16);
            module.Property(m => m.DllFileName).HasMaxLength(260);
            module.HasIndex(m => m.TypeKey).IsUnique();
        });

        modelBuilder.Entity<LoopAgent>(agent =>
        {
            agent.Property(a => a.Name).HasMaxLength(200);
            agent.Property(a => a.Model).HasMaxLength(100);
            agent.Property(a => a.Effort).HasConversion<string>().HasMaxLength(20);
            agent.HasIndex(a => a.WorkflowId);
            agent.HasMany(a => a.Resources).WithMany(r => r.Agents);
            agent.HasMany(a => a.Runs).WithOne(r => r.Agent!).HasForeignKey(r => r.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentRun>(run =>
        {
            run.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            run.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(20);
            run.HasIndex(r => r.StartedAtUtc);
            run.HasIndex(r => new { r.AgentId, r.StartedAtUtc });
            run.HasMany(r => r.Logs).WithOne(l => l.Run!).HasForeignKey(l => l.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppSetting>(setting =>
        {
            setting.HasKey(s => s.Key);
            setting.Property(s => s.Key).HasMaxLength(100);
        });

        modelBuilder.Entity<RunLogEntry>(log =>
        {
            log.Property(l => l.Level).HasMaxLength(10);
            log.HasIndex(l => l.RunId);
        });
    }
}
