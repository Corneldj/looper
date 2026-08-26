using Looper.Api.Domain;

namespace Looper.Api.Features.Workspaces;

public sealed record WorkspaceDto(
    Guid Id,
    Guid ResourceId,
    string PoolName,
    Guid? AgentId,
    string? AgentName,
    string Unit,
    string Path,
    WorkspaceStatus Status,
    string ContextBrief,
    DateTime CreatedAtUtc,
    DateTime LastUsedAtUtc,
    DateTime? DoneAtUtc,
    DateTime? CleanedAtUtc);

public static class WorkspaceMapper
{
    public static WorkspaceDto ToDto(this ManagedWorkspace workspace, string poolName, string? agentName) => new(
        workspace.Id,
        workspace.ResourceId,
        poolName,
        workspace.AgentId,
        agentName,
        workspace.Unit,
        workspace.Path,
        workspace.Status,
        workspace.ContextBrief,
        workspace.CreatedAtUtc,
        workspace.LastUsedAtUtc,
        workspace.DoneAtUtc,
        workspace.CleanedAtUtc);
}

/// <summary>Claim response: the path plus whether this call created it or found the existing claim.</summary>
public sealed record WorkspaceClaimDto(
    Guid Id,
    string Unit,
    string Path,
    bool Created,
    string Brief);
