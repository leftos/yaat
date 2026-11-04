using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Every route altitude restriction records who wrote it: a published procedure's, a published approach segment's, a
/// controller's crossing, or a point a phase inserts as the pilot's own plan. This class pins the stamp at each writer,
/// the snapshot round trip that carries it, and the pre-V44 migration that recovers it from the crossing stamp.
/// </summary>
public class AltitudeRestrictionSourceTests
{
    private readonly ITestOutputHelper _output;

    public AltitudeRestrictionSourceTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>"CFIX GRUDG A110" on OAKES3 joined by JARR: the crossing GRUDG carries is the controller's.</summary>
    [Fact]
    public void Cfix_StampsAtc()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        TickUntilFirstFixPassed(harness);
        SendOk(harness, "JARR OAKES3");
        for (int t = 0; t < 5; t++)
        {
            harness.Engine.TickOneSecond();
        }

        SendOk(harness, "CFIX GRUDG A110");

        NavigationTarget fix = Assert.Single(aircraft.Targets.NavigationRoute, f => f.Name == "GRUDG");
        Assert.NotNull(fix.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionSource.Atc, fix.AltitudeRestriction.Source);
    }

    /// <summary>"DCTF PORTE/A080" on a CNDEL5 departure: the crossing the constrained direct-to writes is the controller's.</summary>
    [Fact]
    public void ConstrainedDctf_StampsAtc()
    {
        AircraftState aircraft = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out _);
        DispatchContext ctx = TestDispatch.Context(Random.Shared);
        DispatchLine(aircraft, "CM 100", ctx);
        DispatchLine(aircraft, "DCTF PORTE/A080", ctx);

        NavigationTarget fix = Assert.Single(aircraft.Targets.NavigationRoute, f => f.Name == "PORTE");
        Assert.NotNull(fix.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionSource.Atc, fix.AltitudeRestriction.Source);
    }

    /// <summary>"JARR OAKES3" onto KOAK's real STAR: every altitude it puts on the route is the STAR's own published one.</summary>
    [Fact]
    public void Jarr_PublishedRestrictions_AreProcedure()
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState aircraft = harness.Aircraft;
        TickUntilFirstFixPassed(harness);
        SendOk(harness, "JARR OAKES3");

        List<NavigationTarget> restricted = [.. aircraft.Targets.NavigationRoute.Where(t => t.AltitudeRestriction is not null)];
        Assert.NotEmpty(restricted);
        Assert.All(restricted, t => Assert.Equal(CifpAltitudeRestrictionSource.Procedure, t.AltitudeRestriction!.Source));
    }

    /// <summary>A pattern entry's PTN-ENTRY descent is the pilot's own plan, so the phase writes the point's altitude itself.</summary>
    [Fact]
    public void PatternEntry_StampsSystem()
    {
        PatternWaypoints waypoints = DefaultWaypoints();
        AircraftState aircraft = PatternAircraft();

        var phase = new PatternEntryPhase
        {
            EntryLat = 37.05,
            EntryLon = -122.0,
            PatternAltitude = waypoints.PatternAltitude,
            Kind = PatternEntryKind.FortyFive,
        };

        phase.OnStart(PatternContext(aircraft));

        NavigationTarget entry = Assert.Single(aircraft.Targets.NavigationRoute, t => t.Name == "PTN-ENTRY");
        Assert.NotNull(entry.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionSource.System, entry.AltitudeRestriction.Source);
    }

    /// <summary>The teardrop re-entry's three descent points are likewise the phase's own plan.</summary>
    [Fact]
    public void TeardropReentry_StampsSystem()
    {
        PatternWaypoints waypoints = DefaultWaypoints();
        AircraftState aircraft = PatternAircraft();

        var phase = new TeardropReentryPhase { Waypoints = waypoints };

        phase.OnStart(PatternContext(aircraft));

        List<NavigationTarget> restricted = [.. aircraft.Targets.NavigationRoute.Where(t => t.AltitudeRestriction is not null)];
        Assert.Equal(3, restricted.Count);
        Assert.All(restricted, t => Assert.Equal(CifpAltitudeRestrictionSource.System, t.AltitudeRestriction!.Source));
    }

    /// <summary>A deferred approach clearance appends the approach's fixes after the connecting fix, at published approach altitudes.</summary>
    [Fact]
    public void ApproachClearance_AppendedFixes_StampApproach()
    {
        NavigationDatabase navDb = Assert.IsType<NavigationDatabase>(TestVnasData.NavigationDb);
        Assert.NotNull(navDb.GetApproach("KOAK", "H12-Z"));

        (double Lat, double Lon)? emzoh = navDb.GetFixPosition("EMZOH");
        (double Lat, double Lon)? hirmo = navDb.GetFixPosition("HIRMO");
        Assert.NotNull(emzoh);
        Assert.NotNull(hirmo);

        AircraftState aircraft = ApproachAircraft();
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "EMZOH", Position = new LatLon(emzoh.Value.Lat, emzoh.Value.Lon) });
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmo.Value.Lat, hirmo.Value.Lon) });
        aircraft.Approach.Expected = "H12-Z";

        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        CommandResult result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Approach.PendingClearance);

        int hirmoIndex = aircraft.Targets.NavigationRoute.FindIndex(t => t.Name == "HIRMO");
        Assert.True(hirmoIndex >= 0);
        List<NavigationTarget> appended = [.. aircraft.Targets.NavigationRoute.Skip(hirmoIndex + 1)];
        Assert.NotEmpty(appended);

        List<NavigationTarget> restricted = [.. appended.Where(t => t.AltitudeRestriction is not null)];
        Assert.NotEmpty(restricted);
        Assert.All(restricted, t => Assert.Equal(CifpAltitudeRestrictionSource.Approach, t.AltitudeRestriction!.Source));
    }

    /// <summary>A route carrying one restriction of each source keeps each through a snapshot round trip.</summary>
    [Fact]
    public void Snapshot_RoundTrip_KeepsSource()
    {
        AircraftState aircraft = SnapshotAircraft();
        aircraft.Targets.NavigationRoute.AddRange([
            RouteFix(
                "CTRL",
                38.0,
                -122.0,
                new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 11000) { Source = CifpAltitudeRestrictionSource.Atc }
            ),
            RouteFix(
                "APPR",
                38.1,
                -122.1,
                new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 4000) { Source = CifpAltitudeRestrictionSource.Approach }
            ),
            RouteFix(
                "SYST",
                38.2,
                -122.2,
                new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 1100) { Source = CifpAltitudeRestrictionSource.System }
            ),
            RouteFix("PUBL", 38.3, -122.3, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrBelow, 10000)),
        ]);

        AircraftSnapshotDto dto = aircraft.ToSnapshot();
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(
            [
                CifpAltitudeRestrictionSource.Atc,
                CifpAltitudeRestrictionSource.Approach,
                CifpAltitudeRestrictionSource.System,
                CifpAltitudeRestrictionSource.Procedure,
            ],
            restored.Targets.NavigationRoute.Select(t => t.AltitudeRestriction!.Source)
        );
    }

    /// <summary>The procedure-leg map carries the source through its own DTO round trip.</summary>
    [Fact]
    public void ProcedureLeg_RoundTrip_KeepsSource()
    {
        var leg = new ProcedureLeg
        {
            Type = ProcedureLegType.CourseToFix,
            FixName = "GRUDG",
            FixPosition = new LatLon(38.0, -122.0),
            AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 11000) { Source = CifpAltitudeRestrictionSource.Atc },
        };

        ProcedureLegDto dto = leg.ToSnapshot();
        var restored = ProcedureLeg.FromSnapshot(dto);

        Assert.NotNull(restored.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionSource.Atc, restored.AltitudeRestriction.Source);
    }

    /// <summary>An approach-navigation phase's fix list carries each fix's source through the phase's own snapshot.</summary>
    [Fact]
    public void ApproachNavigationPhase_RoundTrip_KeepsSource()
    {
        var phase = new ApproachNavigationPhase
        {
            Fixes =
            [
                new ApproachFix(
                    "HIRMO",
                    37.5,
                    -121.8,
                    new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 4000) { Source = CifpAltitudeRestrictionSource.Approach }
                ),
            ],
        };

        ApproachNavigationPhaseDto dto = Assert.IsType<ApproachNavigationPhaseDto>(phase.ToSnapshot());
        var restored = ApproachNavigationPhase.FromSnapshot(dto);

        Assert.NotNull(restored.Fixes[0].Altitude);
        Assert.Equal(CifpAltitudeRestrictionSource.Approach, restored.Fixes[0].Altitude!.Source);
    }

    /// <summary>A published restriction's default source stays out of the snapshot JSON; another source is written as its code.</summary>
    [Theory]
    [InlineData(CifpAltitudeRestrictionSource.Procedure, null)]
    [InlineData(CifpAltitudeRestrictionSource.Atc, "\"Source\":2")]
    public void NavigationTargetDto_Json_WritesSourceOnlyWhenNotProcedure(CifpAltitudeRestrictionSource source, string? expectedMember)
    {
        var dto = new NavigationTargetDto
        {
            Name = "GRUDG",
            Position = new LatLon(38.0, -122.0),
            IsFlyOver = false,
            AltitudeRestriction = new AltitudeRestrictionDto
            {
                Type = (int)CifpAltitudeRestrictionType.At,
                Altitude1 = 11000,
                Source = (int)source,
            },
        };

        string json = JsonSerializer.Serialize(dto, RecordingJsonOptions.Default);

        if (expectedMember is null)
        {
            Assert.DoesNotContain("Source", json, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expectedMember, json, StringComparison.Ordinal);
        }
    }

    /// <summary>A pre-V44 snapshot's crossing stamp is the only record of an Atc restriction; the migrated route keeps it.</summary>
    [Fact]
    public void Migrator_V43_StampsAtcFromTheCrossingStamp()
    {
        AircraftState aircraft = SnapshotAircraft();
        aircraft.Targets.NavigationRoute.AddRange([
            RouteFix("GRUDG", 38.0, -122.0, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 11000)),
            RouteFix("BANND", 38.1, -122.1, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 9000)),
        ]);
        aircraft.Targets.NavigationRoute[0].CrossingTransmissionId = 4;

        var snapshot = new StateSnapshotDto
        {
            SchemaVersion = 43,
            ElapsedSeconds = 0,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [aircraft.ToSnapshot()],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "altitude-restriction-source",
                ScenarioName = "source migration",
                RngSeed = 42,
                ElapsedSeconds = 0,
                AutoClearedToLand = false,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
                ValidateDctFixes = false,
                IsPaused = false,
                SimRate = 1.0,
                AutoAcceptDelaySeconds = 0,
                IsStudentTowerPosition = false,
            },
        };

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(44, snapshot.SchemaVersion);
        List<NavigationTargetDto> route = Assert.Single(snapshot.Aircraft).Targets!.NavigationRoute!;
        Assert.Equal((int)CifpAltitudeRestrictionSource.Atc, route[0].AltitudeRestriction!.Source);
        Assert.Equal((int)CifpAltitudeRestrictionSource.Procedure, route[1].AltitudeRestriction!.Source);
    }

    private static void TickUntilFirstFixPassed(DescendViaSupersedesEarlierAltitudeTests.Harness harness)
    {
        string first = harness.Aircraft.Targets.NavigationRoute[0].Name;
        for (int t = 1; t <= 1800; t++)
        {
            harness.Engine.TickOneSecond();
            if (!harness.Aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals(first, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        Assert.Fail($"The aircraft did not pass {first} within 1800 s");
    }

    private static void SendOk(DescendViaSupersedesEarlierAltitudeTests.Harness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private static void DispatchLine(AircraftState aircraft, string text, DispatchContext ctx)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, ctx);
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }

    private static NavigationTarget RouteFix(string name, double lat, double lon, CifpAltitudeRestriction restriction) =>
        new()
        {
            Name = name,
            Position = new LatLon(lat, lon),
            AltitudeRestriction = restriction,
        };

    private static AircraftState SnapshotAircraft() =>
        new()
        {
            Callsign = "AAL100",
            AircraftType = "B738/L",
            Position = new LatLon(37.7213, -122.2208),
            TrueHeading = new TrueHeading(270),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KLAX",
                HasFlightPlan = true,
            },
        };

    private static AircraftState ApproachAircraft() =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(320),
            Altitude = 5000,
            Position = new LatLon(37.5, -121.8),
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", Route = "KBUR.OROSZ2.COREZ..RGOOD.EMZOH4.KOAK" },
            Procedure = new AircraftProcedure { DestinationRunway = "12" },
        };

    private static RunwayInfo PatternRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

    private static PatternWaypoints DefaultWaypoints()
    {
        RunwayInfo rwy = PatternRunway();
        return PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategory.Jet, "", 0, PatternDirection.Left)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = null,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
    }

    private static AircraftState PatternAircraft() =>
        new()
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(280),
            Altitude = 1100,
            IndicatedAirspeed = 200,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
            Phases = new PhaseList(),
        };

    private static PhaseContext PatternContext(AircraftState aircraft)
    {
        RunwayInfo rwy = PatternRunway();
        return new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
    }
}
