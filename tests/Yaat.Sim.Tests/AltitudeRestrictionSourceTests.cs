using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>How far under a held altitude the aircraft may settle and still count as holding it.</summary>
    private const double LevelToleranceFt = 100;

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
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
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
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
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

    /// <summary>
    /// "DM 120, CAPP I30" on OAKES3 with no descend via defers the approach to the STAR fix it joins. Until the aircraft is
    /// past that fix the approach's altitudes are not in force and it keeps its assigned 12,000; past it they bind, so an
    /// approach floor below 12,000 is no longer met by the cleared altitude and the aircraft descends for the approach
    /// before the approach phases take over at the route's end.
    /// </summary>
    [Fact]
    public void DeferredCapp_NoDvia_HoldsAssignedAltitudeUntilPastConnectingFix()
    {
        if (DeferredI30(descendVia: false) is not { } deferred)
        {
            return;
        }

        (
            DescendViaSupersedesEarlierAltitudeTests.Harness harness,
            string connectingFix,
            CifpAltitudeRestriction approachAltitude,
            string? storedConnectingFix
        ) = deferred;
        AircraftState aircraft = harness.Aircraft;

        double lowestBefore = FlyToConnectingFix(harness, connectingFix, approachAltitude);
        Assert.True(lowestBefore >= 12000 - LevelToleranceFt, $"descended to {lowestBefore:F0} ft before {connectingFix}");
        Assert.True(
            AltitudeRestrictionInForce.IsInForce(aircraft, approachAltitude),
            $"past {connectingFix} the approach altitudes are not in force"
        );

        double lowestPending = aircraft.Altitude;
        for (int t = 1; (t <= 1800) && (aircraft.Approach.PendingClearance is not null); t++)
        {
            harness.Engine.TickOneSecond();
            lowestPending = Math.Min(lowestPending, aircraft.Altitude);
        }

        _output.WriteLine(
            $"Lowest while the approach was pending past {connectingFix}: {lowestPending:F0} ft, assigned {aircraft.Targets.AssignedAltitude}"
        );
        Assert.True(lowestPending < 12000 - 300, $"held {lowestPending:F0} ft past {connectingFix}: the approach altitudes did not bring it down");
        Assert.Equal(connectingFix, storedConnectingFix);
    }

    /// <summary>
    /// "DVIA, CAPP I30" on OAKES3: the STAR's altitudes drive the descent up to the fix the deferred approach joins and the
    /// approach's own only bind past it, so the aircraft does not descend early to the approach's first segment altitude.
    /// </summary>
    [Fact]
    public void DeferredCapp_Dvia_DescendsThroughTransition()
    {
        if (DeferredI30(descendVia: true) is not { } deferred)
        {
            return;
        }

        (
            DescendViaSupersedesEarlierAltitudeTests.Harness harness,
            string connectingFix,
            CifpAltitudeRestriction approachAltitude,
            string? storedConnectingFix
        ) = deferred;
        AircraftState aircraft = harness.Aircraft;
        CifpAltitudeRestriction starBottom = Assert.IsType<CifpAltitudeRestriction>(
            Assert.Single(aircraft.Targets.NavigationRoute, t => t.Name.Equals(connectingFix, StringComparison.OrdinalIgnoreCase)).AltitudeRestriction
        );
        int starFloor = Assert.IsType<int>(starBottom.Altitude2Ft);
        _output.WriteLine($"{connectingFix}: {starBottom}");

        double lowestBefore = FlyToConnectingFix(harness, connectingFix, approachAltitude);
        Assert.True(
            lowestBefore >= starFloor - LevelToleranceFt,
            $"descended to {lowestBefore:F0} ft before {connectingFix}, below the STAR's floor there, {starFloor}"
        );
        Assert.True(
            AltitudeRestrictionInForce.IsInForce(aircraft, approachAltitude),
            $"past {connectingFix} the approach altitudes are not in force"
        );
        Assert.Equal(connectingFix, storedConnectingFix);
    }

    /// <summary>
    /// "DM 120, CAPP I30" deferred at FFIST on OAKES3, then "DCTF PARBB", an approach fix past FFIST: the direct drops FFIST
    /// from the route, but the aircraft flying an unpublished route to PARBB keeps its last assigned altitude until it is
    /// established at PARBB (AIM 5-4-7.f; 7110.65 4-8-1.a.4), so the pending clearance's connecting fix moves to PARBB and
    /// the aircraft holds 12,000 until PARBB sequences.
    /// </summary>
    [Fact]
    public void DeferredCapp_ThenDctToAnApproachFixPastTheConnectingFix_HoldsTheAssignedAltitudeUntilThatFix()
    {
        if (DeferredI30(descendVia: false) is not { } deferred)
        {
            return;
        }

        DescendViaSupersedesEarlierAltitudeTests.Harness harness = deferred.Harness;
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "DCTF PARBB");
        Assert.Equal("PARBB", aircraft.Approach.PendingClearance?.ConnectingFix);
        CifpAltitudeRestriction parbb = Assert.IsType<CifpAltitudeRestriction>(
            Assert.Single(aircraft.Targets.NavigationRoute, t => t.Name == "PARBB").AltitudeRestriction
        );

        bool reachedAssigned = false;
        double lastBeforeParbb = aircraft.Altitude;
        for (int t = 1; aircraft.Targets.NavigationRoute.Exists(f => f.Name == "PARBB"); t++)
        {
            Assert.True(t <= 1800, "The aircraft did not pass PARBB within 1800 s");
            Assert.False(AltitudeRestrictionInForce.IsInForce(aircraft, parbb), $"{t} s after the direct PARBB's altitude is in force");
            Assert.True(
                lastBeforeParbb >= 12000 - 20,
                $"{t} s after the direct, PARBB still ahead, the aircraft left 12,000 for {lastBeforeParbb:F0} ft"
            );
            reachedAssigned |= lastBeforeParbb <= 12000 + 20;
            harness.Engine.TickOneSecond();
            if (aircraft.Targets.NavigationRoute.Exists(f => f.Name == "PARBB"))
            {
                lastBeforeParbb = aircraft.Altitude;
            }
        }

        _output.WriteLine($"At PARBB {lastBeforeParbb:F0} ft, after sequencing it {aircraft.Altitude:F0} ft");
        Assert.True(reachedAssigned, "the aircraft never reached its assigned 12,000 before PARBB");
        Assert.InRange(lastBeforeParbb, 12000 - 20, 12000 + 20);
    }

    /// <summary>
    /// "CAPP H12-Z" deferred at HIRMO, HIRMO passed so the approach's altitudes bind, then "CAPP H12-Z" again: the
    /// re-clearance is the same approach the aircraft is already established on, so its altitudes stay in force and the
    /// aircraft does not level off at its assigned altitude for a segment.
    /// </summary>
    [Fact]
    public void DeferredCapp_ReclearedAfterTheGateOpened_KeepsTheApproachAltitudesInForce()
    {
        AircraftState aircraft = DeferredH12Z();
        aircraft.Targets.AssignedAltitude = 12000;
        aircraft.Altitude = 12000;
        aircraft.Targets.NavigationRoute.RemoveRange(0, aircraft.Targets.NavigationRoute.FindIndex(t => t.Name == "HIRMO") + 1);
        CifpAltitudeRestriction approachAltitude = FirstApproachAltitude(aircraft);
        Assert.True(AltitudeRestrictionInForce.IsInForce(aircraft, approachAltitude), "past HIRMO the approach altitudes are not in force");

        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        CommandResult result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);
        Assert.True(result.Success, result.Message);
        _output.WriteLine(
            $"pending {aircraft.Approach.PendingClearance?.ConnectingFix}, active {aircraft.Phases?.ActiveApproach?.ApproachId}, route "
                + string.Join(" ", aircraft.Targets.NavigationRoute.Select(t => t.Name))
        );

        foreach (
            CifpAltitudeRestriction restriction in aircraft
                .Targets.NavigationRoute.Select(t => t.AltitudeRestriction)
                .OfType<CifpAltitudeRestriction>()
        )
        {
            Assert.True(AltitudeRestrictionInForce.IsInForce(aircraft, restriction), $"after the re-clearance {restriction} is not in force");
        }
    }

    /// <summary>
    /// A deferred approach restored from a pre-V45 snapshot carries no connecting fix: its approach altitudes are in force
    /// while it is pending, as before the gate.
    /// </summary>
    [Fact]
    public void Snapshot_PreV45PendingNullConnectingFix_KeepsUngatedBehaviour()
    {
        AircraftState aircraft = DeferredH12Z();
        CifpAltitudeRestriction approachAltitude = FirstApproachAltitude(aircraft);

        JsonNode json = Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(aircraft.ToSnapshot(), RecordingJsonOptions.Default));
        Assert.IsType<JsonObject>(json["Approach"]?["PendingClearance"]).Remove("ConnectingFix");
        AircraftSnapshotDto dto = Assert.IsType<AircraftSnapshotDto>(json.Deserialize<AircraftSnapshotDto>(RecordingJsonOptions.Default));
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Null(Assert.IsType<PendingApproachInfo>(restored.Approach.PendingClearance).ConnectingFix);
        Assert.Contains(restored.Targets.NavigationRoute, t => t.Name == "HIRMO");
        Assert.True(AltitudeRestrictionInForce.IsInForce(restored, approachAltitude));
    }

    /// <summary>The fix a deferred approach joins survives a snapshot round trip through the recording JSON.</summary>
    [Fact]
    public void Snapshot_RoundTrip_KeepsConnectingFix()
    {
        AircraftState aircraft = DeferredH12Z();
        Assert.Equal("HIRMO", aircraft.Approach.PendingClearance?.ConnectingFix);

        string json = JsonSerializer.Serialize(aircraft.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = Assert.IsType<AircraftSnapshotDto>(
            JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)
        );
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal("HIRMO", restored.Approach.PendingClearance?.ConnectingFix);
    }

    /// <summary>
    /// A deferred approach restored from a snapshot gates on the route it restores with: not in force while the connecting
    /// fix is still ahead, in force once it has been sequenced.
    /// </summary>
    [Fact]
    public void Snapshot_RestoredPending_GateFollowsTheRoute()
    {
        AircraftState aircraft = DeferredH12Z();
        CifpAltitudeRestriction approachAltitude = FirstApproachAltitude(aircraft);

        var beforeFix = AircraftState.FromSnapshot(aircraft.ToSnapshot(), null);
        Assert.False(AltitudeRestrictionInForce.IsInForce(beforeFix, approachAltitude), "in force with HIRMO still ahead");

        aircraft.Targets.NavigationRoute.RemoveRange(0, aircraft.Targets.NavigationRoute.FindIndex(t => t.Name == "HIRMO") + 1);
        var pastFix = AircraftState.FromSnapshot(aircraft.ToSnapshot(), null);
        Assert.True(AltitudeRestrictionInForce.IsInForce(pastFix, approachAltitude), "not in force with HIRMO behind");
    }

    /// <summary>
    /// A V44 snapshot holding a deferred approach migrates to V45 with no connecting fix, so the approach's altitudes stay in
    /// force while it is pending, and its route restrictions keep their sources.
    /// </summary>
    [Fact]
    public void Migrator_V44_LeavesPendingConnectingFixNull()
    {
        AircraftState aircraft = DeferredH12Z();
        PendingApproachInfo pending = Assert.IsType<PendingApproachInfo>(aircraft.Approach.PendingClearance);
        aircraft.Approach.PendingClearance = new PendingApproachInfo
        {
            Clearance = pending.Clearance,
            AssignedRunway = pending.AssignedRunway,
            ConnectingFix = null,
        };
        List<int> sources =
        [
            .. aircraft.Targets.NavigationRoute.Where(t => t.AltitudeRestriction is not null).Select(t => (int)t.AltitudeRestriction!.Source),
        ];
        StateSnapshotDto snapshot = MigratableSnapshot(44, aircraft.ToSnapshot());

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(45, snapshot.SchemaVersion);
        AircraftSnapshotDto migrated = Assert.Single(snapshot.Aircraft);
        Assert.Null(Assert.IsType<PendingApproachDto>(migrated.Approach.PendingClearance).ConnectingFix);
        Assert.Equal(
            sources,
            migrated.Targets!.NavigationRoute!.Where(t => t.AltitudeRestriction is not null).Select(t => t.AltitudeRestriction!.Source)
        );
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

        Assert.Equal(45, snapshot.SchemaVersion);
        List<NavigationTargetDto> route = Assert.Single(snapshot.Aircraft).Targets!.NavigationRoute!;
        Assert.Equal((int)CifpAltitudeRestrictionSource.Atc, route[0].AltitudeRestriction!.Source);
        Assert.Equal((int)CifpAltitudeRestrictionSource.Procedure, route[1].AltitudeRestriction!.Source);
    }

    /// <summary>
    /// A pre-V44 snapshot taken mid-pattern-entry or mid-teardrop: the phase's own points (PTN-ENTRY, TDROP-OUT, TDROP-LI,
    /// TDROP-ABM) migrate as the phase's plan, System, and any other fix keeps the published procedure's source.
    /// </summary>
    [Fact]
    public void MigratorV44_StampsSystemOnPatternFixes()
    {
        AircraftState aircraft = SnapshotAircraft();
        aircraft.Targets.NavigationRoute.AddRange([
            RouteFix("PTN-ENTRY", 38.0, -122.0, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 1100)),
            RouteFix("TDROP-OUT", 38.1, -122.1, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 1350)),
            RouteFix("TDROP-LI", 38.2, -122.2, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 1150)),
            RouteFix(TeardropReentryPhase.AbeamTargetName, 38.3, -122.3, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 1100)),
            RouteFix("BANND", 38.4, -122.4, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 9000)),
        ]);

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

        List<NavigationTargetDto> route = Assert.Single(snapshot.Aircraft).Targets!.NavigationRoute!;
        Assert.Equal(
            [
                (int)CifpAltitudeRestrictionSource.System,
                (int)CifpAltitudeRestrictionSource.System,
                (int)CifpAltitudeRestrictionSource.System,
                (int)CifpAltitudeRestrictionSource.System,
                (int)CifpAltitudeRestrictionSource.Procedure,
            ],
            route.Select(f => f.AltitudeRestriction!.Source)
        );
    }

    /// <summary>
    /// A pattern entry's PTN-ENTRY point is the phase's own plan and in force while it flies: the planner descends to
    /// pattern altitude.
    /// </summary>
    [Fact]
    public void PatternEntry_SystemPointStillDrivesTheDescent()
    {
        (AircraftState aircraft, double patternAltitude) = StartPatternEntryAbovePatternAltitude();

        FlightPhysics.Update(aircraft, 1.0);
        FlightPhysics.Update(aircraft, 1.0);

        _output.WriteLine($"target {aircraft.Targets.TargetAltitude}, planned rate {aircraft.Targets.PlannedVerticalRate}");
        Assert.Equal(patternAltitude, aircraft.Targets.TargetAltitude);
        Assert.True(aircraft.Targets.PlannedVerticalRate < 0, $"no planned descent to PTN-ENTRY: {aircraft.Targets.PlannedVerticalRate}");
    }

    /// <summary>
    /// "CM 040" during the entry cancels the pattern entry (the phase clears): its PTN-ENTRY point is no longer the plan in
    /// force, so the planner stops targeting pattern altitude and the aircraft climbs to the assigned 4,000.
    /// </summary>
    [Fact]
    public void PatternEntryCleared_ByClimbMaintain_SystemPointNoLongerDrives()
    {
        (AircraftState aircraft, double patternAltitude) = StartPatternEntryAbovePatternAltitude();
        FlightPhysics.Update(aircraft, 1.0);

        DispatchLine(aircraft, "CM 040", TestDispatch.Context(Random.Shared));
        _output.WriteLine(
            $"after CM: phase {aircraft.Phases?.CurrentPhase?.Name}, route {string.Join(" ", aircraft.Targets.NavigationRoute.Select(t => t.Name))}"
        );
        FlightPhysics.Update(aircraft, 1.0);
        FlightPhysics.Update(aircraft, 1.0);

        _output.WriteLine($"target {aircraft.Targets.TargetAltitude}, planned rate {aircraft.Targets.PlannedVerticalRate}");
        Assert.Contains(aircraft.Targets.NavigationRoute, t => t.Name == "PTN-ENTRY");
        Assert.True(patternAltitude < 4000, $"setup: pattern altitude {patternAltitude:F0} must sit below the CM's 4,000");
        Assert.Equal(4000, aircraft.Targets.TargetAltitude);
        Assert.Null(aircraft.Targets.PlannedVerticalRate);
    }

    /// <summary>A jet 1,000 ft above pattern altitude with a started 45° pattern entry about 3 nm ahead.</summary>
    private static (AircraftState Aircraft, double PatternAltitude) StartPatternEntryAbovePatternAltitude()
    {
        PatternWaypoints waypoints = DefaultWaypoints();
        AircraftState aircraft = PatternAircraft();
        aircraft.Altitude = waypoints.PatternAltitude + 1000;

        var phase = new PatternEntryPhase
        {
            EntryLat = 37.05,
            EntryLon = -122.0,
            PatternAltitude = waypoints.PatternAltitude,
            Kind = PatternEntryKind.FortyFive,
        };
        PhaseList phases = Assert.IsType<PhaseList>(aircraft.Phases);
        phases.Add(phase);
        phases.Start(PatternContext(aircraft));
        Assert.IsType<PatternEntryPhase>(phases.CurrentPhase);
        Assert.Contains(aircraft.Targets.NavigationRoute, t => t.Name == "PTN-ENTRY");
        return (aircraft, waypoints.PatternAltitude);
    }

    /// <summary>
    /// A jet on OAKES3 past its entry fix, re-joined by JARR, sent <c>DM 120</c> or <c>DVIA</c>, then cleared <c>CAPP I30</c>,
    /// which defers. Returns the STAR fix the approach joined (the last fix the route kept from before the clearance), the
    /// first approach-source altitude after it, and the connecting fix the pending clearance stored.
    /// </summary>
    private (
        DescendViaSupersedesEarlierAltitudeTests.Harness Harness,
        string ConnectingFix,
        CifpAltitudeRestriction ApproachAltitude,
        string? StoredConnectingFix
    )? DeferredI30(bool descendVia)
    {
        if (DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output, 16000) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return null;
        }

        AircraftState aircraft = harness.Aircraft;
        TickUntilFirstFixPassed(harness);
        SendOk(harness, "JARR OAKES3");
        SendOk(harness, descendVia ? "DVIA" : "DM 120");
        List<string> starRoute = [.. aircraft.Targets.NavigationRoute.Select(t => t.Name)];
        SendOk(harness, "CAPP I30");
        PendingApproachInfo pending = Assert.IsType<PendingApproachInfo>(aircraft.Approach.PendingClearance);

        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        int kept = 0;
        while ((kept < Math.Min(starRoute.Count, route.Count)) && route[kept].Name.Equals(starRoute[kept], StringComparison.OrdinalIgnoreCase))
        {
            kept++;
        }

        Assert.True(kept > 0, "the deferred approach kept none of the STAR");
        string connectingFix = route[kept - 1].Name;
        _output.WriteLine(
            $"Joined at {connectingFix} (stored {pending.ConnectingFix}): "
                + string.Join(
                    " ",
                    route.Select(t => $"{t.Name}[{t.AltitudeRestriction?.Source} {t.AltitudeRestriction?.Type} {t.AltitudeRestriction?.Altitude1Ft}]")
                )
        );
        CifpAltitudeRestriction approachAltitude = route
            .Skip(kept)
            .Select(t => t.AltitudeRestriction)
            .OfType<CifpAltitudeRestriction>()
            .First(r => r.Source == CifpAltitudeRestrictionSource.Approach);
        return (harness, connectingFix, approachAltitude, pending.ConnectingFix);
    }

    /// <summary>
    /// Ticks until <paramref name="connectingFix"/> leaves the route, asserting every second before that the approach's
    /// <paramref name="approachAltitude"/> is not in force; returns the lowest altitude flown.
    /// </summary>
    private double FlyToConnectingFix(
        DescendViaSupersedesEarlierAltitudeTests.Harness harness,
        string connectingFix,
        CifpAltitudeRestriction approachAltitude
    )
    {
        AircraftState aircraft = harness.Aircraft;
        double lowest = aircraft.Altitude;
        for (int t = 1; t <= 1800; t++)
        {
            Assert.False(
                AltitudeRestrictionInForce.IsInForce(aircraft, approachAltitude),
                $"{t} s after the clearance, with {connectingFix} still ahead at {aircraft.Altitude:F0} ft, the approach altitudes are in force"
            );
            harness.Engine.TickOneSecond();
            lowest = Math.Min(lowest, aircraft.Altitude);
            if (!aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals(connectingFix, StringComparison.OrdinalIgnoreCase)))
            {
                _output.WriteLine($"Passed {connectingFix} after {t} s at {aircraft.Altitude:F0} ft, lowest {lowest:F0} ft");
                return lowest;
            }
        }

        Assert.Fail($"The aircraft did not pass {connectingFix} within 1800 s");
        return lowest;
    }

    /// <summary>A jet on EMZOH4 with EMZOH and HIRMO ahead, cleared H12-Z, which defers to HIRMO.</summary>
    private static AircraftState DeferredH12Z()
    {
        NavigationDatabase navDb = Assert.IsType<NavigationDatabase>(TestVnasData.NavigationDb);
        (double Lat, double Lon) emzoh = Assert.IsType<(double, double)>(navDb.GetFixPosition("EMZOH"));
        (double Lat, double Lon) hirmo = Assert.IsType<(double, double)>(navDb.GetFixPosition("HIRMO"));

        AircraftState aircraft = ApproachAircraft();
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "EMZOH", Position = new LatLon(emzoh.Lat, emzoh.Lon) });
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HIRMO", Position = new LatLon(hirmo.Lat, hirmo.Lon) });
        aircraft.Approach.Expected = "H12-Z";

        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        CommandResult result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Approach.PendingClearance);
        return aircraft;
    }

    private static CifpAltitudeRestriction FirstApproachAltitude(AircraftState aircraft) =>
        aircraft
            .Targets.NavigationRoute.Select(t => t.AltitudeRestriction)
            .OfType<CifpAltitudeRestriction>()
            .First(r => r.Source == CifpAltitudeRestrictionSource.Approach);

    private static StateSnapshotDto MigratableSnapshot(int schemaVersion, AircraftSnapshotDto aircraft) =>
        new()
        {
            SchemaVersion = schemaVersion,
            ElapsedSeconds = 0,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [aircraft],
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
