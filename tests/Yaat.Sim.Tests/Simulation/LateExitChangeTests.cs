using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
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
    /// <summary>
    /// Build an aircraft in <paramref name="state"/> on OAK 28R, positioned <paramref name="distToBranchFt"/>
    /// short of its committed exit's branch node along the runway heading. <paramref name="turnStarted"/>
    /// latches the phase past the point of no return. The committed exit is the last one on the left — J, the
    /// high-speed exit before 28R's last stretch — so a late <c>EL</c> aimed at any left exit is a change
    /// toward a nearer, tighter one.
    /// </summary>
    private static AircraftState? BuildCommittedExit(RunwayExitPhase.ExitState state, double distToBranchFt, bool turnStarted) =>
        BuildCommittedExit(state, distToBranchFt, turnStarted, speedKts: 25.0);

    private static AircraftState? BuildCommittedExit(RunwayExitPhase.ExitState state, double distToBranchFt, bool turnStarted, double speedKts)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return null;
        }

        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        TrueHeading heading = runway.TrueHeading;
        LatLon threshold = new(runway.ThresholdLatitude, runway.ThresholdLongitude);
        AirportGroundLayout.CenterlineExitResult exit = LastLeftExit(layout, threshold, heading);

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = exit.HoldShort.Id,
            ExitTaxiway = exit.Taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = heading.Degrees,
            ExitStateValue = (int)state,
            TurnStarted = turnStarted,
            ExitWaypointNodeIds = [.. exit.Path.Select(n => n.Id)],
        };

        // Back up along the reciprocal so the branch node sits distToBranchFt ahead on the runway heading.
        LatLon position = GeoMath.ProjectPoint(exit.Path[0].Position, heading.ToReciprocal(), distToBranchFt / GeoMath.FeetPerNm);

        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = position,
            TrueHeading = heading,
            Altitude = 9.0,
            IndicatedAirspeed = speedKts,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(RunwayExitPhase.FromSnapshot(dto, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.LayoutAirportId = "OAK";
        return aircraft;
    }

    /// <summary>
    /// The last OAK 28R exit on the left, walked from the threshold: the exit a jet at coast speed can just brake
    /// for, and the one a late <c>EL</c> has to be refused for.
    /// </summary>
    private static AirportGroundLayout.CenterlineExitResult LastLeftExit(AirportGroundLayout layout, LatLon threshold, TrueHeading heading)
    {
        var walked = new List<AirportGroundLayout.CenterlineExitResult>();
        layout.FindOnSidePreferredExit(
            threshold.Lat,
            threshold.Lon,
            heading,
            "28R",
            new ExitPreference { Side = ExitSide.Left },
            ExitSide.Left,
            filter: candidate =>
            {
                walked.Add(candidate);
                return AirportGroundLayout.CandidateVerdict.Skip;
            },
            maxIterations: 60
        );

        List<AirportGroundLayout.CenterlineExitResult> onTheLeft = [.. walked.Where(c => c.Side == ExitSide.Left)];
        Assert.NotEmpty(onTheLeft);
        return onTheLeft.MaxBy(c => GeoMath.AlongTrackDistanceNm(c.Path[0].Position, threshold, heading));
    }

    /// <summary>
    /// Every OAK 28R exit on the left still ahead of <paramref name="aircraft"/>, in the order it would meet them.
    /// </summary>
    private static List<AirportGroundLayout.CenterlineExitResult> LeftExitsAhead(
        AircraftState aircraft,
        RunwayInfo runway,
        AirportGroundLayout layout
    )
    {
        var walked = new List<AirportGroundLayout.CenterlineExitResult>();
        layout.FindOnSidePreferredExit(
            aircraft.Position.Lat,
            aircraft.Position.Lon,
            runway.TrueHeading,
            runway.Designator,
            new ExitPreference { Side = ExitSide.Left },
            ExitSide.Left,
            filter: candidate =>
            {
                if (
                    (candidate.Side == ExitSide.Left)
                    && (GeoMath.AlongTrackDistanceNm(candidate.Path[0].Position, aircraft.Position, runway.TrueHeading) > 0)
                )
                {
                    walked.Add(candidate);
                }

                return AirportGroundLayout.CandidateVerdict.Skip;
            },
            maxIterations: 60
        );

        return [.. walked.OrderBy(c => GeoMath.AlongTrackDistanceNm(c.Path[0].Position, aircraft.Position, runway.TrueHeading))];
    }

    [Fact]
    public void ExitChange_Committed_ButStillWellShortOfTheBranch_IsAccepted()
    {
        AircraftState? aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 2000.0, turnStarted: false);
        if (aircraft is null)
        {
            return;
        }

        CommandResult result = GroundCommandHandler.TryExitCommand(
            aircraft,
            new ExitPreference { Side = ExitSide.Left },
            noDelete: false,
            expedite: false
        );

        output.WriteLine($"result={result.Success} msg={result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Equal(ExitSide.Left, aircraft.Phases!.RequestedExit?.Side);
    }

    [Fact]
    public void ExitChange_Committed_AfterTheTurnHasStarted_IsRefused()
    {
        AircraftState? aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 2000.0, turnStarted: true);
        if (aircraft is null)
        {
            return;
        }

        ExitPreference? before = aircraft.Phases!.RequestedExit;
        CommandResult result = GroundCommandHandler.TryExitCommand(
            aircraft,
            new ExitPreference { Side = ExitSide.Left },
            noDelete: false,
            expedite: false
        );

        output.WriteLine($"result={result.Success} msg={result.Message}");

        Assert.False(result.Success);
        Assert.Contains("turning off", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, aircraft.Phases.RequestedExit);
    }

    [Fact]
    public void ExitChange_Committed_InsideTheTurnLeadMargin_IsRefused()
    {
        AircraftState? aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 0.0, turnStarted: false);
        if (aircraft is null)
        {
            return;
        }

        ExitPreference? before = aircraft.Phases!.RequestedExit;
        CommandResult result = GroundCommandHandler.TryExitCommand(
            aircraft,
            new ExitPreference { Side = ExitSide.Left },
            noDelete: false,
            expedite: false
        );

        output.WriteLine($"result={result.Success} msg={result.Message}");

        Assert.False(result.Success);
        Assert.Equal(before, aircraft.Phases.RequestedExit);
    }

    [Fact]
    public void ExitChange_Committed_NamedTaxiwayNotAhead_IsRefused()
    {
        AircraftState? aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 2000.0, turnStarted: false);
        if (aircraft is null)
        {
            return;
        }

        ExitPreference? before = aircraft.Phases!.RequestedExit;
        CommandResult result = GroundCommandHandler.TryExitCommand(
            aircraft,
            new ExitPreference { Taxiway = "ZZ9" },
            noDelete: false,
            expedite: false
        );

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
        AircraftState? slow = BuildCommittedExit(
            RunwayExitPhase.ExitState.FollowingExitPath,
            distToBranchFt: 2000.0,
            turnStarted: false,
            speedKts: 25.0
        );
        AircraftState? fast = BuildCommittedExit(
            RunwayExitPhase.ExitState.FollowingExitPath,
            distToBranchFt: 2000.0,
            turnStarted: false,
            speedKts: 150.0
        );
        if (slow is null || fast is null)
        {
            return;
        }

        // The 150 kt refusal is the braking gate, not the turn lead: every left exit still ahead — J, the committed one, is
        // the farthest — needs more than the jet's firm rate at 150 kt, and at least one lies beyond the turn lead, which
        // the lead alone would let through.
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(fast.Ground.Layout);
        List<AirportGroundLayout.CenterlineExitResult> ahead = LeftExitsAhead(fast, runway, layout);
        Assert.NotEmpty(ahead);
        double leadFt = Math.Max(4.0 * (150.0 * GeoMath.FeetPerNm / 3600.0), 100.0) + CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet);
        Assert.All(
            ahead,
            exit =>
            {
                double toBranchNm = GeoMath.AlongTrackDistanceNm(exit.Path[0].Position, fast.Position, runway.TrueHeading);
                double required = RolloutBraking.RequiredDecelKtsPerSec(
                    150.0,
                    CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, exit.ExitAngle),
                    toBranchNm,
                    AircraftCategory.Jet
                );
                Assert.True(
                    required > RolloutBraking.NamedExitBrakingLimit(AircraftCategory.Jet, expedite: false),
                    $"{exit.Taxiway}'s branch {toBranchNm * GeoMath.FeetPerNm:F0} ft ahead needs {required:F2} kt/s at 150 kt"
                );
            }
        );
        Assert.Contains(
            ahead,
            exit => (GeoMath.AlongTrackDistanceNm(exit.Path[0].Position, fast.Position, runway.TrueHeading) * GeoMath.FeetPerNm) > leadFt
        );

        CommandResult atTaxiSpeed = GroundCommandHandler.TryExitCommand(
            slow,
            new ExitPreference { Side = ExitSide.Left },
            noDelete: false,
            expedite: false
        );
        CommandResult atHighEnergy = GroundCommandHandler.TryExitCommand(
            fast,
            new ExitPreference { Side = ExitSide.Left },
            noDelete: false,
            expedite: false
        );

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
        AircraftState? aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt, turnStarted);
        if (aircraft is null)
        {
            return;
        }

        ExitPreference preference = taxiway is null ? new ExitPreference { Side = ExitSide.Left } : new ExitPreference { Taxiway = taxiway };
        CommandResult result = GroundCommandHandler.TryExitCommand(aircraft, preference, noDelete: false, expedite: false);
        Assert.False(result.Success);

        PilotSpeechText speech = PilotResponder.BuildUnable(aircraft, result.Message);
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
        AircraftState? aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath, distToBranchFt: 2000.0, turnStarted: false);
        if (aircraft is null)
        {
            return;
        }

        RunwayExitPhase phase = Assert.IsType<RunwayExitPhase>(aircraft.Phases!.CurrentPhase);
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

        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, aircraft.Ground.Layout!);
        phase.OnTick(ctx);

        string? afterTick = ((RunwayExitPhaseDto)phase.ToSnapshot()).ExitTaxiway;
        output.WriteLine($"committed={committedTaxiway} afterTick={afterTick}");

        Assert.Equal(committedTaxiway, afterTick);
    }

    [Fact]
    public void ExitChange_WhileStillOnTheCenterline_IsAccepted()
    {
        AircraftState? aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.RollingOnCenterline, distToBranchFt: 0.0, turnStarted: false);
        if (aircraft is null)
        {
            return;
        }

        CommandResult result = GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Taxiway = "B" }, noDelete: false, expedite: false);

        output.WriteLine($"result={result.Success} msg={result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Equal("B", aircraft.Phases!.RequestedExit?.Taxiway);
    }
}
