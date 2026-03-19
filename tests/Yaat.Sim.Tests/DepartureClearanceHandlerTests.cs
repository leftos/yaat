using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class DepartureClearanceHandlerTests
{
    private static readonly ILogger Logger = new NullLogger<DepartureClearanceHandlerTests>();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeAircraft(string departure = "OAK", string? route = null)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = 37.728,
            Longitude = -122.218,
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Departure = departure,
            Route = route!,
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "30", airportId: "OAK", heading: 310, elevationFt: 6);

    private static HoldingShortPhase MakeHoldingShort(string runway = "30/12")
    {
        return new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = 10,
                Reason = HoldShortReason.DestinationRunway,
                TargetName = runway,
            }
        );
    }

    private static PhaseContext MinCtx(AircraftState ac) => CommandDispatcher.BuildMinimalContext(ac);

    // -------------------------------------------------------------------------
    // TryDepartureClearance from HoldingShort
    // -------------------------------------------------------------------------

    [Fact]
    public void TryDepartureClearance_FromHoldingShort_LUAW_Succeeds()
    {
        var ac = MakeAircraft();
        var holding = MakeHoldingShort();
        ac.Phases!.Add(holding);
        ac.Phases.Start(MinCtx(ac));

        var rwy = DefaultRunway();
        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy));

        var result = DepartureClearanceHandler.TryDepartureClearance(ac, holding, ClearanceType.LineUpAndWait, new DefaultDeparture(), null, Logger);

        Assert.True(result.Success);
        Assert.Contains("Line up and wait", result.Message!);
        Assert.NotNull(ac.Phases.AssignedRunway);
    }

    [Fact]
    public void TryDepartureClearance_FromHoldingShort_CTO_Succeeds()
    {
        var ac = MakeAircraft();
        var holding = MakeHoldingShort();
        ac.Phases!.Add(holding);
        ac.Phases.Start(MinCtx(ac));

        var rwy = DefaultRunway();
        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new RunwayHeadingDeparture(),
            null,
            Logger
        );

        Assert.True(result.Success);
        Assert.Contains("Cleared for takeoff", result.Message!);
        Assert.Contains("runway heading", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryDepartureClearance from Taxiing (pre-store)
    // -------------------------------------------------------------------------

    [Fact]
    public void TryDepartureClearance_FromTaxiing_StoresClearance()
    {
        var ac = MakeAircraft();
        var taxiPhase = new TaxiingPhase();
        ac.Phases!.Add(taxiPhase);
        ac.Phases.Start(MinCtx(ac));

        // Need a taxi route with a destination hold-short
        ac.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 10,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "30/12",
                },
            ],
        };

        var rwy = DefaultRunway();
        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            taxiPhase,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            5000,
            Logger
        );

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases.DepartureClearance);
        Assert.Equal(ClearanceType.ClearedForTakeoff, ac.Phases.DepartureClearance!.Type);
        Assert.Equal(5000, ac.Phases.DepartureClearance.AssignedAltitude);
    }

    [Fact]
    public void TryDepartureClearance_FromTaxiing_NoRoute_Fails()
    {
        var ac = MakeAircraft();
        var taxiPhase = new TaxiingPhase();
        ac.Phases!.Add(taxiPhase);
        ac.Phases.Start(MinCtx(ac));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            taxiPhase,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            null,
            Logger
        );

        Assert.False(result.Success);
        Assert.Contains("No taxi route", result.Message!);
    }

    // -------------------------------------------------------------------------
    // TryDepartureClearance from LineUp (pre-satisfy)
    // -------------------------------------------------------------------------

    [Fact]
    public void TryDepartureClearance_FromLineUp_CTO_SatisfiesUpcomingLUAW()
    {
        var ac = MakeAircraft();
        var rwy = DefaultRunway();
        ac.Phases!.AssignedRunway = rwy;

        var lineUp = new LineUpPhase(null);
        var luaw = new LinedUpAndWaitingPhase();
        var takeoff = new TakeoffPhase();
        var climb = new InitialClimbPhase { Departure = new DefaultDeparture() };

        ac.Phases.Add(lineUp);
        ac.Phases.Add(luaw);
        ac.Phases.Add(takeoff);
        ac.Phases.Add(climb);
        ac.Phases.Start(MinCtx(ac));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            lineUp,
            ClearanceType.ClearedForTakeoff,
            new RunwayHeadingDeparture(),
            null,
            Logger
        );

        Assert.True(result.Success);
        Assert.True(luaw.Requirements[0].IsSatisfied);
    }

    [Fact]
    public void TryDepartureClearance_FromLineUp_LUAW_Rejected()
    {
        var ac = MakeAircraft();
        var lineUp = new LineUpPhase(null);
        ac.Phases!.Add(lineUp);
        ac.Phases.Start(MinCtx(ac));

        var result = DepartureClearanceHandler.TryDepartureClearance(ac, lineUp, ClearanceType.LineUpAndWait, new DefaultDeparture(), null, Logger);

        Assert.False(result.Success);
    }

    // -------------------------------------------------------------------------
    // TryDepartureClearance from HoldingInPosition (e.g. after WARPG)
    // -------------------------------------------------------------------------

    [Fact]
    public void TryDepartureClearance_FromHoldingInPosition_CTO_Succeeds()
    {
        var ac = MakeAircraft();
        var rwy = DefaultRunway();
        ac.Phases!.AssignedRunway = rwy;

        var holdPhase = new HoldingInPositionPhase();
        ac.Phases.Add(holdPhase);
        ac.Phases.Start(MinCtx(ac));

        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holdPhase,
            ClearanceType.ClearedForTakeoff,
            new RunwayHeadingDeparture(),
            5000,
            Logger
        );

        Assert.True(result.Success);
        Assert.Contains("Cleared for takeoff", result.Message!);
        Assert.Contains("runway heading", result.Message!);
        Assert.Contains("climb and maintain 5,000", result.Message!);

        // Verify tower phases were installed
        Assert.Contains(ac.Phases!.Phases, p => p is LineUpPhase);
        Assert.Contains(ac.Phases.Phases, p => p is LinedUpAndWaitingPhase);
        Assert.Contains(ac.Phases.Phases, p => p is TakeoffPhase);
        Assert.Contains(ac.Phases.Phases, p => p is InitialClimbPhase);

        // LUAW should be pre-satisfied (CTO)
        var luaw = ac.Phases.Phases.OfType<LinedUpAndWaitingPhase>().First();
        Assert.True(luaw.Requirements[0].IsSatisfied);
    }

    [Fact]
    public void TryDepartureClearance_FromHoldingInPosition_LUAW_Succeeds()
    {
        var ac = MakeAircraft();
        var rwy = DefaultRunway();
        ac.Phases!.AssignedRunway = rwy;

        var holdPhase = new HoldingInPositionPhase();
        ac.Phases.Add(holdPhase);
        ac.Phases.Start(MinCtx(ac));

        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holdPhase,
            ClearanceType.LineUpAndWait,
            new DefaultDeparture(),
            null,
            Logger
        );

        Assert.True(result.Success);
        Assert.Contains("Line up and wait", result.Message!);

        // Tower phases installed but LUAW NOT pre-satisfied
        var luaw = ac.Phases!.Phases.OfType<LinedUpAndWaitingPhase>().First();
        Assert.False(luaw.Requirements[0].IsSatisfied);
    }

    [Fact]
    public void TryDepartureClearance_FromHoldingInPosition_NoRunway_Fails()
    {
        var ac = MakeAircraft();
        // No AssignedRunway set

        var holdPhase = new HoldingInPositionPhase();
        ac.Phases!.Add(holdPhase);
        ac.Phases.Start(MinCtx(ac));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holdPhase,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            null,
            Logger
        );

        Assert.False(result.Success);
        Assert.Contains("No runway assigned", result.Message!);
    }

    // -------------------------------------------------------------------------
    // Closed-traffic departure
    // -------------------------------------------------------------------------

    [Fact]
    public void TryDepartureClearance_ClosedTraffic_SetsPatternMode_NoInitialClimb()
    {
        var ac = MakeAircraft();
        var holding = MakeHoldingShort();
        ac.Phases!.Add(holding);
        ac.Phases.Start(MinCtx(ac));

        var rwy = DefaultRunway();
        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy));

        var departure = new ClosedTrafficDeparture(PatternDirection.Left);
        var result = DepartureClearanceHandler.TryDepartureClearance(ac, holding, ClearanceType.ClearedForTakeoff, departure, null, Logger);

        Assert.True(result.Success);
        Assert.Equal(PatternDirection.Left, ac.Phases.TrafficDirection);

        // No InitialClimbPhase should be in the phase list
        Assert.DoesNotContain(ac.Phases.Phases, p => p is InitialClimbPhase);
    }

    // -------------------------------------------------------------------------
    // ResolveLegsToTargets
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveLegsToTargets_PI_Skipped()
    {
        NavigationDatabase.SetInstance(TestNavDbFactory.WithFixes(("FIXPI", 37.0, -122.0), ("FIXNORM", 37.5, -122.5)));

        var legs = new[]
        {
            new CifpLeg("FIXPI", CifpPathTerminator.PI, null, null, null, CifpFixRole.None, 1, null, null, null),
            new CifpLeg("FIXNORM", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 2, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);

        // PI leg should be skipped, only FIXNORM should appear
        Assert.Single(targets);
        Assert.Equal("FIXNORM", targets[0].Name);
    }

    [Fact]
    public void ResolveLegsToTargets_UnknownFix_Skipped()
    {
        NavigationDatabase.SetInstance(TestNavDbFactory.WithFixes(("KNOWN", 37.0, -122.0)));

        var legs = new[]
        {
            new CifpLeg("UNKNOWN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 1, null, null, null),
            new CifpLeg("KNOWN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 2, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);

        Assert.Single(targets);
        Assert.Equal("KNOWN", targets[0].Name);
    }

    [Fact]
    public void ResolveLegsToTargets_WithAltitudeConstraint_Preserved()
    {
        NavigationDatabase.SetInstance(TestNavDbFactory.WithFixes(("FIX1", 37.0, -122.0)));

        var alt = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 5000);
        var legs = new[] { new CifpLeg("FIX1", CifpPathTerminator.TF, null, alt, null, CifpFixRole.None, 1, null, null, null) };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);

        Assert.Single(targets);
        Assert.NotNull(targets[0].AltitudeRestriction);
        Assert.Equal(5000, targets[0].AltitudeRestriction!.Altitude1Ft);
    }

    [Fact]
    public void ResolveLegsToTargets_DuplicateAdjacentFix_Deduplicated()
    {
        NavigationDatabase.SetInstance(TestNavDbFactory.WithFixes(("FIX1", 37.0, -122.0)));

        var legs = new[]
        {
            new CifpLeg("FIX1", CifpPathTerminator.IF, null, null, null, CifpFixRole.None, 1, null, null, null),
            new CifpLeg("FIX1", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 2, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);

        Assert.Single(targets);
    }

    // -------------------------------------------------------------------------
    // BuildDepartureMessage
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildDepartureMessage_CTO_WithAltitude()
    {
        var result = DepartureClearanceHandler.BuildDepartureMessage(ClearanceType.ClearedForTakeoff, "30", new DefaultDeparture(), 5000);

        Assert.True(result.Success);
        Assert.Contains("Cleared for takeoff runway 30", result.Message!);
        Assert.Contains("climb and maintain 5,000", result.Message!);
    }

    [Fact]
    public void BuildDepartureMessage_LUAW()
    {
        var result = DepartureClearanceHandler.BuildDepartureMessage(ClearanceType.LineUpAndWait, "30", new DefaultDeparture(), null);

        Assert.True(result.Success);
        Assert.Contains("Line up and wait", result.Message!);
    }

    // -------------------------------------------------------------------------
    // FormatDepartureInstructionSuffix
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(DefaultDeparture), "")]
    [InlineData(typeof(RunwayHeadingDeparture), ", fly runway heading")]
    [InlineData(typeof(OnCourseDeparture), ", on course")]
    public void FormatDepartureInstructionSuffix_MatchesExpected(Type depType, string expected)
    {
        var dep = (DepartureInstruction)Activator.CreateInstance(depType)!;
        Assert.Equal(expected, DepartureClearanceHandler.FormatDepartureInstructionSuffix(dep));
    }

    [Fact]
    public void FormatDepartureInstructionSuffix_DirectFix()
    {
        var dep = new DirectFixDeparture("EDDYY", 37.0, -122.0, null);
        Assert.Equal(", direct EDDYY", DepartureClearanceHandler.FormatDepartureInstructionSuffix(dep));
    }

    [Fact]
    public void FormatDepartureInstructionSuffix_ClosedTraffic()
    {
        var dep = new ClosedTrafficDeparture(PatternDirection.Right);
        Assert.Contains("right traffic", DepartureClearanceHandler.FormatDepartureInstructionSuffix(dep));
    }

    // -------------------------------------------------------------------------
    // Cross-runway closed traffic
    // -------------------------------------------------------------------------

    private static RunwayInfo Runway28R() =>
        TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280, elevationFt: 6, thresholdLat: 37.727, thresholdLon: -122.218);

    private static RunwayInfo Runway33() =>
        TestRunwayFactory.Make(designator: "33", airportId: "OAK", heading: 330, elevationFt: 6, thresholdLat: 37.725, thresholdLon: -122.220);

    private static RunwayInfo Runway28L() =>
        TestRunwayFactory.Make(designator: "28L", airportId: "OAK", heading: 280, elevationFt: 6, thresholdLat: 37.730, thresholdLon: -122.218);

    [Fact]
    public void CrossRunway_FromHoldShort33_CTO_MRT_28R_BuildsCircuitFor28R()
    {
        var rwy33 = Runway33();
        var rwy28R = Runway28R();
        var ac = MakeAircraft();
        var holding = MakeHoldingShort("33/15");
        ac.Phases!.Add(holding);
        ac.Phases.Start(MinCtx(ac));

        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy33, rwy28R));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new ClosedTrafficDeparture(PatternDirection.Right, "28R"),
            null,
            Logger
        );

        Assert.True(result.Success);
        // AssignedRunway is the takeoff runway (33)
        Assert.Equal("33", ac.Phases.AssignedRunway?.Designator);
        // PatternRunway is the pattern runway (28R)
        Assert.NotNull(ac.Phases.PatternRunway);
        Assert.Equal("28R", ac.Phases.PatternRunway!.Designator);
        // Pattern direction is set
        Assert.Equal(PatternDirection.Right, ac.Phases.TrafficDirection);
        // Circuit phases use the pattern runway's geometry
        var upwind = ac.Phases.Phases.OfType<UpwindPhase>().FirstOrDefault();
        Assert.NotNull(upwind);
    }

    [Fact]
    public void CrossRunway_FromHoldShort33_CTO_MLT_28L_BuildsCircuitFor28L()
    {
        var rwy33 = Runway33();
        var rwy28L = Runway28L();
        var ac = MakeAircraft();
        var holding = MakeHoldingShort("33/15");
        ac.Phases!.Add(holding);
        ac.Phases.Start(MinCtx(ac));

        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy33, rwy28L));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new ClosedTrafficDeparture(PatternDirection.Left, "28L"),
            null,
            Logger
        );

        Assert.True(result.Success);
        Assert.Equal("28L", ac.Phases.PatternRunway!.Designator);
        Assert.Equal(PatternDirection.Left, ac.Phases.TrafficDirection);
    }

    [Fact]
    public void CrossRunway_NoRunwayId_UsesTakeoffRunway()
    {
        var rwy33 = Runway33();
        var ac = MakeAircraft();
        var holding = MakeHoldingShort("33/15");
        ac.Phases!.Add(holding);
        ac.Phases.Start(MinCtx(ac));

        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy33));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new ClosedTrafficDeparture(PatternDirection.Right),
            null,
            Logger
        );

        Assert.True(result.Success);
        // PatternRunway is set to the takeoff runway (fallback)
        Assert.Equal("33", ac.Phases.PatternRunway!.Designator);
        Assert.Null(new ClosedTrafficDeparture(PatternDirection.Right).RunwayId);
    }

    [Fact]
    public void CrossRunway_Message_IncludesPatternRunway()
    {
        var dep = new ClosedTrafficDeparture(PatternDirection.Right, "28R");
        var suffix = DepartureClearanceHandler.FormatDepartureInstructionSuffix(dep);
        Assert.Contains("right traffic", suffix);
        Assert.Contains("runway 28R", suffix);
    }

    [Fact]
    public void CrossRunway_Message_NoRunwayId_OmitsRunway()
    {
        var dep = new ClosedTrafficDeparture(PatternDirection.Left);
        var suffix = DepartureClearanceHandler.FormatDepartureInstructionSuffix(dep);
        Assert.Contains("left traffic", suffix);
        Assert.DoesNotContain("runway", suffix);
    }

    [Fact]
    public void CrossRunway_FromLUAW_CTO_MRT_28R_BuildsCircuitFor28R()
    {
        var rwy33 = Runway33();
        var rwy28R = Runway28R();
        var ac = MakeAircraft();
        ac.Phases!.AssignedRunway = rwy33;

        var luaw = new LinedUpAndWaitingPhase();
        ac.Phases.Add(luaw);
        ac.Phases.Start(MinCtx(ac));

        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy33, rwy28R));
        var cto = new ClearedForTakeoffCommand(new ClosedTrafficDeparture(PatternDirection.Right, "28R"));

        var result = DepartureClearanceHandler.TryClearedForTakeoff(cto, ac, luaw);

        Assert.True(result.Success);
        Assert.Equal("28R", ac.Phases.PatternRunway!.Designator);
        Assert.Equal(PatternDirection.Right, ac.Phases.TrafficDirection);
        // No InitialClimbPhase for closed traffic
        Assert.DoesNotContain(ac.Phases.Phases, p => p is InitialClimbPhase { Status: PhaseStatus.Pending });
    }

    [Fact]
    public void CrossRunway_StoreDuringTaxi_PreResolvesPatternRunway()
    {
        var rwy33 = Runway33();
        var rwy28R = Runway28R();
        var ac = MakeAircraft();
        ac.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 10,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "33/15",
                },
            ],
        };

        var taxiing = new TaxiingPhase();
        ac.Phases!.Add(taxiing);
        ac.Phases.Start(MinCtx(ac));

        NavigationDatabase.SetInstance(TestNavDbFactory.WithRunways(rwy33, rwy28R));

        var result = DepartureClearanceHandler.StoreDepartureClearanceDuringTaxi(
            ac,
            ClearanceType.ClearedForTakeoff,
            new ClosedTrafficDeparture(PatternDirection.Right, "28R"),
            null
        );

        Assert.True(result.Success);
        Assert.NotNull(ac.Phases.DepartureClearance);
        Assert.NotNull(ac.Phases.DepartureClearance!.PatternRunway);
        Assert.Equal("28R", ac.Phases.DepartureClearance.PatternRunway!.Designator);
    }
}
