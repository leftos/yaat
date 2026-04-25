using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E + synthetic tests for the VFR short-final runway-change bug.
///
/// Recording: S2-OAK-3 (1) "VFR Sequencing" — at server t≈335s the user
/// sent "EF 28L, CLAND" to N42416 (VFR C172 at 77.5 kt, on final for 28R)
/// and received "Unable, short final". The guard at
/// PatternCommandHandler.cs:143 applies the same standard-rate/IAS turn
/// geometry to every aircraft, so a slow VFR light-single is judged by
/// the same envelope as a jet and cannot accept the runway switch even
/// though the real-world maneuver is trivial.
///
/// Required cases:
///   (a) VFR accept — recording: N42416 must be able to switch to 28L.
///   (b) VFR reject — genuinely impossible geometry still rejects.
///   (c) IFR reject preserved — jet on short final still rejects (no regression).
/// </summary>
public class VfrShortFinalRunwayChangeTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-vfr-sequencing-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("PatternCommandHandler", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    // -------------------------------------------------------------------------
    // Case (a) — VFR accept: recording-driven
    // -------------------------------------------------------------------------

    /// <summary>
    /// At recording t≈335 N42416 (VFR C172, 77.5 kt IAS, ~3 nm from 28R
    /// threshold, roughly aligned with 28R final) must be permitted to
    /// switch to parallel runway 28L via EF. Invoke TryEnterPattern
    /// directly rather than SendCommand, so we get the raw geometry
    /// check rejection message without phase-list side effects.
    /// </summary>
    [Fact]
    public void N42416_VfrSlow_CanSwitchToParallelRunwayOnFinal()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(335);
            if (snapshot is null)
            {
                output.WriteLine("No snapshot near t=335 — skipping");
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            output.WriteLine($"Restored snapshot at t={snapshot.ElapsedSeconds}");

            var aircraft = engine.FindAircraft("N42416");
            Assert.NotNull(aircraft);
            output.WriteLine(
                $"N42416: IsVfr={aircraft.FlightPlan.IsVfr} type={aircraft.AircraftType} IAS={aircraft.IndicatedAirspeed:F1} "
                    + $"GS={aircraft.GroundSpeed:F1} hdg={aircraft.TrueHeading.Degrees:F0} alt={aircraft.Altitude:F0} "
                    + $"assignedRwy={aircraft.Phases?.AssignedRunway?.Designator}"
            );

            // Mirror what the user typed (`EF 28L`): EnterFinalCommand dispatches
            // with PatternDirection.Left.
            var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Final, "28L", null);

            output.WriteLine($"TryEnterPattern(28L) -> Success={result.Success} Message='{result.Message}'");

            Assert.True(result.Success, $"VFR C172 at {aircraft.IndicatedAirspeed:F0} kt should be able to switch to 28L. Got: '{result.Message}'");
        }
    }

    // -------------------------------------------------------------------------
    // Case (b) — VFR reject: genuinely impossible geometry
    // -------------------------------------------------------------------------

    /// <summary>
    /// A VFR aircraft positioned just past the requested runway's Final entry
    /// point but flying along the runway direction (past and away from the
    /// entry) cannot physically U-turn and realign with the runway within the
    /// available distance, no matter how tight a VFR turn radius is allowed.
    /// The guard must still reject this case.
    ///
    /// Geometry: aircraft 0.1 nm NW of the 28R Final entry (along 28R heading
    /// from the entry), heading 292° (runway direction). Entry point is 0.1
    /// nm to the SE. turnToEntry = 180° (entry is directly behind), turnAtEntry
    /// = 180° (arrival bearing 112° vs. runway 292°). Total turn = 360°. Even
    /// a 60° bank at 80 kt gives ~0.054 nm radius → ~0.34 nm of arc needed,
    /// which exceeds the 0.1 nm of straight-line distance available.
    /// </summary>
    [Fact]
    public void Vfr_PastEntryPointHeadingAway_StillRejected()
    {
        TestVnasData.EnsureInitialized();
        var rwy28R = TestVnasData.NavigationDb?.GetRunway("KOAK", "28R");
        if (rwy28R is null)
        {
            return;
        }

        // Piston pattern altitude ÷ 3° glideslope gives the default Final
        // entry distance GetEntryPoint uses. Place aircraft 0.1 nm further
        // out along the runway direction so the entry point sits 0.1 nm
        // behind the aircraft in bearing terms.
        double patternAglFt = CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Piston);
        double gsFtPerNm = GlideSlopeGeometry.FeetPerNm(GlideSlopeGeometry.AngleForCategory(AircraftCategory.Piston));
        double entryDistNm = patternAglFt / gsFtPerNm;
        double acDistNm = entryDistNm - 0.1;
        var (lat, lon) = GeoMath.ProjectPoint(rwy28R.ThresholdLatitude, rwy28R.ThresholdLongitude, rwy28R.TrueHeading.ToReciprocal(), acDistNm);

        var ac = new AircraftState
        {
            Callsign = "N1VFR",
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
            Position = new LatLon(lat, lon),
            TrueHeading = rwy28R.TrueHeading,
            Altitude = rwy28R.ElevationFt + 300,
            IndicatedAirspeed = 80,
            IsOnGround = false,
        };
        SetUpFinalApproach(ac, rwy28R, AircraftCategory.Piston);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28R", null);

        output.WriteLine($"TryEnterPattern(28R) -> Success={result.Success} Message='{result.Message}'");
        Assert.False(result.Success, "VFR aircraft 0.1 nm past the Final entry heading away must still be rejected");
        Assert.Contains("short final", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Case (c) — IFR regression: existing behavior preserved
    // -------------------------------------------------------------------------

    /// <summary>
    /// Regression guard: the existing IFR rejection from
    /// <c>PatternCommandHandlerTests.TryEnterPattern_RejectsRunwayChange_WhenOnShortFinal</c>
    /// must still hold after the VFR branch is added. A jet on short final
    /// without IsVfr set must still get "Unable, short final".
    /// </summary>
    [Fact]
    public void Ifr_Jet_ShortFinal_StillRejectedAfterVfrBranch()
    {
        TestVnasData.EnsureInitialized();
        var rwy28L = TestVnasData.NavigationDb?.GetRunway("KOAK", "28L");
        if (rwy28L is null)
        {
            return;
        }

        // 0.5 nm from threshold, aligned with 28L final — matches the existing
        // PatternCommandHandlerTests test that already passes today.
        var (lat, lon) = GeoMath.ProjectPoint(rwy28L.ThresholdLatitude, rwy28L.ThresholdLongitude, rwy28L.TrueHeading.ToReciprocal(), 0.5);

        var ac = new AircraftState
        {
            Callsign = "UAL1",
            AircraftType = "B738",
            FlightPlan = new AircraftFlightPlan { FlightRules = "IFR", Destination = "KOAK" },
            Position = new LatLon(lat, lon),
            TrueHeading = rwy28L.TrueHeading,
            Altitude = rwy28L.ElevationFt + 200,
            IndicatedAirspeed = 140,
            IsOnGround = false,
        };
        SetUpFinalApproach(ac, rwy28L, AircraftCategory.Jet);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28R", null);

        output.WriteLine($"TryEnterPattern(28R) -> Success={result.Success} Message='{result.Message}'");
        Assert.False(result.Success);
        Assert.Contains("short final", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Shared setup
    // -------------------------------------------------------------------------

    private static void SetUpFinalApproach(AircraftState ac, RunwayInfo rwy, AircraftCategory category)
    {
        var phases = new PhaseList { AssignedRunway = rwy };
        phases.Add(new FinalApproachPhase());
        phases.Start(
            new PhaseContext
            {
                Aircraft = ac,
                Targets = ac.Targets,
                Category = category,
                DeltaSeconds = 1.0,
                Runway = rwy,
                FieldElevation = rwy.ElevationFt,
                Logger = NullLogger.Instance,
            }
        );
        ac.Phases = phases;
    }
}
