using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E + unit tests for GitHub issue #292: low approach on one runway, then cleared to land on a
/// different, diverging/non-intersecting runway (KOAK: low approach 28R, then "cleared to land 33").
///
/// Recording: S2-OAK-P S2 Rating Practical Exam. Aircraft N104NT (BE36 piston) is doing VFR closed
/// traffic on 28R; at t≈2404 it gets LA (low approach), and the user worked around the missing
/// feature with EF 33 (t=2434) + CLANDF (t=2435). This suite exercises the new supported path:
/// CLAND 33 issued during the low approach retargets the aircraft to land on 33.
///
/// 28R landing course ~292°T; 33 landing course ~344°T (52° divergence). 33's landing threshold sits
/// 0.13 nm from 28R's far end, so the turn onto 33 must begin while the aircraft is still SE of the
/// field — the retarget flies the low pass down 28R and turns onto 33's final at the last feasible gate.
/// </summary>
public class Issue292KoakLowApproachRetargetTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue292-koak-low-approach-33-recording.zip";

    // Runway 33 landing threshold (SE end) and landing course, from the recording's AssignedRunway.
    private const double Rwy33ThrLat = 37.73136275;
    private const double Rwy33ThrLon = -122.21967391666666;
    private const double Rwy33LandingCourseTrueDeg = 344.48;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("LowApproachPhase", Microsoft.Extensions.Logging.LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// Restore the recorded state at ~t=2410 (N104NT established on the 28R low approach) via a
    /// snapshot, rather than a full re-sim from t=0. The recording contains an unrelated aircraft whose
    /// taxi does not converge under a full replay; the snapshot restore isolates N104NT's state cleanly.
    /// Returns null (silent skip) if the recording/snapshot is absent.
    /// </summary>
    private static (SimulationEngine Engine, AircraftState Aircraft)? RestoreToLowApproach(SimulationEngine? engine)
    {
        if (engine is null)
        {
            return null;
        }

        using var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        engine.Replay(archive.ToBaseSessionRecording(), 0);
        var snapshot = archive.ReadSnapshotAt(2410);
        if (snapshot is null)
        {
            return null;
        }

        engine.RestoreFromSnapshot(snapshot.State);
        var ac = engine.FindAircraft("N104NT");
        return ac is null ? null : (engine, ac);
    }

    /// <summary>
    /// Diagnostic: replay to just after LA is applied (N104NT established on the 28R low approach),
    /// issue CLAND 33, then tick (physics only — do NOT replay the user's EF 33/CLANDF) and log the
    /// trajectory so we can see the actual maneuver: where it turns, how low it gets, where it lands.
    /// </summary>
    [Fact]
    public void Diagnostic_Cland33DuringLowApproach()
    {
        var setup = RestoreToLowApproach(BuildEngine());
        if (setup is null)
        {
            return;
        }
        var (engine, ac) = setup.Value;

        output.WriteLine(
            $"t=2410 pre-CLAND: phase={ac.Phases?.CurrentPhase?.GetType().Name} rwy={ac.Phases?.AssignedRunway?.Designator} "
                + $"clnc={ac.Phases?.LandingClearance} alt={ac.Altitude:F0} hdg={ac.TrueHeading.Degrees:F0}"
        );

        var result = engine.SendCommand("N104NT", "CLAND 33");
        output.WriteLine($"CLAND 33 -> success={result.Success} msg=\"{result.Message}\"");
        ac = engine.FindAircraft("N104NT");
        output.WriteLine(
            $"post-CLAND: rwy={ac!.Phases?.AssignedRunway?.Designator} clnc={ac.Phases?.LandingClearance} "
                + $"clearedRwy={ac.Phases?.ClearedRunwayId} destRwy={ac.Procedure.DestinationRunway} "
                + $"chain=[{string.Join(" -> ", ac.Phases?.Phases.Select(p => p.GetType().Name) ?? [])}]"
        );

        for (int t = 1; t <= 220; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("N104NT");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: despawned");
                break;
            }

            double distTo33 = GeoMath.DistanceNm(ac.Position, new LatLon(Rwy33ThrLat, Rwy33ThrLon));
            if ((t % 5 == 0) || ac.IsOnGround)
            {
                output.WriteLine(
                    $"t+{t, -3} phase={ac.Phases?.CurrentPhase?.GetType().Name, -18} alt={ac.Altitude, 6:F0} "
                        + $"hdg={ac.TrueHeading.Degrees, 3:F0} gs={ac.GroundSpeed, 3:F0} destRwy={ac.Procedure.DestinationRunway, -3} "
                        + $"dist33={distTo33:F2}nm onGnd={ac.IsOnGround}"
                );
            }

            if (ac.IsOnGround && ac.GroundSpeed < 30)
            {
                output.WriteLine($"t+{t}: STOPPED on ground, hdg={ac.TrueHeading.Degrees:F0} dist33={distTo33:F2}nm");
                break;
            }
        }
    }

    /// <summary>
    /// The supported #292 path: CLAND 33 during the 28R low approach retargets N104NT to runway 33,
    /// re-issues the landing clearance for 33, keeps the datablock showing 33, and lands it on 33.
    /// </summary>
    [Fact]
    public void N104NT_Cland33_RetargetsAndLandsOn33()
    {
        var setup = RestoreToLowApproach(BuildEngine());
        if (setup is null)
        {
            return;
        }
        var (engine, ac) = setup.Value;

        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedLowApproach, ac.Phases?.LandingClearance);

        var result = engine.SendCommand("N104NT", "CLAND 33");
        Assert.True(result.Success, result.Message);
        Assert.Contains("runway 33", result.Message!, StringComparison.OrdinalIgnoreCase);

        ac = engine.FindAircraft("N104NT");
        Assert.NotNull(ac);
        Assert.Equal("33", ac.Phases?.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases?.LandingClearance);
        Assert.Equal("33", ac.Phases?.ClearedRunwayId);

        bool landed = false;
        bool destRunwayEverBlanked = false;
        for (int t = 1; t <= 260; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("N104NT");
            if (ac is null)
            {
                landed = true;
                break;
            }

            // Datablock must keep showing 33 through the FinalApproach transition (#292 display fix).
            if (ac.Phases?.AssignedRunway?.Designator == "33" && ac.Procedure.DestinationRunway is null)
            {
                destRunwayEverBlanked = true;
            }

            if (ac.IsOnGround && ac.GroundSpeed < 30)
            {
                landed = true;
                break;
            }
        }

        Assert.NotNull(ac);
        Assert.True(landed, "N104NT should land after CLAND 33");
        Assert.False(destRunwayEverBlanked, "Destination-runway datablock must not blank to null while assigned to 33");

        // Landed on 33: near the 33 threshold and aligned with 33's landing course.
        double distTo33 = GeoMath.DistanceNm(ac!.Position, new LatLon(Rwy33ThrLat, Rwy33ThrLon));
        double headingOff33 = ac.TrueHeading.AbsAngleTo(new TrueHeading(Rwy33LandingCourseTrueDeg));
        output.WriteLine($"landed: dist33={distTo33:F2}nm hdgOff33={headingOff33:F0}° hdg={ac.TrueHeading.Degrees:F0}");
        Assert.True(distTo33 < 0.6, $"Should touch down on runway 33 (within 0.6nm of its threshold) but was {distTo33:F2}nm away");
        Assert.True(headingOff33 < 25, $"Should be aligned with runway 33 (~344°T) but heading was {ac.TrueHeading.Degrees:F0}°");
    }

    // --- Geometry guardrail (C) ---

    private static AircraftState MakeAirborne(string type, LatLon pos, double headingTrue) =>
        new()
        {
            Callsign = "N104NT",
            AircraftType = type,
            Position = pos,
            TrueHeading = new TrueHeading(headingTrue),
            TrueTrack = new TrueHeading(headingTrue),
            Altitude = 800,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
        };

    [Fact]
    public void Guardrail_28RTo33_RecordingGeometry_Feasible()
    {
        var setup = RestoreToLowApproach(BuildEngine());
        if (setup is null)
        {
            return;
        }
        var (_, ac) = setup.Value;

        var rwy28R = ac.Phases!.AssignedRunway!;
        var rwy33 = NavigationDatabase.Instance.GetRunway("OAK", "33");
        Assert.NotNull(rwy33);

        var feasibility = PatternCommandHandler.EvaluateLowApproachRetargetFeasibility(ac, rwy28R, rwy33);
        Assert.True(feasibility.Feasible, feasibility.Reason);
    }

    [Fact]
    public void Guardrail_28RTo28L_NearParallel_Rejected()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var rwy28R = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        var rwy28L = NavigationDatabase.Instance.GetRunway("OAK", "28L");
        Assert.NotNull(rwy28R);
        Assert.NotNull(rwy28L);

        var ac = MakeAirborne("C172", new LatLon(37.70, -122.15), 292);
        var feasibility = PatternCommandHandler.EvaluateLowApproachRetargetFeasibility(ac, rwy28R, rwy28L);
        Assert.False(feasibility.Feasible);
        Assert.Contains("too close", feasibility.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guardrail_28RTo10R_NearReciprocal_Rejected()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var rwy28R = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        var rwy10R = NavigationDatabase.Instance.GetRunway("OAK", "10R");
        Assert.NotNull(rwy28R);
        Assert.NotNull(rwy10R);

        var ac = MakeAirborne("C172", new LatLon(37.70, -122.15), 292);
        var feasibility = PatternCommandHandler.EvaluateLowApproachRetargetFeasibility(ac, rwy28R, rwy10R);
        Assert.False(feasibility.Feasible);
        Assert.Contains("re-enter", feasibility.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guardrail_PastFinalForNewRunway_Rejected()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var rwy28R = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        var rwy33 = NavigationDatabase.Instance.GetRunway("OAK", "33");
        Assert.NotNull(rwy28R);
        Assert.NotNull(rwy33);

        // Aircraft sitting at 33's threshold — NW of its final gate, so a turn onto 33's final would
        // require turning away from the field.
        var ac = MakeAirborne("C172", new LatLon(rwy33.ThresholdLatitude, rwy33.ThresholdLongitude), 292);
        var feasibility = PatternCommandHandler.EvaluateLowApproachRetargetFeasibility(ac, rwy28R, rwy33);
        Assert.False(feasibility.Feasible);
        Assert.Contains("past the final", feasibility.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // --- Command handler (B) ---

    [Fact]
    public void Cland33_NoLowApproach_StillRejectsAsBefore()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var rwy28R = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        Assert.NotNull(rwy28R);

        // Normal approach to 28R (a plain LandingPhase, NOT a low approach).
        var ac = MakeAirborne("C172", new LatLon(37.70, -122.15), 292);
        ac.Phases = new PhaseList { AssignedRunway = rwy28R };
        ac.Phases.Add(new LandingPhase());

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand { RunwayId = "33" }, ac, null);
        Assert.False(result.Success);
        Assert.Contains("established for runway", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("28R", ac.Phases.AssignedRunway?.Designator);
    }

    [Fact]
    public void Cland28L_DuringLowApproach_GuardrailRejects_LeavesLowApproachIntact()
    {
        var setup = RestoreToLowApproach(BuildEngine());
        if (setup is null)
        {
            return;
        }
        var (_, ac) = setup.Value;

        // 28L is near-parallel to 28R → the guardrail rejects; the low approach must be left untouched.
        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand { RunwayId = "28L" }, ac, null);
        Assert.False(result.Success);
        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedLowApproach, ac.Phases?.LandingClearance);
        Assert.Contains(ac.Phases!.Phases, p => p is LowApproachPhase);
    }

    [Fact]
    public void Cland28R_SameRunwayDuringLowApproach_LandsNormallyNotRetarget()
    {
        var setup = RestoreToLowApproach(BuildEngine());
        if (setup is null)
        {
            return;
        }
        var (_, ac) = setup.Value;

        // Same runway as assigned → the runway-mismatch branch is not taken; this is an ordinary CLAND
        // that converts the pending low approach into a full-stop landing on 28R.
        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand { RunwayId = "28R" }, ac, null);
        Assert.True(result.Success, result.Message);
        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases?.LandingClearance);
    }

    [Fact]
    public void Cland33_JetOnLowApproach_RejectedAsLightAircraftManeuver()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var rwy28R = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        Assert.NotNull(rwy28R);

        // A jet on a low approach: the tight ~1 nm final on a diverging runway is unflyable, so reject.
        var ac = MakeAirborne("B738", new LatLon(37.70, -122.15), 292);
        ac.Phases = new PhaseList { AssignedRunway = rwy28R, LandingClearance = ClearanceType.ClearedLowApproach };
        ac.Phases.Add(new LowApproachPhase());

        var result = PatternCommandHandler.TryClearedToLand(new ClearedToLandCommand { RunwayId = "33" }, ac, null);
        Assert.False(result.Success);
        Assert.Contains("light-aircraft", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("28R", ac.Phases.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedLowApproach, ac.Phases.LandingClearance);
    }

    // --- Phraseology (7110.65 §3-10-5 "change to runway") ---

    [Fact]
    public void Cland33Retarget_ControllerAndPilotSay_ChangeToRunway()
    {
        var setup = RestoreToLowApproach(BuildEngine());
        if (setup is null)
        {
            return;
        }
        var (_, ac) = setup.Value;

        var cland = new ClearedToLandCommand { RunwayId = "33" };
        var result = PatternCommandHandler.TryClearedToLand(cland, ac, null);
        Assert.True(result.Success, result.Message);
        Assert.Contains("change to runway 33", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleared to land", result.Message!, StringComparison.OrdinalIgnoreCase);

        var readback = PilotResponder.BuildReadback(new CompoundCommand([new ParsedBlock(null, [cland])]), ac);
        Assert.NotNull(readback);
        Assert.Contains("change to runway", readback.Terminal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleared to land", readback.Terminal, StringComparison.OrdinalIgnoreCase);
    }
}
