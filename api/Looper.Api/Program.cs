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
builder.Services.AddSingleton<ClaudeCliExecutor>();
builder.Services.AddSingleton<SimulatedAgentExecutor>();
builder.Services.AddSingleton<TestingActionRunner>();
builder.Services.AddSingleton<AgentRunCoordinator>();
builder.Services.AddSingleton<ClaudeCliStatusService>();
builder.Services.AddHostedService<AgentSchedulerService>();

// Dynamic resource-type modules.
builder.Services.AddSingleton<Looper.Api.Modules.ResourceModuleCompiler>();
builder.Services.AddSingleton<Looper.Api.Modules.ResourceModuleRegistry>();
builder.Services.AddSingleton<Looper.Api.Modules.ResourceModuleInstaller>();
builder.Services.AddSingleton<Looper.Api.Modules.ClaudeModuleSourceGenerator>();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:4200")
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LooperDbContext>();
    await db.Database.EnsureCreatedAsync();

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

    // Bring dynamic resource-type modules back to life.
    var moduleRecords = await db.ResourceModules.AsNoTracking().ToListAsync();
    await scope.ServiceProvider.GetRequiredService<Looper.Api.Modules.ResourceModuleRegistry>()
        .ReconcileAsync(moduleRecords, scope.ServiceProvider.GetRequiredService<Looper.Api.Modules.ResourceModuleCompiler>());
}

app.UseCors();
app.UseMiddleware<ExceptionMappingMiddleware>();
app.MapOpenApi();
app.MapFeatureEndpoints();

app.Run();

/// <summary>Exposes the entry point to WebApplicationFactory-based integration tests.</summary>
public partial class Program;
