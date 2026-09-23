using Xunit;

namespace Yaat.LayoutInspector.Tests;

/// <summary>
/// <c>--ticks</c> is repeatable and each value is <c>[LABEL=]PATH</c>. The split happens on
/// the first '=' only when the text before it looks like a label (no path separator, no '.').
/// </summary>
public class CliOptionsTicksTests
{
    [Fact]
    public void Repeated_ticks_flags_keep_their_labels_and_order()
    {
        CliOptions options = Parse("--ticks", "A=x.json", "--ticks", "y.json");

        Assert.Equal(2, options.TickSources.Count);
        Assert.Equal("A", options.TickSources[0].Label);
        Assert.Equal("x.json", options.TickSources[0].Path);
        Assert.Null(options.TickSources[1].Label);
        Assert.Equal("y.json", options.TickSources[1].Path);
    }

    [Theory]
    [InlineData("X:/tmp/a.json")]
    [InlineData("X:/tmp/a=b.json")]
    [InlineData(".tmp/a=b.json")]
    [InlineData("TODAY.D1=x.json")]
    public void Ticks_value_is_a_whole_path_when_it_does_not_start_with_a_label(string value)
    {
        CliOptions options = Parse("--ticks", value);

        Assert.Null(options.TickSources[0].Label);
        Assert.Equal(value, options.TickSources[0].Path);
    }

    [Fact]
    public void Ticks_label_splits_on_the_first_equals_only()
    {
        CliOptions options = Parse("--ticks", "A=x=y.json");

        Assert.Equal("A", options.TickSources[0].Label);
        Assert.Equal("x=y.json", options.TickSources[0].Path);
    }

    [Fact]
    public void Ticks_absent_leaves_no_sources()
    {
        CliOptions options = Parse("--tick-table");

        Assert.Empty(options.TickSources);
    }

    private static CliOptions Parse(params string[] args)
    {
        string[] all = ["sfo.geojson", .. args];
        Assert.True(CliOptions.TryParse(all, out CliOptions options, out string? error), error);
        return options;
    }
}
