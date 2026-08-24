using Looper.Api.Modules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Looper.Api.Tests;

public class ResourceModuleCompilerTests
{
    public const string SampleModuleSource = """
        using Looper.Api.Modules;

        public sealed class SlackWebhookModule : IResourceTypeModule
        {
            public string TypeKey => "SlackWebhook";
            public string DisplayName => "Slack Webhook";
            public string Icon => "💬";
            public string Blurb => "Lets the agent post updates to a Slack channel.";

            public IReadOnlyList<ResourceField> Fields { get; } =
            [
                new("webhookUrl", "Webhook URL", ResourceFieldKind.Password, Required: true),
                new("channel", "Channel", ResourceFieldKind.Text)
            ];

            public ResourceContribution Contribute(ResourceModuleContext context)
            {
                var contribution = new ResourceContribution();
                var url = context.GetString("webhookUrl");
                if (string.IsNullOrWhiteSpace(url)) return contribution;

                contribution.EnvironmentVariables["SLACK_WEBHOOK_URL"] = url;
                contribution.PromptSections.Add("A Slack webhook is available via SLACK_WEBHOOK_URL.");
                return contribution;
            }
        }
        """;

    [Fact]
    public void Compiles_a_valid_module_and_reports_its_type_key()
    {
        var result = new ResourceModuleCompiler().Compile(SampleModuleSource, "Looper.Module.Test1");

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal("SlackWebhook", result.TypeKey);
        Assert.NotNull(result.Assembly);
        Assert.NotEmpty(result.Assembly!);
    }

    [Fact]
    public void Reports_compiler_errors_for_broken_source()
    {
        var result = new ResourceModuleCompiler().Compile("public class Broken {", "Looper.Module.Test2");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Rejects_source_without_a_module_implementation()
    {
        var result = new ResourceModuleCompiler().Compile("public class NotAModule { }", "Looper.Module.Test3");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("IResourceTypeModule"));
    }

    [Fact]
    public void Compiled_module_loads_and_contributes()
    {
        var compiled = new ResourceModuleCompiler().Compile(SampleModuleSource, "Looper.Module.Test4");
        Assert.True(compiled.Success, string.Join("\n", compiled.Errors));

        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        Directory.CreateDirectory(registry.ModulesDirectory);
        var dllPath = Path.Combine(registry.ModulesDirectory, $"test-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(dllPath, compiled.Assembly!);
        try
        {
            var module = registry.LoadFromFile(dllPath);

            Assert.Equal("SlackWebhook", module.TypeKey);
            Assert.True(registry.TryGet("slackwebhook", out _)); // lookup is case-insensitive

            var contribution = module.Contribute(new ResourceModuleContext("""{"webhookUrl":"https://hooks.slack/x"}"""));
            Assert.Equal("https://hooks.slack/x", contribution.EnvironmentVariables["SLACK_WEBHOOK_URL"]);
            Assert.Single(contribution.PromptSections);

            // Missing config values must degrade gracefully, not throw.
            var empty = module.Contribute(new ResourceModuleContext("{}"));
            Assert.Empty(empty.EnvironmentVariables);
        }
        finally
        {
            registry.Remove("SlackWebhook");
            try { File.Delete(dllPath); } catch (IOException) { /* held by the loaded assembly on some platforms */ }
        }
    }

    [Fact]
    public void Module_context_reads_are_case_insensitive_and_typed()
    {
        var context = new ResourceModuleContext("""{"Host":"h","port":5432,"ssl":true}""");

        Assert.Equal("h", context.GetString("host"));
        Assert.Equal(5432, context.GetNumber("PORT"));
        Assert.True(context.GetBool("ssl"));
        Assert.Null(context.GetString("missing"));
        Assert.False(context.GetBool("missing"));
    }
}
