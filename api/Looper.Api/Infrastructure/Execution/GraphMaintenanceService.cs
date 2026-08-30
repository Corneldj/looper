using System.Diagnostics;
using System.Text.Json;
using Looper.Api.Domain;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>Snapshot persisted as health.json inside each graph folder (and served by /api/graphs).</summary>
public sealed record GraphHealthSnapshot(
    DateTime CheckedUtc,
    string Signature,
    string? LastRaisedSignature,
    JsonElement Report);

/// <summary>
/// The asynchronous-maintenance half of decoupled memory: graphs need continuous background
/// engineering, and none of it may block (or depend on) the executing loops. On a timer, every
/// graph resource gets its workspace upgraded, its health measured deterministically
/// (loopergraph health --json), the snapshot persisted — and when curation is due (inbox at
/// threshold, competing facts, structural problems), a graph.&lt;slug&gt;.needs-curation event
/// wakes the curator loop. Signature-deduped, so an unchanged backlog never re-raises.
/// </summary>
public sealed class GraphMaintenanceService(
    IDbContextFactory<LooperDbContext> dbFactory,
    ResourceModuleRegistry moduleRegistry,
    EventDispatcher eventDispatcher,
    AgentRunCoordinator coordinator,
    IOptions<LooperOptions> options,
    ILogger<GraphMaintenanceService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int HealthTimeoutMs = 20_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // First pass soon after startup so fresh installs see health without waiting a cycle.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            await SafePassAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.GraphMaintenanceMinutes)));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SafePassAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task SafePassAsync(CancellationToken cancellationToken)
    {
        try
        {
            var events = await RunPassAsync(cancellationToken);
            if (events > 0) logger.LogInformation("Graph maintenance raised {Count} curation event(s)", events);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Graph maintenance pass failed");
        }
    }

    /// <summary>One maintenance pass over every graph resource. Returns curation events raised.</summary>
    public async Task<int> RunPassAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var graphs = await db.Resources.AsNoTracking()
            .Where(r => r.Type == ResourceType.Custom && r.CustomTypeKey != null)
            .ToListAsync(cancellationToken);
        graphs = graphs.Where(GraphInfrastructure.IsGraph).ToList();

        var raised = 0;
        foreach (var resource in graphs)
        {
            try
            {
                if (await MaintainOneAsync(db, resource, cancellationToken)) raised++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Graph maintenance failed for resource {Name}", resource.Name);
            }
        }

        if (raised > 0) await eventDispatcher.PumpAsync(db, coordinator, cancellationToken);
        return raised;
    }

    private async Task<bool> MaintainOneAsync(LooperDbContext db, Resource resource, CancellationToken cancellationToken)
    {
        var context = new ResourceModuleContext(resource.ConfigJson);
        var path = context.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Keep the workspace current: seeds missing files, upgrades the toolkit version.
        if (moduleRegistry.TryGet(resource.CustomTypeKey!, out var module))
        {
            module.PrepareRun(context);
        }

        var reportJson = await RunHealthAsync(path, cancellationToken);
        if (reportJson is null) return false;

        using var document = JsonDocument.Parse(reportJson);
        var report = document.RootElement.Clone();
        var pending = report.TryGetProperty("inbox_pending", out var p) ? p.GetInt32() : 0;
        var competing = report.TryGetProperty("competing_count", out var c) ? c.GetInt32() : 0;
        var problems = report.TryGetProperty("problem_count", out var pr) ? pr.GetInt32() : 0;
        var signature = $"{pending}|{competing}|{problems}";

        var healthPath = Path.Combine(path, GraphInfrastructure.HealthFileName);
        var previous = ReadSnapshot(healthPath);

        var config = GraphInfrastructure.SharedConfig(context);
        var needsCuration = pending >= config.EffectiveInboxThreshold || competing > 0 || problems > 0;
        var shouldRaise = needsCuration && signature != previous?.LastRaisedSignature;

        string? raisedSignature = shouldRaise ? signature : previous?.LastRaisedSignature;
        WriteSnapshot(healthPath, new GraphHealthSnapshot(DateTime.UtcNow, signature, raisedSignature, report));
        if (!shouldRaise) return false;

        var topic = GraphInfrastructure.CurationTopic(resource.Name);
        var payload =
            $"Graph '{resource.Name}' needs curation: {pending} inbox item(s) pending, " +
            $"{competing} competing fact group(s), {problems} structural problem(s). Storage: {path}";
        await eventDispatcher.RaiseAsync(db, topic, payload, EventSource.Harness, null, null, 0, cancellationToken);
        logger.LogInformation("Raised {Topic}: {Payload}", topic, payload);
        return true;
    }

    private async Task<string?> RunHealthAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Value.PythonCommand,
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(path, GraphWorkspace.ToolFileName));
        startInfo.ArgumentList.Add("--dir");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("health");
        startInfo.ArgumentList.Add("--json");

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("process did not start");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No python on this machine: health simply stays unmeasured. Logged once per pass at debug.
            logger.LogDebug(ex, "Cannot run graph health ({Python} unavailable?)", options.Value.PythonCommand);
            return null;
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HealthTimeoutMs);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 && output.TrimStart().StartsWith('{') ? output : null;
        }
    }

    public static GraphHealthSnapshot? ReadSnapshot(string healthPath)
    {
        try
        {
            if (!File.Exists(healthPath)) return null;
            return JsonSerializer.Deserialize<GraphHealthSnapshot>(File.ReadAllText(healthPath), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteSnapshot(string healthPath, GraphHealthSnapshot snapshot) =>
        File.WriteAllText(healthPath, JsonSerializer.Serialize(snapshot, Json) + "\n");
}
