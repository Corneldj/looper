using System.Text.Json.Nodes;
using Looper.Api.Domain;
using Looper.Api.Modules;

namespace Looper.Api.Features.Resources;

public sealed record ResourceDto(
    Guid Id,
    string Name,
    ResourceType Type,
    string? CustomTypeKey,
    string Description,
    string ConfigJson,
    int AgentCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid WorkflowId);

public static class ResourceMapper
{
    public static ResourceDto ToDto(this Resource resource, int agentCount, ResourceModuleRegistry registry) => new(
        resource.Id,
        resource.Name,
        resource.Type,
        resource.CustomTypeKey,
        resource.Description,
        SecretMasker.Mask(resource, registry),
        agentCount,
        resource.CreatedAtUtc,
        resource.UpdatedAtUtc,
        resource.WorkflowId);
}

/// <summary>
/// Credential values never leave the API: reads replace them with a sentinel, and writes that
/// still carry the sentinel keep the stored value instead of overwriting it.
/// Built-in credential types mask well-known keys; dynamic types mask their Password fields.
/// </summary>
public static class SecretMasker
{
    public const string Sentinel = "__SECRET_UNCHANGED__";

    private static readonly string[] BuiltInSecretKeys = ["value", "token", "clientSecret", "password", "apiKey"];

    public static string Mask(Resource resource, ResourceModuleRegistry registry)
    {
        var isSecret = SecretKeyPredicate(resource.Type, resource.CustomTypeKey, registry);
        if (isSecret is null) return resource.ConfigJson;

        return Transform(resource.ConfigJson, (key, node) =>
            isSecret(key) && !string.IsNullOrEmpty(node?.GetValue<string?>()) ? JsonValue.Create(Sentinel) : node);
    }

    /// <summary>
    /// Strips secret values entirely (empty string, not the sentinel) and reports which keys were
    /// stripped — for exports that leave the machine. An import then creates the resource with the
    /// secret blank and tells the user to fill it in.
    /// </summary>
    public static (string Json, IReadOnlyList<string> RedactedKeys) Redact(Resource resource, ResourceModuleRegistry registry)
    {
        var isSecret = SecretKeyPredicate(resource.Type, resource.CustomTypeKey, registry);
        if (isSecret is null) return (resource.ConfigJson, []);

        var redacted = new List<string>();
        var json = Transform(resource.ConfigJson, (key, node) =>
        {
            if (!isSecret(key) || string.IsNullOrEmpty(node?.GetValue<string?>())) return node;
            redacted.Add(key);
            return JsonValue.Create("");
        });
        return (json, redacted);
    }

    public static string PreserveSecrets(Resource stored, string incomingJson, ResourceModuleRegistry registry)
    {
        var isSecret = SecretKeyPredicate(stored.Type, stored.CustomTypeKey, registry);
        if (isSecret is null) return incomingJson;

        JsonObject? storedConfig;
        try
        {
            storedConfig = JsonNode.Parse(stored.ConfigJson) as JsonObject;
        }
        catch (Exception)
        {
            storedConfig = null;
        }

        return Transform(incomingJson, (key, node) =>
        {
            if (!isSecret(key) || node?.GetValue<string?>() != Sentinel) return node;
            var storedValue = storedConfig?.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
            return storedValue is null ? JsonValue.Create("") : JsonValue.Create(storedValue.GetValue<string>());
        });
    }

    /// <summary>Null means the type has no secret fields, so configs pass through untouched.</summary>
    private static Func<string, bool>? SecretKeyPredicate(
        ResourceType type, string? customTypeKey, ResourceModuleRegistry registry)
    {
        if (type is ResourceType.PatToken or ResourceType.AzureConnection)
        {
            return key => BuiltInSecretKeys.Any(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase));
        }

        if (type == ResourceType.Custom && customTypeKey is not null && registry.TryGet(customTypeKey, out var module))
        {
            var passwordKeys = module.Fields
                .Where(f => f.Kind == ResourceFieldKind.Password)
                .Select(f => f.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return passwordKeys.Count == 0 ? null : passwordKeys.Contains;
        }

        return null;
    }

    private static string Transform(string json, Func<string, JsonNode?, JsonNode?> visit)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception)
        {
            return json;
        }
        if (root is null) return json;

        foreach (var key in root.Select(p => p.Key).ToList())
        {
            var current = root[key];
            if (current is not null && current.GetValueKind() != System.Text.Json.JsonValueKind.String) continue;
            var replacement = visit(key, current);
            if (!ReferenceEquals(replacement, current)) root[key] = replacement;
        }

        return root.ToJsonString();
    }
}
