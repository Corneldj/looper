using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Cqrs.Decorators;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LooperOptions>(builder.Configuration.GetSection(LooperOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Persistence: a pooled factory for background execution plus a scoped context for handlers.
var connectionString = builder.Configuration.GetConnectionString("Looper")
    ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "looper.db")}";
builder.Services.AddDbContextFactory<LooperDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddDbContext<LooperDbContext>(
    options => options.UseSqlite(connectionString),
    optionsLifetime: ServiceLifetime.Singleton);

// CQRS: scan handlers and validators, then compose the decorator pipeline.
// Decoration order matters — the last registration is the outermost wrapper:
//   Logging -> Validation -> handler for commands, Logging -> handler for queries.
builder.Services.AddScoped<IDispatcher, Dispatcher>();
builder.Services.Scan(scan => scan
    .FromAssembliesOf(typeof(Program))
    .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<,>)))
        .AsImplementedInterfaces().WithScopedLifetime()
    .AddClasses(classes => classes.AssignableTo(typeof(IQueryHandler<,>)))
        .AsImplementedInterfaces().WithScopedLifetime());
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.Decorate(typeof(ICommandHandler<,>), typeof(ValidationCommandHandlerDecorator<,>));
builder.Services.Decorate(typeof(ICommandHandler<,>), typeof(LoggingCommandHandlerDecorator<,>));
builder.Services.Decorate(typeof(IQueryHandler<,>), typeof(LoggingQueryHandlerDecorator<,>));

// Execution engine.
builder.Services.AddSingleton<ClaudeAuthProvider>();
builder.Services.AddSingleton<ClaudeCliExecutor>();
builder.Services.AddSingleton<SimulatedAgentExecutor>();
builder.Services.AddSingleton<TestingActionRunner>();
builder.Services.AddSingleton<ScriptRunner>();
builder.Services.AddSingleton<ScriptAssistant>();
builder.Services.AddSingleton<MetricRecorder>();
builder.Services.AddSingleton<ReviewRunner>();
builder.Services.AddSingleton<EventDispatcher>();
builder.Services.AddSingleton<GraphContextService>();
builder.Services.AddHostedService<GraphMaintenanceService>();
builder.Services.AddSingleton<Looper.Api.Infrastructure.Workspaces.WorkspaceProvisioner>();
builder.Services.AddHostedService<Looper.Api.Infrastructure.Workspaces.WorkspaceJanitorService>();
builder.Services.AddSingleton<AgentRunCoordinator>();
builder.Services.AddSingleton<ClaudeCliStatusService>();
builder.Services.AddHostedService<AgentSchedulerService>();

// Delivery tracking: PR sync via gh, code survival via git blame.
builder.Services.AddSingleton<Looper.Api.Infrastructure.Delivery.GitHubPrInspector>();
builder.Services.AddSingleton<Looper.Api.Infrastructure.Delivery.CodeSurvivalCalculator>();
builder.Services.AddSingleton<Looper.Api.Infrastructure.Delivery.PullRequestSynchronizer>();
builder.Services.AddHostedService<Looper.Api.Infrastructure.Delivery.DeliverySyncService>();

// Dynamic resource-type modules.
builder.Services.AddSingleton<Looper.Api.Modules.ResourceModuleCompiler>();
builder.Services.AddSingleton<Looper.Api.Modules.ResourceModuleRegistry>();
builder.Services.AddSingleton<Looper.Api.Modules.ResourceModuleInstaller>();
builder.Services.AddSingleton<Looper.Api.Modules.IModuleSourceGenerator, Looper.Api.Modules.ClaudeModuleSourceGenerator>();
builder.Services.AddSingleton<Looper.Api.Features.ResourceTypes.ResourceTypeGenerationJobs>();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:4200")
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LooperDbContext>();

    // Databases from before the migrations era were created with EnsureCreated and have no
    // migrations history. Stamp them with the baseline so Migrate() applies only what's new,
    // preserving live data.
    var hasLegacySchema = false;
    if (await db.Database.CanConnectAsync())
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Runs') > 0 " +
            "AND (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory') = 0";
        hasLegacySchema = Convert.ToBoolean(await command.ExecuteScalarAsync());
    }
    if (hasLegacySchema)
    {
        var baseline = db.Database.GetMigrations().First(); // InitialCreate matches the legacy schema
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL)");
        await db.Database.ExecuteSqlAsync(
            $"INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ({baseline}, '10.0.11')");
        app.Logger.LogInformation("Stamped pre-migrations database with baseline {Baseline}", baseline);
    }
    await db.Database.MigrateAsync();

    // Crash recovery: runs left 'Running' by a previous process can never finish now.
    var interrupted = await db.Runs
        .Where(r => r.Status == Looper.Api.Domain.RunStatus.Running)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(r => r.Status, Looper.Api.Domain.RunStatus.Failed)
            .SetProperty(r => r.CompletedAtUtc, DateTime.UtcNow)
            .SetProperty(r => r.ErrorMessage, "Interrupted: the Looper API restarted while this run was executing."));
    if (interrupted > 0)
    {
        app.Logger.LogWarning("Marked {Count} orphaned run(s) from a previous process as failed", interrupted);
    }

    // The Event Listener resource type was retired: an agent's own "on events" trigger is the listener.
    await Looper.Api.Infrastructure.LegacyEventListeners.RetireAsync(db, app.Logger, CancellationToken.None);

    // Shipped graph resource types, then the user's dynamic modules on top.
    var registry = scope.ServiceProvider.GetRequiredService<Looper.Api.Modules.ResourceModuleRegistry>();
    registry.RegisterBuiltIn(new Looper.Api.Modules.BuiltIn.ContinuousVectorMemoryGraphModule());
    registry.RegisterBuiltIn(new Looper.Api.Modules.BuiltIn.KnowledgeGraphModule());
    registry.RegisterBuiltIn(new Looper.Api.Modules.BuiltIn.MemoryGraphModule());
    registry.RegisterBuiltIn(new Looper.Api.Modules.BuiltIn.ExecutionGraphModule());
    registry.RegisterBuiltIn(new Looper.Api.Modules.BuiltIn.SpecificationModule());
    registry.RegisterBuiltIn(new Looper.Api.Modules.BuiltIn.ScriptModule());
    registry.RegisterBuiltIn(new Looper.Api.Modules.BuiltIn.MetricModule());
    registry.RegisterBuiltIn(new Looper.Api.Modules.BuiltIn.EventRaiserModule());

    var moduleRecords = await db.ResourceModules.AsNoTracking().ToListAsync();
    await registry.ReconcileAsync(moduleRecords, scope.ServiceProvider.GetRequiredService<Looper.Api.Modules.ResourceModuleCompiler>());
}

app.UseCors();
app.UseMiddleware<ExceptionMappingMiddleware>();
app.MapOpenApi();
app.MapFeatureEndpoints();

app.Run();

/// <summary>Exposes the entry point to WebApplicationFactory-based integration tests.</summary>
public partial class Program;
