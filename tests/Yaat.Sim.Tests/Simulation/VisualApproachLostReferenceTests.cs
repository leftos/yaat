using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// AIM §5-5-11.a.3 is a disjunction: a pilot on a visual approach must at all times have
/// EITHER the airport OR the preceding aircraft in sight. These tests pin the consequence
/// matrix (issue #343): losing one reference while holding the other continues the visual
/// (with the follow handed back when the lost reference is the lead); losing the only
/// reference ends it — a go-around when committed (short final), otherwise a level-off on
/// present heading with a request for vectors (7110.65 §7-4-1.a.2 puts the climb decision
/// on ATC). An ended visual voids the clearance so a weather flicker cannot silently
/// resurrect an approach the controller believes is dead.
/// </summary>
public class VisualApproachLostReferenceTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-visual-lost-reference",
                ScenarioName = "Visual Approach Lost Reference",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
    }

    private static AircraftState MakeB738OnFinal(string callsign, double lat, double lon, double headingDeg, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(headingDeg),
            TrueTrack = new TrueHeading(headingDeg),
            Altitude = altitude,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr((int)altitude),
            },
        };

    private static WeatherProfile OakVisibility(string visToken) => new() { Metars = [$"KOAK 121853Z 27012KT {visToken} CLR 20/12 A2992"] };

    private static (double Lat, double Lon) OnFinal(RunwayInfo rwy, double distanceNm)
    {
        double reciprocal = (rwy.TrueHeading.Degrees + 180) % 360;
        return GeoMath.ProjectPointRaw(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, distanceNm);
    }

    private static AircraftState EstablishCvaWithFieldInSight(SimulationEngine engine, double finalDistanceNm, double altitude)
    {
        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        var (lat, lon) = OnFinal(rwy, finalDistanceNm);
        var ac = MakeB738OnFinal("FLD1", lat, lon, rwy.TrueHeading.Degrees, altitude);
        engine.World.AddAircraft(ac);

        Assert.True(engine.SendCommand("FLD1", "RFIS").Success);
        Assert.True(engine.SendCommand("FLD1", "CVA 30").Success);
        for (int t = 1; t <= 15 && !ac.Approach.HasReportedFieldInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(ac.Approach.HasReportedFieldInSight, "field should be in sight in clear weather on final");

        return ac;
    }

    [Fact]
    public void FieldLost_NoFollow_NotCommitted_LevelsOffRequestsVectors()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return; // navdata absent → skip, per no-synthetic-data convention
        }

        var ac = EstablishCvaWithFieldInSight(engine, finalDistanceNm: 6.0, altitude: 2500);

        double expectedLevelOff = Math.Round(ac.Altitude / 100.0) * 100.0;
        engine.World.Weather = OakVisibility("2SM");
        ac.PendingWarnings.Clear();

        engine.TickVisualDetection();

        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.False(ac.Approach.HasReportedFieldInSight);
        Assert.True(
            ac.Phases?.CurrentPhase is not GoAroundPhase,
            "Away from the runway the pilot does not go around on their own — an unrequested climb off a visual is a separation hazard"
        );
        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Equal(expectedLevelOff, ac.Targets.TargetAltitude);
        Assert.NotNull(ac.Targets.AssignedMagneticHeading);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("unable the visual", StringComparison.OrdinalIgnoreCase));

        // The clearance is void: clearing weather must not silently resurrect the approach.
        engine.World.Weather = OakVisibility("10SM");
        ac.PendingNotifications.Clear();
        for (int t = 0; t < 10; t++)
        {
            engine.TickOneSecond();
        }
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.False(ac.Approach.HasReportedFieldInSight);
        Assert.DoesNotContain(ac.PendingNotifications, n => n.Contains("field in sight", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FieldLost_Committed_GoesAround()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var ac = EstablishCvaWithFieldInSight(engine, finalDistanceNm: 6.0, altitude: 2000);

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        var (lat, lon) = OnFinal(rwy, 2.5);
        ac.Position = new LatLon(lat, lon);
        ac.Altitude = 800;
        engine.World.Weather = OakVisibility("1SM");
        ac.PendingWarnings.Clear();

        engine.TickVisualDetection();

        Assert.True(ac.Phases?.CurrentPhase is GoAroundPhase, $"expected GoAroundPhase, got {ac.Phases?.CurrentPhase?.GetType().Name}");
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.False(
            ((GoAroundPhase)ac.Phases!.CurrentPhase!).ReenterPattern,
            "IFR visual → runway-heading climb awaiting vectors, not a pattern re-entry"
        );
        Assert.Contains(ac.PendingWarnings, w => w.Contains("going around", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Case (b): a FOLLOW-cleared aircraft that loses the field but still has its lead in
    /// sight is in a legal steady state (7110.65 §7-4-3.c.2 NOTE — the field report was never
    /// required). The clearance stands and nothing is said on frequency.
    /// </summary>
    [Fact]
    public void FieldLost_TrafficHeld_VisualContinues_Silent()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        double finalCourse = rwy.TrueHeading.Degrees;
        var (leadLat, leadLon) = OnFinal(rwy, 4.0);
        var (trailLat, trailLon) = OnFinal(rwy, 6.0);
        var leader = MakeB738OnFinal("LEAD1", leadLat, leadLon, finalCourse, altitude: 1300);
        var trailer = MakeB738OnFinal("TRAIL1", trailLat, trailLon, finalCourse, altitude: 2000);
        engine.World.AddAircraft(leader);
        engine.World.AddAircraft(trailer);

        Assert.True(engine.SendCommand("LEAD1", "RFIS").Success);
        Assert.True(engine.SendCommand("LEAD1", "CVA 30").Success);
        Assert.True(engine.SendCommand("TRAIL1", "RFIS").Success);
        Assert.True(engine.SendCommand("TRAIL1", "RTIS LEAD1").Success);
        for (int t = 1; t <= 15 && !(trailer.Approach.HasReportedFieldInSight && trailer.Approach.HasReportedTrafficInSight); t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(trailer.Approach.HasReportedFieldInSight);
        Assert.True(trailer.Approach.HasReportedTrafficInSight);
        Assert.True(engine.SendCommand("TRAIL1", "CVA 30 FOLLOW LEAD1").Success);
        for (int t = 1; t <= 15 && !(trailer.Approach.HasReportedFieldInSight && trailer.Approach.HasReportedTrafficInSight); t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(trailer.Approach.HasReportedFieldInSight);
        Assert.True(trailer.Approach.HasReportedTrafficInSight);

        // 3SM: the field (6 nm) falls outside the maintained range while the lead (2 nm) stays
        // inside it. Field lost, traffic held → legal, silent.
        engine.World.Weather = OakVisibility("3SM");
        trailer.PendingWarnings.Clear();
        trailer.PendingPilotSpeech.Clear();

        engine.TickVisualDetection();

        Assert.False(trailer.Approach.HasReportedFieldInSight);
        Assert.True(trailer.Approach.HasReportedTrafficInSight);
        Assert.NotNull(trailer.Approach.FollowingCallsign);
        Assert.NotNull(trailer.Phases?.ActiveApproach);
        Assert.DoesNotContain(trailer.PendingWarnings, w => w.Contains("lost sight of the field", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(trailer.PendingWarnings, w => w.Contains("unable the visual", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Case (c), follow variant: a §7-4-3.c.2 follower that never reported the field loses the
    /// lead — nothing is in sight, so the visual ends (level-off + request vectors; the trailer
    /// is far out and well above 1000 AGL).
    /// </summary>
    [Fact]
    public void TrafficLost_FieldNeverHeld_EndsVisual()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        double finalCourse = rwy.TrueHeading.Degrees;
        // 5SM caps field acquisition well short of the trailer's 15 nm, so the field is never
        // held; the 4 nm gap to the lead is inside the traffic acquisition range.
        engine.World.Weather = OakVisibility("5SM");
        var (leadLat, leadLon) = OnFinal(rwy, 11.0);
        var (trailLat, trailLon) = OnFinal(rwy, 15.0);
        var leader = MakeB738OnFinal("LEAD1", leadLat, leadLon, finalCourse, altitude: 3000);
        var trailer = MakeB738OnFinal("TRAIL1", trailLat, trailLon, finalCourse, altitude: 3500);
        engine.World.AddAircraft(leader);
        engine.World.AddAircraft(trailer);

        Assert.True(engine.SendCommand("LEAD1", "RFISF").Success);
        Assert.True(engine.SendCommand("LEAD1", "CVA 30").Success);
        Assert.True(engine.SendCommand("TRAIL1", "RTIS LEAD1").Success);
        for (int t = 1; t <= 15 && !trailer.Approach.HasReportedTrafficInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(trailer.Approach.HasReportedTrafficInSight);
        Assert.True(engine.SendCommand("TRAIL1", "CVA 30 FOLLOW LEAD1").Success);
        for (int t = 1; t <= 15 && !trailer.Approach.HasReportedTrafficInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(trailer.Approach.HasReportedTrafficInSight);
        Assert.False(trailer.Approach.HasReportedFieldInSight, "setup: the field must never have been reported");

        // Visibility collapses below the gap: the only reference is gone.
        engine.World.Weather = OakVisibility("2SM");
        var warnings = CaptureWarnings(engine, "TRAIL1");

        engine.TickOneSecond();

        Assert.Null(trailer.Approach.FollowingCallsign);
        Assert.Null(trailer.Phases?.ActiveApproach);
        Assert.True(trailer.Phases?.CurrentPhase is not GoAroundPhase, "not committed — level off and request vectors, no self-initiated climb");
        Assert.Contains(warnings, w => w.Contains("unable the visual", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lead lands while the follower never held the field: the follower immediately tries to
    /// acquire the field (it is typically short final by then). Success → the visual continues
    /// on the field report.
    /// </summary>
    [Fact]
    public void LeadLands_FieldNeverHeld_ReacquiresAndContinues()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var (leader, trailer) = EstablishFollowWithoutField(engine);

        // Lead touches down; the trailer is repositioned to short final where the field
        // (measured to the ARP by the acquisition path) is inside the 6SM range.
        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        GroundLeader(leader, rwy);
        var (lat, lon) = OnFinal(rwy, 2.5);
        trailer.Position = new LatLon(lat, lon);
        trailer.Altitude = 1200;
        var warnings = CaptureWarnings(engine, "TRAIL1");

        for (int t = 0; t < 3; t++)
        {
            engine.TickOneSecond();
        }

        Assert.Null(trailer.Approach.FollowingCallsign);
        Assert.True(trailer.Approach.HasReportedFieldInSight, "short final in 6SM: the field must be re-acquired when the lead lands");
        Assert.NotNull(trailer.Phases?.ActiveApproach);
        Assert.DoesNotContain(warnings, w => w.Contains("unable the visual", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lead lands while the follower never held the field AND the field is not acquirable
    /// (still far out): nothing is in sight — the visual ends.
    /// </summary>
    [Fact]
    public void LeadLands_FieldNotAcquirable_EndsVisual()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var (leader, trailer) = EstablishFollowWithoutField(engine);

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        GroundLeader(leader, rwy);
        var warnings = CaptureWarnings(engine, "TRAIL1");

        for (int t = 0; t < 3; t++)
        {
            engine.TickOneSecond();
        }

        Assert.Null(trailer.Approach.FollowingCallsign);
        Assert.False(trailer.Approach.HasReportedFieldInSight);
        Assert.Null(trailer.Phases?.ActiveApproach);
        Assert.Contains(warnings, w => w.Contains("unable the visual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ManualGoAround_OnVisual_VoidsClearance()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var ac = EstablishCvaWithFieldInSight(engine, finalDistanceNm: 6.0, altitude: 2000);

        Assert.True(engine.SendCommand("FLD1", "GA").Success);

        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.False(ac.Approach.HasReportedFieldInSight);
        Assert.Null(ac.Approach.FollowingCallsign);
    }

    /// <summary>
    /// The CVA gate checks the disjunction and must not then erase the very report it
    /// gated on — otherwise every clearance opens a blind window (and, once the lost-all
    /// consequence exists, a level-based check would end the visual right after issuing it).
    /// </summary>
    [Fact]
    public void Cva_PreservesGatingBasisAcrossClearance()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        var (lat, lon) = OnFinal(rwy, 6.0);
        var ac = MakeB738OnFinal("FLD1", lat, lon, rwy.TrueHeading.Degrees, altitude: 2000);
        engine.World.AddAircraft(ac);

        Assert.True(engine.SendCommand("FLD1", "RFIS").Success);
        for (int t = 1; t <= 15 && !ac.Approach.HasReportedFieldInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(ac.Approach.HasReportedFieldInSight);

        Assert.True(engine.SendCommand("FLD1", "CVA 30").Success);

        Assert.True(ac.Approach.HasReportedFieldInSight, "the clearance is predicated on the field report — issuing it must not erase it");

        // No re-acquisition dance: the pilot does not re-report a field they already reported.
        ac.PendingNotifications.Clear();
        for (int t = 0; t < 3; t++)
        {
            engine.TickOneSecond();
        }
        Assert.DoesNotContain(ac.PendingNotifications, n => n.Contains("has the field in sight", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Captures every warning the engine drains for <paramref name="callsign"/> — the
    /// post-physics drain empties <see cref="AircraftState.PendingWarnings"/> each tick, so
    /// tests that tick the engine must listen to <see cref="SimulationEngine.WarningEmitted"/>
    /// instead of reading the (already-drained) list.
    /// </summary>
    private static List<string> CaptureWarnings(SimulationEngine engine, string callsign)
    {
        var warnings = new List<string>();
        engine.WarningEmitted += (cs, warning) =>
        {
            if (cs.Equals(callsign, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(warning);
            }
        };
        return warnings;
    }

    /// <summary>
    /// Parks the leader on the runway as genuinely landed: its own approach phases are
    /// dropped so physics doesn't lift it back off the manual on-ground state.
    /// </summary>
    private static void GroundLeader(AircraftState leader, RunwayInfo rwy)
    {
        leader.Phases = new PhaseList();
        leader.Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        leader.Altitude = rwy.ElevationFt;
        leader.IndicatedAirspeed = 0;
        leader.IsOnGround = true;
    }

    /// <summary>
    /// Establishes LEAD1 (8 nm final, CVA) + TRAIL1 (11 nm final, RTIS + CVA FOLLOW) under 6SM,
    /// where the field (measured to the ARP) is beyond the trailer's acquisition range (field
    /// never held) but the 3 nm gap to the lead is inside it.
    /// </summary>
    private (AircraftState Leader, AircraftState Trailer) EstablishFollowWithoutField(SimulationEngine engine)
    {
        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(rwy);
        double finalCourse = rwy.TrueHeading.Degrees;
        engine.World.Weather = OakVisibility("6SM");
        var (leadLat, leadLon) = OnFinal(rwy, 8.0);
        var (trailLat, trailLon) = OnFinal(rwy, 11.0);
        var leader = MakeB738OnFinal("LEAD1", leadLat, leadLon, finalCourse, altitude: 2500);
        var trailer = MakeB738OnFinal("TRAIL1", trailLat, trailLon, finalCourse, altitude: 3500);
        engine.World.AddAircraft(leader);
        engine.World.AddAircraft(trailer);

        Assert.True(engine.SendCommand("LEAD1", "RFISF").Success);
        Assert.True(engine.SendCommand("LEAD1", "CVA 30").Success);
        Assert.True(engine.SendCommand("TRAIL1", "RTIS LEAD1").Success);
        for (int t = 1; t <= 15 && !trailer.Approach.HasReportedTrafficInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(trailer.Approach.HasReportedTrafficInSight);
        Assert.True(engine.SendCommand("TRAIL1", "CVA 30 FOLLOW LEAD1").Success);
        for (int t = 1; t <= 15 && !trailer.Approach.HasReportedTrafficInSight; t++)
        {
            engine.TickOneSecond();
        }
        Assert.True(trailer.Approach.HasReportedTrafficInSight);
        Assert.False(trailer.Approach.HasReportedFieldInSight, "setup: the field must never have been reported");

        return (leader, trailer);
    }
}
