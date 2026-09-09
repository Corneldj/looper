namespace Looper.Api.Domain;

/// <summary>
/// A workflow is one workbench: its own resources, agents and metrics, wired together for one
/// purpose (a marketing loop, a support loop, a codebase). Everything that existed before
/// workflows lives in the default one; new workflows start empty. Events cross workflows —
/// a loop in one may listen for a topic raised in another.
/// </summary>
public class Workflow
{
    /// <summary>The workflow every pre-existing resource and agent belongs to. Cannot be deleted.</summary>
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<Resource> Resources { get; set; } = [];
    public List<LoopAgent> Agents { get; set; } = [];
}
