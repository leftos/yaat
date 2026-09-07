using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Commands.Arguments;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Runway-versus-altitude is decided by <em>position</em>, not by the token alone: where both types are
/// candidates the runway binds first (so <c>MLT 15</c> is runway 15 and a field's 12/15/30/33 can be
/// named), and once the runway slot is bound the next slot is an altitude only, with the full
/// <see cref="AltitudeResolver"/> grammar — two-digit shorthand included (<c>MLT 28R 15</c> is 1,500 ft).
/// The exhaustive sweep below is the guard: every token shape either binds the way the rule says or is
/// rejected, in both positions.
/// </summary>
public class RunwayAndAltitudeArgumentTests
{
    /// <summary>The pattern modifier's shapes: nothing, a runway, an altitude, or a runway then an altitude.</summary>
    private static readonly CommandArgumentType[][] Shapes =
    [
        [],
        [CommandArgumentType.Runway],
        [CommandArgumentType.Altitude],
        [CommandArgumentType.Runway, CommandArgumentType.Altitude],
    ];

    /// <summary>Every numeric token 0–9999, its zero-padded two-digit form, each with no suffix and with L/C/R.</summary>
    private static IEnumerable<string> AllTokens()
    {
        foreach (var suffix in new[] { "", "L", "C", "R" })
        {
            for (int number = 0; number <= 9999; number++)
            {
                yield return $"{number}{suffix}";
                if (number < 10)
                {
                    yield return $"{number:D2}{suffix}";
                }
            }
        }
    }

    /// <summary>The rule as stated: one or two digits naming 01–36, with an optional L/C/R suffix.</summary>
    private static bool IsRunwayShaped(string token)
    {
        var digits = (token.Length > 0) && (token[^1] is 'L' or 'C' or 'R') ? token[..^1] : token;
        return (digits.Length is 1 or 2) && int.TryParse(digits, out int number) && (number is >= 1 and <= 36);
    }

    [Fact]
    public void AmbiguousPosition_BindsARunwayWhenTheTokenIsRunwayShaped_AnAltitudeOtherwise()
    {
        foreach (var token in AllTokens())
        {
            var resolution = CommandArgumentResolver.Resolve([token], Shapes);

            if (IsRunwayShaped(token))
            {
                Assert.True(resolution.Failure is null, $"'{token}' should bind as a runway but failed: {resolution.Failure}");
                Assert.True(resolution.Has(CommandArgumentType.Runway), $"'{token}' bound as {resolution.Values[0].Type}, expected Runway");
            }
            else if (AltitudeResolver.Resolve(token) is { } feet)
            {
                Assert.True(resolution.Failure is null, $"'{token}' should bind as an altitude but failed: {resolution.Failure}");
                Assert.True(resolution.Has(CommandArgumentType.Altitude), $"'{token}' bound as {resolution.Values[0].Type}, expected Altitude");
                Assert.Equal(feet, resolution.ValueOf<int>(CommandArgumentType.Altitude));
            }
            else
            {
                Assert.False(resolution.Failure is null, $"'{token}' is neither a runway nor an altitude and must be rejected");
            }
        }
    }

    [Fact]
    public void AfterABoundRunway_EveryAltitudeTokenBindsAsAnAltitude()
    {
        foreach (var token in AllTokens())
        {
            var resolution = CommandArgumentResolver.Resolve(["28R", token], Shapes);
            var expected = AltitudeResolver.Resolve(token);

            if (expected is { } feet)
            {
                Assert.True(resolution.Failure is null, $"'28R {token}' should bind {token} as an altitude but failed: {resolution.Failure}");
                Assert.Equal("28R", resolution.ValueOf<string>(CommandArgumentType.Runway));
                Assert.Equal(feet, resolution.ValueOf<int>(CommandArgumentType.Altitude));
            }
            else
            {
                Assert.False(resolution.Failure is null, $"'28R {token}' must be rejected — {token} is not an altitude");
            }
        }
    }

    [Theory]
    [InlineData("9", "09")]
    [InlineData("09", "09")]
    [InlineData("15", "15")]
    [InlineData("28R", "28R")]
    [InlineData("28r", "28R")]
    [InlineData("1L", "01L")]
    [InlineData("36", "36")]
    [InlineData("18C", "18C")]
    public void RunwayArgument_AcceptsRunwayShapes_Normalized(string token, string expected)
    {
        Assert.Equal(expected, RunwayArgument.TryParse(token));
    }

    [Theory]
    [InlineData("015")] // three digits — an altitude, never a runway
    [InlineData("1500")]
    [InlineData("100")]
    [InlineData("0")] // no runway 0
    [InlineData("37")] // designators stop at 36
    [InlineData("28X")] // X is not a runway suffix
    [InlineData("")]
    [InlineData("KOAK+010")]
    public void RunwayArgument_RejectsEverythingElse(string token)
    {
        Assert.Null(RunwayArgument.TryParse(token));
    }

    /// <summary>
    /// The altitude validator keeps the whole <see cref="AltitudeResolver"/> grammar — the two-digit
    /// shorthand included. Which of the two types claims a token is the resolver's job, not this one's.
    /// </summary>
    [Theory]
    [InlineData("15", 1500)]
    [InlineData("015", 1500)]
    [InlineData("020", 2000)]
    [InlineData("1500", 1500)]
    [InlineData("100", 10000)]
    public void AltitudeArgument_ReadsEveryAltitudeForm(string token, int expected)
    {
        Assert.Equal(expected, AltitudeArgument.TryParse(token));
    }

    [Theory]
    [InlineData("28R")]
    [InlineData("28X")]
    [InlineData("0")]
    [InlineData("")]
    public void AltitudeArgument_RejectsWhatIsNotAnAltitude(string token)
    {
        Assert.Null(AltitudeArgument.TryParse(token));
    }

    // ---------------------------------------------------------------------------------------------
    // Overload resolution
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_NoTokens_MatchesTheEmptyShape()
    {
        var resolution = CommandArgumentResolver.Resolve([], Shapes);

        Assert.Null(resolution.Failure);
        Assert.False(resolution.Has(CommandArgumentType.Runway));
        Assert.False(resolution.Has(CommandArgumentType.Altitude));
    }

    [Fact]
    public void Resolve_RunwayOnly_PicksTheRunwayOverload()
    {
        var resolution = CommandArgumentResolver.Resolve(["33"], Shapes);

        Assert.Null(resolution.Failure);
        Assert.Equal("33", resolution.ValueOf<string>(CommandArgumentType.Runway));
        Assert.False(resolution.Has(CommandArgumentType.Altitude));
    }

    [Fact]
    public void Resolve_ThreeDigitToken_PicksTheAltitudeOverload()
    {
        var resolution = CommandArgumentResolver.Resolve(["015"], Shapes);

        Assert.Null(resolution.Failure);
        Assert.False(resolution.Has(CommandArgumentType.Runway));
        Assert.Equal(1500, resolution.ValueOf<int>(CommandArgumentType.Altitude));
    }

    [Fact]
    public void Resolve_RunwayThenTwoDigitAltitude_FillsBothSlots()
    {
        var resolution = CommandArgumentResolver.Resolve(["28R", "15"], Shapes);

        Assert.Null(resolution.Failure);
        Assert.Equal("28R", resolution.ValueOf<string>(CommandArgumentType.Runway));
        Assert.Equal(1500, resolution.ValueOf<int>(CommandArgumentType.Altitude));
        Assert.Equal("15", resolution.TokenOf(CommandArgumentType.Altitude));
    }

    /// <summary>Two runway-shaped tokens: the second slot is an altitude, and 28L is not one.</summary>
    [Fact]
    public void Resolve_SecondRunwayDesignator_FailsNamingThePosition()
    {
        var resolution = CommandArgumentResolver.Resolve(["28R", "28L"], Shapes);

        Assert.NotNull(resolution.Failure);
        Assert.Contains("'28L'", resolution.Failure, StringComparison.Ordinal);
        Assert.Contains("after runway 28R", resolution.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnreadableFirstToken_NamesBothCandidateTypes()
    {
        var resolution = CommandArgumentResolver.Resolve(["BOGUS"], Shapes);

        Assert.NotNull(resolution.Failure);
        Assert.Contains("BOGUS", resolution.Failure, StringComparison.Ordinal);
        Assert.Contains("runway", resolution.Failure, StringComparison.Ordinal);
        Assert.Contains("altitude", resolution.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_MoreTokensThanAnyOverloadTakes_Fails()
    {
        var resolution = CommandArgumentResolver.Resolve(["28R", "015", "020"], Shapes);

        Assert.NotNull(resolution.Failure);
        Assert.Contains("'020'", resolution.Failure, StringComparison.Ordinal);
        Assert.Contains("no more arguments", resolution.Failure, StringComparison.Ordinal);
    }

    /// <summary>An overload that still needs an argument is a failure, not a silent partial match.</summary>
    [Fact]
    public void Resolve_ShapeNeedingMoreArguments_Fails()
    {
        CommandArgumentType[][] runwayThenAltitudeOnly =
        [
            [CommandArgumentType.Runway, CommandArgumentType.Altitude],
        ];

        var resolution = CommandArgumentResolver.Resolve(["28R"], runwayThenAltitudeOnly);

        Assert.NotNull(resolution.Failure);
        Assert.Contains("altitude", resolution.Failure, StringComparison.Ordinal);
    }
}
