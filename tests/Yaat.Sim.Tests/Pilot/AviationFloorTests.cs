using Xunit;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The aviation floor a shortened readback clause must keep: the same anchors as the canonical clause, in the same order —
/// numbers and runway designators, their type keywords, turn and altitude directions, "hold short" and "cross".
/// </summary>
public class AviationFloorTests
{
    [Theory]
    [InlineData("descend and maintain five thousand", "down to five thousand")]
    [InlineData("climb and maintain flight level two three zero", "climbing to flight level two three zero")]
    [InlineData("turn right heading two seven zero", "right heading two seven zero")]
    [InlineData("maintain two one zero knots", "speed two one zero knots")]
    [InlineData("turn thirty degrees left", "thirty degrees left")]
    [InlineData("proceed direct sunol", "direct sunol")]
    [InlineData("taxi to runway one left", "taxi runway one left")]
    [InlineData("taxi via bravo, cross runway two eight right", "via bravo, cross runway two eight right")]
    [InlineData(
        "taxi to runway one left via alpha, hold short of runway two eight right",
        "runway one left via alpha, hold short of runway two eight right"
    )]
    [InlineData("descend and maintain five thousand five hundred", "down to five thousand five hundred")]
    public void Holds_VariantKeepsTheFloor_Accepts(string canonical, string variant) => Assert.True(AviationFloor.Holds(canonical, variant));

    [Theory]
    [InlineData("turn right heading two seven zero", "right two seven zero")]
    [InlineData("turn right heading two seven zero", "left heading two seven zero")]
    [InlineData("climb and maintain one one thousand", "down to one one thousand")]
    [InlineData("descend and maintain five thousand", "down to five thousand, one zero thousand")]
    [InlineData("cross runway two eight right", "runway two eight right")]
    [InlineData("climb and maintain one one thousand", "climbing")]
    [InlineData("runway two eight right, cleared to land", "cleared to land two eight left")]
    [InlineData("heading two seven zero, maintain five thousand", "five thousand, heading two seven zero")]
    [InlineData("taxi to runway one left via alpha, hold short of runway two eight right", "runway one left via alpha")]
    [InlineData("climb and maintain flight level two three zero", "climbing to two three thousand")]
    [InlineData("descend and maintain five thousand", "down to five zero zero zero")]
    public void Holds_VariantBreaksTheFloor_Rejects(string canonical, string variant) => Assert.False(AviationFloor.Holds(canonical, variant));
}
