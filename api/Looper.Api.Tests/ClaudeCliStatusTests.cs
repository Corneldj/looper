using System.Net.Http.Json;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Looper.Api.Tests;

public class ClaudeCliStatusServiceTests
{
    private static ClaudeCliStatusService Create(string command, string installCommand = "echo installed") =>
        new(Options.Create(new LooperOptions { ClaudeCommand = command, ClaudeInstallCommand = installCommand }),
            NullLogger<ClaudeCliStatusService>.Instance);

    [Fact]
    public async Task Reports_available_with_a_version_for_a_runnable_command()
    {
        // /bin/echo accepts --version, exits 0 and prints a version line, standing in for the CLI.
        var service = Create("/bin/echo");

        var status = await service.GetStatusAsync(refresh: false, default);

        Assert.True(status.Available);
        Assert.False(string.IsNullOrWhiteSpace(status.Version));
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Reports_unavailable_with_the_launch_error_when_the_command_is_missing()
    {
        var service = Create("definitely-not-a-real-command-xyz");

        var status = await service.GetStatusAsync(refresh: false, default);

        Assert.False(status.Available);
        Assert.Null(status.Version);
        Assert.NotNull(status.Error);
    }

    [Fact]
    public async Task Reports_unavailable_when_the_command_exits_nonzero()
    {
        var service = Create("/bin/false");

        var status = await service.GetStatusAsync(refresh: false, default);

        Assert.False(status.Available);
        Assert.Contains("exited with code", status.Error);
    }

    [Fact]
    public async Task Install_runs_the_configured_installer_and_reprobes()
    {
        var service = Create("/bin/echo", installCommand: "echo install-went-fine");

        var (success, output, status) = await service.InstallAsync(default);

        Assert.True(success);
        Assert.Contains("install-went-fine", output);
        Assert.True(status.Available);
    }

    [Fact]
    public async Task Install_failure_is_reported_with_the_installer_output()
    {
        var service = Create("definitely-not-a-real-command-xyz", installCommand: "echo boom >&2; exit 1");

        var (success, output, status) = await service.InstallAsync(default);

        Assert.False(success);
        Assert.Contains("boom", output);
        Assert.False(status.Available);
    }
}

/// <summary>The status endpoint over real HTTP, with the CLI stubbed to a deterministic command.</summary>
public sealed class ClaudeStatusApiFactory : LooperApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Looper:ClaudeCommand", "definitely-not-a-real-command-xyz");
    }
}

public class ClaudeStatusApiTests(ClaudeStatusApiFactory factory) : IClassFixture<ClaudeStatusApiFactory>
{
    private sealed record StatusResponse(bool Available, string? Version, string Command, string? Error);

    [Fact]
    public async Task Status_endpoint_reports_the_missing_cli_with_its_configured_command()
    {
        var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<StatusResponse>("/api/system/claude-status", TestJson.Options);

        Assert.NotNull(status);
        Assert.False(status!.Available);
        Assert.Equal("definitely-not-a-real-command-xyz", status.Command);
        Assert.NotNull(status.Error);
    }
}
