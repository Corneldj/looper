using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;

namespace Looper.Api.Tests;

// ============================================================================
// Run limits: Settings first, appsettings as the fallback, and no value — stored
// or configured — can turn into an instant timeout or a run that throws at start.
// ============================================================================

public class RunLimitsTests
{
    private static readonly LooperOptions Fallback = new() { RunTimeoutMinutes = 30 };

    private static AppSetting Row(string key, string value) => new() { Key = key, Value = value };

    [Fact]
    public void Appsettings_supplies_the_limits_when_nothing_is_stored()
    {
        var limits = RunLimits.FromRows([], Fallback);

        Assert.Equal(30, limits.TimeoutMinutes);
        Assert.True(limits.HasTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), limits.Timeout);
        Assert.Null(limits.DefaultMaxBudgetUsd);
        Assert.Equal("time=30 min, default budget=none", limits.Describe());
    }

    [Fact]
    public void Stored_values_override_appsettings_and_zero_means_no_limit()
    {
        var limits = RunLimits.FromRows(
            [Row(AppSettingKeys.RunTimeoutMinutes, "0"), Row(AppSettingKeys.DefaultMaxBudgetUsd, "2.50")],
            Fallback);

        Assert.Equal(0, limits.TimeoutMinutes);
        Assert.False(limits.HasTimeout);
        Assert.Null(limits.Timeout);
        Assert.Equal(2.5m, limits.DefaultMaxBudgetUsd);
        Assert.Equal("time=no limit, default budget=$2.5", limits.Describe());

        Assert.Equal(240, RunLimits.FromRows([Row(AppSettingKeys.RunTimeoutMinutes, "240")], Fallback).TimeoutMinutes);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData("30 min")]
    public void A_stored_time_limit_that_does_not_parse_falls_back_to_appsettings(string stored)
    {
        var limits = RunLimits.FromRows([Row(AppSettingKeys.RunTimeoutMinutes, stored)], Fallback);
        Assert.Equal(30, limits.TimeoutMinutes);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("")]
    public void A_stored_budget_that_is_not_a_positive_amount_means_no_default(string stored)
    {
        var limits = RunLimits.FromRows([Row(AppSettingKeys.DefaultMaxBudgetUsd, stored)], Fallback);
        Assert.Null(limits.DefaultMaxBudgetUsd);
    }

    [Fact]
    public void Appsettings_at_or_below_zero_means_no_limit_rather_than_a_crash()
    {
        Assert.False(RunLimits.FromRows([], new LooperOptions { RunTimeoutMinutes = 0 }).HasTimeout);
        Assert.False(RunLimits.FromRows([], new LooperOptions { RunTimeoutMinutes = -1 }).HasTimeout);
    }

    // CancellationTokenSource refuses delays past 0xFFFFFFFE ms; without the clamp a generous
    // appsettings value would throw at the start of every run.
    [Fact]
    public void Values_past_what_a_timer_accepts_are_clamped_so_the_run_can_start()
    {
        var fromConfig = RunLimits.FromRows([], new LooperOptions { RunTimeoutMinutes = 100_000 });
        var fromStore = RunLimits.FromRows([Row(AppSettingKeys.RunTimeoutMinutes, "999999")], Fallback);

        Assert.Equal(RunLimits.MaxTimeoutMinutes, fromConfig.TimeoutMinutes);
        Assert.Equal(RunLimits.MaxTimeoutMinutes, fromStore.TimeoutMinutes);
        using var source = new CancellationTokenSource(fromConfig.Timeout!.Value);
        Assert.False(source.IsCancellationRequested);
    }
}
