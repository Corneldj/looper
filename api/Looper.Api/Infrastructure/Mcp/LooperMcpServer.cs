using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentValidation;
using Looper.Api.Common;

namespace Looper.Api.Infrastructure.Mcp;

/// <summary>
/// A minimal Model Context Protocol server over Streamable HTTP, stateless: each POST carries one
/// JSON-RPC message and gets one JSON response (notifications get 202, no session ids, no SSE).
/// It speaks exactly what Claude Code needs to discover and call tools — initialize, ping,
/// tools/list, tools/call — and nothing else. Failures the model can fix (a bad argument, a
/// refused registration) come back as tool errors it can read; everything else is a JSON-RPC error.
/// </summary>
public static class LooperMcpServer
{
    public const string ProtocolVersion = "2025-06-18";
    private static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];
    private static readonly JsonSerializerOptions ResultJson = CreateResultJson();

    private static JsonSerializerOptions CreateResultJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public sealed record Reply(int Status, JsonObject? Body);

    public static async Task<Reply> HandleAsync(JsonNode? message, IReadOnlyList<LooperTool> tools, ToolCallContext context,
        string serverVersion, ILogger logger)
    {
        if (message is not JsonObject request)
        {
            return new Reply(400, Error(null, -32700, "Parse error: expected one JSON-RPC message object."));
        }

        var id = request["id"];
        var method = request["method"]?.GetValue<string>();
        var parameters = request["params"] as JsonObject;
        if (string.IsNullOrEmpty(method))
        {
            return new Reply(400, Error(id?.DeepClone(), -32600, "Invalid request: no method."));
        }

        // Notifications carry no id and get no body.
        if (id is null) return new Reply(202, null);

        try
        {
            switch (method)
            {
                case "initialize":
                    var requested = parameters?["protocolVersion"]?.GetValue<string>();
                    var version = SupportedVersions.Contains(requested) ? requested! : ProtocolVersion;
                    return new Reply(200, Result(id, new JsonObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = LooperTools.ServerName, ["version"] = serverVersion },
                        ["instructions"] = "Looper's tools for this run. Each one records something the harness and the dashboard " +
                                           "rely on; call them for real facts only, and read the tool descriptions for when."
                    }));

                case "ping":
                    return new Reply(200, Result(id, new JsonObject()));

                case "tools/list":
                    return new Reply(200, Result(id, new JsonObject
                    {
                        ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["inputSchema"] = t.InputSchema.DeepClone()
                        }).ToArray())
                    }));

                case "tools/call":
                    return new Reply(200, Result(id, await CallAsync(parameters, tools, context, logger)));

                default:
                    return new Reply(200, Error(id.DeepClone(), -32601, $"Method not found: {method}"));
            }
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP {Method} failed for run {RunId}", method, context.RunId);
            return new Reply(200, Error(id.DeepClone(), -32603, $"Internal error: {ex.Message}"));
        }
    }

    private static async Task<JsonObject> CallAsync(JsonObject? parameters, IReadOnlyList<LooperTool> tools, ToolCallContext context, ILogger logger)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? "";
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (tool is null)
        {
            return ToolError($"Unknown tool '{name}'. This run offers: {string.Join(", ", tools.Select(t => t.Name))}.");
        }
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            var result = await tool.Invoke(arguments, context);
            var text = result is null ? "ok" : JsonSerializer.Serialize(result, ResultJson);
            logger.LogInformation("MCP tool {Tool} called by run {RunId}", tool.Name, context.RunId);
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = false
            };
        }
        catch (ToolArgumentException ex)
        {
            return ToolError(ex.Message);
        }
        catch (ValidationException ex)
        {
            var detail = ex.Errors.Any() ? string.Join(" ", ex.Errors.Select(e => e.ErrorMessage)) : ex.Message;
            return ToolError("Refused: " + detail);
        }
        catch (NotFoundException ex)
        {
            return ToolError("Refused: " + ex.Message);
        }
    }

    /// <summary>A tool-level error: the model sees the text and can correct its call.</summary>
    private static JsonObject ToolError(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = true
    };

    private static JsonObject Result(JsonNode id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
