using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// <c>REPORT REACHING {altitude}</c> and <c>REPORT LEAVING {altitude}</c> (7110.65 6-6-1, AIM 5-3-1.b.2): a real B738
/// east of KOAK flown by the engine, the pilot's call read off the frequency. Reaching fires when the climb or descent
/// crosses the altitude between two one-second samples or levels on it, and lapses silently once the assigned altitude no
/// longer lies at or beyond it; leaving fires once the aircraft is 100 ft past the altitude moving away from it.
/// </summary>
public class ReportAltitudeTests
{
    private const string Callsign = "UAL325";

    /// <summary>The terminal readback clause of <c>CM 090</c>.</summary>
    private const string ClimbToNiner = "climb and maintain 9000";

    private readonly ITestOutputHelper _output;

    public ReportAltitudeTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _output = output;
    }

    [Fact]
    public void Reaching_ClimbThroughIt_PassingFiveThousandForNinerThousand_OnceOnTheCrossingTick()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        Send(rig, "REPORT REACHING 050");

        (double before, double at) = TickUntilReachingFires(rig, 600, null);

        Assert.True((before < 5000) && (at >= 5000), $"fired at {at:F0} ft from {before:F0} ft, not on the tick it passed 5,000");
        Tick(rig, 120);
        AssertSingleCall(rig, PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 9000, levelOnIt: false));
        Assert.Contains(
            $"passing {PhraseologyVerbalizer.AltitudeWords(5000)} for {PhraseologyVerbalizer.AltitudeWords(9000)}",
            PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 9000, levelOnIt: false).Tts,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Reaching_TheAssignedAltitude_LevelAtLevelOff_Once()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        Send(rig, "REPORT REACHING 090");

        (_, double at) = TickUntilReachingFires(rig, 900, null);

        Assert.InRange(at, 9000 - FlightPhysics.AltitudeSnapFt, 9000 + FlightPhysics.AltitudeSnapFt);
        Tick(rig, 120);
        PilotSpeechText expected = PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 9000, 9000, levelOnIt: true);
        Assert.StartsWith("level ", expected.Terminal, StringComparison.Ordinal);
        AssertSingleCall(rig, expected);
    }

    /// <summary>
    /// An expedited B738 climb (over 3,000 fpm, some 50 ft between one-second samples). A first, identical run finds the two
    /// samples either side of 5,000; the report altitude is set halfway between them, so no sample falls within the
    /// capture band of it, and the call still fires on the tick the climb crosses it.
    /// </summary>
    [Fact]
    public void Reaching_ClimbAtThreeThousandFeetPerMinute_CaughtWithNoSampleInTheCaptureBand()
    {
        Rig probe = Stage(altitude: 3000, assigned: 3000);
        Send(probe, "CM 090");
        Send(probe, "EXP");
        List<double> probed = [];
        for (int t = 0; (t < 600) && (probe.Aircraft.Altitude < 5000); t++)
        {
            TickOnce(probe);
            probed.Add(probe.Aircraft.Altitude);
        }

        int reportFt = (int)Math.Round((probed[^2] + probed[^1]) / 2);
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        Send(rig, "EXP");
        Send(rig, $"REPORT REACHING {reportFt}");
        List<double> samples = [];

        (double before, double at) = TickUntilReachingFires(rig, 600, samples);

        Assert.True(rig.Aircraft.VerticalSpeed >= 3000, $"climbing at {rig.Aircraft.VerticalSpeed:F0} fpm, not 3,000");
        Assert.DoesNotContain(samples, s => Math.Abs(s - reportFt) <= FlightPhysics.AltitudeSnapFt);
        Assert.True((before < reportFt) && (at > reportFt + FlightPhysics.AltitudeSnapFt), $"samples {before:F0} -> {at:F0} for {reportFt}");
        Tick(rig, 120);
        AssertSingleCall(rig, PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, reportFt, 9000, levelOnIt: false));
    }

    [Fact]
    public void Reaching_DescendBelowItBeforeReachingIt_LapsesSilently()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        Send(rig, "REPORT REACHING 050");
        TickUntil(rig, () => rig.Aircraft.Altitude >= 4500, 300);
        Send(rig, "DM 040");

        Tick(rig, 1);

        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Assert.Null(rig.Aircraft.Approach.ReportLastSampleAltitudeFt);
        Tick(rig, 300);
        AssertNoAltitudeCall(rig);
    }

    [Fact]
    public void Reaching_NewClimbStopsShortOfIt_Lapses()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        Send(rig, "REPORT REACHING 070");
        TickUntil(rig, () => rig.Aircraft.Altitude >= 5000, 300);
        Send(rig, "CM 060");

        Tick(rig, 1);

        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Tick(rig, 300);
        AssertNoAltitudeCall(rig);
    }

    [Fact]
    public void Reaching_NewClimbBeyondIt_StillFiresAtSevenThousand()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        Send(rig, "REPORT REACHING 070");
        TickUntil(rig, () => rig.Aircraft.Altitude >= 5000, 300);
        Send(rig, "CM 110");

        (double before, double at) = TickUntilReachingFires(rig, 600, null);

        Assert.True((before < 7000) && (at >= 7000), $"fired at {at:F0} ft from {before:F0} ft");
        Tick(rig, 120);
        AssertSingleCall(rig, PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 7000, 11000, levelOnIt: false));
    }

    [Fact]
    public void Leaving_LevelThenClimbClearance_LeavingFiveThousandForNinerThousand_OnceAtOneHundredFeetAbove()
    {
        Rig rig = Stage(altitude: 5000, assigned: 5000);
        Send(rig, "REPORT LEAVING 050");
        Send(rig, "CM 090");

        double at = TickUntilLeavingFires(rig, 300);

        Assert.True(at >= 5100, $"fired at {at:F0} ft");
        Tick(rig, 120);
        PilotSpeechText expected = PilotResponder.BuildLeavingAltitudeReport(rig.Aircraft, 5000, 9000);
        Assert.Contains(
            $"leaving {PhraseologyVerbalizer.AltitudeWords(5000)} for {PhraseologyVerbalizer.AltitudeWords(9000)}",
            expected.Tts,
            StringComparison.Ordinal
        );
        AssertSingleCall(rig, expected);
    }

    [Fact]
    public void Leaving_LevelWithNoNewAssignment_NoCall()
    {
        Rig rig = Stage(altitude: 5000, assigned: 5000);
        Send(rig, "REPORT LEAVING 050");

        Tick(rig, 60);

        Assert.Equal(5000, rig.Aircraft.Approach.ReportLeavingAltitudeFt);
        Assert.Equal(5000, Assert.IsType<ArmedReportsDto>(ArmedReportsDto.From(rig.Aircraft.Approach)).LeavingAltitudeFt);
        AssertNoAltitudeCall(rig);
    }

    [Fact]
    public void Leaving_LevelThenClimbClearanceInALaterTick_Fires()
    {
        Rig rig = Stage(altitude: 5000, assigned: 5000);
        Send(rig, "REPORT LEAVING 050");
        Tick(rig, 10);
        Send(rig, "CM 090");

        double at = TickUntilLeavingFires(rig, 300);

        Assert.True(at >= 5100, $"fired at {at:F0} ft");
        Tick(rig, 120);
        AssertSingleCall(rig, PilotResponder.BuildLeavingAltitudeReport(rig.Aircraft, 5000, 9000));
    }

    [Fact]
    public void Leaving_ClimbingTowardIt_ThenLevelsShortOfIt_Lapses()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        Send(rig, "REPORT LEAVING 070");
        Tick(rig, 10);
        Assert.Equal(7000, rig.Aircraft.Approach.ReportLeavingAltitudeFt);
        Send(rig, "CM 060");

        Tick(rig, 1);

        Assert.Null(rig.Aircraft.Approach.ReportLeavingAltitudeFt);
        Tick(rig, 300);
        AssertNoAltitudeCall(rig);
    }

    [Fact]
    public void Leaving_ClimbingTowardIt_PassesThrough_Fires()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        Send(rig, "REPORT LEAVING 050");

        double at = TickUntilLeavingFires(rig, 600);

        Assert.True(at >= 5100, $"fired at {at:F0} ft");
        Tick(rig, 120);
        AssertSingleCall(rig, PilotResponder.BuildLeavingAltitudeReport(rig.Aircraft, 5000, 9000));
    }

    /// <summary>
    /// Rewrite of the earlier <c>Reaching_AlreadyLevelAtIt_FiresOnTheNextTickNotFromTheHandler</c> under the owner ruling: a
    /// REPORT REACHING for the altitude the aircraft is level at is answered "already level {altitude}" and arms nothing.
    /// </summary>
    [Fact]
    public void Reaching_LevelOnIt_AnswersAlreadyLevel_ArmsNothing()
    {
        Rig rig = Stage(altitude: 5000, assigned: 5000);

        CommandResult result = Send(rig, "REPORT REACHING 050");

        Assert.Equal("Already level 5,000", result.Message);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Tick(rig, 60);
        Assert.Contains(rig.Lines, l => l.Text.Contains("already level 5,000", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Lines, l => l.Text.Contains("will report", StringComparison.Ordinal));
        AssertNoAltitudeCall(rig);
    }

    [Fact]
    public void Reaching_ArmedPastIt_AnswersAlreadyThrough_ArmsNothing()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        TickUntil(rig, () => rig.Aircraft.Altitude >= 7000, 600);

        CommandResult result = Send(rig, "REPORT REACHING 050");

        Assert.Equal("Already through 5,000", result.Message);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Assert.Null(rig.Aircraft.Approach.ReportLastSampleAltitudeFt);
        Tick(rig, 60);
        Assert.Contains(rig.Lines, l => l.Text.Contains("already through 5,000", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Lines, l => l.Text.Contains("will report", StringComparison.Ordinal));
        AssertNoAltitudeCall(rig);
    }

    [Fact]
    public void Leaving_ArmedPastIt_AnswersAlreadyThrough_ArmsNothing()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        TickUntil(rig, () => rig.Aircraft.Altitude >= 7000, 600);

        CommandResult result = Send(rig, "REPORT LEAVING 050");

        Assert.Equal("Already through 5,000", result.Message);
        Assert.Null(rig.Aircraft.Approach.ReportLeavingAltitudeFt);
        Tick(rig, 60);
        Assert.Contains(rig.Lines, l => l.Text.Contains("already through 5,000", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Lines, l => l.Text.Contains("will report", StringComparison.Ordinal));
        AssertNoAltitudeCall(rig);
    }

    /// <summary>
    /// Level below the altitude, holding its assigned altitude: a REPORT REACHING above it is beyond the altitude it is
    /// assigned, so the pilot answers "not instructed to climb 5,000" and arms nothing; a later climb clearance through it
    /// brings no call.
    /// </summary>
    [Fact]
    public void Reaching_LevelHoldingBelowIt_AnswersNotInstructedToClimb_ArmsNothing()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);

        CommandResult result = Send(rig, "REPORT REACHING 050");

        Assert.Equal("Not instructed to climb 5,000", result.Message);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Tick(rig, 10);
        Send(rig, "CM 090");
        Tick(rig, 300);
        AssertSaid(rig, "not instructed to climb 5,000");
        AssertNotSaid(rig, "will report");
        AssertNoAltitudeCall(rig);
    }

    [Fact]
    public void Reaching_LevelHoldingSevenThousand_ReachingNinerThousand_AnswersNotInstructedToClimb()
    {
        Rig rig = Stage(altitude: 7000, assigned: 7000);

        CommandResult result = Send(rig, "REPORT REACHING 090");

        Assert.Equal("Not instructed to climb 9,000", result.Message);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Tick(rig, 60);
        AssertSaid(rig, "not instructed to climb 9,000");
        AssertNotSaid(rig, "will report");
        AssertNoAltitudeCall(rig);
    }

    [Fact]
    public void Reaching_LevelHoldingSevenThousand_ReachingFiveThousand_AnswersNotInstructedToDescend()
    {
        Rig rig = Stage(altitude: 7000, assigned: 7000);

        CommandResult result = Send(rig, "REPORT REACHING 050");

        Assert.Equal("Not instructed to descend 5,000", result.Message);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Tick(rig, 60);
        AssertSaid(rig, "not instructed to descend 5,000");
        AssertNotSaid(rig, "will report");
        AssertNoAltitudeCall(rig);
    }

    /// <summary>Level 1,000 ft above the altitude, then a climb away from it: the report never had the altitude near, and lapses.</summary>
    [Fact]
    public void Leaving_LevelAboveIt_ThenClimbAway_LapsesSilently()
    {
        Rig rig = Stage(altitude: 6000, assigned: 6000);
        CommandResult result = Send(rig, "REPORT LEAVING 050");
        Assert.Equal("Will report leaving 5,000", result.Message);
        Assert.Equal(5000, rig.Aircraft.Approach.ReportLeavingAltitudeFt);
        Assert.False(rig.Aircraft.Approach.ReportLeavingNearSideSeen);
        Send(rig, "CM 090");

        Tick(rig, 1);

        Assert.Null(rig.Aircraft.Approach.ReportLeavingAltitudeFt);
        Tick(rig, 300);
        AssertNoAltitudeCall(rig);
    }

    [Fact]
    public void Leaving_LevelAboveIt_ThenDescendThroughIt_LeavingFiveThousandForThreeThousand()
    {
        Rig rig = Stage(altitude: 6000, assigned: 6000);
        Send(rig, "REPORT LEAVING 050");
        Send(rig, "DM 030");

        double at = TickUntilLeavingFires(rig, 600);

        Assert.True(at <= 4900, $"fired at {at:F0} ft");
        Tick(rig, 120);
        AssertSingleCall(rig, PilotResponder.BuildLeavingAltitudeReport(rig.Aircraft, 5000, 3000));
    }

    [Fact]
    public void Reaching_ClimbingFromFourThousandToNiner_Wilco_PassingFiveThousandForNinerThousand()
    {
        Rig rig = Stage(altitude: 4000, assigned: 9000, verticalSpeedFpm: 1500);

        CommandResult result = Send(rig, "REPORT REACHING 050");

        Assert.Equal("Will report reaching 5,000", result.Message);
        (double before, double at) = TickUntilReachingFires(rig, 300, null);
        Assert.True((before < 5000) && (at >= 5000), $"fired at {at:F0} ft from {before:F0} ft");
        Tick(rig, 60);
        AssertSingleCall(rig, PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 9000, levelOnIt: false));
    }

    [Fact]
    public void Reaching_ClimbingFromSixThousandToNiner_AnswersAlreadyThrough()
    {
        Rig rig = Stage(altitude: 6000, assigned: 9000, verticalSpeedFpm: 1500);

        CommandResult result = Send(rig, "REPORT REACHING 050");

        Assert.Equal("Already through 5,000", result.Message);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Tick(rig, 60);
        AssertSaid(rig, "already through 5,000");
        AssertNotSaid(rig, "will report");
        AssertNoAltitudeCall(rig);
    }

    [Fact]
    public void Reaching_DescendingFromSevenThousandToThree_Wilco_PassingFiveThousandForThreeThousand()
    {
        Rig rig = Stage(altitude: 7000, assigned: 3000, verticalSpeedFpm: -1500);

        CommandResult result = Send(rig, "REPORT REACHING 050");

        Assert.Equal("Will report reaching 5,000", result.Message);
        (double before, double at) = TickUntilReachingFires(rig, 300, null);
        Assert.True((before > 5000) && (at <= 5000), $"fired at {at:F0} ft from {before:F0} ft");
        Tick(rig, 60);
        AssertSingleCall(rig, PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 3000, levelOnIt: false));
    }

    [Fact]
    public void Reaching_DescendingFromFortyFiveHundredToThree_AnswersAlreadyThrough()
    {
        Rig rig = Stage(altitude: 4500, assigned: 3000, verticalSpeedFpm: -1500);

        CommandResult result = Send(rig, "REPORT REACHING 050");

        Assert.Equal("Already through 5,000", result.Message);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Tick(rig, 60);
        AssertSaid(rig, "already through 5,000");
        AssertNotSaid(rig, "will report");
        AssertNoAltitudeCall(rig);
    }

    /// <summary>
    /// Descending through 4,500 to 3,000, told "CM 090, REPORT REACHING 050": the transmission assigns 9,000, so 5,000 lies
    /// ahead of the climb it orders, whatever the descent now. The pilot wilcos and reports passing 5,000 on the climb.
    /// </summary>
    [Fact]
    public void Reaching_DescendingBelowIt_ClimbClearanceFirstInTheBlock_ArmsAndReportsPassingOnTheClimb()
    {
        Rig rig = Stage(altitude: 4500, assigned: 3000, verticalSpeedFpm: -1500);

        Send(rig, "CM 090, REPORT REACHING 050");

        Assert.Equal(5000, rig.Aircraft.Approach.ReportReachingAltitudeFt);
        (double before, double at) = TickUntilReachingFires(rig, 300, null);
        Assert.True((before < 5000) && (at >= 5000), $"fired at {at:F0} ft from {before:F0} ft");
        Tick(rig, 60);
        string readback = LineWith(rig, "will report reaching 5,000");
        Assert.StartsWith(ClimbToNiner, readback, StringComparison.Ordinal);
        AssertSingleCall(rig, PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 9000, levelOnIt: false));
    }

    /// <summary>The same with the clauses the other way round: the climb later in the block is the altitude it is judged against.</summary>
    [Fact]
    public void Reaching_DescendingBelowIt_ClimbClearanceLaterInTheBlock_ArmsAndReportsPassingOnTheClimb()
    {
        Rig rig = Stage(altitude: 4500, assigned: 3000, verticalSpeedFpm: -1500);

        Send(rig, "REPORT REACHING 050, CM 090");

        Assert.Equal(5000, rig.Aircraft.Approach.ReportReachingAltitudeFt);
        (double before, double at) = TickUntilReachingFires(rig, 300, null);
        Assert.True((before < 5000) && (at >= 5000), $"fired at {at:F0} ft from {before:F0} ft");
        Tick(rig, 60);
        string readback = LineWith(rig, "will report reaching 5,000");
        Assert.StartsWith("will report reaching 5,000", readback, StringComparison.Ordinal);
        Assert.Contains(ClimbToNiner, readback, StringComparison.Ordinal);
        AssertSingleCall(rig, PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 9000, levelOnIt: false));
    }

    [Fact]
    public void Reaching_BeyondTheAssignedAltitude_AnswersNotInstructedToClimb()
    {
        Rig rig = Stage(altitude: 4000, assigned: 7000, verticalSpeedFpm: 1500);

        CommandResult result = Send(rig, "REPORT REACHING 090");

        Assert.Equal("Not instructed to climb 9,000", result.Message);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Tick(rig, 60);
        AssertSaid(rig, "not instructed to climb 9,000");
        AssertNotSaid(rig, "will report");
        AssertNoAltitudeCall(rig);
    }

    /// <summary>
    /// Descending through 5,150 at 1,500 fpm with a 5 s reaction delay: the wilco is read back at once, the report arms when
    /// the delay fires above 5,000, and the call comes on the tick the descent crosses it.
    /// </summary>
    [Fact]
    public void Reaching_CrossedAfterTheReactionDelay_FiresOnTheCrossing()
    {
        Rig rig = Stage(altitude: 5150, assigned: 3000, verticalSpeedFpm: -1500, delaySeconds: 5);
        Send(rig, "REPORT REACHING 050");
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Assert.Equal(Pledge(ReportTrigger.ReachingAltitude, PilotProactive.ReportAltitudeAnswer.Wilco, 5150), PledgeOf(rig));

        double atFire = TickUntilDelayFires(rig, 30);

        Assert.True(atFire > 5000, $"the delay fired at {atFire:F0} ft");
        Assert.Equal(5000, rig.Aircraft.Approach.ReportReachingAltitudeFt);
        (double before, double at) = TickUntilReachingFires(rig, 60, null);
        Assert.True((before > 5000) && (at <= 5000), $"fired at {at:F0} ft from {before:F0} ft");
        Tick(rig, 60);
        AssertSaid(rig, "will report reaching 5,000");
        AssertSingleCall(rig, PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 3000, levelOnIt: false));
    }

    /// <summary>
    /// The same with a 10 s delay: the descent crosses 5,000 before the delay fires (about 4,900 then). The wilco stands, the
    /// report arms with the issue altitude as its first sample, and the crossing is reported on the first report pass.
    /// </summary>
    [Fact]
    public void Reaching_CrossedDuringTheReactionDelay_FiresOnTheFirstTickAfterIt()
    {
        Rig rig = Stage(altitude: 5150, assigned: 3000, verticalSpeedFpm: -1500, delaySeconds: 10);
        Send(rig, "REPORT REACHING 050");

        double atFire = TickUntilDelayFires(rig, 30);

        Assert.True(atFire < 5000, $"the delay fired at {atFire:F0} ft");
        PilotSpeechText expected = PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 3000, levelOnIt: false);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Assert.True(HasSaidOrQueued(rig, expected), "the crossing during the delay was not reported on the tick the delay fired");
        Tick(rig, 60);
        AssertSaid(rig, "will report reaching 5,000");
        AssertNotSaid(rig, "already through");
        AssertSingleCall(rig, expected);
    }

    /// <summary>
    /// Climbing through 4,950 to 9,000 with a 10 s delay: the aircraft is past 5,100 when the delay fires. It was near the
    /// altitude at issue, so the report arms having seen its near side and fires on the first report pass.
    /// </summary>
    [Fact]
    public void Leaving_PassedDuringTheReactionDelay_FiresOnTheFirstTickAfterIt()
    {
        Rig rig = Stage(altitude: 4950, assigned: 9000, verticalSpeedFpm: 1500, delaySeconds: 10);
        Send(rig, "REPORT LEAVING 050");
        Assert.Equal(Pledge(ReportTrigger.LeavingAltitude, PilotProactive.ReportAltitudeAnswer.Wilco, 4950), PledgeOf(rig));

        double atFire = TickUntilDelayFires(rig, 30);

        Assert.True(atFire >= 5100, $"the delay fired at {atFire:F0} ft");
        PilotSpeechText expected = PilotResponder.BuildLeavingAltitudeReport(rig.Aircraft, 5000, 9000);
        Assert.Null(rig.Aircraft.Approach.ReportLeavingAltitudeFt);
        Assert.True(HasSaidOrQueued(rig, expected), "leaving 5,000 during the delay was not reported on the tick the delay fired");
        Tick(rig, 60);
        AssertSaid(rig, "will report leaving 5,000");
        AssertSingleCall(rig, expected);
    }

    [Fact]
    public void Reaching_NothingAssigned_Descending_PassingFiveThousand()
    {
        Rig rig = Stage(altitude: 5500, assigned: null, verticalSpeedFpm: -1500, target: 3000);

        CommandResult result = Send(rig, "REPORT REACHING 050");

        Assert.Equal("Will report reaching 5,000", result.Message);
        (double before, double at) = TickUntilReachingFires(rig, 300, null);
        Assert.True((before > 5000) && (at <= 5000), $"fired at {at:F0} ft from {before:F0} ft");
        Tick(rig, 60);
        PilotSpeechText expected = PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, null, levelOnIt: false);
        Assert.DoesNotContain(" for ", expected.Terminal, StringComparison.Ordinal);
        AssertSingleCall(rig, expected);
    }

    [Fact]
    public void Reaching_NothingAssigned_Level_ArmsAndFiresOnTheCrossing()
    {
        Rig rig = Stage(altitude: 4000, assigned: null);

        CommandResult result = Send(rig, "REPORT REACHING 050");

        Assert.Equal("Will report reaching 5,000", result.Message);
        Tick(rig, 10);
        Assert.Equal(5000, rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Send(rig, "CM 060");
        (double before, double at) = TickUntilReachingFires(rig, 300, null);
        Assert.True((before < 5000) && (at >= 5000), $"fired at {at:F0} ft from {before:F0} ft");
        Tick(rig, 60);
        AssertSingleCall(rig, PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 6000, levelOnIt: false));
    }

    [Fact]
    public void Reaching_AlreadyThroughAtIssue_UnderAReactionDelay_ArmsNothingAtFire()
    {
        Rig rig = Stage(altitude: 4500, assigned: 3000, verticalSpeedFpm: -1500, delaySeconds: 5);
        Send(rig, "REPORT REACHING 050");
        Assert.Equal(Pledge(ReportTrigger.ReachingAltitude, PilotProactive.ReportAltitudeAnswer.AlreadyThrough, 4500), PledgeOf(rig));

        TickUntilDelayFires(rig, 30);

        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Tick(rig, 60);
        AssertSaid(rig, "already through 5,000");
        AssertNotSaid(rig, "will report");
        AssertNoAltitudeCall(rig);
    }

    /// <summary>
    /// Descending through 5,150 to 3,000 with a 10 s delay, told "CM 090, REPORT REACHING 050": the climb the transmission
    /// orders leaves 5,000 behind, so the pilot reads back "already through 5,000". When the delay fires the aircraft is
    /// below 5,000 and the climb applies, so a judge then would wilco; the answer given stands and nothing is armed.
    /// </summary>
    [Fact]
    public void Reaching_AlreadyThroughAtIssue_StaysTheAnswerWhenTheDelayFires()
    {
        Rig rig = Stage(altitude: 5150, assigned: 3000, verticalSpeedFpm: -1500, delaySeconds: 10);
        Send(rig, "CM 090, REPORT REACHING 050");
        Assert.Equal(Pledge(ReportTrigger.ReachingAltitude, PilotProactive.ReportAltitudeAnswer.AlreadyThrough, 5150), PledgeOf(rig));

        double atFire = TickUntilDelayFires(rig, 30);

        Assert.True(atFire < 5000, $"the delay fired at {atFire:F0} ft");
        Assert.Equal(9000, rig.Aircraft.Targets.AssignedAltitude);
        Assert.Equal(
            PilotProactive.ReportAltitudeAnswer.Wilco,
            PilotProactive.AnswerReportAltitude(ReportTrigger.ReachingAltitude, 5000, rig.Aircraft.Altitude, rig.Aircraft.VerticalSpeed, 9000)
        );
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Tick(rig, 180);
        Assert.True(rig.Aircraft.Altitude > 5500, $"at {rig.Aircraft.Altitude:F0} ft, not back above 5,000");
        string readback = LineWith(rig, "already through 5,000");
        Assert.StartsWith(ClimbToNiner, readback, StringComparison.Ordinal);
        AssertNotSaid(rig, "will report");
        AssertNoAltitudeCall(rig);
    }

    /// <summary>
    /// "FH 270; REPORT REACHING 050" descending through 5,500: the REPORT is in the second block, so its readback is the plain
    /// wilco. The turn takes long enough that the aircraft is below 5,000 when the block applies, and the pilot reports
    /// passing 5,000 at once, arming nothing.
    /// </summary>
    [Fact]
    public void Reaching_InALaterBlock_AppliedBelowIt_ReportsPassingAtOnce()
    {
        Rig rig = Stage(altitude: 5500, assigned: 3000, verticalSpeedFpm: -1500);
        Send(rig, "FH 270; REPORT REACHING 050");

        double atApply = TickUntilReportBlockApplies(rig, 300);

        Assert.True(atApply < 5000, $"the block applied at {atApply:F0} ft");
        PilotSpeechText expected = PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 3000, levelOnIt: false);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Assert.True(HasSaidOrQueued(rig, expected), "no call on the tick the block applied");
        Tick(rig, 60);
        AssertSaid(rig, "will report reaching 5,000");
        AssertNotSaid(rig, "already through");
        AssertSingleCall(rig, expected);
    }

    [Fact]
    public void Reaching_InALaterBlock_AppliedLevelOnIt_ReportsLevelAtOnce()
    {
        Rig rig = Stage(altitude: 5000, assigned: 5000);
        Send(rig, "FH 270; REPORT REACHING 050");

        TickUntilReportBlockApplies(rig, 300);

        PilotSpeechText expected = PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 5000, levelOnIt: true);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Assert.True(HasSaidOrQueued(rig, expected), "no call on the tick the block applied");
        Tick(rig, 60);
        AssertSaid(rig, "will report reaching 5,000");
        AssertNotSaid(rig, "already level");
        AssertSingleCall(rig, expected);
    }

    [Fact]
    public void Reaching_InALaterBlock_AppliedLevelBelowIt_ReportsNotInstructedAtOnce()
    {
        Rig rig = Stage(altitude: 7000, assigned: 7000);
        Send(rig, "FH 270; REPORT REACHING 090");

        TickUntilReportBlockApplies(rig, 300);

        const string NotInstructed = "not instructed to climb 9,000";
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Assert.True(
            rig.Lines.Exists(l => l.Text.StartsWith(NotInstructed, StringComparison.Ordinal))
                || rig.Aircraft.PendingPilotTransmissions.Exists(t => t.Text.StartsWith(NotInstructed, StringComparison.Ordinal)),
            "no call on the tick the block applied"
        );
        Tick(rig, 60);
        AssertSaid(rig, "will report reaching 9,000");
        Assert.Single(rig.Lines, l => l.Text.StartsWith(NotInstructed, StringComparison.Ordinal));
        AssertNoAltitudeCall(rig);
    }

    /// <summary>
    /// The 5 s delay case with a snapshot taken mid-delay: the restored run fires the same call on the same tick as the
    /// original.
    /// </summary>
    [Fact]
    public void Reaching_SnapshotMidReactionDelay_FiresTheSameCallAtTheSameTick()
    {
        Rig rig = Stage(altitude: 5150, assigned: 3000, verticalSpeedFpm: -1500, delaySeconds: 5);
        Send(rig, "REPORT REACHING 050");
        Tick(rig, 2);
        Assert.Single(rig.Aircraft.DeferredDispatches);
        StateSnapshotDto snapshot = rig.Engine.CaptureSnapshot();

        SimulationEngine restoredEngine = NewEngine(solo: true);
        restoredEngine.RestoreFromSnapshot(snapshot);
        AircraftState restoredAircraft = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(Callsign));
        Rig restored = new(restoredEngine, restoredAircraft, []);
        Assert.Equal(Pledge(ReportTrigger.ReachingAltitude, PilotProactive.ReportAltitudeAnswer.Wilco, 5150), PledgeOf(restored));

        int original = TicksUntil(rig, () => rig.Aircraft.DeferredDispatches.Count == 0, 30) + TicksUntilReachingFires(rig, 60);
        int replayed = TicksUntil(restored, () => restored.Aircraft.DeferredDispatches.Count == 0, 30) + TicksUntilReachingFires(restored, 60);
        Tick(rig, 120);
        Tick(restored, 120);

        Assert.Equal(original, replayed);
        PilotSpeechText expected = PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 3000, levelOnIt: false);
        AssertSingleCall(rig, expected);
        AssertSingleCall(restored, expected);
        Assert.Equal(rig.Lines.Single(l => l.Text == expected.Terminal).AtSeconds, restored.Lines.Single(l => l.Text == expected.Terminal).AtSeconds);
    }

    /// <summary>
    /// Solo, with the pilot tuned to an AI-staffed OAK_TWR when the second block of "FH 270; REPORT REACHING 050" applies below
    /// 5,000: the passing call said at once goes where the report pass sends that pilot's calls, onto the frequency, not into
    /// a warning line.
    /// </summary>
    [Fact]
    public void Reaching_InALaterBlock_TunedToAnAiStaffedPosition_TheCallSaidAtOnceIsSpoken()
    {
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        Rig rig = Stage(altitude: 5500, assigned: 3000, verticalSpeedFpm: -1500);
        SimScenarioState scenario = Assert.IsType<SimScenarioState>(rig.Engine.Scenario);
        scenario.ArtccConfig = zoa;
        scenario.SetAiStaffedPositions([TestAiPositions.OakTower(zoa)]);
        Send(rig, "FH 270; REPORT REACHING 050");
        rig.Aircraft.TunedPosition = TestAiPositions.OakTower(zoa).Identity;
        Assert.True(PilotResponder.IsTunedToAiStaffedPosition(rig.Aircraft, scenario.PilotContacts), "not tuned to an AI-staffed position");

        double atApply = TickUntilReportBlockApplies(rig, 300);

        Assert.True(atApply < 5000, $"the block applied at {atApply:F0} ft");
        PilotSpeechText expected = PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 3000, levelOnIt: false);
        Assert.DoesNotContain(expected.Terminal, rig.Aircraft.PendingWarnings);
        Assert.True(HasSaidOrQueued(rig, expected), "the call said at once was not spoken on the frequency");
    }

    /// <summary>
    /// "REPORT REACHING 050, REPORT LEAVING 070" descending through 5,150 to level at its assigned 5,000, with a 10 s delay:
    /// the readback wilcos the reaching report (5,000 lies ahead) and answers "already through 7,000" for the leaving one.
    /// When the delay fires the aircraft is level at 5,000, where a judge then would answer "already level" and the wilco;
    /// each report is obeyed as its own readback clause said: the reaching report arms and calls, the leaving one arms nothing.
    /// </summary>
    [Fact]
    public void TwoReports_UnderAReactionDelay_EachObeysItsOwnReadbackClause()
    {
        Rig rig = Stage(altitude: 5150, assigned: 5000, verticalSpeedFpm: -1500, delaySeconds: 10);
        Send(rig, "REPORT REACHING 050, REPORT LEAVING 070");
        Assert.Equal(TwoReportPledges, PledgesOf(rig));

        TickUntilDelayFires(rig, 30);

        AssertJudgedNow(rig, ReportTrigger.ReachingAltitude, 5000, PilotProactive.ReportAltitudeAnswer.AlreadyLevel);
        AssertJudgedNow(rig, ReportTrigger.LeavingAltitude, 7000, PilotProactive.ReportAltitudeAnswer.Wilco);
        Assert.Null(rig.Aircraft.Approach.ReportLeavingAltitudeFt);
        Tick(rig, 60);
        Assert.Contains("already through 7,000", LineWith(rig, "will report reaching 5,000"), StringComparison.Ordinal);
        Assert.Null(rig.Aircraft.Approach.ReportReachingAltitudeFt);
        Assert.Single(rig.Lines, IsReachingFiveThousandCall);
        AssertNotSaid(rig, "leaving 7,000");
    }

    /// <summary>
    /// The two-report case with a snapshot taken mid-delay: the restored delay carries both answers, and the restored run
    /// obeys each as its readback clause said.
    /// </summary>
    [Fact]
    public void TwoReports_SnapshotMidReactionDelay_RestoresBothAnswers()
    {
        Rig rig = Stage(altitude: 5150, assigned: 5000, verticalSpeedFpm: -1500, delaySeconds: 10);
        Send(rig, "REPORT REACHING 050, REPORT LEAVING 070");
        Tick(rig, 2);
        StateSnapshotDto snapshot = rig.Engine.CaptureSnapshot();

        SimulationEngine restoredEngine = NewEngine(solo: true);
        restoredEngine.RestoreFromSnapshot(snapshot);
        Rig restored = new(restoredEngine, Assert.IsType<AircraftState>(restoredEngine.FindAircraft(Callsign)), []);
        Assert.Equal(TwoReportPledges, PledgesOf(rig));
        Assert.Equal(TwoReportPledges, PledgesOf(restored));

        TickUntilDelayFires(restored, 30);

        Assert.Null(restored.Aircraft.Approach.ReportLeavingAltitudeFt);
        Tick(restored, 60);
        Assert.Null(restored.Aircraft.Approach.ReportReachingAltitudeFt);
        Assert.Single(restored.Lines, IsReachingFiveThousandCall);
        AssertNotSaid(restored, "leaving 7,000");
    }

    /// <summary>Level below the altitude with its present altitude assigned: REPORT LEAVING waits for the climb clearance too.</summary>
    [Fact]
    public void Leaving_LevelBelowIt_WaitsForTheClimbClearance_Fires()
    {
        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "REPORT LEAVING 050");
        Tick(rig, 10);
        Assert.Equal(5000, rig.Aircraft.Approach.ReportLeavingAltitudeFt);
        Send(rig, "CM 090");

        double at = TickUntilLeavingFires(rig, 600);

        Assert.True(at >= 5100, $"fired at {at:F0} ft");
        Tick(rig, 120);
        AssertSingleCall(rig, PilotResponder.BuildLeavingAltitudeReport(rig.Aircraft, 5000, 9000));
    }

    /// <summary>
    /// No assigned altitude, climbing to a target of 5,000 (AIM 5-3-1.b.2(a)): levelling on the report altitude says "level",
    /// not "passing".
    /// </summary>
    [Fact]
    public void Reaching_LevelOnIt_NotAssigned_SaysLevel()
    {
        Rig rig = Stage(altitude: 3000, assigned: null);
        rig.Aircraft.Targets.TargetAltitude = 5000;
        Send(rig, "REPORT REACHING 050");

        (_, double at) = TickUntilReachingFires(rig, 600, null);

        Assert.InRange(at, 5000 - FlightPhysics.AltitudeSnapFt, 5000 + FlightPhysics.AltitudeSnapFt);
        Tick(rig, 120);
        PilotSpeechText expected = PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, null, levelOnIt: true);
        Assert.StartsWith("level ", expected.Terminal, StringComparison.Ordinal);
        AssertSingleCall(rig, expected);
    }

    /// <summary>
    /// A snapshot taken one tick before the crossing (found by an identical probe run) restores the previous report sample, so
    /// the restored run fires the same call on the same tick as the original. Without that sample the restored run would see
    /// no crossing and lapse the report.
    /// </summary>
    [Fact]
    public void Reaching_RestoredSnapshotMidClimb_FiresTheSameCallAtTheSameTick()
    {
        Rig probe = Stage(altitude: 3000, assigned: 3000);
        Send(probe, "CM 090");
        Send(probe, "REPORT REACHING 050");
        int crossingTick = TicksUntilReachingFires(probe, 600);

        Rig rig = Stage(altitude: 3000, assigned: 3000);
        Send(rig, "CM 090");
        Send(rig, "REPORT REACHING 050");
        Tick(rig, crossingTick - 1);
        Assert.NotNull(rig.Aircraft.Approach.ReportLastSampleAltitudeFt);
        StateSnapshotDto snapshot = rig.Engine.CaptureSnapshot();

        SimulationEngine restoredEngine = NewEngine(solo: true);
        restoredEngine.RestoreFromSnapshot(snapshot);
        AircraftState restoredAircraft = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(Callsign));
        Rig restored = new(restoredEngine, restoredAircraft, []);

        int original = TicksUntilReachingFires(rig, 600);
        int replayed = TicksUntilReachingFires(restored, 600);
        Tick(rig, 120);
        Tick(restored, 120);

        Assert.Equal(1, original);
        Assert.Equal(original, replayed);
        PilotSpeechText expected = PilotResponder.BuildReachingAltitudeReport(rig.Aircraft, 5000, 9000, levelOnIt: false);
        AssertSingleCall(rig, expected);
        AssertSingleCall(restored, expected);
        Assert.Equal(rig.Lines.Single(l => l.Text == expected.Terminal).AtSeconds, restored.Lines.Single(l => l.Text == expected.Terminal).AtSeconds);
    }

    /// <summary>Solo, the pilot on the student's frequency: the reaching call is queued for the frequency, as the at-fix call is.</summary>
    [Fact]
    public void Reaching_Solo_GoesWhereTheAtFixReportGoes()
    {
        (AircraftState atFix, AircraftState reaching) = ArmedPair();
        SimScenarioState scenario = Scenario(solo: true);

        PilotProactive.TickReportTriggers(atFix, scenario);
        PilotProactive.TickReportTriggers(reaching, scenario);

        Assert.Single(atFix.PendingPilotTransmissions);
        PilotTransmission tx = Assert.Single(reaching.PendingPilotTransmissions);
        Assert.Equal(PilotResponder.BuildReachingAltitudeReport(reaching, 5000, 9000, levelOnIt: false).Terminal, tx.Text);
        Assert.Empty(reaching.PendingWarnings);
        Assert.Empty(reaching.PendingPilotSpeech);
    }

    /// <summary>An RPO room that hides pilot speech: the reaching call is a warning in the RPO form, as the at-fix call is.</summary>
    [Fact]
    public void Reaching_Rpo_GoesWhereTheAtFixReportGoes()
    {
        (AircraftState atFix, AircraftState reaching) = ArmedPair();
        SimScenarioState scenario = Scenario(solo: false);

        PilotProactive.TickReportTriggers(atFix, scenario);
        PilotProactive.TickReportTriggers(reaching, scenario);

        Assert.Single(atFix.PendingWarnings);
        Assert.Empty(atFix.PendingPilotTransmissions);
        string warning = Assert.Single(reaching.PendingWarnings);
        Assert.Equal(PilotResponder.BuildReachingAltitudeReport(reaching, 5000, 9000, levelOnIt: false).TerminalForRpo, warning);
        Assert.Empty(reaching.PendingPilotTransmissions);
    }

    /// <summary>Two copies of the aircraft at SUNOL passing 5,000 for 9,000: one with the at-fix report armed, one with reaching.</summary>
    private static (AircraftState AtFix, AircraftState Reaching) ArmedPair()
    {
        (double lat, double lon) = Assert.IsType<(double, double)>(NavigationDatabase.Instance.GetFixPosition("SUNOL"));
        AircraftState atFix = Aircraft(new LatLon(lat, lon), 5050, 9000);
        atFix.Approach.ReportAtFixName = "SUNOL";
        atFix.Approach.ReportAtFixLat = lat;
        atFix.Approach.ReportAtFixLon = lon;
        AircraftState reaching = Aircraft(new LatLon(lat, lon), 5050, 9000);
        reaching.Approach.ReportReachingAltitudeFt = 5000;
        reaching.Approach.ReportLastSampleAltitudeFt = 4990;
        return (atFix, reaching);
    }

    /// <summary>
    /// The aircraft east of KOAK at <paramref name="altitude"/>, assigned <paramref name="assigned"/>. A non-zero
    /// <paramref name="verticalSpeedFpm"/> has it climbing or descending at that rate toward <paramref name="target"/> (else the
    /// assigned altitude); <paramref name="delaySeconds"/> is the room's command-run delay, min and max.
    /// </summary>
    private Rig Stage(double altitude, double? assigned, double verticalSpeedFpm = 0, int delaySeconds = 0, double? target = null)
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        LatLon field = Assert.IsType<LatLon>(NavigationDatabase.Instance.GetAirportPosition("KOAK") is { } pos ? new LatLon(pos.Lat, pos.Lon) : null);
        AircraftState aircraft = Aircraft(GeoMath.ProjectPoint(field, new TrueHeading(90), 20), altitude, assigned);
        if (verticalSpeedFpm != 0)
        {
            aircraft.VerticalSpeed = verticalSpeedFpm;
            aircraft.Targets.TargetAltitude = target ?? assigned;
            aircraft.Targets.DesiredVerticalRate = Math.Abs(verticalSpeedFpm);
        }

        SimulationEngine engine = NewEngine(solo: true, delaySeconds);
        engine.World.AddAircraft(aircraft);
        return new Rig(engine, aircraft, []);
    }

    private static AircraftState Aircraft(LatLon position, double altitude, double? assigned)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(90),
            TrueTrack = new TrueHeading(90),
            Altitude = altitude,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            HasMadeInitialContact = true,
            HasControllerAcknowledgedInitialContact = true,
            FlightPlan = new AircraftFlightPlan { FlightRules = "IFR", Destination = "KOAK" },
        };
        aircraft.Targets.AssignedAltitude = assigned;
        return aircraft;
    }

    private static SimulationEngine NewEngine(bool solo, int delaySeconds = 0) =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped()) { Scenario = Scenario(solo, delaySeconds) };

    private static SimScenarioState Scenario(bool solo, int delaySeconds = 0) =>
        new()
        {
            ScenarioId = "report-altitude",
            ScenarioName = "Report reaching and leaving an altitude",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            SoloTrainingMode = solo,
            StudentPositionType = "APP",
            CommandRunDelayMinSeconds = delaySeconds,
            CommandRunDelayMaxSeconds = delaySeconds,
        };

    private CommandResult Send(Rig rig, string command)
    {
        CommandResult result = rig.Engine.SendCommand(Callsign, command);
        _output.WriteLine($"{command} -> {result.Message}");
        Assert.True(result.Success, $"{command}: {result.Message}");
        return result;
    }

    private static void Tick(Rig rig, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            TickOnce(rig);
        }
    }

    private static void TickOnce(Rig rig)
    {
        double? lastAt = rig.Aircraft.LastPilotTransmission?.AtSeconds;
        rig.Engine.TickOneSecond();
        if ((rig.Aircraft.LastPilotTransmission is { } line) && (line.AtSeconds != lastAt))
        {
            rig.Lines.Add(line);
        }
    }

    private static void TickUntil(Rig rig, Func<bool> done, int maxSeconds)
    {
        for (int t = 0; (t < maxSeconds) && !done(); t++)
        {
            TickOnce(rig);
        }

        Assert.True(done(), $"condition not met within {maxSeconds} s (at {rig.Aircraft.Altitude:F0} ft)");
    }

    /// <summary>Ticks until the reaching report is no longer armed; the altitudes sampled before and at that tick.</summary>
    private (double Before, double At) TickUntilReachingFires(Rig rig, int maxSeconds, List<double>? samples)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            double before = rig.Aircraft.Altitude;
            TickOnce(rig);
            samples?.Add(rig.Aircraft.Altitude);
            if (rig.Aircraft.Approach.ReportReachingAltitudeFt is null)
            {
                _output.WriteLine($"Reaching fired after {t} s: {before:F0} -> {rig.Aircraft.Altitude:F0} ft, {rig.Aircraft.VerticalSpeed:F0} fpm");
                return (before, rig.Aircraft.Altitude);
            }
        }

        Assert.Fail($"The reaching report never fired within {maxSeconds} s (at {rig.Aircraft.Altitude:F0} ft)");
        return default;
    }

    private static int TicksUntilReachingFires(Rig rig, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            TickOnce(rig);
            if (rig.Aircraft.Approach.ReportReachingAltitudeFt is null)
            {
                return t;
            }
        }

        Assert.Fail($"The reaching report never fired within {maxSeconds} s");
        return -1;
    }

    private double TickUntilLeavingFires(Rig rig, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            TickOnce(rig);
            if (rig.Aircraft.Approach.ReportLeavingAltitudeFt is null)
            {
                _output.WriteLine($"Leaving fired after {t} s at {rig.Aircraft.Altitude:F0} ft, {rig.Aircraft.VerticalSpeed:F0} fpm");
                return rig.Aircraft.Altitude;
            }
        }

        Assert.Fail($"The leaving report never fired within {maxSeconds} s (at {rig.Aircraft.Altitude:F0} ft)");
        return default;
    }

    private static void AssertSingleCall(Rig rig, PilotSpeechText expected)
    {
        AircraftLastPilotTransmission line = Assert.Single(rig.Lines, l => l.Text == expected.Terminal);
        Assert.Equal(expected.Tts, line.SpeechText);
    }

    /// <summary>The answer a 5,000 ft report's reaction delay should carry.</summary>
    private static ReportPledge Pledge(ReportTrigger trigger, PilotProactive.ReportAltitudeAnswer answer, double issueAltitudeFt) =>
        new(trigger, 5000, answer, issueAltitudeFt);

    /// <summary>The one answer the aircraft's one pending reaction delay carries.</summary>
    private static ReportPledge PledgeOf(Rig rig) => Assert.Single(PledgesOf(rig));

    /// <summary>The answers the aircraft's one pending reaction delay carries.</summary>
    private static IReadOnlyList<ReportPledge> PledgesOf(Rig rig) => Assert.Single(rig.Aircraft.DeferredDispatches).ReportPledges;

    /// <summary>The answers "REPORT REACHING 050, REPORT LEAVING 070" issued at 5,150 descending to 5,000 should carry.</summary>
    private static readonly ReportPledge[] TwoReportPledges =
    [
        Pledge(ReportTrigger.ReachingAltitude, PilotProactive.ReportAltitudeAnswer.Wilco, 5150),
        new(ReportTrigger.LeavingAltitude, 7000, PilotProactive.ReportAltitudeAnswer.AlreadyThrough, 5150),
    ];

    /// <summary>Ticks until <paramref name="done"/> holds; the number of ticks it took.</summary>
    private static int TicksUntil(Rig rig, Func<bool> done, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            TickOnce(rig);
            if (done())
            {
                return t;
            }
        }

        Assert.Fail($"condition not met within {maxSeconds} s (at {rig.Aircraft.Altitude:F0} ft)");
        return -1;
    }

    /// <summary>Ticks until the reaction delay has fired; the aircraft's altitude on that tick.</summary>
    private static double TickUntilDelayFires(Rig rig, int maxSeconds)
    {
        TicksUntil(rig, () => rig.Aircraft.DeferredDispatches.Count == 0, maxSeconds);
        return rig.Aircraft.Altitude;
    }

    /// <summary>Ticks until the queued block carrying the REPORT has applied; the aircraft's altitude on that tick.</summary>
    private static double TickUntilReportBlockApplies(Rig rig, int maxSeconds)
    {
        TicksUntil(
            rig,
            () => !rig.Aircraft.Queue.Blocks.Exists(b => !b.IsApplied && (b.ParsedCommands?.Exists(c => c is ReportCommand) == true)),
            maxSeconds
        );
        return rig.Aircraft.Altitude;
    }

    /// <summary>Whether the pilot has said <paramref name="expected"/>, on the air or still queued for the frequency.</summary>
    private static bool HasSaidOrQueued(Rig rig, PilotSpeechText expected) =>
        rig.Lines.Exists(l => l.Text == expected.Terminal) || rig.Aircraft.PendingPilotTransmissions.Exists(t => t.Text == expected.Terminal);

    /// <summary>Whether <paramref name="line"/> is the pilot's call on reaching 5,000: passing it, or level on it.</summary>
    private static bool IsReachingFiveThousandCall(AircraftLastPilotTransmission line) =>
        line.Text.StartsWith("passing 5,000", StringComparison.Ordinal) || line.Text.StartsWith("level 5,000", StringComparison.Ordinal);

    /// <summary>Asserts that a judge now, against the altitude the aircraft is assigned, would answer <paramref name="expected"/>.</summary>
    private static void AssertJudgedNow(Rig rig, ReportTrigger trigger, int reportFt, PilotProactive.ReportAltitudeAnswer expected) =>
        Assert.Equal(
            expected,
            PilotProactive.AnswerReportAltitude(
                trigger,
                reportFt,
                rig.Aircraft.Altitude,
                rig.Aircraft.VerticalSpeed,
                PilotProactive.AssignedAltitudeFt(rig.Aircraft)
            )
        );

    private static void AssertSaid(Rig rig, string fragment) => Assert.Contains(rig.Lines, l => l.Text.Contains(fragment, StringComparison.Ordinal));

    private static void AssertNotSaid(Rig rig, string fragment) =>
        Assert.DoesNotContain(rig.Lines, l => l.Text.Contains(fragment, StringComparison.Ordinal));

    /// <summary>The one line that carries <paramref name="fragment"/>.</summary>
    private static string LineWith(Rig rig, string fragment) =>
        Assert.Single(rig.Lines, l => l.Text.Contains(fragment, StringComparison.Ordinal)).Text;

    private static void AssertNoAltitudeCall(Rig rig) =>
        Assert.DoesNotContain(
            rig.Lines,
            l =>
                l.Text.StartsWith("passing ", StringComparison.Ordinal)
                || l.Text.StartsWith("level ", StringComparison.Ordinal)
                || l.Text.StartsWith("leaving ", StringComparison.Ordinal)
        );

    private sealed record Rig(SimulationEngine Engine, AircraftState Aircraft, List<AircraftLastPilotTransmission> Lines);
}
