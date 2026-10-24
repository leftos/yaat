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

    [Theory]
    // A capture-less "wilco" is a busy readback of resume normal speed and reduce to final approach speed only.
    [InlineData("resume normal speed", CanonicalCommandType.ResumeNormalSpeed, "wilco", PilotVariantTags.None, FrequencyLoadBand.Busy)]
    [InlineData(
        "reduce to final approach speed",
        CanonicalCommandType.ReduceToFinalApproachSpeed,
        "wilco",
        PilotVariantTags.None,
        FrequencyLoadBand.Busy
    )]
    [InlineData(
        "reduce to final approach speed",
        CanonicalCommandType.ReduceToFinalApproachSpeed,
        "{cs}, wilco",
        PilotVariantTags.None,
        FrequencyLoadBand.Busy
    )]
    // A row may place the callsign once, anywhere; it is not a capture of the canonical.
    [InlineData(
        "climb and? maintain {alt}",
        CanonicalCommandType.ClimbMaintain,
        "alright, we'll climb and maintain {alt}, {cs}, thanks",
        PilotVariantTags.None,
        FrequencyLoadBand.Quiet
    )]
    // The speed-change rows: plain SPD, quiet or normal, "knots" right after the speed.
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "slowing to {spd} knots", PilotVariantTags.SlowingOnly, FrequencyLoadBand.Quiet)]
    [InlineData(
        "maintain {spd} knots",
        CanonicalCommandType.Speed,
        "speeding up to {spd} knots",
        PilotVariantTags.SpeedingOnly,
        FrequencyLoadBand.Normal
    )]
    [InlineData(
        "maintain {spd} knots",
        CanonicalCommandType.Speed,
        "maintaining {spd} knots",
        PilotVariantTags.MaintainingOnly,
        FrequencyLoadBand.Quiet
    )]
    // Only a courtesy follows a callsign the row places.
    [InlineData(
        "turn left heading {hdg}",
        CanonicalCommandType.TurnLeft,
        "left heading {hdg}, {cs}, thanks",
        PilotVariantTags.None,
        FrequencyLoadBand.Quiet
    )]
    [InlineData(
        "fly heading {hdg}",
        CanonicalCommandType.FlyHeading,
        "fly heading {hdg}, {cs}, good day",
        PilotVariantTags.None,
        FrequencyLoadBand.Quiet
    )]
    public void HoldsTemplate_TaggedOrPlacedVariantKeepsTheFloor_Accepts(
        string canonicalPattern,
        CanonicalCommandType type,
        string template,
        PilotVariantTags tags,
        FrequencyLoadBand band
    ) => Assert.True(AviationFloor.HoldsTemplate(canonicalPattern.Split(' '), type, new PilotVariant(template, tags), band));

    [Theory]
    // "wilco" never stands for delete speed restrictions (7110.65 5-7-4.a NOTE vs 5-7-4.d), a speed, or outside busy.
    [InlineData("delete speed restrictions", CanonicalCommandType.DeleteSpeedRestrictions, "wilco", PilotVariantTags.None, FrequencyLoadBand.Busy)]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "wilco", PilotVariantTags.None, FrequencyLoadBand.Busy)]
    [InlineData("resume normal speed", CanonicalCommandType.ResumeNormalSpeed, "wilco", PilotVariantTags.None, FrequencyLoadBand.Normal)]
    // The callsign at most once.
    [InlineData(
        "climb and? maintain {alt}",
        CanonicalCommandType.ClimbMaintain,
        "{cs}, climbing {alt}, {cs}, thanks",
        PilotVariantTags.None,
        FrequencyLoadBand.Quiet
    )]
    // A speed-change row is quiet or normal SPD only, and always says "knots" after the speed.
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "slowing to {spd} knots", PilotVariantTags.SlowingOnly, FrequencyLoadBand.Busy)]
    [InlineData(
        "climb and? maintain {alt}",
        CanonicalCommandType.ClimbMaintain,
        "climbing {alt}",
        PilotVariantTags.SlowingOnly,
        FrequencyLoadBand.Quiet
    )]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "slowing to {spd}", PilotVariantTags.SlowingOnly, FrequencyLoadBand.Quiet)]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "slowing, speed {spd}", PilotVariantTags.SlowingOnly, FrequencyLoadBand.Quiet)]
    // The speed-change verb matches its tag, and is never spoken untagged.
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "speeding up to {spd} knots", PilotVariantTags.None, FrequencyLoadBand.Quiet)]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "slowing down to {spd} knots", PilotVariantTags.None, FrequencyLoadBand.Quiet)]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "slowing to {spd} knots", PilotVariantTags.None, FrequencyLoadBand.Normal)]
    [InlineData(
        "maintain {spd} knots",
        CanonicalCommandType.Speed,
        "speeding up to {spd} knots",
        PilotVariantTags.SlowingOnly,
        FrequencyLoadBand.Quiet
    )]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "slowing to {spd} knots", PilotVariantTags.SpeedingOnly, FrequencyLoadBand.Quiet)]
    [InlineData(
        "maintain {spd} knots",
        CanonicalCommandType.Speed,
        "speeding to {spd} knots",
        PilotVariantTags.SpeedingOnly,
        FrequencyLoadBand.Quiet
    )]
    [InlineData(
        "maintain {spd} knots",
        CanonicalCommandType.Speed,
        "slowing to {spd} knots",
        PilotVariantTags.MaintainingOnly,
        FrequencyLoadBand.Quiet
    )]
    [InlineData("maintain {spd} knots", CanonicalCommandType.Speed, "holding {spd} knots", PilotVariantTags.MaintainingOnly, FrequencyLoadBand.Quiet)]
    // Nothing but a courtesy follows a callsign the row places.
    [InlineData(
        "climb and? maintain {alt}",
        CanonicalCommandType.ClimbMaintain,
        "left heading {hdg}, {cs}, climbing {alt}",
        PilotVariantTags.None,
        FrequencyLoadBand.Quiet
    )]
    [InlineData(
        "climb and? maintain {alt}",
        CanonicalCommandType.ClimbMaintain,
        "climbing, {cs}, {alt}",
        PilotVariantTags.None,
        FrequencyLoadBand.Quiet
    )]
    [InlineData(
        "turn left heading {hdg}",
        CanonicalCommandType.TurnLeft,
        "turn left, {cs}, heading {hdg}",
        PilotVariantTags.None,
        FrequencyLoadBand.Quiet
    )]
    public void HoldsTemplate_TaggedOrPlacedVariantBreaksTheFloor_Rejects(
        string canonicalPattern,
        CanonicalCommandType type,
        string template,
        PilotVariantTags tags,
        FrequencyLoadBand band
    ) => Assert.False(AviationFloor.HoldsTemplate(canonicalPattern.Split(' '), type, new PilotVariant(template, tags), band));

    [Theory]
    // An expedite with an altitude: a busy pilot keeps the verb and says "expediting" for "expedite climb|descent".
    [InlineData("climb and maintain {alt}, expedite climb", CanonicalCommandType.Expedite, "climbing {alt}, expediting", FrequencyLoadBand.Busy)]
    [InlineData(
        "descend and maintain {alt}, expedite descent",
        CanonicalCommandType.Expedite,
        "descending {alt:group}, expediting",
        FrequencyLoadBand.Busy
    )]
    // A bare expedite: "expediting" stands for "expedite climb|descent" in normal and busy.
    [InlineData("expedite climb", CanonicalCommandType.Expedite, "expediting", FrequencyLoadBand.Normal)]
    [InlineData("expedite descent", CanonicalCommandType.Expedite, "expediting", FrequencyLoadBand.Busy)]
    [InlineData("expedite descent", CanonicalCommandType.Expedite, "expediting descent", FrequencyLoadBand.Quiet)]
    [InlineData("taxi without delay", CanonicalCommandType.Expedite, "taxiing without delay", FrequencyLoadBand.Quiet)]
    // An altitude restriction and a cross-fix restriction keep their qualifier, and the crossing keeps "cross".
    [InlineData("maintain VFR at or below {alt}", CanonicalCommandType.ClimbMaintain, "VFR at or below {alt:group}", FrequencyLoadBand.Busy)]
    [InlineData("cross {fix} at or above {alt}", CanonicalCommandType.CrossFix, "we'll cross {fix} at or above {alt}", FrequencyLoadBand.Quiet)]
    [InlineData("cross {fix} at {alt}", CanonicalCommandType.CrossFix, "cross {fix} at {alt:group}", FrequencyLoadBand.Busy)]
    public void HoldsTemplate_ClauseBuilderRowKeepsTheFloor_Accepts(
        string canonicalPattern,
        CanonicalCommandType type,
        string template,
        FrequencyLoadBand band
    ) => Assert.True(AviationFloor.HoldsTemplate(canonicalPattern.Split(' '), type, new PilotVariant(template, PilotVariantTags.None), band));

    [Theory]
    // No expedite is read back with a bare "wilco": the controller needs to hear "expedite" confirmed (7110.65 2-1-5.b).
    [InlineData("climb and maintain {alt}, expedite climb", CanonicalCommandType.Expedite, "wilco", FrequencyLoadBand.Busy)]
    [InlineData("descend and maintain {alt}, expedite descent", CanonicalCommandType.Expedite, "wilco", FrequencyLoadBand.Busy)]
    [InlineData("expedite climb", CanonicalCommandType.Expedite, "wilco", FrequencyLoadBand.Busy)]
    [InlineData("expedite descent", CanonicalCommandType.Expedite, "{cs}, wilco", FrequencyLoadBand.Busy)]
    [InlineData("taxi without delay", CanonicalCommandType.Expedite, "wilco", FrequencyLoadBand.Busy)]
    [InlineData("exit without delay", CanonicalCommandType.Expedite, "wilco", FrequencyLoadBand.Busy)]
    // An expedite with an altitude never drops the verb, nor the expedite clause.
    [InlineData("climb and maintain {alt}, expedite climb", CanonicalCommandType.Expedite, "{alt}, expediting", FrequencyLoadBand.Busy)]
    [InlineData("descend and maintain {alt}, expedite descent", CanonicalCommandType.Expedite, "{alt:group}, expediting", FrequencyLoadBand.Busy)]
    [InlineData("climb and maintain {alt}, expedite climb", CanonicalCommandType.Expedite, "climbing {alt}", FrequencyLoadBand.Busy)]
    [InlineData("descend and maintain {alt}, expedite descent", CanonicalCommandType.Expedite, "descending {alt}", FrequencyLoadBand.Busy)]
    [InlineData("expedite descent", CanonicalCommandType.Expedite, "expediting climb", FrequencyLoadBand.Quiet)]
    // An altitude restriction keeps "at or above" / "at or below" whole (F3).
    [InlineData("maintain at or above {alt}", CanonicalCommandType.ClimbMaintain, "above {alt}", FrequencyLoadBand.Busy)]
    [InlineData("maintain at or below {alt}", CanonicalCommandType.ClimbMaintain, "below {alt}", FrequencyLoadBand.Busy)]
    [InlineData("maintain VFR at or above {alt}", CanonicalCommandType.ClimbMaintain, "VFR above {alt}", FrequencyLoadBand.Busy)]
    [InlineData("maintain at or above {alt}", CanonicalCommandType.ClimbMaintain, "at or below {alt}", FrequencyLoadBand.Busy)]
    // A VFR restriction keeps "VFR" in every band.
    [InlineData("maintain VFR at or above {alt}", CanonicalCommandType.ClimbMaintain, "at or above {alt}", FrequencyLoadBand.Busy)]
    // A cross-fix row keeps "cross".
    [InlineData("cross {fix} at or above {alt}", CanonicalCommandType.CrossFix, "{fix} at or above {alt}", FrequencyLoadBand.Busy)]
    [InlineData("cross {fix} at or below {alt}", CanonicalCommandType.CrossFix, "we'll be at {fix} at or below {alt}", FrequencyLoadBand.Quiet)]
    // The group form is busy only.
    [InlineData(
        "climb and maintain {alt}, expedite climb",
        CanonicalCommandType.Expedite,
        "climbing {alt:group}, expediting",
        FrequencyLoadBand.Normal
    )]
    [InlineData(
        "maintain at or above {alt}",
        CanonicalCommandType.ClimbMaintain,
        "maintaining at or above {alt:group}, {cs}, thanks",
        FrequencyLoadBand.Quiet
    )]
    [InlineData("cross {fix} at {alt}", CanonicalCommandType.CrossFix, "we'll cross {fix} at {alt:group}", FrequencyLoadBand.Quiet)]
    public void HoldsTemplate_ClauseBuilderRowBreaksTheFloor_Rejects(
        string canonicalPattern,
        CanonicalCommandType type,
        string template,
        FrequencyLoadBand band
    ) => Assert.False(AviationFloor.HoldsTemplate(canonicalPattern.Split(' '), type, new PilotVariant(template, PilotVariantTags.None), band));
}
