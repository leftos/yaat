using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #405: the GeoJSON <c>turnoff</c> property carries one value per
/// physical runway and the parser flips it for the reciprocal end, so KMIA "8L - 26R"
/// (<c>turnoff: left</c>) sends 8L arrivals left but 26R arrivals right — while the facility wants
/// 26R arrivals to vacate left too. The <c>exitDirections</c> sidecar section overrides the default
/// exit side per runway end; the shipped ZMA sidecar carries the 26R → left entry.
///
/// No recording — uses the real MIA layout (TestData/mia.geojson) plus the sidecar catalog.
/// </summary>
public class Issue405ExitDirectionOverrideTests(ITestOutputHelper output)
{
    private const string Callsign = "AAL405";

    /// <summary>
    /// Unit tier: a hand-built catalog override for 26R must win over the flipped GeoJSON turnoff,
    /// while un-overridden ends keep their GeoJSON-derived sides.
    /// </summary>
    [Fact]
    public void InferPreferredExitSide_SidecarOverride_WinsOverFlippedGeoJsonTurnoff()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("MIA");
        if (layout is null)
        {
            return;
        }

        var rwy26R = NavigationDatabase.Instance.GetRunway("MIA", "26R");
        var rwy8L = NavigationDatabase.Instance.GetRunway("MIA", "8L");
        Assert.NotNull(rwy26R);
        Assert.NotNull(rwy8L);

        var catalog = new AirportSidecarCatalog([
            new AirportSidecar("KMIA") { ExitDirections = [new ExitDirectionOverride("26R", ExitSide.Left, null)] },
        ]);

        // Baseline sanity: the GeoJSON authors "turnoff": "left" on "8L - 26R", so without the
        // override 8L resolves Left and 26R gets the flipped Right.
        Assert.Equal(ExitSide.Left, layout.InferPreferredExitSide("8L", rwy8L.TrueHeading, AirportSidecarCatalog.Empty));
        Assert.Equal(ExitSide.Right, layout.InferPreferredExitSide("26R", rwy26R.TrueHeading, AirportSidecarCatalog.Empty));

        // The override flips 26R to Left and leaves 8L alone.
        Assert.Equal(ExitSide.Left, layout.InferPreferredExitSide("26R", rwy26R.TrueHeading, catalog));
        Assert.Equal(ExitSide.Left, layout.InferPreferredExitSide("8L", rwy8L.TrueHeading, catalog));
    }

    /// <summary>
    /// Shipped-data tier: the bundled ZMA sidecar (Data/ARTCCs/ZMA/Airports/mia.json) carries the
    /// 26R → left entry, and the parameterless overload reads it through the global
    /// NavigationDatabase — the path LandingPhase/RunwayExitPhase take.
    /// </summary>
    [Fact]
    public void InferPreferredExitSide_ShippedZmaSidecar_Sends26RLeft()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("MIA");
        if (layout is null)
        {
            return;
        }

        var rwy26R = NavigationDatabase.Instance.GetRunway("MIA", "26R");
        Assert.NotNull(rwy26R);

        Assert.Equal(ExitSide.Left, NavigationDatabase.Instance.AirportSidecars.GetExitDirection("KMIA", "26R"));
        Assert.Equal(ExitSide.Left, layout.InferPreferredExitSide("26R", rwy26R.TrueHeading));
    }

    /// <summary>
    /// Full-sim tier: a B738 landing 26R with no exit instruction must vacate on the left
    /// (south) side per the sidecar override — before the fix it exits right per the flipped
    /// GeoJSON turnoff.
    /// </summary>
    [Fact]
    public void B738_Landing26R_NoExitInstruction_ExitsLeft()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var navDb = NavigationDatabase.Instance;
        var rwy26R = navDb.GetRunway("MIA", "26R");
        if (rwy26R is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("MIA");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var engine = new SimulationEngine(new TestAirportGroundData());

        double reciprocal = (rwy26R.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(rwy26R.ThresholdLatitude, rwy26R.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = rwy26R.TrueHeading,
            Altitude = rwy26R.ElevationFt + 318,
            IndicatedAirspeed = 145,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "MIA",
                Destination = "MIA",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
        };

        aircraft.Phases = new PhaseList { AssignedRunway = rwy26R };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(ctx);

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-issue405-mia-26r-exit",
            ScenarioName = "MIA Rwy 26R Exit Direction Override Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "MIA",
        };

        var clear = engine.SendCommand(Callsign, "CLAND");
        Assert.True(clear.Success, $"CLAND failed: {clear.Message}");

        var threshold = LandingThreshold.Resolve(rwy26R, layout);
        string? exitTaxiway = null;
        double stoppedCrossTrackFt = double.NaN;

        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "none";

            if (aircraft.Ground.CurrentTaxiway is not null && exitTaxiway is null)
            {
                exitTaxiway = aircraft.Ground.CurrentTaxiway;
                output.WriteLine($"t={t}: committed to exit {exitTaxiway} (gs={aircraft.GroundSpeed:F1}, phase={phase})");
            }

            if ((aircraft.GroundSpeed <= 1.0) && (t > 30))
            {
                stoppedCrossTrackFt = GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, threshold, rwy26R.TrueHeading) * 6076.12;
                output.WriteLine(
                    $"t={t}: stopped, crossTrack={stoppedCrossTrackFt:F0} ft (positive=right of 26R), "
                        + $"phase={phase}, taxiway={aircraft.Ground.CurrentTaxiway ?? "(none)"}"
                );
                break;
            }
        }

        Assert.NotNull(exitTaxiway);
        Assert.False(double.IsNaN(stoppedCrossTrackFt), "Aircraft never came to a stop after landing within 400s");
        Assert.True(
            stoppedCrossTrackFt < 0,
            $"B738 landing MIA 26R stopped {stoppedCrossTrackFt:F0} ft right of the centerline (exit {exitTaxiway}) — "
                + "the shipped exitDirections override says 26R arrivals vacate LEFT."
        );
    }
}
