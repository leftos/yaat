using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// The AP/1B chapter 5 refueling clearance (FAA JO 7110.65 §9-2-13): CAR, its block altitude
/// clause, direction selection, and the §9-2-13 phraseology that inverts the designator.
/// </summary>
[Collection("NavDbMutator")]
public sealed class AerialRefuelingCommandTests
{
    public AerialRefuelingCommandTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>A tanker positioned just before the given variant's first point, flying down it.</summary>
    private static AircraftState AircraftOn(MilitaryRouteVariant variant)
    {
        var entry = variant.Points[0].Position;
        var next = variant.Points[1].Position;
        double bearing = GeoMath.BearingTo(entry, next);
        return new AircraftState
        {
            Callsign = "ETHAN41",
            AircraftType = "K35R",
            // Back the aircraft up along the inbound bearing so the entry point is genuinely ahead.
            Position = GeoMath.ProjectPoint(entry, new TrueHeading((bearing + 180) % 360), 10),
            TrueHeading = new TrueHeading(bearing),
            Altitude = 25000,
            IndicatedAirspeed = 290,
        };
    }

    private static AircraftState AircraftOnAr1() => AircraftOn(NavigationDatabase.Instance.GetMilitaryRoute("AR1")!.Variants[0]);

    private static CommandResult Apply(AircraftState aircraft, string text)
    {
        var parsed = CommandParser.Parse(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return CommandDispatcher.Dispatch(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
    }

    private static void TickPhase(AircraftState aircraft)
    {
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        foreach (var phase in aircraft.Phases!.Phases)
        {
            phase.Status = PhaseStatus.Active;
            phase.OnTick(ctx);
        }
    }

    [Fact]
    public void Car_ClearsTheTankerOntoTheTrackAtItsPublishedBlock()
    {
        var aircraft = AircraftOnAr1();

        var result = Apply(aircraft, "CAR AR1");

        Assert.True(result.Success, result.Message);
        Assert.Equal("AR1", aircraft.MilitaryRoute.Designator);
        Assert.Equal(MilitaryRouteType.Ar, aircraft.MilitaryRoute.Kind);
        Assert.Equal(MilitaryRouteStatus.ClearedIn, aircraft.MilitaryRoute.Status);
        Assert.Contains(aircraft.Phases!.Phases, p => p is MilitaryRoutePhase);
        // AR1 publishes FL240/FL310.
        Assert.Contains("24,000 through 31,000", result.Message);
    }

    [Fact]
    public void Car_PublishedBlock_IsArmedAsAnAltitudeFloorAndCeiling()
    {
        var aircraft = AircraftOnAr1();
        Apply(aircraft, "CAR AR1");

        TickPhase(aircraft);

        Assert.Equal(24000, aircraft.Targets.AltitudeFloor);
        Assert.Equal(31000, aircraft.Targets.AltitudeCeiling);
        // Mid-block, not parked on a bound: §9-2-13.i has the tanker leaving from the top of the
        // block and the receiver from the bottom, so the operation sits between them.
        Assert.Equal(27500, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void Car_AssignedBlock_OverridesThePublishedOne()
    {
        var aircraft = AircraftOnAr1();

        var result = Apply(aircraft, "CAR AR1 250 270");
        TickPhase(aircraft);

        Assert.True(result.Success, result.Message);
        Assert.Equal(MilitaryRouteAltitudeSource.AssignedBlock, aircraft.MilitaryRoute.AltitudeSource);
        Assert.Equal(25000, aircraft.Targets.AltitudeFloor);
        Assert.Equal(27000, aircraft.Targets.AltitudeCeiling);
    }

    [Fact]
    public void Car_TrackIsNeverMarsaMerelyForBeingATrack()
    {
        // §9-2-13 NOTE 3: MARSA begins only when the tanker advises ATC it is accepting MARSA.
        var aircraft = AircraftOnAr1();

        Apply(aircraft, "CAR AR1");

        Assert.False(aircraft.MilitaryRoute.Marsa);
    }

    [Fact]
    public void Car_SelectsThePublishedDirectionTheAircraftIsPositionedToFly()
    {
        var route = NavigationDatabase.Instance.GetMilitaryRoute("AR4A")!;
        Assert.Equal(2, route.Variants.Count);

        foreach (var variant in route.Variants)
        {
            var aircraft = AircraftOn(variant);

            var result = Apply(aircraft, "CAR AR4A");

            Assert.True(result.Success, result.Message);
            Assert.Equal(variant.Direction, aircraft.MilitaryRoute.Direction);
            Assert.Equal(variant.Points[0].Id, aircraft.MilitaryRoute.EntryPointId);
        }
    }

    [Fact]
    public void Car_UnknownTrack_IsRejected()
    {
        var result = Apply(AircraftOnAr1(), "CAR AR999999");

        Assert.False(result.Success);
        Assert.Contains("Unknown aerial refueling track", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Car_OnATrainingRoute_PointsAtCmtr()
    {
        var result = Apply(AircraftOnAr1(), "CAR IR149");

        Assert.False(result.Success);
        Assert.Contains("use CMTR", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cmtr_OnARefuelingTrack_PointsAtCar()
    {
        // §9-2-6 is titled IFR Military Training Routes; refueling has its own clearance in §9-2-13.
        var result = Apply(AircraftOnAr1(), "CMTR AR1");

        Assert.False(result.Success);
        Assert.Contains("use CAR", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A tanker just outside AR601's published entry point.</summary>
    private static AircraftState AircraftOnAr601()
    {
        var variant = NavigationDatabase.Instance.GetMilitaryRoute("AR601")!.Variants[0];
        var entry = variant.Points[0].Position;
        return new AircraftState
        {
            Callsign = "ETHAN41",
            AircraftType = "K35R",
            Position = GeoMath.ProjectPoint(entry, new TrueHeading(0), 15),
            TrueHeading = new TrueHeading(180),
            Altitude = 20000,
            IndicatedAirspeed = 280,
        };
    }

    [Fact]
    public void Car_OnAnAnchor_InstallsTheOrbitPhase()
    {
        var aircraft = AircraftOnAr601();

        var result = Apply(aircraft, "CAR AR601");

        Assert.True(result.Success, result.Message);
        Assert.Contains("anchor", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(aircraft.Phases!.Phases, p => p is AerialRefuelingAnchorPhase);
        Assert.Equal("AR601", aircraft.MilitaryRoute.Designator);
        // AP/1B publishes 16000/FL260 for AR601.
        Assert.Contains("16,000 through 26,000", result.Message);
    }

    [Fact]
    public void Anchor_FliesTheRunInThenOrbitsThePublishedPatternIndefinitely()
    {
        var aircraft = AircraftOnAr601();
        Apply(aircraft, "CAR AR601");
        var phase = aircraft.Phases!.Phases.OfType<AerialRefuelingAnchorPhase>().Single();
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        phase.Status = PhaseStatus.Active;
        phase.OnTick(ctx);

        // The run-in is flown once.
        Assert.Equal(phase.EntryNames, aircraft.Targets.NavigationRoute.Select(t => t.Name));
        Assert.Equal(0, phase.Laps);

        // Consuming the whole route must not end the phase -- an anchor is left only by clearance.
        for (int lap = 1; lap <= 3; lap++)
        {
            aircraft.Targets.NavigationRoute.Clear();

            Assert.False(phase.OnTick(ctx));
            Assert.Equal(lap, phase.Laps);
            Assert.Equal(phase.PatternNames, aircraft.Targets.NavigationRoute.Select(t => t.Name));
        }

        Assert.Equal(MilitaryRouteStatus.Established, aircraft.MilitaryRoute.Status);
    }

    [Fact]
    public void Anchor_ArmsThePublishedBlockAndClearsItOnExit()
    {
        var aircraft = AircraftOnAr601();
        Apply(aircraft, "CAR AR601");
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        var phase = aircraft.Phases!.Phases.OfType<AerialRefuelingAnchorPhase>().Single();
        phase.Status = PhaseStatus.Active;
        phase.OnTick(ctx);

        Assert.Equal(16000, aircraft.Targets.AltitudeFloor);
        Assert.Equal(26000, aircraft.Targets.AltitudeCeiling);
        Assert.Equal(21000, aircraft.Targets.TargetAltitude);

        aircraft.Phases.Clear(ctx);

        Assert.Null(aircraft.Targets.AltitudeFloor);
        Assert.Null(aircraft.Targets.AltitudeCeiling);
    }

    [Fact]
    public void Anchor_SurvivesASnapshotRoundTrip()
    {
        var aircraft = AircraftOnAr601();
        Apply(aircraft, "CAR AR601");
        var original = aircraft.Phases!.Phases.OfType<AerialRefuelingAnchorPhase>().Single();
        original.Status = PhaseStatus.Active;
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        original.OnTick(ctx);
        aircraft.Targets.NavigationRoute.Clear();
        original.OnTick(ctx);

        var dto = new PhaseListDto { Phases = [original.ToSnapshot()], CurrentIndex = 0 };
        var restored = (AerialRefuelingAnchorPhase)PhaseList.FromSnapshot(dto, groundLayout: null).Phases.Single();

        Assert.Equal(original.Designator, restored.Designator);
        Assert.Equal(original.Laps, restored.Laps);
        Assert.Equal(original.PatternNames, restored.PatternNames);
        Assert.Equal(original.EntryNames, restored.EntryNames);
    }

    [Fact]
    public void Anchor_WithoutAPublishedPattern_IsRejected()
    {
        // AR662V is a VFR helicopter refueling area and publishes no orbit to fly.
        var result = Apply(AircraftOnAr601(), "CAR AR662V");

        Assert.False(result.Success);
        Assert.Contains("no orbit pattern", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Car_ParserRejectsALoneAltitude()
    {
        // A block has two bounds; inventing the second would authorise airspace nobody assigned.
        var parsed = CommandParser.Parse("CAR AR1 250");

        Assert.False(parsed.IsSuccess);
    }

    [Fact]
    public void Car_ParserRejectsAnInvertedBlock()
    {
        var parsed = CommandParser.Parse("CAR AR1 310 240");

        Assert.False(parsed.IsSuccess);
        Assert.Contains("below its ceiling", parsed.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpellRefuelingTrack_NamesTheNumberWithoutTheLetters()
    {
        // §9-2-13 "CLEARED TO CONDUCT REFUELING ALONG (number) TRACK" names the number only, which
        // inverts §2-5-1.f's training-route form ("I-R five thirty one").
        Assert.Equal("three twelve", PhraseologyVerbalizer.SpellRefuelingTrack("AR312"));
        Assert.Equal("six hundred", PhraseologyVerbalizer.SpellRefuelingTrack("AR600"));
        Assert.Equal("one", PhraseologyVerbalizer.SpellRefuelingTrack("AR1"));
        Assert.Equal("three hotel", PhraseologyVerbalizer.SpellRefuelingTrack("AR3H"));
    }

    [Fact]
    public void SpellMilitaryRoute_StillUsesTheTrainingRouteForm()
    {
        // The two spellers must not converge: §2-5-1.f speaks the letters, §9-2-13 does not.
        Assert.Equal("i-r five thirty one", PhraseologyVerbalizer.SpellMilitaryRoute("IR531"));
        Assert.Equal("v-r fifty two", PhraseologyVerbalizer.SpellMilitaryRoute("VR52"));
    }

    [Fact]
    public void Car_CanonicalRoundTripsThroughTheDescriber()
    {
        var parsed = CommandParser.Parse("CAR AR1 250 270");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        Assert.Equal("CAR AR1 25000 27000", CommandDescriber.DescribeCommand(parsed.Value!));
    }
}
