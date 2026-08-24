using Looper.Api.Infrastructure.Execution;

namespace Looper.Api.Tests;

public class ClaudeCliOutputParsingTests
{
    [Fact]
    public void Parses_a_clean_json_result()
    {
        using var doc = ClaudeCliExecutor.ExtractResultObject("""{"result":"done","total_cost_usd":0.12}""");

        Assert.NotNull(doc);
        Assert.Equal("done", doc!.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void Skips_leading_noise_including_stray_braces()
    {
        var stdout = "npm warn config {legacy} something\nDebugger attached.\n" +
                     """{"result":"ok","num_turns":3}""";

        using var doc = ClaudeCliExecutor.ExtractResultObject(stdout);

        Assert.NotNull(doc);
        Assert.Equal(3, doc!.RootElement.GetProperty("num_turns").GetInt32());
    }

    [Fact]
    public void Tolerates_trailing_output_after_the_result()
    {
        var stdout = """{"result":"ok","is_error":false}""" + "\nSome wrapper printed this afterwards\n";

        using var doc = ClaudeCliExecutor.ExtractResultObject(stdout);

        Assert.NotNull(doc);
        Assert.False(doc!.RootElement.GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public void Prefers_the_object_with_result_markers_over_other_json()
    {
        var stdout = """{"level":"warn","msg":"unrelated json log line"}""" + "\n" +
                     """{"result":"the real one","total_cost_usd":1.5}""";

        using var doc = ClaudeCliExecutor.ExtractResultObject(stdout);

        Assert.NotNull(doc);
        Assert.Equal("the real one", doc!.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void Returns_null_when_no_json_exists()
    {
        Assert.Null(ClaudeCliExecutor.ExtractResultObject("plain text, no json at all"));
        Assert.Null(ClaudeCliExecutor.ExtractResultObject(""));
    }
}
