using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Speech;

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

    [Theory]
    [InlineData("climb and? maintain {alt}", CanonicalCommandType.ClimbMaintain, "{alt}", FrequencyLoadBand.Busy)]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "speed {spd}", FrequencyLoadBand.Normal)]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "speed {spd} knots", FrequencyLoadBand.Busy)]
    [InlineData("cleared to land runway {rwy}", CanonicalCommandType.ClearedToLand, "cleared to land {rwy}", FrequencyLoadBand.Busy)]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "speeding up to {spd} knots", FrequencyLoadBand.Quiet)]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "slowing down to {spd} knots", FrequencyLoadBand.Quiet)]
    // Repeats merge after omission: with the runway dropped, the two lefts read as one.
    [InlineData("left runway left {rwy}", CanonicalCommandType.ClearedToLand, "left left {rwy}", FrequencyLoadBand.Busy)]
    // A group-form placeholder is its capture: the same value, spoken in the busy number form.
    [InlineData("climb and? maintain {alt}", CanonicalCommandType.ClimbMaintain, "{alt:group}", FrequencyLoadBand.Busy)]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "speed {spd:group}", FrequencyLoadBand.Busy)]
    // The verb drop: a busy pilot may drop climb or descend, keeping any qualifier.
    [InlineData("descend and? maintain {alt}", CanonicalCommandType.DescendMaintain, "{alt}", FrequencyLoadBand.Busy)]
    [InlineData("climb and? maintain at or above {alt}", CanonicalCommandType.ClimbMaintain, "at or above {alt}", FrequencyLoadBand.Busy)]
    public void HoldsTemplate_VariantKeepsTheFloor_Accepts(
        string canonicalPattern,
        CanonicalCommandType type,
        string template,
        FrequencyLoadBand band
    ) => Assert.True(AviationFloor.HoldsTemplate(canonicalPattern.Split(' '), type, new PilotVariant(template, PilotVariantTags.None), band));

    [Theory]
    [InlineData("climb and? maintain {alt}", CanonicalCommandType.ClimbMaintain, "{alt}", FrequencyLoadBand.Normal)]
    [InlineData("climb and? maintain {alt}", CanonicalCommandType.ClimbMaintain, "descending {alt}", FrequencyLoadBand.Busy)]
    [InlineData("fly heading {hdg}", CanonicalCommandType.FlyHeading, "heading", FrequencyLoadBand.Busy)]
    [InlineData("proceed direct to? {fix}", CanonicalCommandType.DirectTo, "direct {fix} {alt}", FrequencyLoadBand.Busy)]
    [InlineData("{spd} knots until {fix}", CanonicalCommandType.Speed, "{spd} knots", FrequencyLoadBand.Busy)]
    [InlineData("maintain at or above {alt}", CanonicalCommandType.ClimbMaintain, "maintain {alt}", FrequencyLoadBand.Busy)]
    [InlineData("turn left heading {hdg}", CanonicalCommandType.TurnLeft, "right heading {hdg}", FrequencyLoadBand.Busy)]
    [InlineData("hold short runway {rwy}", CanonicalCommandType.HoldShort, "hold short {rwy}", FrequencyLoadBand.Busy)]
    [InlineData("cross runway {rwy}", CanonicalCommandType.CrossRunway, "cross {rwy}", FrequencyLoadBand.Busy)]
    [InlineData("taxi runway {rwy}", CanonicalCommandType.Taxi, "taxi {rwy}", FrequencyLoadBand.Busy)]
    [InlineData("line up and wait runway {rwy}", CanonicalCommandType.LineUpAndWait, "lining up runway {rwy}", FrequencyLoadBand.Busy)]
    [InlineData("expect {app} approach", CanonicalCommandType.ExpectApproach, "{app} approach", FrequencyLoadBand.Busy)]
    [InlineData("when able direct to? {fix}", CanonicalCommandType.AppendDirectTo, "direct {fix}", FrequencyLoadBand.Busy)]
    [InlineData("cross {fix} at {alt}", CanonicalCommandType.CrossFix, "cross {fix} {alt}", FrequencyLoadBand.Busy)]
    // The group form is busy only.
    [InlineData("climb and? maintain {alt}", CanonicalCommandType.ClimbMaintain, "{alt:group}", FrequencyLoadBand.Normal)]
    [InlineData("climb and? maintain {alt}", CanonicalCommandType.ClimbMaintain, "climb and maintain {alt:group}", FrequencyLoadBand.Quiet)]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "{spd:group} knots", FrequencyLoadBand.Normal)]
    [InlineData("climb and? maintain {alt}", CanonicalCommandType.ClimbMaintain, "climb and maintain {alt:group...}", FrequencyLoadBand.Normal)]
    // The verb drop: never in quiet or normal, never swapped for the other direction, never taking a qualifier, cruise,
    // cross, except maintain or expedite with it.
    [InlineData("climb and? maintain {alt}", CanonicalCommandType.ClimbMaintain, "{alt}", FrequencyLoadBand.Quiet)]
    [InlineData("descend and? maintain {alt}", CanonicalCommandType.DescendMaintain, "climbing {alt}", FrequencyLoadBand.Busy)]
    [InlineData("climb and? maintain at or above {alt}", CanonicalCommandType.ClimbMaintain, "{alt}", FrequencyLoadBand.Busy)]
    [InlineData("descend and? maintain at or below {alt}", CanonicalCommandType.DescendMaintain, "{alt}", FrequencyLoadBand.Busy)]
    [InlineData("cruise {alt}", CanonicalCommandType.Cruise, "{alt}", FrequencyLoadBand.Busy)]
    [InlineData("cross {fix} at {alt}", CanonicalCommandType.CrossFix, "{fix} at {alt}", FrequencyLoadBand.Busy)]
    [InlineData("climb via sid except maintain {alt}", CanonicalCommandType.ClimbVia, "climb via sid {alt}", FrequencyLoadBand.Busy)]
    [InlineData("expedite climb to? {alt}", CanonicalCommandType.Expedite, "{alt}", FrequencyLoadBand.Busy)]
    public void HoldsTemplate_VariantBreaksTheFloor_Rejects(
        string canonicalPattern,
        CanonicalCommandType type,
        string template,
        FrequencyLoadBand band
    ) => Assert.False(AviationFloor.HoldsTemplate(canonicalPattern.Split(' '), type, new PilotVariant(template, PilotVariantTags.None), band));
}
