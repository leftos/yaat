using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <c>EL</c>/<c>ER</c>/<c>EXIT &lt;twy&gt;</c> issued after <see cref="RunwayExitPhase"/> has handed a route
/// to the navigator. Handing off the route is not the same thing as turning: the route's first segment is a
/// virtual straight down the runway centerline to the branch node, so the aircraft can be committed and still
/// be tracking straight with thousands of feet to run.
///
/// While it is still on that segment, and more than the turn-lead margin short of the branch, a late change
/// re-targets the exit. Once the turn-off is under way — or about to be — the controller is told the aircraft
/// is unable, which is what a pilot already established in the turn would say.
/// </summary>
public class LateExitChangeTests(ITestOutputHelper output)
{
    private const double RunwayHeadingDeg = 281.0;

    private static (GroundNode Branch, GroundNode HoldShort, string Taxiway)? FindExitPair(AirportGroundLayout layout, string runwayId)
    {
        foreach (var holdShort in layout.GetRunwayHoldShortNodes(runwayId))
        {
            foreach (var edge in holdShort.Edges)
            {
                if (string.IsNullOrEmpty(edge.TaxiwayName))
                {
                    continue;
                }

                foreach (var node in edge.Nodes)
                {
                    if (node.Id != holdShort.Id)
                    {
                        return (node, holdShort, edge.TaxiwayName);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Build an aircraft in <paramref name="state"/> on OAK 28R, positioned <paramref name="distToBranchFt"/>
    /// short of its committed exit's branch node along the runway heading. <paramref name="turnStarted"/>
    /// latches the phase past the point of no return.
    /// </summary>
    private static AircraftState? BuildCommittedExit(RunwayExitPhase.ExitState state, double distToBranchFt, bool turnStarted) =>
        BuildCommittedExit(state, distToBranchFt, turnStarted, speedKts: 25.0);

    private static AircraftState? BuildCommittedExit(RunwayExitPhase.ExitState state, double distToBranchFt, bool turnStarted, double speedKts)
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return null;
        }

        var pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return null;
        }

        var (branch, holdShort, taxiway) = pair.Value;

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = holdShort.Id,
            ExitTaxiway = taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = RunwayHeadingDeg,
            ExitStateValue = (int)state,
            TurnStarted = turnStarted,
            ExitWaypointNodeIds = [branch.Id, holdShort.Id],
        };

        // Back up along the reciprocal so the branch node sits distToBranchFt ahead on the runway heading.
        var reciprocal = new TrueHeading((RunwayHeadingDeg + 180.0) % 360.0);
        var position = GeoMath.ProjectPoint(branch.Position, reciprocal, distToBranchFt / GeoMath.FeetPerNm);

        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(RunwayHeadingDeg),
            Altitude = 9.0,
            IndicatedAirspeed = speedKts,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(RunwayExitPhase.FromSnapshot(dto, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.LayoutAirportId = "OAK";
        return aircraft;
    }

    [Fact]
    public void ExitChange_Committed_ButStillWellShortOfTheBranch_IsAccepted()
    {
        var aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 2000.0, turnStarted: false);
        if (aircraft is null)
        {
            return;
        }

        var result = GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Side = ExitSide.Left }, noDelete: false, expedite: false);

        output.WriteLine($"result={result.Success} msg={result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Equal(ExitSide.Left, aircraft.Phases!.RequestedExit?.Side);
    }

    [Fact]
    public void ExitChange_Committed_AfterTheTurnHasStarted_IsRefused()
    {
        var aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 2000.0, turnStarted: true);
        if (aircraft is null)
        {
            return;
        }

        var before = aircraft.Phases!.RequestedExit;
        var result = GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Side = ExitSide.Left }, noDelete: false, expedite: false);

        output.WriteLine($"result={result.Success} msg={result.Message}");

        Assert.False(result.Success);
        Assert.Contains("turning off", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, aircraft.Phases.RequestedExit);
    }

    [Fact]
    public void ExitChange_Committed_InsideTheTurnLeadMargin_IsRefused()
    {
        var aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 0.0, turnStarted: false);
        if (aircraft is null)
        {
            return;
        }

        var before = aircraft.Phases!.RequestedExit;
        var result = GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Side = ExitSide.Left }, noDelete: false, expedite: false);

        output.WriteLine($"result={result.Success} msg={result.Message}");

        Assert.False(result.Success);
        Assert.Equal(before, aircraft.Phases.RequestedExit);
    }

    [Fact]
    public void ExitChange_Committed_NamedTaxiwayNotAhead_IsRefused()
    {
        var aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 2000.0, turnStarted: false);
        if (aircraft is null)
        {
            return;
        }

        var before = aircraft.Phases!.RequestedExit;
        var result = GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Taxiway = "ZZ9" }, noDelete: false, expedite: false);

        output.WriteLine($"result={result.Success} msg={result.Message}");

        Assert.False(result.Success);
        Assert.Contains("ZZ9", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, aircraft.Phases.RequestedExit);
    }

    /// <summary>
    /// The turn lead answers "has the pilot got time to take the instruction", not "can the aircraft make
    /// that exit" — <c>RunRetargetSearch</c> checks braking separately. At the shipped 4 s lead the lead is
    /// the tighter of the two for any normally-rolling aircraft, so this isolates the braking gate with a
    /// deliberately high energy state: at 150 kt the lead (≈1,040 ft) still passes at this distance, and only
    /// the braking check can reject. Without it, the lead alone would wave the exit through.
    /// </summary>
    [Fact]
    public void ExitChange_ToAnExitTheAircraftCannotBrakeFor_IsRefused()
    {
        var slow = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 2000.0, turnStarted: false, speedKts: 25.0);
        var fast = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 2000.0, turnStarted: false, speedKts: 150.0);
        if (slow is null || fast is null)
        {
            return;
        }

        var atTaxiSpeed = GroundCommandHandler.TryExitCommand(slow, new ExitPreference { Side = ExitSide.Left }, noDelete: false, expedite: false);
        var atHighEnergy = GroundCommandHandler.TryExitCommand(fast, new ExitPreference { Side = ExitSide.Left }, noDelete: false, expedite: false);

        output.WriteLine($"25kt -> {atTaxiSpeed.Success} ({atTaxiSpeed.Message}); 150kt -> {atHighEnergy.Success} ({atHighEnergy.Message})");

        Assert.True(atTaxiSpeed.Success, atTaxiSpeed.Message);
        Assert.False(atHighEnergy.Success);
    }

    /// <summary>
    /// `EL`/`ER`/`EXIT` are `Ground`-category verbs, which <c>CommandRegistry.DefaultProducesPilotUnable</c>
    /// routes into <c>PilotResponder.BuildUnable</c> — so a refusal here is spoken by the pilot in solo mode,
    /// not just printed. <c>CleanUnableReason</c> strips the leading "unable" with an ASCII-only character
    /// class, so an em dash after it survives and the pilot says "unable, — already turning off at G".
    /// Any refusal built by this phase has to come out the far end as clean phraseology.
    /// </summary>
    [Theory]
    [InlineData(false, 2000.0, "ZZ9")] // no such taxiway ahead
    [InlineData(false, 0.0, null)] // inside the turn lead
    [InlineData(true, 2000.0, null)] // turn already started
    public void RefusalText_SurvivesThePilotUnablePipeline(bool turnStarted, double distToBranchFt, string? taxiway)
    {
        var aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt, turnStarted);
        if (aircraft is null)
        {
            return;
        }

        var preference = taxiway is null ? new ExitPreference { Side = ExitSide.Left } : new ExitPreference { Taxiway = taxiway };
        var result = GroundCommandHandler.TryExitCommand(aircraft, preference, noDelete: false, expedite: false);
        Assert.False(result.Success);

        var speech = PilotResponder.BuildUnable(aircraft, result.Message);
        output.WriteLine($"msg={result.Message} -> terminal={speech.Terminal} tts={speech.Tts}");

        Assert.Matches("^unable, [a-z]", speech.Terminal);
        Assert.DoesNotContain("—", speech.Terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("—", speech.Tts, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preference already standing on the aircraft is what produced the committed exit — possibly after a
    /// relaxation — so it is not a late change. A committed phase must keep its exit when nothing new has been
    /// issued, or a restore (which rebuilds the route from scratch) would silently re-resolve and undo both
    /// <c>LandingPhase</c>'s commit and any relaxation behind it.
    /// </summary>
    [Fact]
    public void StandingPreference_OnACommittedPhase_DoesNotRetarget()
    {
        var aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 2000.0, turnStarted: false);
        if (aircraft is null)
        {
            return;
        }

        var phase = Assert.IsType<RunwayExitPhase>(aircraft.Phases!.CurrentPhase);
        string? committedTaxiway = ((RunwayExitPhaseDto)phase.ToSnapshot()).ExitTaxiway;
        Assert.NotNull(committedTaxiway);

        // C1 is the last exit on 28R, so it resolves *ahead* of an aircraft short of J — a spurious
        // re-target would actually move the exit, rather than being masked by an unreachable taxiway.
        const string AheadTaxiway = "C1";
        if (committedTaxiway == AheadTaxiway)
        {
            return;
        }

        aircraft.Phases.RequestedExit = new ExitPreference { Taxiway = AheadTaxiway };

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, aircraft.Ground.Layout!);
        phase.OnTick(ctx);

        string? afterTick = ((RunwayExitPhaseDto)phase.ToSnapshot()).ExitTaxiway;
        output.WriteLine($"committed={committedTaxiway} afterTick={afterTick}");

        Assert.Equal(committedTaxiway, afterTick);
    }

    [Fact]
    public void ExitChange_WhileStillOnTheCenterline_IsAccepted()
    {
        var aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.RollingOnCenterline, distToBranchFt: 0.0, turnStarted: false);
        if (aircraft is null)
        {
            return;
        }

        var result = GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Taxiway = "B" }, noDelete: false, expedite: false);

        output.WriteLine($"result={result.Success} msg={result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Equal("B", aircraft.Phases!.RequestedExit?.Taxiway);
    }
}
