using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases.Ground;

/// <summary>
/// A command that clears a tow while the aircraft is still moving does not stop it dead: the tug brakes it to rest at
/// <see cref="CategoryPerformance.TugDecelRate"/> (about 5 s and 21 ft from 5 kt) in a <see cref="TugReleasePhase"/> that
/// shows as "Pushback", and the phase the command installed starts only once the aircraft is at rest, with no
/// towbar-disconnect dwell. <c>DEL</c> still stops it dead. The tow is the SFO E6 push to spot 6B, cleared on its
/// push-off once it is up to speed.
/// </summary>
public class TugReleaseTests
{
    private const string Pusher = "PSH1";
    private const string PusherType = "B738";
    private const string PusherGate = "E6";
    private const string AlleySpot = "6B";

    /// <summary>The clearance that takes the tug off mid-move (see <c>PushbackMoveBoundaryTests</c> for why it is a TAXIAUTO).</summary>
    private const string TaxiClearance = $"TAXIAUTO ${AlleySpot}";

    /// <summary>The speed the push is cleared at, knots, and the second it has it by.</summary>
    private const double ClearedAtKts = 4.5;
    private const int ClearedAtBySecond = 17;

    /// <summary>How long the brake to rest may take from push speed, seconds: 5 kt at 1 kt/s is 5 s.</summary>
    private const int BrakeBudgetSeconds = 6;

    /// <summary>How far the brake to rest may carry the aircraft from 4.5-5 kt, feet: v²/2a is 17-21 ft.</summary>
    private const double BrakeMinFt = 15.0;
    private const double BrakeMaxFt = 25.0;

    /// <summary>How far a sub-tick's speed change may overshoot the towbar rate, knots; float noise.</summary>
    private const double RateToleranceKts = 0.01;

    /// <summary>How far the brake's track may lie off the direction the tow was moving, degrees.</summary>
    private const double TrackToleranceDeg = 5.0;

    /// <summary>The steer angle the tow must have on the turn it is cleared on, degrees.</summary>
    private const double TurningSteerDeg = 10.0;

    /// <summary>How soon after the release ends the taxi must be rolling, seconds: no towbar-disconnect dwell.</summary>
    private const int RollingAfterRestSeconds = 5;

    /// <summary>
    /// How far the rest point may lie from the predicted stop pose, feet: physics brakes before it moves each
    /// quarter-second sub-tick, which comes up about <c>v·dt / 2</c> (about a foot from 5 kt) short of <c>v² / 2a</c>.
    /// </summary>
    private const double StopToleranceFt = 2.0;

    /// <summary>The budget for the taxi to the spot once the brake ends, seconds.</summary>
    private const int TaxiBudgetSeconds = 300;

    /// <summary>Slack over the half-fuselage nose-at-spot setback a finished taxi to the spot may rest with, feet.</summary>
    private const double SpotToleranceMarginFt = 25.0;

    private readonly ITestOutputHelper _output;

    public TugReleaseTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// TAXIAUTO mid-push: the phase reads "Pushback" and is the release, the speed falls no faster than the towbar rate,
    /// the aircraft is at rest within <see cref="BrakeBudgetSeconds"/> some 15-25 ft on along the push heading, and the
    /// taxi then starts with the tug gone and rolls without a dwell.
    /// </summary>
    [Fact]
    public void ClearedMidMove_ShowsPushbackAndBrakesToRestAtTheTowbarRate()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        TrueHeading pushHeading = ac.Ground.PushbackTrueHeading ?? throw new InvalidOperationException("the push-off has no push heading");
        LatLon clearedAt = ac.Position;
        Clear(ground, TaxiClearance);
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        Assert.Equal("Pushback", ac.Phases!.CurrentPhase!.Name);

        BrakeRun run = RunBrake(ground, ac, null);
        double decelPerSubTick = CategoryPerformance.TugDecelRate(AircraftCategory.Jet) / SimulationEngine.PhysicsSubTickRate;
        Assert.True(run.MaxDropKts <= decelPerSubTick + RateToleranceKts, $"a sub-tick shed {run.MaxDropKts:F3} kt, past the towbar rate");
        Assert.True(run.Released, $"the release never ended within {BrakeBudgetSeconds}s");
        Assert.True(run.Seconds <= BrakeBudgetSeconds, $"the brake took {run.Seconds:F2}s");

        double travelledFt = FeetBetween(clearedAt, run.RestPosition);
        double trackOffDeg = pushHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(clearedAt, run.RestPosition)));
        _output.WriteLine($"braked {travelledFt:F1} ft in {run.Seconds:F2}s, track {trackOffDeg:F1}° off the push heading");
        Assert.InRange(travelledFt, BrakeMinFt, BrakeMaxFt);
        Assert.True(trackOffDeg <= TrackToleranceDeg, $"the brake ran {trackOffDeg:F1}° off the push heading");

        Assert.IsType<TaxiingPhase>(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Ground.PushbackTrueHeading);
        Assert.Null(ac.Ground.TowbarTrueHeading);
        Assert.Null(ac.Targets.DesiredAccelRate);
        Assert.Null(ac.Targets.DesiredDecelRate);
        int rolling = SfoGroundHarness.TickUntil(ground.Engine, () => ac.GroundSpeed > 0.1, RollingAfterRestSeconds, null);
        Assert.True(rolling > 0, $"the taxi was not rolling {RollingAfterRestSeconds}s after the tug let go");
    }

    /// <summary>The brake runs tail-first along the push the whole way: no sub-tick moves the aircraft nose-first.</summary>
    [Fact]
    public void DuringTheBrake_TheAircraftNeverDrivesNoseFirst()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Clear(ground, TaxiClearance);
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        LatLon last = ac.Position;
        double worstDeg = 180.0;
        SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            Pusher,
            a => a.Phases?.CurrentPhase is not TugReleasePhase,
            BrakeBudgetSeconds,
            a =>
            {
                if ((a.Phases?.CurrentPhase is TugReleasePhase) && (FeetBetween(last, a.Position) > 0.01))
                {
                    worstDeg = Math.Min(worstDeg, a.TrueHeading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(last, a.Position))));
                }

                last = a.Position;
            }
        );

        Assert.True(worstDeg > 90.0, $"a braking step ran {worstDeg:F1}° off the nose — nose-first");
    }

    /// <summary>
    /// Cleared on the push turn: the brake starts from the gear speed the turn had, steers nothing (the nose stays where
    /// the last driven tick left it) and is at rest within the budget.
    /// </summary>
    [Fact]
    public void ClearedOnATurn_BrakesFromTheGearSpeed_AndFreezesTheNose()
    {
        if (StartPush() is not var (ground, ac))
        {
            return;
        }

        AircraftState? turning = SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            Pusher,
            a => (a.Phases?.CurrentPhase is PushbackPhase) && (a.GroundSpeed > 1.0) && (SteerDeg(a) >= TurningSteerDeg),
            120,
            _ => { }
        );
        Assert.NotNull(turning);
        double gearKts = ac.GroundSpeed;
        double nose = ac.TrueHeading.Degrees;
        Clear(ground, TaxiClearance);
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);

        double fastest = 0.0;
        double noseDrift = 0.0;
        BrakeRun run = RunBrake(
            ground,
            ac,
            a =>
            {
                fastest = Math.Max(fastest, a.GroundSpeed);
                noseDrift = Math.Max(noseDrift, new TrueHeading(nose).AbsAngleTo(a.TrueHeading));
            }
        );
        _output.WriteLine($"cleared on the turn at {gearKts:F2} kt; fastest in the brake {fastest:F2} kt, nose drift {noseDrift:F3}°");
        Assert.True(fastest <= gearKts + RateToleranceKts, $"the brake sped up from {gearKts:F2} to {fastest:F2} kt");
        Assert.True(noseDrift <= 0.01, $"the nose turned {noseDrift:F3}° during the brake");
        Assert.True(run.Released && (run.Seconds <= BrakeBudgetSeconds), $"the brake took {run.Seconds:F2}s");
    }

    /// <summary>Cleared while already at rest (the reversal's dwell): no release, the taxi takes over at once.</summary>
    [Fact]
    public void ClearedAtRest_InstallsNoRelease()
    {
        if (StartPush() is not var (ground, ac))
        {
            return;
        }

        int dwelling = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (ac.Phases?.CurrentPhase is PushbackPhase { Move.DwellBefore: true }) && (ac.GroundSpeed <= TugReleasePhase.AtRestKts),
            120,
            null
        );
        Assert.True(dwelling > 0, "the push never reached its reversal dwell");
        Clear(ground, TaxiClearance);
        Assert.IsNotType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Ground.PushbackTrueHeading);
        Assert.Null(ac.Ground.TowbarTrueHeading);
    }

    /// <summary>Cleared while a HOLD is braking the tow: the release takes over from the speed the hold left.</summary>
    [Fact]
    public void ClearedWhileHeld_BrakesFromTheHeldSpeed()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Clear(ground, "HOLD");
        ground.Engine.TickOneSecond();
        double heldKts = ac.GroundSpeed;
        Assert.True(heldKts > 1.0, $"the held tow was already down to {heldKts:F2} kt");
        Clear(ground, TaxiClearance);
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(heldKts, ac.GroundSpeed, 6);

        BrakeRun run = RunBrake(ground, ac, null);
        Assert.True(run.Released && (run.Seconds <= BrakeBudgetSeconds), $"the brake took {run.Seconds:F2}s");
        Assert.IsType<TaxiingPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>A second clearing command mid-brake installs a fresh release that brakes on from the live speed.</summary>
    [Fact]
    public void SecondClearingCommand_DuringTheRelease_ReplacesItFromTheLiveSpeed()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Clear(ground, TaxiClearance);
        ground.Engine.TickOneSecond();
        TugReleasePhase first = Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        double liveKts = ac.GroundSpeed;
        Assert.True(liveKts > 1.0, $"the first release was already down to {liveKts:F2} kt");

        Clear(ground, TaxiClearance);
        TugReleasePhase second = Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        Assert.NotSame(first, second);
        Assert.Equal(liveKts, ac.GroundSpeed, 6);
        Assert.NotNull(ac.Ground.TowbarTrueHeading);

        BrakeRun run = RunBrake(ground, ac, null);
        Assert.True(run.Released && (run.Seconds <= BrakeBudgetSeconds), $"the second brake took {run.Seconds:F2}s");
        Assert.IsType<TaxiingPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>A PUSHM during the release is a redirect of a tug still attached: accepted, behind a fresh release.</summary>
    [Fact]
    public void PushmDuringTheRelease_IsAccepted()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Clear(ground, TaxiClearance);
        ground.Engine.TickOneSecond();
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        Clear(ground, $"PUSHM ${AlleySpot}");
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        Assert.Contains(ac.Phases!.Phases, p => p is PushbackPhase);
    }

    /// <summary>
    /// HOLD during the release is taken ("during pushback"); the brake still ends at rest and the taxi behind it stays
    /// put under the hold until RES, which lets it roll.
    /// </summary>
    [Fact]
    public void HoldAndResDuringTheRelease()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Clear(ground, TaxiClearance);
        ground.Engine.TickOneSecond();
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        CommandResult hold = ground.Engine.SendCommand(Pusher, "HOLD");
        Assert.True(hold.Success, $"HOLD during the release failed: {hold.Message}");
        Assert.Contains("during pushback", hold.Message);

        BrakeRun run = RunBrake(ground, ac, null);
        Assert.True(run.Released, $"the held release never ended within {BrakeBudgetSeconds}s");
        for (int t = 0; t < RollingAfterRestSeconds; t++)
        {
            ground.Engine.TickOneSecond();
            Assert.True(ac.GroundSpeed <= 0.05, $"the held taxi moved at {ac.GroundSpeed:F2} kt");
        }

        Clear(ground, "RES");
        int rolling = SfoGroundHarness.TickUntil(ground.Engine, () => ac.GroundSpeed > 0.5, 20, null);
        Assert.True(rolling > 0, "the taxi never rolled after RES");
    }

    /// <summary>A heading-only PUSH FACE during the release has no push-off left to amend: refused.</summary>
    [Fact]
    public void PushFaceDuringTheRelease_IsRefused()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Clear(ground, TaxiClearance);
        ground.Engine.TickOneSecond();
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        CommandResult face = ground.Engine.SendCommand(Pusher, "PUSH FACE E");
        _output.WriteLine($"PUSH FACE E -> {face.Success}: {face.Message}");
        Assert.False(face.Success);
        Assert.Equal("Unable, tug stopping", face.Message);
        CommandResult push = ground.Engine.SendCommand(Pusher, $"PUSH ${AlleySpot}");
        Assert.False(push.Success);
        Assert.Equal("Unable, tug stopping", push.Message);
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// DEL mid-tow stops the aircraft dead and leaves no tug state behind. A DEL clears the chain through
    /// <c>CommandDispatcher.ClearPhaseChain</c> (a queued or conditional DEL firing mid-tow included) and installs no chain
    /// behind it, so no release follows; an engine with no scenario keeps the aircraft until something removes it.
    /// </summary>
    [Fact]
    public void DelMidTow_StopsDeadAndLeavesNoTugState()
    {
        if (PushToSpeed() is not var (_, ac))
        {
            return;
        }

        CommandDispatcher.ClearPhaseChain(ac, "DEL");
        Assert.Null(ac.Phases);
        Assert.Equal(0.0, ac.IndicatedAirspeed);
        Assert.Null(ac.Ground.PushbackTrueHeading);
        Assert.Null(ac.Ground.TowbarTrueHeading);
        Assert.Null(ac.Targets.DesiredAccelRate);
        Assert.Null(ac.Targets.DesiredDecelRate);
    }

    /// <summary>
    /// A PUSHM that redirects a moving tow plans from where the brake will leave the aircraft — the live pose advanced
    /// along the push heading by <c>v² / 2a</c> — not the live pose: the new tow's row anchor is that pose, and the
    /// aircraft comes to rest on it.
    /// </summary>
    [Fact]
    public void PushmMidTow_PlansFromThePredictedStopPose()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        TrueHeading pushHeading = ac.Ground.PushbackTrueHeading ?? throw new InvalidOperationException("the push-off has no push heading");
        double speedKts = ac.GroundSpeed;
        double brakeFt = speedKts * speedKts / (2.0 * CategoryPerformance.TugDecelRate(AircraftCategory.Jet)) * GeoMath.FeetPerNm / 3600.0;
        LatLon predicted = GeoMath.ProjectPoint(ac.Position, pushHeading, brakeFt / GeoMath.FeetPerNm);
        double nose = ac.TrueHeading.Degrees;
        Clear(ground, $"PUSHM ${AlleySpot}");
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);

        PushbackPose anchor = Assert.NotNull(ac.Ground.TowRowAnchor).TowStartPose;
        double anchorOffFt = FeetBetween(anchor.Position, predicted);
        _output.WriteLine($"redirected at {speedKts:F2} kt: brake {brakeFt:F1} ft; anchor {anchorOffFt:F2} ft off the predicted stop");
        Assert.True(anchorOffFt <= 0.1, $"the new tow was anchored {anchorOffFt:F2} ft from the predicted stop pose");
        Assert.Equal(nose, anchor.NoseTrueDeg, 6);

        BrakeRun run = RunBrake(ground, ac, null);
        double restOffFt = FeetBetween(run.RestPosition, anchor.Position);
        _output.WriteLine($"came to rest {restOffFt:F2} ft from the planned start");
        Assert.True(run.Released, "the release never ended");
        Assert.True(restOffFt <= StopToleranceFt, $"the aircraft came to rest {restOffFt:F2} ft from where the new tow was planned from");
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// A PUSHM whose first move reverses the running push (a pull back onto the gate) keeps the reversal's dwell: the
    /// brake ends at rest, then the pull waits out <see cref="PushbackPhase.DwellSeconds"/> stopped before it moves.
    /// </summary>
    [Fact]
    public void PushmMidTow_ReversingFirstMove_DwellsAfterTheBrake()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Clear(ground, $"PUSHM @{PusherGate}");
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        PushbackPhase pull = ac.Phases!.Phases.OfType<PushbackPhase>().First();
        Assert.Equal(PushbackLegKind.Pull, pull.Kind);
        Assert.True(pull.Move.DwellBefore, "a pull replacing a running push is a reversal, so it dwells first");

        BrakeRun run = RunBrake(ground, ac, null);
        Assert.True(run.Released, "the release never ended");
        int stillSubTicks = 0;
        SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            Pusher,
            a => a.GroundSpeed > TugReleasePhase.AtRestKts,
            30,
            a => stillSubTicks += a.GroundSpeed > TugReleasePhase.AtRestKts ? 0 : 1
        );
        double stillSeconds = (double)stillSubTicks / SimulationEngine.PhysicsSubTickRate;
        _output.WriteLine($"stood {stillSeconds:F2}s at rest between the brake and the pull");
        Assert.Same(pull, ac.Phases?.CurrentPhase);
        Assert.True(stillSeconds >= PushbackPhase.DwellSeconds - 0.5, $"the pull moved after only {stillSeconds:F2}s at rest");
    }

    /// <summary>
    /// A TAXIAUTO mid-push plans its route from the live pose, and the brake then carries the aircraft some 20 ft on
    /// before the taxi starts; the taxi still follows that route to the spot.
    /// </summary>
    [Fact]
    public void TaxiRouteStartedFromTheLivePose_StillFollowsTheRouteAfterTheBrake()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        GroundNode spot = ground.Layout.FindSpotNodeByName(AlleySpot) ?? throw new InvalidOperationException($"SFO has no spot {AlleySpot}");
        Clear(ground, TaxiClearance);
        bool taxied = false;
        int done = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
            {
                taxied |= ac.Phases?.CurrentPhase is TaxiingPhase;
                return taxied && (ac.Phases?.CurrentPhase is not TaxiingPhase);
            },
            TaxiBudgetSeconds,
            null
        );
        double offSpotFt = FeetBetween(ac.Position, spot.Position);
        double allowedFt = (AircraftLength.ResolveFt(PusherType) / 2.0) + SpotToleranceMarginFt;
        _output.WriteLine($"taxi ended t={done}s in {ac.Phases?.CurrentPhase?.Name ?? "null"}, {offSpotFt:F1} ft from spot {AlleySpot}");
        Assert.True(done > 0, $"the taxi never finished within {TaxiBudgetSeconds}s (phase={ac.Phases?.CurrentPhase?.Name})");
        Assert.True(offSpotFt <= allowedFt, $"the taxi ended {offSpotFt:F1} ft from spot {AlleySpot}, past {allowedFt:F0} ft");
    }

    /// <summary>WARP mid-tow lets the tug go: no tug state survives, and a chain started airborne gets no release.</summary>
    [Fact]
    public void WarpMidTow_LeavesNoTugAndStartsNoReleaseAirborne()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Clear(ground, "WARP SUNOL 270 50 120");
        Assert.Null(ac.Ground.TowbarTrueHeading);
        Assert.Null(ac.Ground.PushbackTrueHeading);
        Assert.Null(ac.Targets.DesiredDecelRate);

        // The seam itself: a chain started on an airborne aircraft that somehow still carries a towbar gets no release.
        ac.Ground.TowbarTrueHeading = ac.TrueHeading;
        bool wasOnGround = ac.IsOnGround;
        var phases = new PhaseList();
        phases.Add(new HoldingAfterPushbackPhase());
        phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        Assert.False(wasOnGround);
        Assert.IsNotType<TugReleasePhase>(phases.CurrentPhase);
    }

    /// <summary>WARPG mid-tow lets the tug go: the aircraft never shows a "Pushback" tick at its new spot.</summary>
    [Fact]
    public void WarpGroundMidTow_ShowsNoPushback()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        GroundNode spot = ground.Layout.FindSpotNodeByName(AlleySpot) ?? throw new InvalidOperationException($"SFO has no spot {AlleySpot}");
        Clear(ground, $"WARPG #{spot.Id}");
        Assert.Null(ac.Ground.TowbarTrueHeading);
        Assert.IsNotType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        ground.Engine.TickOneSecond();
        Assert.NotEqual("Pushback", ac.Phases?.CurrentPhase?.Name);
    }

    /// <summary>
    /// During the brake after a TAXIAUTO, the taxi's own follow-ups are judged by the taxi behind the release: HS, CROSS
    /// and FOLLOWG get the taxi's answer, and an SPD sets the taxi speed cap rather than an airborne target.
    /// </summary>
    [Fact]
    public void TaxiFollowUps_DuringTheRelease_AreJudgedByTheTaxi()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Clear(ground, TaxiClearance);
        TugReleasePhase release = Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        TaxiingPhase taxi = Assert.IsType<TaxiingPhase>(release.Next);
        foreach (
            CanonicalCommandType cmd in new[] { CanonicalCommandType.HoldShort, CanonicalCommandType.CrossRunway, CanonicalCommandType.FollowGround }
        )
        {
            Assert.Equal(taxi.CanAcceptCommand(cmd), release.CanAcceptCommand(cmd));
        }

        Clear(ground, "SPD 10");
        Assert.Equal(10.0, ac.Ground.CommandedTaxiSpeedKts);
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>A reversing PUSHM during the release still dwells: the release's kind is the aircraft's last motion.</summary>
    [Fact]
    public void PushmDuringTheRelease_ReversingFirstMove_Dwells()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Clear(ground, TaxiClearance);
        ground.Engine.TickOneSecond();
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        Clear(ground, $"PUSHM @{PusherGate}");
        PushbackPhase pull = ac.Phases!.Phases.OfType<PushbackPhase>().First();
        Assert.Equal(PushbackLegKind.Pull, pull.Kind);
        Assert.True(pull.Move.DwellBefore, "a pull after a braking push is a reversal, so it dwells first");
    }

    /// <summary>A second TAXIAUTO during the release is taken the way the first was, mid-tow: the same readback.</summary>
    [Fact]
    public void SecondTaxi_DuringTheRelease_IsTakenLikeTheFirst()
    {
        if (PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        CommandResult first = ground.Engine.SendCommand(Pusher, TaxiClearance);
        ground.Engine.TickOneSecond();
        Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);
        CommandResult second = ground.Engine.SendCommand(Pusher, TaxiClearance);
        _output.WriteLine($"first: {first.Message}; second: {second.Message}");
        Assert.True(second.Success, second.Message);
        Assert.Equal(first.Message, second.Message);
    }

    /// <summary>
    /// The conflict detector reads a braking push's closing direction off its push heading, not its nose (mid-turn the two
    /// differ), and a braking pull's off its nose.
    /// </summary>
    [Fact]
    public void ClosingDirection_OfARelease_IsItsTravel()
    {
        var where = new LatLon(37.615, -122.39);
        AircraftState push = Released(where, PushbackLegKind.Push, noseDeg: 0.0, pushDeg: 170.0);
        AircraftState pull = Released(where, PushbackLegKind.Pull, noseDeg: 30.0, pushDeg: null);

        Assert.Equal(170.0, GroundConflictDetector.ClosingDirectionDeg(push) ?? double.NaN, 6);
        Assert.Equal(30.0, GroundConflictDetector.ClosingDirectionDeg(pull) ?? double.NaN, 6);
    }

    private static AircraftState Released(LatLon position, PushbackLegKind kind, double noseDeg, double? pushDeg)
    {
        var ac = new AircraftState
        {
            Callsign = kind.ToString(),
            AircraftType = PusherType,
            Position = position,
            TrueHeading = new TrueHeading(noseDeg),
            IsOnGround = true,
            IndicatedAirspeed = 3.0,
            Ground = new AircraftGroundOps
            {
                PushbackTrueHeading = pushDeg is { } deg ? new TrueHeading(deg) : null,
                TowbarTrueHeading = new TrueHeading(noseDeg),
            },
            Phases = new PhaseList(),
        };
        ac.Phases.Add(new TugReleasePhase { Kind = kind });
        ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        return ac;
    }

    /// <summary>The E6 push to 6B issued, not yet ticked; null to skip when the layout or navdata is unavailable.</summary>
    private (SfoGround Ground, AircraftState Aircraft)? StartPush()
    {
        if (SfoGroundHarness.Build(_output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return null;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, Pusher, PusherType, PusherGate);
        Clear(ground, $"PUSH ${AlleySpot}");
        return (ground, ac);
    }

    /// <summary>The E6 push ticked until it is up to <see cref="ClearedAtKts"/> on its push-off.</summary>
    internal (SfoGround Ground, AircraftState Aircraft)? PushToSpeed()
    {
        if (StartPush() is not var (ground, ac))
        {
            return null;
        }

        int rolling = SfoGroundHarness.TickUntil(ground.Engine, () => ac.GroundSpeed >= ClearedAtKts, ClearedAtBySecond, null);
        Assert.True(rolling > 0, $"the push never reached {ClearedAtKts:F1} kt within {ClearedAtBySecond}s");
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        return (ground, ac);
    }

    private static void Clear(SfoGround ground, string command)
    {
        CommandResult result = ground.Engine.SendCommand(Pusher, command);
        Assert.True(result.Success, $"'{command}' failed: {result.Message}");
    }

    /// <summary>
    /// Ticks sub-tick by sub-tick until the release ends: how long it ran, the largest speed drop over a sub-tick, and the
    /// position at the first sub-tick after it (the rest point; the next phase starts from it).
    /// </summary>
    private static BrakeRun RunBrake(SfoGround ground, AircraftState ac, Action<AircraftState>? eachReleaseSubTick)
    {
        double lastKts = ac.GroundSpeed;
        double maxDrop = 0.0;
        int subTicks = 0;
        LatLon? rest = null;
        SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            Pusher,
            a => a.Phases?.CurrentPhase is not TugReleasePhase,
            BrakeBudgetSeconds + 2,
            a =>
            {
                if (rest is not null)
                {
                    return;
                }

                if (a.Phases?.CurrentPhase is not TugReleasePhase)
                {
                    rest = a.Position;
                    return;
                }

                subTicks++;
                maxDrop = Math.Max(maxDrop, lastKts - a.GroundSpeed);
                lastKts = a.GroundSpeed;
                eachReleaseSubTick?.Invoke(a);
            }
        );

        return new BrakeRun(rest is not null, (double)subTicks / SimulationEngine.PhysicsSubTickRate, maxDrop, rest ?? ac.Position);
    }

    /// <summary>One release run: whether it ended, its length in seconds, its largest sub-tick speed drop, where it came to rest.</summary>
    private readonly record struct BrakeRun(bool Released, double Seconds, double MaxDropKts, LatLon RestPosition);

    private static double SteerDeg(AircraftState a) => a.Ground.TowbarTrueHeading is { } towbar ? towbar.AbsAngleTo(a.TrueHeading) : 0.0;

    internal static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;
}
