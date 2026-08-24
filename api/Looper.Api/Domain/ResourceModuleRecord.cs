namespace Looper.Api.Domain;

/// <summary>
/// A dynamically created resource type: the generated C# source plus the compiled DLL
/// (stored in the modules directory) that is loaded into the registry at startup.
/// </summary>
public class ResourceModuleRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TypeKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Blurb { get; set; } = "";

    /// <summary>The full C# source, kept for transparency and regeneration.</summary>
    public string SourceCode { get; set; } = "";

    /// <summary>File name of the compiled assembly inside the modules directory.</summary>
    public string DllFileName { get; set; } = "";

    /// <summary>The user's natural-language request, when the module was AI-generated.</summary>
    public string? GenerationPrompt { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
