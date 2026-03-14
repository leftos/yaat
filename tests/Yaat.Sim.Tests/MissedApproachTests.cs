using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class MissedApproachTests
{
    private static readonly CifpLeg MapLeg1 = new(
        FixIdentifier: "MAPWP",
        PathTerminator: CifpPathTerminator.TF,
        TurnDirection: null,
        Altitude: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 2000),
        Speed: null,
        FixRole: CifpFixRole.None,
        Sequence: 10,
        OutboundCourse: null,
        LegDistanceNm: null,
        VerticalAngle: null
    );

    private static readonly CifpLeg MapLeg2 = new(
        FixIdentifier: "MHOLD",
        PathTerminator: CifpPathTerminator.TF,
        TurnDirection: null,
        Altitude: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 3000),
        Speed: null,
        FixRole: CifpFixRole.None,
        Sequence: 20,
        OutboundCourse: null,
        LegDistanceNm: null,
        VerticalAngle: null
    );

    private static readonly CifpLeg MapHoldLeg = new(
        FixIdentifier: "MHOLD",
        PathTerminator: CifpPathTerminator.HM,
        TurnDirection: 'R',
        Altitude: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 3000),
        Speed: null,
        FixRole: CifpFixRole.None,
        Sequence: 30,
        OutboundCourse: 100.0,
        LegDistanceNm: null,
        VerticalAngle: null
    );

    private static readonly CifpLeg CommonLeg = new(
        FixIdentifier: "FINIX",
        PathTerminator: CifpPathTerminator.TF,
        TurnDirection: null,
        Altitude: new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 2500),
        Speed: null,
        FixRole: CifpFixRole.FAF,
        Sequence: 10,
        OutboundCourse: null,
        LegDistanceNm: null,
        VerticalAngle: null
    );

    private static CifpApproachProcedure MakeProcedure(IReadOnlyList<CifpLeg>? mapLegs = null)
    {
        return new CifpApproachProcedure(
            Airport: "KTEST",
            ApproachId: "I28",
            TypeCode: 'I',
            ApproachTypeName: "ILS",
            Runway: "28",
            CommonLegs: [CommonLeg],
            Transitions: new Dictionary<string, CifpTransition>(),
            MissedApproachLegs: mapLegs ?? [MapLeg1, MapLeg2],
            HasHoldInLieu: false,
            HoldInLieuLeg: null
        );
    }

    private static RunwayInfo MakeRunway()
    {
        return TestRunwayFactory.Make(designator: "28", airportId: "KTEST", heading: 280, elevationFt: 100);
    }

    private static AircraftState MakeAircraft(bool isPattern = false)
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Heading = 280,
            Altitude = 200,
            Latitude = 37.0,
            Longitude = -122.0,
            Destination = "KTEST",
        };

        var runway = MakeRunway();
        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        if (isPattern)
        {
            aircraft.Phases.TrafficDirection = PatternDirection.Left;
        }

        return aircraft;
    }

    private static NavigationDatabase MakeFixLookup()
    {
        return TestNavDbFactory.WithFixes(("FINIX", 37.01, -122.05), ("MAPWP", 37.02, -122.08), ("MHOLD", 37.03, -122.10));
    }

    [Fact]
    public void BuildMissedApproachFixes_ResolvesFixPositions()
    {
        var procedure = MakeProcedure();
        var fixes = MakeFixLookup();

        var result = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);

        Assert.Equal(2, result.Count);
        Assert.Equal("MAPWP", result[0].Name);
        Assert.Equal(37.02, result[0].Latitude);
        Assert.Equal(-122.08, result[0].Longitude);
        Assert.NotNull(result[0].Altitude);
        Assert.Equal(2000, result[0].Altitude!.Altitude1Ft);
        Assert.Equal("MHOLD", result[1].Name);
    }

    [Fact]
    public void BuildMissedApproachFixes_EmptyLegs_ReturnsEmpty()
    {
        var procedure = MakeProcedure(mapLegs: []);
        var fixes = MakeFixLookup();

        var result = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildMissedApproachFixes_SkipsUnresolvableFixes()
    {
        var procedure = MakeProcedure();
        var fixes = TestNavDbFactory.WithFixes(("MAPWP", 37.02, -122.08));

        var result = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);

        Assert.Single(result);
        Assert.Equal("MAPWP", result[0].Name);
    }

    [Fact]
    public void BuildMissedApproachPhases_InstrumentWithMap_ReturnsNavigationPhase()
    {
        var aircraft = MakeAircraft();
        var procedure = MakeProcedure();
        var fixes = MakeFixLookup();
        var mapFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = procedure,
            MissedApproachFixes = mapFixes,
        };

        var result = ApproachCommandHandler.BuildMissedApproachPhases(aircraft);

        Assert.Single(result);
        Assert.IsType<ApproachNavigationPhase>(result[0]);
        var navPhase = (ApproachNavigationPhase)result[0];
        Assert.Equal(2, navPhase.Fixes.Count);
        Assert.Equal("MAPWP", navPhase.Fixes[0].Name);
    }

    [Fact]
    public void BuildMissedApproachPhases_NoMapData_ReturnsEmpty()
    {
        var aircraft = MakeAircraft();
        var procedure = MakeProcedure(mapLegs: []);

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = procedure,
            MissedApproachFixes = [],
        };

        var result = ApproachCommandHandler.BuildMissedApproachPhases(aircraft);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildMissedApproachPhases_VisualApproach_ReturnsEmpty()
    {
        var aircraft = MakeAircraft();

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "VIS28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = MakeProcedure(),
            MissedApproachFixes = [new ApproachFix("MAPWP", 37.02, -122.08)],
        };

        var result = ApproachCommandHandler.BuildMissedApproachPhases(aircraft);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildMissedApproachPhases_NoProcedure_ReturnsEmpty()
    {
        var aircraft = MakeAircraft();

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = null,
        };

        var result = ApproachCommandHandler.BuildMissedApproachPhases(aircraft);

        Assert.Empty(result);
    }

    [Fact]
    public void GetMissedApproachAltitude_FromFirstRestriction()
    {
        var mapFixes = new List<ApproachFix>
        {
            new("MAPWP", 37.02, -122.08, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 2000)),
            new("MHOLD", 37.03, -122.10, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 3000)),
        };

        var alt = ApproachCommandHandler.GetMissedApproachAltitude(mapFixes);

        Assert.Equal(2000, alt);
    }

    [Fact]
    public void GetMissedApproachAltitude_NoRestriction_ReturnsNull()
    {
        var mapFixes = new List<ApproachFix> { new("MAPWP", 37.02, -122.08) };

        var alt = ApproachCommandHandler.GetMissedApproachAltitude(mapFixes);

        Assert.Null(alt);
    }

    [Fact]
    public void ManualGoAround_NoOverride_QueuesMapNavigation()
    {
        var aircraft = MakeAircraft();
        var procedure = MakeProcedure();
        var fixes = MakeFixLookup();
        var mapFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = procedure,
            MissedApproachFixes = mapFixes,
        };

        // Add a current phase so ReplaceUpcoming works
        aircraft.Phases.Add(new FinalApproachPhase());
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var ga = new GoAroundCommand();
        var result = PatternCommandHandler.TryGoAround(ga, aircraft);

        Assert.True(result.Success);

        // Should have GoAroundPhase + ApproachNavigationPhase
        var phaseTypes = aircraft.Phases.Phases.Select(p => p.GetType()).ToList();
        Assert.Contains(typeof(GoAroundPhase), phaseTypes);
        Assert.Contains(typeof(ApproachNavigationPhase), phaseTypes);
    }

    [Fact]
    public void ManualGoAround_WithAtcOverride_NoMapPhases()
    {
        var aircraft = MakeAircraft();
        var procedure = MakeProcedure();
        var fixes = MakeFixLookup();
        var mapFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = procedure,
            MissedApproachFixes = mapFixes,
        };

        aircraft.Phases.Add(new FinalApproachPhase());
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        // ATC override: explicit heading
        var ga = new GoAroundCommand(AssignedHeading: 090);
        var result = PatternCommandHandler.TryGoAround(ga, aircraft);

        Assert.True(result.Success);

        var phaseTypes = aircraft.Phases.Phases.Select(p => p.GetType()).ToList();
        Assert.Contains(typeof(GoAroundPhase), phaseTypes);
        Assert.DoesNotContain(typeof(ApproachNavigationPhase), phaseTypes);
    }

    [Fact]
    public void ManualGoAround_WithAltOverride_NoMapPhases()
    {
        var aircraft = MakeAircraft();
        var procedure = MakeProcedure();
        var fixes = MakeFixLookup();
        var mapFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = procedure,
            MissedApproachFixes = mapFixes,
        };

        aircraft.Phases.Add(new FinalApproachPhase());
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var ga = new GoAroundCommand(TargetAltitude: 5000);
        var result = PatternCommandHandler.TryGoAround(ga, aircraft);

        Assert.True(result.Success);

        var phaseTypes = aircraft.Phases.Phases.Select(p => p.GetType()).ToList();
        Assert.DoesNotContain(typeof(ApproachNavigationPhase), phaseTypes);
    }

    [Fact]
    public void PatternTraffic_GoAround_NoMapPhases()
    {
        var aircraft = MakeAircraft(isPattern: true);
        var procedure = MakeProcedure();
        var fixes = MakeFixLookup();
        var mapFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = procedure,
            MissedApproachFixes = mapFixes,
        };

        aircraft.Phases.Add(new FinalApproachPhase());
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var ga = new GoAroundCommand();
        var result = PatternCommandHandler.TryGoAround(ga, aircraft);

        Assert.True(result.Success);

        var phaseTypes = aircraft.Phases.Phases.Select(p => p.GetType()).ToList();
        Assert.DoesNotContain(typeof(ApproachNavigationPhase), phaseTypes);
    }

    [Fact]
    public void MapGoAround_TargetAltitude_FromFirstRestriction()
    {
        var aircraft = MakeAircraft();
        var procedure = MakeProcedure();
        var fixes = MakeFixLookup();
        var mapFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = procedure,
            MissedApproachFixes = mapFixes,
        };

        aircraft.Phases.Add(new FinalApproachPhase());
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var ga = new GoAroundCommand();
        PatternCommandHandler.TryGoAround(ga, aircraft);

        var goAroundPhase = aircraft.Phases.Phases.OfType<GoAroundPhase>().FirstOrDefault();
        Assert.NotNull(goAroundPhase);
        Assert.Equal(2000, goAroundPhase.TargetAltitude);
    }

    private static CifpApproachProcedure MakeProcedureWithHold()
    {
        return MakeProcedure(mapLegs: [MapLeg1, MapLeg2, MapHoldLeg]);
    }

    [Fact]
    public void ExtractMissedApproachHold_ReturnsHoldFromHmLeg()
    {
        var procedure = MakeProcedureWithHold();
        var fixes = MakeFixLookup();

        var hold = ApproachCommandHandler.ExtractMissedApproachHold(procedure, fixes);

        Assert.NotNull(hold);
        Assert.Equal("MHOLD", hold.FixName);
        Assert.Equal(37.03, hold.FixLat);
        Assert.Equal(-122.10, hold.FixLon);
        Assert.Equal(280, hold.InboundCourse); // (100 + 180) % 360
        Assert.True(hold.IsMinuteBased);
        Assert.Equal(TurnDirection.Right, hold.Direction);
    }

    [Fact]
    public void ExtractMissedApproachHold_NoHoldLeg_ReturnsNull()
    {
        var procedure = MakeProcedure(); // only TF legs
        var fixes = MakeFixLookup();

        var hold = ApproachCommandHandler.ExtractMissedApproachHold(procedure, fixes);

        Assert.Null(hold);
    }

    [Fact]
    public void BuildMissedApproachPhases_WithHold_QueuesHoldingPhase()
    {
        var aircraft = MakeAircraft();
        var procedure = MakeProcedureWithHold();
        var fixes = MakeFixLookup();
        var mapFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);
        var mapHold = ApproachCommandHandler.ExtractMissedApproachHold(procedure, fixes);

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = procedure,
            MissedApproachFixes = mapFixes,
            MapHold = mapHold,
        };

        var result = ApproachCommandHandler.BuildMissedApproachPhases(aircraft);

        Assert.Equal(2, result.Count);
        Assert.IsType<ApproachNavigationPhase>(result[0]);
        Assert.IsType<HoldingPatternPhase>(result[1]);
        var holdPhase = (HoldingPatternPhase)result[1];
        Assert.Equal("MHOLD", holdPhase.FixName);
        Assert.Null(holdPhase.MaxCircuits);
    }

    [Fact]
    public void BuildMissedApproachPhases_NoHold_NoHoldingPhase()
    {
        var aircraft = MakeAircraft();
        var procedure = MakeProcedure(); // no hold leg
        var fixes = MakeFixLookup();
        var mapFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = procedure,
            MissedApproachFixes = mapFixes,
            MapHold = null,
        };

        var result = ApproachCommandHandler.BuildMissedApproachPhases(aircraft);

        Assert.Single(result);
        Assert.IsType<ApproachNavigationPhase>(result[0]);
    }

    [Fact]
    public void ManualGoAround_WithHold_QueuesFullSequence()
    {
        var aircraft = MakeAircraft();
        var procedure = MakeProcedureWithHold();
        var fixes = MakeFixLookup();
        var mapFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure, fixes);
        var mapHold = ApproachCommandHandler.ExtractMissedApproachHold(procedure, fixes);

        aircraft.Phases!.ActiveApproach = new ApproachClearance
        {
            ApproachId = "I28",
            AirportCode = "KTEST",
            RunwayId = "28",
            FinalApproachCourse = 280,
            Procedure = procedure,
            MissedApproachFixes = mapFixes,
            MapHold = mapHold,
        };

        aircraft.Phases.Add(new FinalApproachPhase());
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var ga = new GoAroundCommand();
        PatternCommandHandler.TryGoAround(ga, aircraft);

        var phaseTypes = aircraft.Phases.Phases.Select(p => p.GetType()).ToList();
        Assert.Contains(typeof(GoAroundPhase), phaseTypes);
        Assert.Contains(typeof(ApproachNavigationPhase), phaseTypes);
        Assert.Contains(typeof(HoldingPatternPhase), phaseTypes);

        // Verify order: GoAround before ApproachNav before Holding
        int gaIdx = phaseTypes.IndexOf(typeof(GoAroundPhase));
        int navIdx = phaseTypes.IndexOf(typeof(ApproachNavigationPhase));
        int holdIdx = phaseTypes.IndexOf(typeof(HoldingPatternPhase));
        Assert.True(gaIdx < navIdx);
        Assert.True(navIdx < holdIdx);
    }
}
