using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Proto;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Bug: N513SJ (C421, KOAK 28R, filed "NIMI6 OAK V6 SAC", bare CTO) turned direct to the first
/// enroute fix (FESIK) on departure instead of holding the NIMI6 radar-vectors heading and awaiting
/// vectors.
///
/// Root cause: the NIMI radar-vectors SID was retired from the current FAA CIFP cycle, so
/// <see cref="NavigationDatabase.GetSid"/> returned null and the published vectors heading (which lives
/// only in CIFP) could not be read. The vNAS NavData.dat still carries NIMI6, so the route expanded, and
/// <see cref="DepartureClearanceHandler.ResolveDepartureRoute"/> fell through to navigating that enroute
/// route LATERALLY — turning direct to FESIK off the runway end.
///
/// Fix: when CIFP can't resolve the SID but NavData.dat recognizes the first route token as a
/// radar-vectors SID (no published lateral path), degrade to a radar-vectors departure — hold runway
/// heading and await vectors, retaining the enroute fixes as the post-vectors route (FAA 7110.65 5-8-2
/// "FLY RUNWAY HEADING").
///
/// The recording replays correctly under the test harness (which wires a supplementary CIFP bundle that
/// still contains NIMI5), so these tests force the CIFP-miss condition directly instead.
/// </summary>
public class IssueN513sjNimiRvSidCifpMissTests
{
    public IssueN513sjNimiRvSidCifpMissTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void IsRadarVectorsSidWithoutLateralPath_DetectsNimiAndOak6FromNavData()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        // Both NIMI and OAK6 are radar-vectors SIDs at KOAK (NavData body is only the colocated VOR).
        Assert.True(navDb.IsRadarVectorsSidWithoutLateralPath("NIMI6", "KOAK"));
        Assert.True(navDb.IsRadarVectorsSidWithoutLateralPath("OAK6", "KOAK"));

        // A plain fix name (not a SID) and an unknown SID must not be misclassified.
        Assert.False(navDb.IsRadarVectorsSidWithoutLateralPath("OAK", "KOAK"));
        Assert.False(navDb.IsRadarVectorsSidWithoutLateralPath("ZZZZ9", "KOAK"));
    }

    /// <summary>
    /// Forces the production condition: a NavigationDatabase with real NavData.dat (which still carries
    /// NIMI6) but a primary CIFP that lacks NIMI and NO supplementary bundle, so GetSid returns null.
    /// ResolveDepartureRoute must degrade to a radar-vectors departure (hold runway heading) rather than
    /// returning the direct-to-FESIK nav route.
    /// </summary>
    [Fact]
    public void ResolveDepartureRoute_NimiRvSid_WhenCifpMissing_HoldsRunwayHeadingNotDirectToFix()
    {
        var missDb = BuildCifpMissNavDb();
        if (missDb is null)
        {
            return;
        }

        // Only meaningful when the env's primary CIFP genuinely lacks NIMI (networked cycle).
        // Offline runs fall back to the bundled CIFP which still has NIMI5 — skip those.
        if (missDb.GetSid("KOAK", "NIMI6") is not null)
        {
            return;
        }

        using var _ = NavigationDatabase.ScopedOverride(missDb);
        var ac = MakeOakDeparture("NIMI6 OAK V6 SAC", "28R", 292.0);

        var result = DepartureClearanceHandler.ResolveDepartureRoute(new DefaultDeparture(), ac);

        Assert.NotNull(result);
        Assert.True(result.RvSidHoldRunwayHeading, "RV SID with unresolvable heading must hold runway heading, not navigate direct.");
        Assert.Null(result.DepartureHeadingMagnetic);
        Assert.Null(result.SidId);
        // The enroute fixes are retained as the post-vectors route (loaded after handoff).
        Assert.NotEmpty(result.Targets);
        Assert.Contains(result.Targets, t => t.Name == "FESIK");
    }

    /// <summary>
    /// End-to-end behavior: an InitialClimb built for an RV SID with no resolvable heading holds runway
    /// heading and does not load the enroute route until comms handoff — exactly how the sim already
    /// handles an RV SID with a published heading, but using runway heading as the held heading.
    /// </summary>
    [Fact]
    public void InitialClimb_RvSidHoldRunwayHeading_HoldsRunwayHeadingThenJoinsRouteAfterHandoff()
    {
        const double fieldElev = 9.0;
        const double runwayTrueHeading = 292.0;
        var runway = TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: runwayTrueHeading, elevationFt: fieldElev);
        var ac = new AircraftState
        {
            Callsign = "N513SJ",
            AircraftType = "C421",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(runwayTrueHeading),
            Altitude = fieldElev + 1500,
            IndicatedAirspeed = 130,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KAUN",
                Route = "NIMI6 OAK V6 SAC",
                CruiseAltitude = 5000,
                FlightRules = "IFR",
            },
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };

        var climb = new InitialClimbPhase
        {
            Departure = new DefaultDeparture(),
            AssignedAltitude = 5000,
            DepartureRoute = [new NavigationTarget { Name = "FESIK", Position = new LatLon(37.836, -122.111) }],
            SidDepartureHeadingMagnetic = null,
            RvSidHoldRunwayHeading = true,
            IsVfr = false,
            CruiseAltitude = 5000,
        };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = fieldElev,
            Logger = NullLogger.Instance,
        };
        climb.OnStart(ctx);

        // RV hold is active and the held heading is the runway heading — NOT direct to FESIK (~040°).
        Assert.True(((InitialClimbPhaseDto)climb.ToSnapshot()).RvSidActive);
        Assert.NotNull(ctx.Targets.TargetTrueHeading);
        Assert.True(
            ctx.Targets.TargetTrueHeading.Value.AbsAngleTo(runway.TrueHeading) < 1.0,
            $"Expected runway heading {runway.TrueHeading.Degrees:F0}, got {ctx.Targets.TargetTrueHeading.Value.Degrees:F0}"
        );

        // Controller still has comms: holds runway heading, no route loaded.
        Assert.False(ac.HasLeftStudentFrequency);
        for (int i = 0; i < 60; i++)
        {
            climb.OnTick(ctx);
        }

        Assert.True(ctx.Targets.TargetTrueHeading!.Value.AbsAngleTo(runway.TrueHeading) < 1.0);
        Assert.Empty(ctx.Targets.NavigationRoute);

        // After comms handoff + 5s grace, the post-vectors route loads.
        ac.HasLeftStudentFrequency = true;
        for (int i = 0; i < 5; i++)
        {
            climb.OnTick(ctx);
        }

        Assert.Single(ctx.Targets.NavigationRoute);
        Assert.Equal("FESIK", ctx.Targets.NavigationRoute[0].Name);
    }

    private static AircraftState MakeOakDeparture(string route, string runwayDesignator, double runwayHeading)
    {
        var ac = new AircraftState
        {
            Callsign = "N513SJ",
            AircraftType = "C421",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(runwayHeading),
            Altitude = 9,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KAUN",
                Route = route,
                CruiseAltitude = 5000,
                FlightRules = "IFR",
            },
        };
        ac.Phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(designator: runwayDesignator, airportId: "OAK", heading: runwayHeading, elevationFt: 9),
        };
        return ac;
    }

    /// <summary>
    /// Builds a NavigationDatabase from the real NavData.dat plus the primary current-cycle CIFP but with
    /// NO supplementary bundle, mirroring the production wiring that lacks retired-procedure fallback.
    /// Returns null if test data is unavailable.
    /// </summary>
    private static NavigationDatabase? BuildCifpMissNavDb()
    {
        var navDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", "NavData.dat");
        var cifpPath = TestVnasData.GetCifpPath();
        if (!File.Exists(navDataPath) || cifpPath is null)
        {
            return null;
        }

        var navData = NavDataSet.Parser.ParseFrom(File.ReadAllBytes(navDataPath));
        var db = new NavigationDatabase(navData, cifpPath, supplementaryCifpFilePaths: null);

        // Guard: NavData must still carry NIMI (the whole premise of the degradation path).
        return db.ResolveSidId("NIMI6") is not null ? db : null;
    }
}
