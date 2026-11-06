using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A TDLS "maintain" altitude stored by a bare departure clearance
/// (<see cref="AircraftProcedure.SidInitialAltitudeFt"/>) is the cleared altitude a crossing is judged against while no via
/// clearance or approach supersedes it: a bound it already meets assigns nothing and the aircraft holds the maintain on
/// either side of the SID handover, and an overlay that restores a fix's published altitude drops the altitude reverts a
/// superseded crossing left with it. A via clearance put in force afterwards is a newer altitude clearance, so the
/// maintain stops governing. Real KSJC SJC3, KOAK QUAKE2 and KOAK CNDEL5 navdata and the committed ZOA ARTCC snapshot.
/// </summary>
public class TdlsMaintainCrossingBoundTests
{
    private const int MaintainFt = 5000;
    private const double LevelToleranceFt = 50.0;

    private readonly ITestOutputHelper _output;

    public TdlsMaintainCrossingBoundTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private sealed record Departure(AircraftState Aircraft, PhaseContext Context);

    /// <summary>A departure to stage: a bare CTO under the ZOA TDLS defaults from <paramref name="Runway"/>.</summary>
    private sealed record SidSpec(string Airport, string Runway, string Destination, string Route);

    private static SidSpec Spec(string key) =>
        key switch
        {
            "QUAKE2-30" => new("KOAK", "30", "KLAX", "QUAKE2 SAU"),
            "SJC3-30L" => new("KSJC", "30L", "KLAS", "SJC3 AVE"),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };

    /// <summary>
    /// A bare CTO on SJC3 (ZOA TDLS "maintain 5,000"), its route live from takeoff: an at-or-above below the maintain is met
    /// by it, so nothing is assigned and the aircraft stays at 5,000 past MOONY.
    /// </summary>
    [Fact]
    public void Sjc3TdlsMaintain_CfixAtOrAboveBelowMaintain_AssignsNothingStaysAtMaintain()
    {
        Departure d = Stage("SJC3-30L");
        Assert.True(ReachHandoverLevel(d));
        Assert.Null(NavigationCommandHandler.PendingDepartureRoute(d.Aircraft));

        Assert.True(DispatchResult(d.Aircraft, "CFIX MOONY A030").Success);

        Assert.Null(d.Aircraft.Targets.AssignedAltitude);
        (double min, _) = FlyPast(d, "MOONY", 120);
        Assert.True(min > MaintainFt - LevelToleranceFt, $"descended toward the crossing: {min:F0}");
        Assert.InRange(d.Aircraft.Altitude, MaintainFt - LevelToleranceFt, MaintainFt + LevelToleranceFt);
    }

    /// <summary>The same departure, "CFIX MOONY B100": the maintain meets the ceiling, so the aircraft never climbs past 5,000.</summary>
    [Fact]
    public void Sjc3TdlsMaintain_CfixAtOrBelowAboveMaintain_DoesNotClimbPastMaintain()
    {
        Departure d = Stage("SJC3-30L");
        Assert.True(ReachHandoverLevel(d));

        Assert.True(DispatchResult(d.Aircraft, "CFIX MOONY B100").Success);

        Assert.Null(d.Aircraft.Targets.AssignedAltitude);
        (_, double max) = FlyPast(d, "MOONY", 120);
        Assert.True(max < MaintainFt + LevelToleranceFt, $"climbed past the maintain: {max:F0}");
    }

    /// <summary>
    /// QUAKE2 after its runway-heading hold is handed off (descent planning on, SidViaMode off): an at-or-above below the
    /// maintain must not bring the aircraft down to the crossing altitude.
    /// </summary>
    [Fact]
    public void Quake2TdlsMaintain_AfterHandoff_CfixAtOrAboveBelowMaintain_NoDescent()
    {
        Departure d = Stage("QUAKE2-30");
        Assert.True(ReachHandoverLevel(d));

        Assert.True(DispatchResult(d.Aircraft, "CFIX SAU A030").Success);

        Assert.Null(d.Aircraft.Targets.AssignedAltitude);
        (double min, _) = FlyPast(d, "SAU", 120);
        Assert.True(min > MaintainFt - LevelToleranceFt, $"descended toward the crossing: {min:F0}");
    }

    /// <summary>
    /// A climb via put in force after a bare CTO replaces the TDLS maintain as the altitude clearance (7110.65 4-5-7.h.11
    /// NOTE 1), so it does not come back once a heading ends the via: the crossing at or above 4,000 is unmet and assigns
    /// 4,000, and the planner descends to that crossing rather than to the maintain.
    /// </summary>
    [Fact]
    public void Sjc3TdlsMaintain_ThenClimbVia_ThenHeading_CfixAtOrAboveBelowMaintain_AssignsItsAltitude()
    {
        Departure d = Stage("SJC3-30L");
        Assert.True(ReachHandoverLevel(d));
        AircraftState ac = d.Aircraft;
        Assert.Equal(MaintainFt, ac.Procedure.SidInitialAltitudeFt);

        Assert.True(DispatchResult(ac, "CVIA 090").Success);
        Assert.True(
            ac.Procedure.ClimbViaTargetFt > MaintainFt + 1000,
            $"setup: the climb via top {ac.Procedure.ClimbViaTargetFt} is not above the maintain"
        );
        for (int t = 0; (t < 600) && (ac.Altitude <= MaintainFt + 2000); t++)
        {
            Tick(d);
        }

        Assert.True(ac.Altitude > MaintainFt + 2000, $"the climb via never climbed past the maintain: {ac.Altitude:F0}");
        // The heading ends the via and takes the aircraft off its route, so the crossing names a fix the SID had ahead.
        string fix = PickFix(ac);
        Assert.True(DispatchResult(ac, "FH 270").Success);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);

        Assert.True(DispatchResult(ac, $"CFIX {fix} A040").Success);
        Assert.Equal(4000, ac.Targets.AssignedAltitude);

        for (int t = 0; t < 120; t++)
        {
            Tick(d);
        }

        Assert.Equal(4000.0, ac.Targets.TargetAltitude);
        Assert.True(ac.Altitude > 4000 - LevelToleranceFt, $"descended below its crossing: {ac.Altitude:F0}");
    }

    /// <summary>
    /// The TDLS climb-via activation overlays PORTE's published altitude over an earlier crossing there and drops the
    /// crossing's altitude reverts with it. The aircraft climbs to the altitude the crossing captures as its revert.
    /// </summary>
    [Fact]
    public void TdlsClimbViaActivation_RestoringPublishedAltitude_DropsTheAltitudeReverts()
    {
        AircraftState ac = ClimbViaTopAltitudeTests.Cndel5Departure(sidAlreadyActive: false, out CifpAltitudeRestriction porte);
        const int altitudeFt = 6000;
        ac.Targets.TargetAltitude = altitudeFt;
        ac.Targets.AssignedAltitude = altitudeFt;
        Assert.True(DispatchResult(ac, "CFIX PORTE B045").Success);
        NavigationTarget fix = Assert.Single(ac.Targets.NavigationRoute, f => f.Name == "PORTE");
        Assert.Equal(altitudeFt, fix.RevertAltitude); // setup: the crossing captured both reverts
        Assert.Equal(altitudeFt, fix.RevertAssignedAltitude);

        Assert.True(NavigationCommandHandler.TryPutTdlsClimbViaInForce(ac, transmissionId: 0, exceptMaintainFt: null));

        Assert.Equal(porte, fix.AltitudeRestriction);
        Assert.Null(fix.RevertAltitude);
        Assert.Null(fix.RevertAssignedAltitude);
    }

    /// <summary>
    /// Flies until <paramref name="fix"/> leaves the live route, then <paramref name="after"/> s more; the lowest and highest
    /// altitude flown.
    /// </summary>
    private static (double Min, double Max) FlyPast(Departure d, string fix, int after)
    {
        double min = d.Aircraft.Altitude,
            max = d.Aircraft.Altitude;
        for (int t = 0; (t < 1500) && d.Aircraft.Targets.NavigationRoute.Exists(f => f.Name == fix); t++)
        {
            Tick(d);
            min = Math.Min(min, d.Aircraft.Altitude);
            max = Math.Max(max, d.Aircraft.Altitude);
        }

        Assert.DoesNotContain(d.Aircraft.Targets.NavigationRoute, f => f.Name == fix);
        for (int t = 0; t < after; t++)
        {
            Tick(d);
            min = Math.Min(min, d.Aircraft.Altitude);
            max = Math.Max(max, d.Aircraft.Altitude);
        }

        return (min, max);
    }

    /// <summary>The effective-route fix the crossing names: the first not yet within 8 nm, else the last.</summary>
    private static string PickFix(AircraftState ac)
    {
        List<NavigationTarget> route = NavigationCommandHandler.EffectiveRoute(ac);
        Assert.NotEmpty(route);
        NavigationTarget? far = route.Find(f => GeoMath.DistanceNm(ac.Position, f.Position) >= 8.0);
        return (far ?? route[^1]).Name;
    }

    // ---- Staging ----

    /// <summary>A real bare CTO under the ZOA TDLS defaults from holding short, then airborne past the DER in the initial climb.</summary>
    private Departure Stage(string key)
    {
        SidSpec spec = Spec(key);
        RunwayInfo rwy = DepartureEnd(spec.Airport, spec.Runway);
        AircraftState ac = HoldingShort(spec.Airport, spec.Destination, spec.Route, rwy);
        CommandResult cto = DispatchResult(ac, "CTO");
        Assert.True(cto.Success, cto.Message);

        Assert.Equal(MaintainFt, ac.Procedure.SidInitialAltitudeFt);
        Assert.Null(ac.Targets.AssignedAltitude);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);

        ac.IsOnGround = false;
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.EndLatitude, rwy.EndLongitude), rwy.TrueHeading, 0.3);
        ac.TrueHeading = rwy.TrueHeading;
        ac.Altitude = rwy.ElevationFt + 300;
        ac.IndicatedAirspeed = 170;
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
        Assert.IsType<PhaseList>(ac.Phases).SkipTo<InitialClimbPhase>(ctx);
        var d = new Departure(ac, ctx);
        Tick(d);
        return d;
    }

    /// <summary>Flies until the SID handover has loaded the live route and the aircraft is level at the maintain.</summary>
    private bool ReachHandoverLevel(Departure d)
    {
        AircraftState ac = d.Aircraft;
        for (int t = 0; t < 1200; t++)
        {
            bool handedOver = (NavigationCommandHandler.PendingDepartureRoute(ac) is null) && (ac.Targets.NavigationRoute.Count > 0);
            if (handedOver && (Math.Abs(ac.Altitude - MaintainFt) < 20))
            {
                _output.WriteLine($"handover done and level at t={t}");
                return true;
            }

            Tick(d);
            ReleaseRvHoldIfStuck(d, t);
        }

        return false;
    }

    /// <summary>A radar-vectors SID holds its heading until comms are handed off: hand them off after 90 s so the route loads.</summary>
    private static void ReleaseRvHoldIfStuck(Departure d, int t)
    {
        AircraftState ac = d.Aircraft;
        if ((t == 90) && (ac.Phases?.CurrentPhase is InitialClimbPhase) && (NavigationCommandHandler.PendingDepartureRoute(ac) is not null))
        {
            ac.TunedPosition = TrackOwner.Elsewhere;
        }
    }

    private static void Tick(Departure d)
    {
        PhaseRunner.Tick(d.Aircraft, d.Context);
        FlightPhysics.Update(d.Aircraft, 1.0);
    }

    private CommandResult DispatchResult(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(
            Assert.IsType<CompoundCommand>(parsed.Value),
            aircraft,
            TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZoa())
        );
        _output.WriteLine($"{text} -> {result.Success} '{result.Message}'");
        return result;
    }

    private static ArtccConfigRoot LoadZoa() => Assert.IsType<ArtccConfigRoot>(TestArtccConfig.LoadZoa());

    private static AircraftState HoldingShort(string airport, string destination, string route, RunwayInfo rwy)
    {
        var ac = new AircraftState
        {
            Callsign = "SWA1852",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airport,
                Destination = destination,
                Route = route,
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        ac.Phases.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 10,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = rwy.Designator,
                },
                HoldShortArrival.HeldShort
            )
        );
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        return ac;
    }

    private static RunwayInfo DepartureEnd(string airport, string designator)
    {
        RunwayInfo phys = Assert.IsType<RunwayInfo>(
            NavigationDatabase
                .Instance.GetRunways(airport)
                .Concat(NavigationDatabase.Instance.GetRunways(NavigationDatabase.NormalizeAirport(airport)))
                .FirstOrDefault(r => (r.Id.End1 == designator) || (r.Id.End2 == designator))
        );
        bool isEnd2 = phys.Id.End1 != designator;
        return new RunwayInfo
        {
            AirportId = airport,
            Id = phys.Id,
            Designator = designator,
            Lat1 = isEnd2 ? phys.Lat2 : phys.Lat1,
            Lon1 = isEnd2 ? phys.Lon2 : phys.Lon1,
            Elevation1Ft = isEnd2 ? phys.Elevation2Ft : phys.Elevation1Ft,
            TrueHeading1 = isEnd2 ? phys.TrueHeading2 : phys.TrueHeading1,
            Lat2 = isEnd2 ? phys.Lat1 : phys.Lat2,
            Lon2 = isEnd2 ? phys.Lon1 : phys.Lon2,
            Elevation2Ft = isEnd2 ? phys.Elevation1Ft : phys.Elevation2Ft,
            TrueHeading2 = isEnd2 ? phys.TrueHeading1 : phys.TrueHeading2,
            WidthFt = phys.WidthFt,
        };
    }
}
