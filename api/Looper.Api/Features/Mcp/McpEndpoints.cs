using System.Text.Json;
using System.Text.Json.Nodes;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Mcp;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Mcp;

/// <summary>
/// One MCP server per run at /mcp/runs/{runId}: the run tells us the agent, the agent's resources
/// tell us the tools. A run that does not exist gets 404 before any protocol talk; GET is refused
/// because there is no server-initiated stream; DELETE (session end) is a no-op.
/// </summary>
public sealed class McpEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/mcp/runs/{runId:guid}", async (Guid runId, HttpRequest request, LooperDbContext db, IDispatcher dispatcher,
            ILogger<McpEndpoints> logger, CancellationToken ct) =>
        {
            var run = await db.Runs.AsNoTracking().Where(r => r.Id == runId).Select(r => new { r.Id, r.AgentId }).FirstOrDefaultAsync(ct);
            if (run is null) return Results.Problem(title: $"Run '{runId}' was not found.", statusCode: StatusCodes.Status404NotFound);

            var resources = await db.Agents.AsNoTracking().Where(a => a.Id == run.AgentId).SelectMany(a => a.Resources).ToListAsync(ct);
            JsonNode? message;
            try
            {
                message = await JsonNode.ParseAsync(request.Body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                message = null;
            }

            var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev";
            var reply = await LooperMcpServer.HandleAsync(message, LooperTools.ForRun(resources),
                new ToolCallContext(run.Id, run.AgentId, resources, dispatcher, ct), version, logger);
            return reply.Body is null
                ? Results.StatusCode(reply.Status)
                : Results.Content(reply.Body.ToJsonString(), "application/json", statusCode: reply.Status);
        });

        app.MapGet("/mcp/runs/{runId:guid}", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        app.MapDelete("/mcp/runs/{runId:guid}", () => Results.NoContent());
    }
}
