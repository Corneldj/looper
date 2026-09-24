using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;

namespace Looper.Api.Tests;

// ============================================================================
// The File resource: one file by exact path. The agent gets its folder, its path in an
// env var and a prompt section; contents can be inlined; read-only is stated twice; and a
// missing file fails the run before the model starts unless the resource creates it.
// ============================================================================

public sealed class FileModuleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"looper-file-{Guid.NewGuid():N}");
    private readonly FileModule _module = new();

    public FileModuleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Json(string path, string extra = "") =>
        $$"""{"path":"{{path.Replace("\\", "\\\\")}}"{{extra}}}""";

    private static ResourceModuleContext Context(string json, string name = "Company brief", string description = "") =>
        new(json, Guid.NewGuid(), "Agent", Guid.NewGuid(), name, description);

    [Fact]
    public void Grants_the_folder_the_path_and_a_section_that_says_to_read_it()
    {
        var file = Path.Combine(_dir, "company_description.md");
        File.WriteAllText(file, "JBT Marel makes food processing equipment.");

        var contribution = _module.Contribute(Context(Json(file), description: "What the company does"));

        Assert.Equal(file, contribution.EnvironmentVariables["LOOPER_FILE_COMPANY_BRIEF"]);
        Assert.Contains(_dir, contribution.AdditionalDirectories);
        var section = Assert.Single(contribution.PromptSections);
        Assert.Contains("FILE \"Company brief\" — What the company does.", section);
        Assert.Contains($"Path: {file} (also $LOOPER_FILE_COMPANY_BRIEF).", section);
        Assert.Contains("Read it before you start.", section);
        Assert.DoesNotContain("food processing", section);      // not inlined unless asked
        Assert.Empty(contribution.SystemPromptRules);
    }

    [Fact]
    public void Inline_puts_the_contents_in_the_prompt_bounded_and_never_binary()
    {
        var text = Path.Combine(_dir, "brief.md");
        File.WriteAllText(text, "Line one.\nLine two.");
        Assert.Contains("---\nLine one.\nLine two.\n---",
            _module.Contribute(Context(Json(text, ",\"inline\":true"))).PromptSections[0]);

        var big = Path.Combine(_dir, "big.txt");
        File.WriteAllText(big, new string('x', FileModule.InlineLimit + 500));
        var section = _module.Contribute(Context(Json(big, ",\"inline\":true"))).PromptSections[0];
        Assert.Contains("[… truncated — read the rest from disk]", section);
        Assert.True(section.Length < FileModule.InlineLimit + 600);

        var binary = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(binary, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        Assert.Contains("[binary file", _module.Contribute(Context(Json(binary, ",\"inline\":true"))).PromptSections[0]);

        var empty = Path.Combine(_dir, "empty.md");
        File.WriteAllText(empty, "");
        Assert.Contains("[the file is empty]", _module.Contribute(Context(Json(empty, ",\"inline\":true"))).PromptSections[0]);
    }

    [Fact]
    public void Read_only_is_stated_in_the_section_and_as_a_standing_rule()
    {
        var file = Path.Combine(_dir, "policy.md");
        File.WriteAllText(file, "x");

        var contribution = _module.Contribute(Context(Json(file, ",\"readOnly\":true")));

        Assert.Contains("READ-ONLY", contribution.PromptSections[0]);
        var rule = Assert.Single(contribution.SystemPromptRules);
        Assert.Contains(file, rule);
        Assert.Contains("never modify, rename or delete", rule);
    }

    [Fact]
    public void A_missing_file_fails_the_run_before_it_starts_unless_the_resource_creates_it()
    {
        var missing = Path.Combine(_dir, "reports", "weekly.md");

        var ex = Assert.Throws<InvalidOperationException>(() => _module.PrepareRun(Context(Json(missing))));
        Assert.Contains("does not exist", ex.Message);
        Assert.Contains("Create if missing", ex.Message);

        _module.PrepareRun(Context(Json(missing, ",\"createIfMissing\":true")));
        Assert.True(File.Exists(missing));
        Assert.Equal("", File.ReadAllText(missing));

        _module.PrepareRun(Context(Json(missing)));              // exists now: nothing to do, no throw
    }

    [Fact]
    public void A_folder_a_relative_path_or_no_path_is_refused_with_a_reason()
    {
        Assert.Contains("is a folder",
            Assert.Throws<InvalidOperationException>(() => _module.PrepareRun(Context(Json(_dir)))).Message);
        Assert.Contains("absolute path",
            Assert.Throws<InvalidOperationException>(() => _module.PrepareRun(Context(Json("notes/brief.md")))).Message);
        Assert.Contains("No file path",
            Assert.Throws<InvalidOperationException>(() => _module.PrepareRun(Context("{}"))).Message);
        Assert.Empty(_module.Contribute(Context("{}")).PromptSections);
    }

    [Theory]
    [InlineData("Company brief", "LOOPER_FILE_COMPANY_BRIEF")]
    [InlineData("  weekly-report.md ", "LOOPER_FILE_WEEKLY_REPORT_MD")]
    [InlineData("###", "LOOPER_FILE_FILE")]
    public void The_env_var_is_derived_from_the_resource_name(string name, string expected)
    {
        Assert.Equal(expected, FileModule.EnvVarName(name));
    }
}
