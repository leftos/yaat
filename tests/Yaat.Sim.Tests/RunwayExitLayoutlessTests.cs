using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// GitHub issue #429: at an airport with no ground layout (KFAT) an arrival that finished its rollout
/// entered <see cref="RunwayExitPhase"/> and never left it. The phase logged "no ground layout" at start
/// but still ran the centerline roll every tick, holding coast speed down the runway with no exit to find,
/// so the aircraft ended up parked at 0 kt on the runway with the phase still active. The queued
/// <see cref="HoldingAfterExitPhase"/> never started, and the engine's layout-less auto-delete — which keys
/// on that phase — never matched.
/// </summary>
public class RunwayExitLayoutlessTests
{
    private const int CompleteWithinSeconds = 20;
    private const int WatchSeconds = 60;

    public RunwayExitLayoutlessTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void NoLayout_RollsToAStopAndCompletes()
    {
        var aircraft = MakeRolloutAircraft();
        var ctx = MakeLayoutlessContext(aircraft);

        int holdingStartedAt = RunSeconds(aircraft, ctx, WatchSeconds);

        Assert.True(
            holdingStartedAt is > 0 and <= CompleteWithinSeconds,
            $"runway exit did not complete without a layout: startedAt={holdingStartedAt}, "
                + $"phase={aircraft.Phases?.CurrentPhase?.Name ?? "none"}, gs={aircraft.GroundSpeed:F1}kt"
        );
        Assert.IsType<HoldingAfterExitPhase>(aircraft.Phases!.CurrentPhase);
        Assert.True(aircraft.GroundSpeed < 1, $"aircraft is still moving: {aircraft.GroundSpeed:F1}kt");
    }

    [Fact]
    public void NoLayout_HoldingAfterExit_DoesNotReportClearOfRunway()
    {
        var aircraft = MakeRolloutAircraft();
        var ctx = MakeLayoutlessContext(aircraft);

        int holdingStartedAt = RunSeconds(aircraft, ctx, WatchSeconds);

        Assert.True(
            holdingStartedAt is > 0 and <= CompleteWithinSeconds,
            $"runway exit did not complete without a layout: startedAt={holdingStartedAt}, phase={aircraft.Phases?.CurrentPhase?.Name ?? "none"}"
        );

        // The aircraft stopped ON the runway — there is no exit taxiway and no layout, so the pilot has
        // nothing to report clear of.
        var lines = aircraft
            .PendingWarnings.Concat(aircraft.PendingPilotSpeech)
            .Concat(aircraft.PendingPilotTransmissions.SelectMany(t => new[] { t.Text, t.SpeechText }))
            .Where(l => l.Contains("clear of runway", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(lines.Count == 0, $"pilot reported clear of the runway while stopped on it: {string.Join("; ", lines)}");
    }

    /// <summary>
    /// Ticks the phase runner and physics for one second at a time, returning the second at which
    /// <see cref="HoldingAfterExitPhase"/> became current (-1 if it never did). Physics is the only
    /// ground-speed integrator, so the loop must run it — the phase only writes control targets.
    /// </summary>
    private static int RunSeconds(AircraftState aircraft, PhaseContext ctx, int seconds)
    {
        int holdingStartedAt = -1;
        for (int t = 1; t <= seconds; t++)
        {
            PhaseRunner.Tick(aircraft, ctx);
            FlightPhysics.Update(aircraft, 1.0, null, null, simTimeSeconds: t);

            if ((holdingStartedAt < 0) && (aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase))
            {
                holdingStartedAt = t;
            }
        }

        return holdingStartedAt;
    }

    /// <summary>A jet just handed off from the rollout at coast speed, with the post-landing phase pair queued.</summary>
    private static AircraftState MakeRolloutAircraft()
    {
        var aircraft = new AircraftState
        {
            Callsign = "TEST429",
            AircraftType = "B738",
            Position = new LatLon(36.7762, -119.7181),
            TrueHeading = new TrueHeading(297),
            IsOnGround = true,
            IndicatedAirspeed = 40,
            FlightPlan = new AircraftFlightPlan { Departure = "KSFO", Destination = "KFAT" },
        };

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        return aircraft;
    }

    private static PhaseContext MakeLayoutlessContext(AircraftState aircraft) =>
        new()
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = null,
            Logger = NullLogger.Instance,
        };
}
