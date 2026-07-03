using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #12: aircraft with no filed destination airport,
/// once given a pattern entry or landed at the controller's field, must pick up
/// that airport implicitly so the ground layout is assigned — otherwise the
/// aircraft stalls on the runway after touchdown ("no ground layout, will stop
/// immediately") and can never be taxied.
///
/// Root cause: <see cref="SimulationEngine.ResolveGroundLayout"/> resolved the
/// layout only from <c>FlightPlan.Departure</c> / <c>FlightPlan.Destination</c>.
/// Cold-call VFR aircraft (pattern work, full-stop requests) file neither, so the
/// resolver returned null. The fix falls back to the assigned arrival runway's
/// airport, then the spawn-time operational airport context
/// (<see cref="AircraftState.AirportId"/>), without writing into the flight plan.
///
/// Reference bundle (the real-world report this fix is for):
/// <c>TestData/e5c26ff62464.zip</c>
/// — S2-OAK-5 "Practical Exam Preparation / Advanced Concepts". In it the
/// controller had to manually amend Destination=KOAK on N655EX (t=1583) and
/// N10194 (t=356) before they would taxi after landing; a straight replay
/// therefore shows them working (the workaround is baked in), so the regression
/// test below is constructed for determinism rather than replayed.
/// </summary>
public class Issue12ImplicitDestinationLayoutTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    private static AircraftState MakeInboundVfr(RunwayInfo? assignedRunway, string airportId)
    {
        var ac = new AircraftState
        {
            Callsign = "N12345",
            AircraftType = "C172",
            Position = new LatLon(37.70, -122.22),
            TrueHeading = new TrueHeading(280),
            Altitude = 1500,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            // Cold-call VFR: no departure, no destination filed.
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
            AirportId = airportId,
        };

        if (assignedRunway is not null)
        {
            ac.Phases = new PhaseList { AssignedRunway = assignedRunway };
        }

        return ac;
    }

    /// <summary>
    /// No filed departure/destination, but an arrival runway has been assigned
    /// (pattern entry). The layout must resolve from the runway's airport, even
    /// with no spawn-time airport context.
    /// </summary>
    [Fact]
    public void ResolveGroundLayout_NoFlightPlan_AssignedRunway_UsesRunwayAirport()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        Assert.NotNull(rwy);

        var ac = MakeInboundVfr(rwy, airportId: "");

        var layout = engine.ResolveGroundLayout(ac);

        Assert.NotNull(layout);
        Assert.Equal("OAK", layout.AirportId);
    }

    /// <summary>
    /// No filed departure/destination and no assigned runway yet, but the
    /// aircraft carries the scenario's operational airport context. The layout
    /// must resolve from that.
    /// </summary>
    [Fact]
    public void ResolveGroundLayout_NoFlightPlan_AirportContext_UsesSpawnAirport()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var ac = MakeInboundVfr(assignedRunway: null, airportId: "OAK");

        var layout = engine.ResolveGroundLayout(ac);

        Assert.NotNull(layout);
        Assert.Equal("OAK", layout.AirportId);
    }

    /// <summary>
    /// Guard: with no flight plan, no assigned runway, and no airport context,
    /// there is genuinely no airport to infer — the resolver must return null
    /// rather than fabricate a layout.
    /// </summary>
    [Fact]
    public void ResolveGroundLayout_NoContextAtAll_ReturnsNull()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var ac = MakeInboundVfr(assignedRunway: null, airportId: "");

        Assert.Null(engine.ResolveGroundLayout(ac));
    }

    /// <summary>
    /// Control: an aircraft with a filed departure resolves to that airport's
    /// layout exactly as before. The implicit-airport fallback must not perturb
    /// the existing departure/destination path.
    /// </summary>
    [Fact]
    public void ResolveGroundLayout_FiledDeparture_Unchanged()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var ac = MakeInboundVfr(assignedRunway: null, airportId: "");
        ac.FlightPlan.Departure = "OAK";

        var layout = engine.ResolveGroundLayout(ac);

        Assert.NotNull(layout);
        Assert.Equal("OAK", layout.AirportId);
    }

    /// <summary>
    /// Full E2E through the live tick loop: a VFR cold-call (no filed departure or
    /// destination) on short final to OAK 28R, cleared to land. After touchdown it
    /// must exit the runway. The ground layout is never set on the aircraft — it is
    /// resolved per-tick by <see cref="SimulationEngine"/>. Without the implicit
    /// fallback the resolver returns null, RunwayExitPhase bails ("no ground layout,
    /// will stop immediately"), and the aircraft rolls to the runway end and stops
    /// without ever reaching HoldingAfterExit. With the fix it picks up OAK from its
    /// assigned runway / airport context and exits onto a taxiway hold-short.
    ///
    /// Mirrors the real-world bundle (N655EX / N10194 needed a manual KOAK amendment
    /// before they would taxi) but is fully constructed for determinism.
    /// </summary>
    [Fact]
    public void InboundVfr_NoDestination_ExitsRunwayAfterLanding()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        Assert.NotNull(rwy);

        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "issue12",
            ScenarioName = "issue12",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };

        // Place the aircraft on a stabilized short final ~0.6 nm out, on the 28R
        // centerline, ~220 ft AGL, at C172 approach speed.
        var approachHeading = new TrueHeading((rwy.TrueHeading.Degrees + 180.0) % 360.0);
        var fafPoint = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, approachHeading, 0.6);

        var ac = new AircraftState
        {
            Callsign = "N12345",
            AircraftType = "C172",
            Position = new LatLon(fafPoint.Lat, fafPoint.Lon),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt + 220,
            IndicatedAirspeed = 68,
            IsOnGround = false,
            // Cold-call VFR: no departure, no destination filed.
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
            AirportId = "OAK",
        };

        var phases = new PhaseList
        {
            AssignedRunway = rwy,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = rwy.Designator,
        };
        phases.Add(new FinalApproachPhase());
        phases.Add(new LandingPhase());
        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        // Deliberately leave Ground.Layout null — the per-tick resolver must supply it.
        Assert.Null(ac.Ground.Layout);

        engine.World.AddAircraft(ac);

        bool exitedRunway = false;
        bool touchedDown = false;
        string? lastPhase = null;
        for (int t = 1; t <= 240; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("N12345");
            if (ac is null)
            {
                break;
            }

            touchedDown |= ac.IsOnGround;
            lastPhase = ac.Phases?.CurrentPhase?.GetType().Name;
            if (t % 15 == 0)
            {
                output.WriteLine($"t={t}: alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} onGround={ac.IsOnGround} phase={lastPhase}");
            }

            if (lastPhase == "HoldingAfterExitPhase")
            {
                exitedRunway = true;
                output.WriteLine($"t={t}: runway exit complete at ({ac.Position.Lat:F6},{ac.Position.Lon:F6})");
                break;
            }
        }

        Assert.True(touchedDown, $"Aircraft never touched down (last phase: {lastPhase}) — test setup issue, not the bug under test.");
        Assert.True(
            exitedRunway,
            $"Aircraft never exited the runway after landing (last phase: {lastPhase}). Without an implicit destination airport the ground layout is null and the aircraft stalls on the runway."
        );
    }

    // --- S1-OAK-7 report: the mirror of the issue-#12 case above. There departure AND
    // destination were empty, so the resolver returned null. Here a parked departure has a
    // filed VFR destination (KSMF) but no departure, so the destination layout pre-empts the
    // physical-airport fallback: ResolveGroundLayout returned SMF's layout while N248ZV sat
    // parked at OAK, and every taxiway/parking lookup failed ("Cannot find taxiway F in
    // layout", "Parking/spot 'OLD1' not found in airport layout"). An aircraft on the ground
    // must taxi on the airport its wheels are on, regardless of what the flight plan names.
    //
    // Reference bundle: "S1-OAK-7 | Evaluation Preparation" (VFR FP to KSMF created via CRC
    // STARS while parked at OAK). As with issue #12 the recording bakes in the manual
    // KOAK-amend workaround, so these are constructed for determinism rather than replayed.

    private static AircraftState MakeParkedDeparture(string airportId, string filedDestination)
    {
        return new AircraftState
        {
            Callsign = "N248ZV",
            AircraftType = "C150",
            Position = new LatLon(37.7389, -122.2256), // OAK North Field parking
            TrueHeading = new TrueHeading(7),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = filedDestination },
            AirportId = airportId,
        };
    }

    /// <summary>
    /// An aircraft physically on the ground at OAK with a filed VFR destination of KSMF and no
    /// filed departure must resolve to OAK's layout — the airport its wheels are on — not the
    /// destination's. Before the fix the resolver returned SMF because a filed destination
    /// pre-empted the physical-airport fallback.
    /// </summary>
    [Fact]
    public void ResolveGroundLayout_OnGround_FiledDestinationElsewhere_UsesPhysicalAirport()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var ac = MakeParkedDeparture(airportId: "OAK", filedDestination: "KSMF");

        var layout = engine.ResolveGroundLayout(ac);

        Assert.NotNull(layout);
        Assert.Equal("OAK", layout.AirportId);
    }

    /// <summary>
    /// The actual path from the report: a parked OAK aircraft has a VFR flight plan to KSMF
    /// created (an <see cref="SimulationEngine.AmendFlightPlan"/> with a destination but no
    /// departure). Its ground layout must remain OAK, and an OAK-only parking spot (OLD1) must
    /// still resolve — proving the symptom (WARPG @OLD1 failed against the wrong layout), not
    /// just the field.
    /// </summary>
    [Fact]
    public void AmendFlightPlan_ParkedDeparture_FiledDestinationElsewhere_KeepsPhysicalLayout()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var groundData = new TestAirportGroundData();
        var engine = new SimulationEngine(groundData);

        var oakLayout = groundData.GetLayout("OAK");
        Assert.NotNull(oakLayout);

        var ac = MakeParkedDeparture(airportId: "OAK", filedDestination: "");
        ac.Ground.Layout = oakLayout;
        engine.World.AddAircraft(ac);

        engine.AmendFlightPlan("N248ZV", new FlightPlanAmendment(Destination: "KSMF", FlightRules: "VFR"));

        ac = engine.FindAircraft("N248ZV");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Ground.Layout);
        Assert.Equal("OAK", ac.Ground.Layout.AirportId);
        Assert.NotNull(ac.Ground.Layout.FindSpotByName("OLD1"));
    }
}
