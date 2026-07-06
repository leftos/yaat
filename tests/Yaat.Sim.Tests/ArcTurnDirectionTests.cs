using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="GeoMath.ResolveArcTurnRight"/> — the DME/RF arc turn-direction resolver.
/// An explicit ARINC 424 turn code is honored; a null code defaults to the minor arc (≤ 180° sweep)
/// so a wrong-way (reflex) DME arc is never drawn/flown.
/// </summary>
public class ArcTurnDirectionTests
{
    [Theory]
    [InlineData('R', 0.0, 270.0)] // reflex-right geometry, but explicit R is honored
    [InlineData('r', 10.0, 350.0)]
    public void ExplicitRight_AlwaysTurnsRight(char turn, double start, double end)
    {
        Assert.True(GeoMath.ResolveArcTurnRight(turn, start, end));
    }

    [Theory]
    [InlineData('L', 0.0, 90.0)] // minor arc is right, but explicit L is honored
    [InlineData('l', 350.0, 10.0)]
    public void ExplicitLeft_AlwaysTurnsLeft(char turn, double start, double end)
    {
        Assert.False(GeoMath.ResolveArcTurnRight(turn, start, end));
    }

    [Theory]
    [InlineData(0.0, 90.0)] // right sweep 90° ≤ 180 → minor arc is right
    [InlineData(350.0, 10.0)] // right sweep 20° across north → right
    [InlineData(0.0, 180.0)] // exactly 180° → right (boundary)
    public void NullTurn_PicksMinorArc_Right(double start, double end)
    {
        Assert.True(GeoMath.ResolveArcTurnRight(null, start, end));
    }

    [Theory]
    [InlineData(0.0, 270.0)] // right sweep 270° > 180 → minor arc is left
    [InlineData(10.0, 350.0)] // right sweep 340° > 180 → left
    [InlineData(0.0, 181.0)]
    public void NullTurn_PicksMinorArc_Left(double start, double end)
    {
        Assert.False(GeoMath.ResolveArcTurnRight(null, start, end));
    }
}
