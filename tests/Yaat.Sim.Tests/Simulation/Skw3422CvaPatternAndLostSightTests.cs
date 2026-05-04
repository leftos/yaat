using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E + unit tests for two bugs surfaced by SKW3422 in
/// <c>S3-NCTC-3 | Area C Complete</c>:
///
/// 1. CVA 30 issued while ~12 nm NNW of KOAK chose a LEFT downwind, forcing the
///    aircraft to cross the field through the departure corridor before joining.
///    Aircraft was already on the right (NE) side of the runway-30 extended
///    centerline; the safe entry is right-traffic (right downwind on the NE side).
///    Root cause: <see cref="Yaat.Sim.Commands.ApproachCommandHandler"/>.DeterminePatternDirection
///    returned the inverted side relative to the aircraft's cross-track sign.
///
/// 2. Once on short final, the maintained-contact field check fired
///    "{Callsign} has lost sight of the field" because the airport reference
///    point fell behind the aircraft (BehindOwnship). The airport is a square
///    mile of concrete the pilot is approaching; only weather should cause a
///    loss of contact in this regime. Fix: <see cref="VisualDetection.TryMaintainAirportContact"/>
///    runs only the weather-related checks and is wired into yaat-server's
///    TickProcessor maintained-contact path.
/// </summary>
public class Skw3422CvaPatternAndLostSightTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/b143fc615682.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// SKW3422 at t=525 is at lat 37.904, lon -122.267 (~12 nm NNW of KOAK), heading 143° SE.
    /// Cross-track from runway-30 extended centerline is positive (~+8 nm) → aircraft is on
    /// the RIGHT side of the runway. CVA 30 (no explicit traffic direction) at t=527 must
    /// pick PatternDirection.Right so the aircraft makes right turns and stays on its
    /// current side (avoids crossing the departure corridor).
    /// </summary>
    [Fact]
    public void Skw3422_CvaRunway30_PicksRightDownwind()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay through the CVA command at t=527.
        engine.Replay(recording, 528);

        var aircraft = engine.FindAircraft("SKW3422");
        Assert.NotNull(aircraft);
        Assert.NotNull(aircraft.Phases);

        var downwind = aircraft.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(downwind);
        Assert.NotNull(downwind.Waypoints);

        var navDb = NavigationDatabase.Instance;
        var rwy = navDb.GetRunway("OAK", "30");
        output.WriteLine(
            $"OAK runway 30: thresh=({rwy?.ThresholdLatitude:F4}, {rwy?.ThresholdLongitude:F4}) "
                + $"end=({rwy?.EndLatitude:F4}, {rwy?.EndLongitude:F4}) trueHdg={rwy?.TrueHeading.Degrees:F1}"
        );
        output.WriteLine(
            $"SKW3422 at t=528: pos=({aircraft.Position.Lat:F4}, {aircraft.Position.Lon:F4}) hdg={aircraft.TrueHeading.Degrees:F0} "
                + $"DownwindAbeamLat={downwind.Waypoints.DownwindAbeamLat:F4} "
                + $"DownwindAbeamLon={downwind.Waypoints.DownwindAbeamLon:F4} "
                + $"DownwindHdg={downwind.Waypoints.DownwindHeading.Degrees:F1} "
                + $"CrosswindHdg={downwind.Waypoints.CrosswindHeading.Degrees:F1} "
                + $"Direction={downwind.Waypoints.Direction}"
        );

        Assert.Equal(PatternDirection.Right, downwind.Waypoints.Direction);

        // Geometry sanity: runway 30 threshold is at the SE end of the runway
        // (true heading 310°). For a right pattern the downwind leg lies NE of
        // the runway centerline, so the downwind-abeam point must be north and
        // east of the threshold. This guards against future refactors that keep
        // the enum value Right but build geometry on the wrong side.
        Assert.NotNull(rwy);
        Assert.True(
            downwind.Waypoints.DownwindAbeamLat > rwy.ThresholdLatitude,
            $"DownwindAbeamLat ({downwind.Waypoints.DownwindAbeamLat:F4}) should be NORTH of rwy 30 threshold ({rwy.ThresholdLatitude:F4})"
        );
        Assert.True(
            downwind.Waypoints.DownwindAbeamLon > rwy.ThresholdLongitude,
            $"DownwindAbeamLon ({downwind.Waypoints.DownwindAbeamLon:F4}) should be EAST of rwy 30 threshold ({rwy.ThresholdLongitude:F4})"
        );
    }

    /// <summary>
    /// Maintained-contact check on short final must NOT report "lost sight of the field"
    /// purely because the airport reference point falls behind the aircraft. An aircraft
    /// past the threshold of runway 30 at low altitude has the runway directly under it;
    /// only weather (BKN/OVC layer between aircraft and ground, Class A) can cause a
    /// genuine loss of contact in this regime.
    /// </summary>
    [Fact]
    public void TryMaintainAirportContact_PastThresholdLowAltitude_StillAcquired()
    {
        // KOAK ARP, runway 30 threshold at ~37.7196, -122.2206, true heading ~313°.
        // Project the aircraft 0.3 nm DOWN-runway from threshold (past it) at 100 ft AGL,
        // heading the runway direction. The airport reference (ARP) is now behind/below
        // the aircraft → BehindOwnship would fire under the old check.
        var (acLat, acLon) = GeoMath.ProjectPointRaw(37.7196, -122.2206, 313.0, 0.3);
        var aircraft = new AircraftState
        {
            Callsign = "SKW3422",
            AircraftType = "E75L",
            Position = new LatLon(acLat, acLon),
            TrueHeading = new TrueHeading(313),
            TrueTrack = new TrueHeading(313),
            Altitude = 100,
            IndicatedAirspeed = 130,
        };

        // Confirm the geometry actually trips the old method (sanity check that the
        // configuration is the real-world short-final case, not something benign).
        var oldResult = VisualDetection.TryAcquireAirportForRunway(
            aircraft,
            airportLat: 37.7240,
            airportLon: -122.2199,
            airportElevation: 9.0,
            layers: null,
            visibilitySm: 10.0,
            runwayHeading: new TrueHeading(313),
            bankAngleDeg: 0.0,
            airportSizeCapNm: 25.0
        );
        Assert.False(oldResult.Acquired);
        output.WriteLine($"Old TryAcquireAirportForRunway: Acquired=false, Reason={oldResult.Reason} (false positive — bug)");

        // New method: weather/Class-A only, no geometric checks. Should acquire.
        var maintained = VisualDetection.TryMaintainAirportContact(aircraft, airportElevation: 9.0, layers: null);
        Assert.True(
            maintained.Acquired,
            $"TryMaintainAirportContact should not lose sight of the field on short final; got Reason={maintained.Reason}"
        );
    }

    /// <summary>
    /// IFR visual approaches must work for a range of downwind altitudes —
    /// 2000 ft AGL (just above standard VFR pattern) up through 5000 ft AGL
    /// (typical IFR vector-to-visual altitude). The aircraft should be able to
    /// extend the downwind as needed to lose altitude before turning base.
    /// </summary>
    [Theory]
    [InlineData(2000)]
    [InlineData(3500)]
    [InlineData(5000)]
    public void Cva_FromDownwindAltitudeAgl_LandsSafely(int entryAltAgl)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-cva-altitude",
                ScenarioName = "CVA IFR downwind altitude E2E",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };

        var navDb = NavigationDatabase.Instance;
        var rwy30 = navDb.GetRunway("OAK", "30");
        Assert.NotNull(rwy30);

        // Spawn 5 nm at bearing 350° from KOAK ARP, heading 090 (east). Same
        // geometry as Cva_FromNorthDownwind. With the IFR fix the aircraft
        // picks Right traffic (positive cross-track) and routes through pattern
        // entry → downwind → base → final → landing.
        const double koakArpLat = 37.7240;
        const double koakArpLon = -122.2199;
        var (spawnLat, spawnLon) = GeoMath.ProjectPointRaw(koakArpLat, koakArpLon, 350.0, 5.0);

        double spawnMsl = rwy30.ElevationFt + entryAltAgl;
        var aircraft = new AircraftState
        {
            Callsign = "UAL738",
            AircraftType = "B738",
            Position = new LatLon(spawnLat, spawnLon),
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = spawnMsl,
            IndicatedAirspeed = 230,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                CruiseAltitude = (int)spawnMsl,
            },
        };
        engine.World.AddAircraft(aircraft);
        output.WriteLine($"Spawned UAL738 at AGL={entryAltAgl} (MSL={spawnMsl:F0})");

        var rfis = engine.SendCommand("UAL738", "RFIS");
        Assert.True(rfis.Success, $"RFIS failed: {rfis.Message}");

        var cva = engine.SendCommand("UAL738", "CVA 30");
        Assert.True(cva.Success, $"CVA 30 failed: {cva.Message}");

        var cland = engine.SendCommand("UAL738", "CLAND");
        Assert.True(cland.Success, $"CLAND failed: {cland.Message}");

        // Tick until landed or timeout. Higher entry altitudes need longer to
        // descend, so the budget scales.
        int budget = entryAltAgl switch
        {
            <= 2000 => 600,
            <= 3500 => 750,
            _ => 900,
        };
        bool landed = false;
        bool wentAround = false;
        string? gaMsg = null;
        for (int t = 1; t <= budget; t++)
        {
            engine.TickOneSecond();
            if (aircraft.IsOnGround && aircraft.GroundSpeed < 40)
            {
                output.WriteLine($"t+{t}s: landed (gs={aircraft.GroundSpeed:F0})");
                landed = true;
                break;
            }
            foreach (var w in aircraft.PendingWarnings)
            {
                if (w.Contains("going around", StringComparison.OrdinalIgnoreCase))
                {
                    wentAround = true;
                    gaMsg = w;
                }
            }
            if (t % 60 == 0)
            {
                output.WriteLine(
                    $"  t+{t}s: alt={aircraft.Altitude:F0}ft, gs={aircraft.GroundSpeed:F0}kt, "
                        + $"hdg={aircraft.TrueHeading.Degrees:F0}, "
                        + $"phase={aircraft.Phases?.CurrentPhase?.GetType().Name ?? "(none)"}"
                );
            }
        }

        Assert.False(wentAround, $"Aircraft entering at {entryAltAgl} ft AGL went around: {gaMsg}");
        Assert.True(
            landed,
            $"Aircraft entering at {entryAltAgl} ft AGL did not land within {budget}s. "
                + $"Final alt={aircraft.Altitude:F0}ft, phase={aircraft.Phases?.CurrentPhase?.GetType().Name ?? "(none)"}"
        );
    }

    /// <summary>
    /// The maintained-contact check still surfaces weather-driven loss of contact:
    /// when an aircraft climbs above (or stays above) a BKN/OVC layer that obscures
    /// the field, contact is genuinely lost.
    /// </summary>
    [Fact]
    public void TryMaintainAirportContact_AboveBknCeiling_LosesSight()
    {
        // Aircraft at 2000 ft MSL, BKN layer at 1000 ft AGL above 9-ft elevation field
        // → cloud base at 1009 ft MSL, aircraft is above it → AboveCeiling.
        var aircraft = new AircraftState
        {
            Callsign = "TST100",
            AircraftType = "B738",
            Position = new LatLon(37.7240, -122.2199),
            TrueHeading = new TrueHeading(313),
            TrueTrack = new TrueHeading(313),
            Altitude = 2000,
            IndicatedAirspeed = 200,
        };

        IReadOnlyList<MetarParser.CloudLayer> bkn1000 = [new MetarParser.CloudLayer(MetarParser.CloudCover.Broken, 1000)];

        var result = VisualDetection.TryMaintainAirportContact(aircraft, airportElevation: 9.0, layers: bkn1000);
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.AboveCeiling, result.Reason);
        Assert.NotNull(result.BindingLayer);
        Assert.Equal(1000, result.BindingLayer.BaseFeetAgl);
    }
}
