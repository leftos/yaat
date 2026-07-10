using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// The activation gate: a generator normally follows its authored [startTimeOffset, maxTime] window, but an
/// instructor's Active toggle overrides that window in both directions and is never latched, so a generator
/// can be switched back on after its window has expired.
/// </summary>
public class GeneratorActivationTests
{
    private static ScenarioGeneratorConfig Config(int startTimeOffset, int? maxTime, bool? enabled) =>
        new()
        {
            Id = "gen",
            StartTimeOffset = startTimeOffset,
            MaxTime = maxTime,
            Enabled = enabled,
        };

    [Theory]
    [InlineData(0, 0, 1000, true)] // inside the window
    [InlineData(600, 500, 1200, false)] // before it opens
    [InlineData(600, 600, 1200, true)] // exactly when it opens
    [InlineData(600, 1200, 1200, true)] // exactly when it closes
    [InlineData(600, 1201, 1200, false)] // after it closes
    public void WithoutOverride_FollowsTheAuthoredWindow(int startTimeOffset, double elapsed, int maxTime, bool expected) =>
        Assert.Equal(expected, GeneratorActivation.IsActive(Config(startTimeOffset, maxTime, enabled: null), elapsed));

    [Fact]
    public void WithoutOverride_OmittedMaxTime_NeverCloses() =>
        Assert.True(GeneratorActivation.IsActive(Config(startTimeOffset: 0, maxTime: null, enabled: null), elapsedSeconds: 999_999));

    [Fact]
    public void EnabledTrue_ActivatesBeforeTheWindowOpens() =>
        Assert.True(GeneratorActivation.IsActive(Config(startTimeOffset: 600, maxTime: 1200, enabled: true), elapsedSeconds: 10));

    [Fact]
    public void EnabledTrue_ActivatesAfterTheWindowCloses() =>
        Assert.True(GeneratorActivation.IsActive(Config(startTimeOffset: 0, maxTime: 1200, enabled: true), elapsedSeconds: 5000));

    [Fact]
    public void EnabledFalse_DeactivatesInsideTheWindow() =>
        Assert.False(GeneratorActivation.IsActive(Config(startTimeOffset: 0, maxTime: 1200, enabled: false), elapsedSeconds: 600));

    [Fact]
    public void EnabledFalse_StaysOffOutsideTheWindow() =>
        Assert.False(GeneratorActivation.IsActive(Config(startTimeOffset: 0, maxTime: 1200, enabled: false), elapsedSeconds: 5000));

    /// <summary>Every generator kind shares the gate through <see cref="IGeneratorConfig"/>.</summary>
    [Fact]
    public void AppliesToVfrArrivalAndOverflightGenerators()
    {
        var vfrArrival = new VfrArrivalGeneratorConfig
        {
            Id = "vfr",
            StartTimeOffset = 300,
            MaxTime = 900,
        };
        var overflight = new OverflightGeneratorConfig
        {
            Id = "of",
            StartTimeOffset = 300,
            MaxTime = 900,
            Enabled = true,
        };

        Assert.False(GeneratorActivation.IsActive(vfrArrival, elapsedSeconds: 100));
        Assert.True(GeneratorActivation.IsActive(vfrArrival, elapsedSeconds: 600));
        Assert.False(GeneratorActivation.IsActive(vfrArrival, elapsedSeconds: 1000));

        Assert.True(GeneratorActivation.IsActive(overflight, elapsedSeconds: 100));
        Assert.True(GeneratorActivation.IsActive(overflight, elapsedSeconds: 1000));
    }
}
