using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A CAPP whose chosen transition still connects to a fix on the STAR route is held as a pending clearance and
/// builds no phase sequence until the route empties. CLAND in that window was refused with "Aircraft has no active
/// phase sequence" because it never read the pending clearance, so only a second CAPP made the landing clearance
/// take.
///
/// Recording: S3-FAT-3. N160TM is cleared <c>CAPP I29RY</c> at t=188 while still on its STAR (held); the recorded
/// <c>CLAND</c> at t=478 is refused, a second <c>CAPP</c> at t=491 activates at once, and the <c>CLAND</c> at t=493
/// then works. Every test restores t=470 (the approach still held) and replays to t=477, one second before the
/// recording's own CLAND, then issues its own commands.
/// </summary>
public class S3Fat3ClandUnderHeldCappTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s3-fat-3-star-end-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N160TM";
    private const string Runway = "29R";
    private const string Approach = "I29RY";
    private const string EvergreenWhy = "CLAND accepted while an approach clearance is held for the STAR route";

    /// <summary>Snapshot restore point: the approach still held, the STAR route not yet empty.</summary>
    private const int RestoreTime = 470;

    /// <summary>One second before the recording's own (refused) CLAND at t=478.</summary>
    private const int ClandTime = 477;

    /// <summary>Past the route emptying (near t=570), where the held approach activates.</summary>
    private const int ActivationTime = 575;

    /// <summary>Just after the recorded CAPP at t=188, while the STAR route still carries fixes past the connecting fix.</summary>
    private const int EarlyRestoreTime = 200;

    [Fact]
    public void ClandUnderHeldCapp_IsAccepted()
    {
        WithRestoredEngine(engine =>
        {
            AircraftState ac = engine.FindAircraft(Callsign)!;
            Assert.Null(ac.Phases);
            Assert.NotNull(ac.Approach.PendingClearance);

            CommandResult cland = engine.SendCommand(Callsign, "CLAND");

            Assert.True(cland.Success, cland.Message);
            Assert.Equal("Cleared to land, Runway 29R", cland.Message);
        });
    }

    [Fact]
    public void HeldLandingClearance_ReachesTheActivatedApproach()
    {
        WithRestoredEngine(engine =>
        {
            AircraftState before = engine.FindAircraft(Callsign)!;
            string heldDesignator = before.Approach.PendingClearance!.AssignedRunway.Designator;
            Assert.True(engine.SendCommand(Callsign, "CLAND").Success);

            engine.FastForwardTo(ActivationTime, []);

            AircraftState ac = engine.FindAircraft(Callsign)!;
            Assert.NotNull(ac.Phases);
            Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);
            Assert.Equal(heldDesignator, ac.Phases.ClearedRunwayId);
            Assert.Equal(Runway, RunwayIdentifier.ToDisplayDesignator(ac.Phases.ClearedRunwayId!));
        });
    }

    [Fact]
    public void ClandForAnotherRunway_IsRefusedAndLeavesTheHeldClearanceUnstamped()
    {
        WithRestoredEngine(engine =>
        {
            CommandResult refused = engine.SendCommand(Callsign, "CLAND 11L");

            Assert.False(refused.Success);
            Assert.Equal(
                "Cannot clear for runway 11L — N160TM is cleared for the I29RY approach to runway 29R; "
                    + "re-issue the approach for runway 11L first",
                refused.Message
            );

            AircraftState ac = engine.FindAircraft(Callsign)!;
            Assert.Null(ac.Phases);
            Assert.Null(ac.Approach.PendingClearance!.LandingClearance);
        });
    }

    /// <summary>
    /// A direct past the connecting fix rewrites the held clearance's connecting fix (the aircraft now flies an
    /// unpublished route to the direct's fix). The landing clearance issued before the direct has to ride that rewrite.
    /// The recording's own CLAND at t=478 sits where nothing is left on the route past the connecting fix, so this test
    /// restores just after the CAPP and flies to the first second at which a direct still moves that fix.
    /// </summary>
    [Fact]
    public void DirectPastTheConnectingFix_KeepsTheHeldLandingClearance()
    {
        WithRestoredEngine(
            engine =>
            {
                AircraftState ac = engine.FindAircraft(Callsign)!;
                int t = EarlyRestoreTime;
                while ((t < ClandTime) && !DirectWouldMoveTheConnectingFix(ac, out _))
                {
                    t++;
                    engine.FastForwardTo(t, []);
                    ac = engine.FindAircraft(Callsign)!;
                }

                Assert.True(
                    DirectWouldMoveTheConnectingFix(ac, out string directFix),
                    $"No second up to t={ClandTime} where a direct moves the connecting fix (route: "
                        + $"{string.Join(", ", ac.Targets.NavigationRoute.Select(target => target.Name))})"
                );

                Assert.True(engine.SendCommand(Callsign, "CLAND").Success);

                CommandResult dct = engine.SendCommand(Callsign, $"DCT {directFix}");

                Assert.True(dct.Success, dct.Message);
                PendingApproachInfo? after = engine.FindAircraft(Callsign)!.Approach.PendingClearance;
                Assert.NotNull(after);
                Assert.Equal(directFix, after!.ConnectingFix!, ignoreCase: true);
                Assert.Equal(ClearanceType.ClearedToLand, after.LandingClearance);
            },
            EarlyRestoreTime,
            EarlyRestoreTime
        );
    }

    [Fact]
    public void ClandWithAHeldApproachAndAnEmptyPhaseSequence_IsAccepted()
    {
        WithRestoredEngine(engine =>
        {
            CommandResult rwy = engine.SendCommand(Callsign, "RWY 29R");
            Assert.True(rwy.Success, rwy.Message);
            AircraftState assigned = engine.FindAircraft(Callsign)!;
            Assert.NotNull(assigned.Phases);
            Assert.NotNull(assigned.Approach.PendingClearance);

            CommandResult cland = engine.SendCommand(Callsign, "CLAND");

            Assert.True(cland.Success, cland.Message);
            Assert.Equal("Cleared to land, Runway 29R", cland.Message);
            Assert.Equal(ClearanceType.ClearedToLand, engine.FindAircraft(Callsign)!.Approach.PendingClearance!.LandingClearance);
        });
    }

    [Fact]
    public void ClandUnderHeldCapp_ClearsThePersistentPatternDirection()
    {
        WithRestoredEngine(engine =>
        {
            AircraftState before = engine.FindAircraft(Callsign)!;
            before.Pattern.TrafficDirection = PatternDirection.Right;

            Assert.True(engine.SendCommand(Callsign, "CLAND").Success);

            Assert.Null(engine.FindAircraft(Callsign)!.Pattern.TrafficDirection);
        });
    }

    [Fact]
    public void HeldLandingClearance_SurvivesASnapshotRoundTrip()
    {
        WithRestoredEngine(engine =>
        {
            Assert.True(engine.SendCommand(Callsign, "CLAND").Success);

            StateSnapshotDto serialized = RoundTrip(engine.CaptureSnapshot());
            var restored = new SimulationEngine(RecordingGroundData.Evergreen(EvergreenWhy), PushPlanStore.CreateShipped());
            restored.RestoreFromSnapshot(serialized);

            AircraftState ac = restored.FindAircraft(Callsign)!;
            Assert.Null(ac.Phases);
            Assert.Equal(ClearanceType.ClearedToLand, ac.Approach.PendingClearance!.LandingClearance);
        });
    }

    [Fact]
    public void ReclearingTheApproach_DropsTheHeldLandingClearance()
    {
        WithRestoredEngine(engine =>
        {
            Assert.True(engine.SendCommand(Callsign, "CLAND").Success);

            engine.SendCommand(Callsign, $"CAPP {Approach}");

            AircraftState ac = engine.FindAircraft(Callsign)!;
            Assert.Null(ac.Approach.PendingClearance?.LandingClearance);
            Assert.Null(ac.Phases?.LandingClearance);
        });
    }

    private void WithRestoredEngine(Action<SimulationEngine> act, int restoreTime = RestoreTime, int replayTo = ClandTime)
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        Assert.NotNull(archive);
        using (archive)
        {
            TestVnasData.EnsureInitialized();
            SimLogBuilder.CreateForTest(output).InitializeSimLog();

            var engine = new SimulationEngine(RecordingGroundData.Evergreen(EvergreenWhy), PushPlanStore.CreateShipped());
            SessionRecording recording = archive.ToBaseSessionRecording();
            engine.Replay(recording, 0);
            engine.RestoreFromSnapshot(archive.ReadSnapshotAt(restoreTime)!.State);
            engine.FastForwardTo(replayTo, recording.Actions);

            act(engine);
        }
    }

    /// <summary>
    /// Whether a direct to the route's last fix would drop the held clearance's connecting fix from the route — i.e.
    /// whether <c>MovePendingApproachConnectingFixToDirect</c> has anything to rewrite at all.
    /// </summary>
    private static bool DirectWouldMoveTheConnectingFix(AircraftState aircraft, out string directFix)
    {
        directFix = "";
        if (aircraft.Approach.PendingClearance?.ConnectingFix is not { } connectingFix)
        {
            return false;
        }

        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        if (route.Count == 0)
        {
            return false;
        }

        directFix = route[^1].Name;
        return !string.Equals(directFix, connectingFix, StringComparison.OrdinalIgnoreCase);
    }

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, RecordingJsonOptions.Default), RecordingJsonOptions.Default)!;
}
