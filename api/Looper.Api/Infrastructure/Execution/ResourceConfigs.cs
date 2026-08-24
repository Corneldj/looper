using System.Text.Json;
using Looper.Api.Domain;

namespace Looper.Api.Infrastructure.Execution;

// Typed views over Resource.ConfigJson. The frontend forms produce these shapes;
// the executor consumes them. Unknown fields are ignored so configs can evolve.

public sealed class McpServerConfig
{
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
    public string? Url { get; set; }
}

public sealed class FileLocationConfig
{
    public string Path { get; set; } = "";
    public bool Primary { get; set; }
}

public sealed class RuleConfig
{
    public string Text { get; set; } = "";
}

public sealed class RagConfig
{
    public string? Path { get; set; }
    public string? Url { get; set; }
    public string Instructions { get; set; } = "";
}

public sealed class SubAgentConfig
{
    public string? Description { get; set; }
    public string Prompt { get; set; } = "";
    public string? Tools { get; set; }
    public string? Model { get; set; }
}

public sealed class TestingActionConfig
{
    public string Command { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public int? TimeoutSeconds { get; set; }
}

public sealed class CredentialConfig
{
    /// <summary>Environment variable name the secret is exposed as during a run.</summary>
    public string EnvVar { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class AzureConnectionConfig
{
    public string? TenantId { get; set; }
    public string? SubscriptionId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

public static class ResourceConfig
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static T Parse<T>(Resource resource) where T : new()
    {
        try
        {
            return JsonSerializer.Deserialize<T>(resource.ConfigJson, Options) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }
}

public static class AgentWorkspace
{
    /// <summary>
    /// Resolves the working directory and extra directories for a run.
    /// Priority: agent override, then the primary (or first) FileLocation resource,
    /// then a managed per-agent workspace folder. Remaining locations become --add-dir entries.
    /// </summary>
    public static (string WorkingDirectory, IReadOnlyList<string> AdditionalDirectories) Resolve(
        LoopAgent agent, IReadOnlyList<Resource> resources)
    {
        var locations = resources
            .Where(r => r.Type == ResourceType.FileLocation)
            .Select(ResourceConfig.Parse<FileLocationConfig>)
            .Where(c => !string.IsNullOrWhiteSpace(c.Path))
            .ToList();

        string? workingDirectory = !string.IsNullOrWhiteSpace(agent.WorkingDirectory)
            ? agent.WorkingDirectory
            : (locations.FirstOrDefault(l => l.Primary) ?? locations.FirstOrDefault())?.Path;

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            workingDirectory = Path.Combine(AppContext.BaseDirectory, "workspaces", agent.Id.ToString("N"));
        }

        Directory.CreateDirectory(workingDirectory);

        var extra = locations
            .Select(l => Path.GetFullPath(l.Path))
            .Where(p => !string.Equals(p, Path.GetFullPath(workingDirectory), StringComparison.Ordinal))
            .Distinct()
            .ToList();

        return (workingDirectory, extra);
    }

    /// <summary>Environment variables contributed by credential-type resources. Values are secrets: never log them.</summary>
    public static Dictionary<string, string> ResolveEnvironment(IReadOnlyList<Resource> resources)
    {
        var env = new Dictionary<string, string>();

        foreach (var resource in resources.Where(r => r.Type == ResourceType.PatToken))
        {
            var config = ResourceConfig.Parse<CredentialConfig>(resource);
            if (!string.IsNullOrWhiteSpace(config.EnvVar) && !string.IsNullOrEmpty(config.Value))
            {
                env[config.EnvVar] = config.Value;
            }
        }

        foreach (var resource in resources.Where(r => r.Type == ResourceType.AzureConnection))
        {
            var config = ResourceConfig.Parse<AzureConnectionConfig>(resource);
            if (!string.IsNullOrWhiteSpace(config.TenantId)) env["AZURE_TENANT_ID"] = config.TenantId;
            if (!string.IsNullOrWhiteSpace(config.SubscriptionId)) env["AZURE_SUBSCRIPTION_ID"] = config.SubscriptionId;
            if (!string.IsNullOrWhiteSpace(config.ClientId)) env["AZURE_CLIENT_ID"] = config.ClientId;
            if (!string.IsNullOrWhiteSpace(config.ClientSecret)) env["AZURE_CLIENT_SECRET"] = config.ClientSecret;
        }

        return env;
    }
}
