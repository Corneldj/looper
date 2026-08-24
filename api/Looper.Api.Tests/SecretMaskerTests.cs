using Looper.Api.Domain;
using Looper.Api.Features.Resources;
using Looper.Api.Modules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Looper.Api.Tests;

public class SecretMaskerTests
{
    private static ResourceModuleRegistry EmptyRegistry() => new(NullLogger<ResourceModuleRegistry>.Instance);

    [Fact]
    public void Masks_pat_token_value_on_read()
    {
        var resource = new Resource
        {
            Type = ResourceType.PatToken,
            ConfigJson = """{"envVar":"MY_PAT","value":"super-secret"}"""
        };

        var masked = SecretMasker.Mask(resource, EmptyRegistry());

        Assert.Contains(SecretMasker.Sentinel, masked);
        Assert.DoesNotContain("super-secret", masked);
        Assert.Contains("MY_PAT", masked); // non-secret keys pass through
    }

    [Fact]
    public void Leaves_non_credential_types_untouched()
    {
        var resource = new Resource
        {
            Type = ResourceType.Rule,
            ConfigJson = """{"text":"value of the rule"}"""
        };

        Assert.Equal(resource.ConfigJson, SecretMasker.Mask(resource, EmptyRegistry()));
    }

    [Fact]
    public void Preserves_stored_secret_when_sentinel_is_sent_back()
    {
        var stored = new Resource
        {
            Type = ResourceType.AzureConnection,
            ConfigJson = """{"tenantId":"t1","clientSecret":"the-real-secret"}"""
        };
        var incoming = $$"""{"tenantId":"t2","clientSecret":"{{SecretMasker.Sentinel}}"}""";

        var result = SecretMasker.PreserveSecrets(stored, incoming, EmptyRegistry());

        Assert.Contains("the-real-secret", result);
        Assert.Contains("t2", result); // non-secret change applied
        Assert.DoesNotContain(SecretMasker.Sentinel, result);
    }

    [Fact]
    public void Overwrites_secret_when_a_new_value_is_sent()
    {
        var stored = new Resource
        {
            Type = ResourceType.PatToken,
            ConfigJson = """{"envVar":"P","value":"old"}"""
        };

        var result = SecretMasker.PreserveSecrets(stored, """{"envVar":"P","value":"new"}""", EmptyRegistry());

        Assert.Contains("\"new\"", result);
        Assert.DoesNotContain("old", result);
    }

    [Fact]
    public void Masks_password_fields_of_dynamic_modules()
    {
        var registry = EmptyRegistry();
        registry.Register(new FakeModule());
        var resource = new Resource
        {
            Type = ResourceType.Custom,
            CustomTypeKey = "FakeThing",
            ConfigJson = """{"host":"db.example.com","password":"hunter2"}"""
        };

        var masked = SecretMasker.Mask(resource, registry);

        Assert.DoesNotContain("hunter2", masked);
        Assert.Contains("db.example.com", masked);
    }

    private sealed class FakeModule : IResourceTypeModule
    {
        public string TypeKey => "FakeThing";
        public string DisplayName => "Fake Thing";
        public string Icon => "🧪";
        public string Blurb => "Test module.";
        public IReadOnlyList<ResourceField> Fields { get; } =
        [
            new("host", "Host", ResourceFieldKind.Text, Required: true),
            new("password", "Password", ResourceFieldKind.Password, Required: true)
        ];
        public ResourceContribution Contribute(ResourceModuleContext context) => new();
    }
}
