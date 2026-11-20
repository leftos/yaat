using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// YAAT-2299: a plain, non-forced CAPP for the approach an aircraft is already cleared for keeps its phases and continues
/// from where it is (AIM 5-4-3.b.1(d), 5-4-6.f, 5-4-9.a), instead of tearing the approach down and rebuilding it from the
/// filed transition (FRAME and its hold-in-lieu at KFAT). <c>JFAC, CAPP</c> on final or intercepting is the CAPP alone;
/// while flying the approach's fixes it is the join, upgraded in place. <c>CAPPF</c>, the AT/DCT/altitude forms, a
/// different or newer expected approach, a visual and a missed approach still take the full handler. Restored from the
/// owner's KFAT PTAC and CAPP bundle.
/// </summary>
public sealed class Yaat2299ReclearedWhileEstablishedTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/yaat2298-fat-ptac-capp-recording.yaat-bug-report-bundle.zip";
    private const string CancelledBy = "cancelled by";
    private const string ActiveApproachId = "R11L";

    [Theory]
    [InlineData("N111TF", 2920, 2927)]
    [InlineData("N172YY", 725, 731)]
    public void SameApproachCapp_OnAnEstablishedFinal_ContinuesTheFinal(string callsign, int restoreAt, int stepTo)
    {
        if (Restore(restoreAt, callsign) is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        AircraftState aircraft = session.Aircraft();
        Phase? finalBefore = aircraft.Phases?.CurrentPhase;
        Assert.IsType<FinalApproachPhase>(finalBefore);
        Assert.False(aircraft.Phases!.ActiveApproach!.LateralInterceptOnly);

        session.StepTo(stepTo);
        aircraft = session.Aircraft();

        Assert.Same(finalBefore, aircraft.Phases?.CurrentPhase);
        Assert.Equal(ActiveApproachId, aircraft.Phases!.ActiveApproach?.ApproachId);
        Assert.Empty(aircraft.Targets.NavigationRoute);
        session.AssertNoCancelledWarning();
    }

    [Fact]
    public void RecordedJfacCapp_AfterTheFixedCapp_DoesNotRestartAtFrame()
    {
        if (Restore(2920, "N111TF") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        for (int t = (int)session.StartedSeconds + 1; t <= 2940; t++)
        {
            session.StepTo(t);
            AircraftState aircraft = session.Aircraft();
            PhaseList phases = aircraft.Phases ?? throw new InvalidOperationException($"no phases at t={t}");
            Assert.DoesNotContain(phases.Phases, p => p is HoldingPatternPhase);
            Assert.DoesNotContain(aircraft.Targets.NavigationRoute, n => n.Name.Equals("FRAME", StringComparison.OrdinalIgnoreCase));
            Assert.False(phases.ActiveApproach?.LateralInterceptOnly, $"the clearance is lateral-only at t={t}");
        }

        session.AssertNoCancelledWarning();
    }

    [Fact]
    public void JfacThenCapp_OnAnEstablishedFinal_KeepsDescending()
    {
        if (Restore(725, "N172YY") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        Phase? finalBefore = session.Aircraft().Phases?.CurrentPhase;
        Assert.IsType<FinalApproachPhase>(finalBefore);
        session.SendInPlace("JFAC, CAPP");
        Assert.Same(finalBefore, session.Aircraft().Phases?.CurrentPhase);

        double previous = session.Aircraft().Altitude;
        for (int second = 1; second <= 20; second++)
        {
            session.StepTo((int)session.StartedSeconds + second);
            AircraftState aircraft = session.Aircraft();
            Assert.True(aircraft.Altitude <= previous + 0.5, $"climbed from {previous:F0} to {aircraft.Altitude:F0} ft at +{second} s");
            Assert.False(aircraft.Phases?.ActiveApproach?.LateralInterceptOnly ?? false, $"the clearance is lateral-only at +{second} s");
            previous = aircraft.Altitude;
        }

        session.AssertNoCancelledWarning();
    }

    [Fact]
    public void JfacThenCapp_WhileFlyingTheApproachFixes_JoinsTheFinalCourse()
    {
        if (Restore(2929, "N111TF") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        Assert.IsType<ApproachNavigationPhase>(session.Aircraft().Phases?.CurrentPhase);

        session.StepTo(2940);
        AircraftState aircraft = session.Aircraft();

        PhaseList phases = aircraft.Phases ?? throw new InvalidOperationException("no phases after JFAC, CAPP");
        Assert.DoesNotContain(phases.Phases, p => p is HoldingPatternPhase or ApproachNavigationPhase);
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, n => n.Name.Equals("FRAME", StringComparison.OrdinalIgnoreCase));
        ApproachClearance clearance = phases.ActiveApproach ?? throw new InvalidOperationException("no active approach");
        Assert.False(clearance.LateralInterceptOnly);
        Assert.Equal(ActiveApproachId, clearance.ApproachId);
    }

    [Fact]
    public void SameApproachCapp_WhileFlyingTheApproachFixes_Continues()
    {
        if (Restore(2929, "N111TF") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        AircraftState aircraft = session.Aircraft();
        Phase? navBefore = aircraft.Phases?.CurrentPhase;
        Assert.IsType<ApproachNavigationPhase>(navBefore);
        int phaseCount = aircraft.Phases!.Phases.Count;

        session.SendInPlace("CAPP");

        aircraft = session.Aircraft();
        Assert.Same(navBefore, aircraft.Phases?.CurrentPhase);
        Assert.Equal(phaseCount, aircraft.Phases!.Phases.Count);
        session.AssertNoCancelledWarning();
    }

    [Fact]
    public void SameApproachCapp_WhileIntercepting_KeepsTheIntercept()
    {
        if (Restore(2055, "N200WM") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        AircraftState aircraft = session.Aircraft();
        Phase? interceptBefore = aircraft.Phases?.CurrentPhase;
        Assert.IsType<InterceptCoursePhase>(interceptBefore);
        int routeBefore = aircraft.Targets.NavigationRoute.Count;

        session.SendInPlace("CAPP");
        session.StepTo((int)session.StartedSeconds + 1);
        aircraft = session.Aircraft();

        Assert.Same(interceptBefore, aircraft.Phases?.CurrentPhase);
        Assert.DoesNotContain(aircraft.Phases!.Phases, p => p is ApproachNavigationPhase or HoldingPatternPhase);
        Assert.True(aircraft.Targets.NavigationRoute.Count <= routeBefore, "the re-cleared intercept gained route fixes");
        session.AssertNoCancelledWarning();
    }

    [Fact]
    public void SameApproachCapp_InTheHoldInLieu_KeepsTheHold()
    {
        // The recorded 2924 rebuild routed N111TF through FRAME to the MORLA hold-in-lieu; no snapshot has it in the hold,
        // so it is flown there unattended (no recorded actions, which would join it to the final course at 2930).
        if (Restore(2929, "N111TF") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        Assert.IsType<ApproachNavigationPhase>(session.Aircraft().Phases?.CurrentPhase);
        session.TickUntil(a => a.Phases?.CurrentPhase is HoldingPatternPhase, 2400);
        AircraftState aircraft = session.Aircraft();
        Phase? holdBefore = aircraft.Phases?.CurrentPhase;
        Assert.True(holdBefore is HoldingPatternPhase { IsHoldInLieu: true }, $"not in the hold-in-lieu: {holdBefore?.Name}");
        int phaseCount = aircraft.Phases!.Phases.Count;

        session.SendInPlace("CAPP");

        aircraft = session.Aircraft();
        Assert.Same(holdBefore, aircraft.Phases?.CurrentPhase);
        Assert.Equal(phaseCount, aircraft.Phases!.Phases.Count);
        session.AssertNoCancelledWarning();
    }

    [Fact]
    public void SameApproachCapp_SupersedesAPendingWait()
    {
        if (Restore(725, "N172YY") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        Assert.True(session.Engine.SendCommand("N172YY", "WAIT 20 FH 200").Success);
        session.SendInPlace("CAPP");

        for (int second = 1; second <= 25; second++)
        {
            session.Tick();
            AircraftState aircraft = session.Aircraft();
            Assert.NotEqual(200, (int?)Math.Round(aircraft.Targets.AssignedMagneticHeading?.Degrees ?? 0));
            Assert.True(
                aircraft.TrueHeading.AbsAngleTo(aircraft.Phases!.ActiveApproach!.FinalApproachCourse) < 30,
                $"turned off final at +{second} s"
            );
        }
    }

    [Fact]
    public void ForcedCapp_OnAnEstablishedFinal_StillRebuilds() => AssertRebuilds("N172YY", 725, "CAPPF");

    [Fact]
    public void DifferentApproachCapp_OnAnEstablishedFinal_Rebuilds()
    {
        if (AssertRebuilds("N172YY", 725, "CAPP L11LY") is { } aircraft)
        {
            Assert.NotEqual(ActiveApproachId, aircraft.Phases?.ActiveApproach?.ApproachId);
        }
    }

    [Fact]
    public void DctCapp_WhileFlyingTheApproachFixes_Rebuilds() => AssertRebuilds("N111TF", 2929, $"CAPP DCT SLACK {ActiveApproachId}");

    [Fact]
    public void BareCapp_WithANewerExpectedApproach_TakesTheExpectedOne()
    {
        if (Restore(725, "N172YY") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        Phase? finalBefore = session.Aircraft().Phases?.CurrentPhase;
        Assert.IsType<FinalApproachPhase>(finalBefore);
        CommandResult expect = session.Engine.SendCommand("N172YY", "EAPP L11LY");
        Assert.True(expect.Success, expect.Message);

        CommandResult result = session.Engine.SendCommand("N172YY", "CAPP");
        output.WriteLine($"CAPP -> success={result.Success} message={result.Message}");
        session.Describe("after CAPP");

        Assert.NotSame(finalBefore, session.Aircraft().Phases?.CurrentPhase);
        Assert.NotEqual(ActiveApproachId, session.Aircraft().Phases?.ActiveApproach?.ApproachId);
    }

    [Fact]
    public void BareCapp_WithAVisualExpected_IsNotClearedInPlace()
    {
        if (Restore(725, "N172YY") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        Phase? finalBefore = session.Aircraft().Phases?.CurrentPhase;
        Assert.IsType<FinalApproachPhase>(finalBefore);
        CommandResult expect = session.Engine.SendCommand("N172YY", "EAPP VIS11L");
        Assert.True(expect.Success, expect.Message);

        CommandResult result = session.Engine.SendCommand("N172YY", "CAPP");
        output.WriteLine($"CAPP -> success={result.Success} message={result.Message}");
        session.Describe("after CAPP");

        bool clearedInPlace =
            result.Success
            && ReferenceEquals(finalBefore, session.Aircraft().Phases?.CurrentPhase)
            && (session.Aircraft().Phases?.ActiveApproach?.ApproachId == ActiveApproachId);
        Assert.False(clearedInPlace, "a bare CAPP with a visual expected re-cleared the instrument approach in place");
    }

    [Fact]
    public void SameApproachCapp_OnAMissedApproach_Rebuilds()
    {
        if (Restore(725, "N172YY") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        Phase missed = FlyTheMissedApproach(session, "N172YY");
        CommandResult result = session.Engine.SendCommand("N172YY", "CAPP");
        output.WriteLine($"CAPP -> success={result.Success} message={result.Message}");
        Assert.NotSame(missed, session.Aircraft().Phases?.CurrentPhase);
    }

    [Fact]
    public void JfacThenCapp_WhileFlyingTheApproachFixes_KeepsTheMissedApproach()
    {
        if (Restore(2929, "N111TF") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        Assert.IsType<ApproachNavigationPhase>(session.Aircraft().Phases?.CurrentPhase);
        PutBackMissedApproachData(session);
        session.SendInPlace("JFAC, CAPP");
        Assert.NotEmpty(session.Aircraft().Phases!.ActiveApproach!.MissedApproachFixes);

        // A go-around is accepted inside the final approach fix (SLACK, about 5 NM out).
        session.TickUntil(a => a.Phases?.CurrentPhase is FinalApproachPhase { DistanceToThresholdNm: < 3.0 }, 900);
        Assert.IsType<FinalApproachPhase>(session.Aircraft().Phases?.CurrentPhase);
        FlyTheMissedApproach(session, "N111TF");
    }

    [Fact]
    public void JfacThenCapp_WhileFlyingTheApproachFixes_ReleasesTheTurnRateOverride()
    {
        if (Restore(2929, "N111TF") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        CommandResult rate = session.Engine.SendCommand("N111TF", "TRATE 5");
        Assert.True(rate.Success, rate.Message);
        Assert.NotNull(session.Aircraft().Targets.TurnRateOverride);

        session.SendInPlace("JFAC, CAPP");

        Assert.Null(session.Aircraft().Targets.TurnRateOverride);
    }

    [Fact]
    public void JfacThenCapp_OnALateralJoin_WithANewerExpectedApproach_ClearsTheExpectedOne()
    {
        if (RestoreOnALateralJoin("EAPP L11LY") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        CommandResult result = session.Engine.SendCommand("N172YY", "JFAC, CAPP");
        output.WriteLine($"JFAC, CAPP -> success={result.Success} message={result.Message}");
        session.Describe("after JFAC, CAPP");

        Assert.True(result.Success, result.Message);
        Assert.Equal("L11LY", session.Aircraft().Phases?.ActiveApproach?.ApproachId);
    }

    [Fact]
    public void JfacThenCapp_OnALateralJoin_WithAVisualExpected_IsRefused()
    {
        if (RestoreOnALateralJoin("EAPP VIS11L") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        CommandResult result = session.Engine.SendCommand("N172YY", "JFAC, CAPP");
        output.WriteLine($"JFAC, CAPP -> success={result.Success} message={result.Message}");
        session.Describe("after JFAC, CAPP");

        Assert.False(result.Success, "a bare JFAC with a visual expected was accepted");
        Assert.True(session.Aircraft().Phases?.ActiveApproach?.LateralInterceptOnly, "the lateral join was cleared for the instrument approach");
    }

    [Fact]
    public void SameApproachCapp_OnAPatternFinal_IsNotClearedInPlace()
    {
        if (Restore(725, "N172YY") is not { } session)
        {
            return;
        }

        using RecordingArchive archive = session.Archive;
        // Flown as VFR, so its go-around re-enters the pattern (an IFR one flies the missed approach).
        session.Aircraft().FlightPlan.FlightRules = "VFR";
        CommandResult goAround = session.Engine.SendCommand("N172YY", "GA");
        Assert.True(goAround.Success, goAround.Message);

        // A VFR go-around re-enters the pattern; flown unattended to the circuit's final.
        session.TickUntil(a => (a.Phases?.TrafficDirection is not null) && (a.Phases.CurrentPhase is FinalApproachPhase), 900);
        PhaseList phases = session.Aircraft().Phases ?? throw new InvalidOperationException("no phases on the pattern");
        Phase? patternFinal = phases.CurrentPhase;
        Assert.IsType<FinalApproachPhase>(patternFinal);
        Assert.NotNull(phases.TrafficDirection);
        Assert.NotNull(phases.ActiveApproach);

        CommandResult result = session.Engine.SendCommand("N172YY", "CAPP");
        output.WriteLine($"CAPP -> success={result.Success} message={result.Message}");
        session.Describe("after CAPP");

        Assert.NotSame(patternFinal, session.Aircraft().Phases?.CurrentPhase);
    }

    /// <summary>N172YY at 725 joined laterally to R11L (<c>JFAC R11L</c>), then told to expect another approach.</summary>
    private Session? RestoreOnALateralJoin(string expect)
    {
        if (Restore(725, "N172YY") is not { } session)
        {
            return null;
        }

        CommandResult join = session.Engine.SendCommand("N172YY", $"JFAC {ActiveApproachId}");
        Assert.True(join.Success, join.Message);
        Assert.True(session.Aircraft().Phases?.ActiveApproach?.LateralInterceptOnly, "JFAC did not leave a lateral-only join");
        CommandResult expected = session.Engine.SendCommand("N172YY", expect);
        Assert.True(expected.Success, expected.Message);
        session.Describe($"after JFAC {ActiveApproachId}; {expect}");
        return session;
    }

    /// <summary>
    /// Puts back what a go-around needs to fly the published missed approach. The restored clearance carries neither its
    /// procedure (<see cref="ApproachClearance.FromSnapshot"/> does not restore it) nor, as recorded, its missed-approach
    /// fixes; the flight is made IFR, since a VFR go-around re-enters the pattern.
    /// </summary>
    private static void PutBackMissedApproachData(Session session)
    {
        AircraftState aircraft = session.Aircraft();
        aircraft.FlightPlan.FlightRules = "IFR";
        PhaseList phases = aircraft.Phases!;
        ApproachClearance restored = phases.ActiveApproach!;
        CifpApproachProcedure procedure =
            NavigationDatabase.Instance.GetApproach(restored.AirportCode, restored.ApproachId)
            ?? throw new InvalidOperationException($"{restored.ApproachId} is not in the CIFP");
        List<ApproachFix> missedFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure);
        Assert.NotEmpty(missedFixes);
        phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = restored.ApproachId,
            AirportCode = restored.AirportCode,
            RunwayId = restored.RunwayId,
            FinalApproachCourse = restored.FinalApproachCourse,
            TransitionName = restored.TransitionName,
            FinalApproachAnchorLat = restored.FinalApproachAnchorLat,
            FinalApproachAnchorLon = restored.FinalApproachAnchorLon,
            MapAltitudeFt = restored.MapAltitudeFt,
            MapDistanceNm = restored.MapDistanceNm,
            LateralInterceptOnly = restored.LateralInterceptOnly,
            MissedApproachFixes = missedFixes,
            Procedure = procedure,
        };
    }

    /// <summary>
    /// A go-around flown unattended (recorded actions would intervene) until the published missed approach is the current
    /// phase, which is returned.
    /// </summary>
    private static ApproachNavigationPhase FlyTheMissedApproach(Session session, string callsign)
    {
        if (session.Aircraft().Phases?.ActiveApproach?.MissedApproachFixes is not { Count: > 0 })
        {
            PutBackMissedApproachData(session);
        }

        CommandResult goAround = session.Engine.SendCommand(callsign, "GA");
        Assert.True(goAround.Success, goAround.Message);
        session.TickUntil(a => a.Phases?.CurrentPhase is ApproachNavigationPhase { IsMissedApproach: true }, 300);
        Phase? current = session.Aircraft().Phases?.CurrentPhase;
        if (current is not ApproachNavigationPhase { IsMissedApproach: true } missed)
        {
            throw new Xunit.Sdk.XunitException($"not on the missed approach: {current?.Name}");
        }

        Assert.NotNull(session.Aircraft().Phases!.ActiveApproach);
        return missed;
    }

    private AircraftState? AssertRebuilds(string callsign, int restoreAt, string command)
    {
        if (Restore(restoreAt, callsign) is not { } session)
        {
            return null;
        }

        using RecordingArchive archive = session.Archive;
        Phase? before = session.Aircraft().Phases?.CurrentPhase;
        Assert.NotNull(before);

        CommandResult result = session.Engine.SendCommand(callsign, command);
        output.WriteLine($"{command} -> success={result.Success} message={result.Message}");
        Assert.True(result.Success, result.Message);
        AircraftState aircraft = session.Aircraft();
        session.Describe($"after {command}");

        Assert.NotNull(aircraft.Phases?.CurrentPhase);
        Assert.NotSame(before, aircraft.Phases!.CurrentPhase);
        return aircraft;
    }

    /// <summary>The latest snapshot at or before <paramref name="seconds"/> with the replay driver armed behind it.</summary>
    private Session? Restore(double seconds, string callsign)
    {
        TestVnasData.EnsureInitialized();
        if (RecordingLoader.OpenArchive(RecordingPath) is not { } archive)
        {
            output.WriteLine($"Skipped: {RecordingPath} is not present");
            return null;
        }

        if (archive.ReadSnapshotAt(seconds) is not { } snapshot)
        {
            archive.Dispose();
            output.WriteLine($"Skipped: no snapshot at or before t={seconds}");
            return null;
        }

        var groundData = RecordingGroundData.VersionPinned(
            RecordingPath,
            "the recorded KFAT state is replayed on the airport as the archive bundles it"
        );
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        SessionRecording recording = archive.ToBaseSessionRecording();
        engine.Replay(recording, 0);
        engine.RestoreFromSnapshot(snapshot.State);
        engine.ArmReplay(recording.Actions);

        var session = new Session(engine, archive, snapshot.ElapsedSeconds, callsign, output);
        session.Describe("restored");
        return session;
    }

    private sealed class Session
    {
        private readonly string _callsign;
        private readonly ITestOutputHelper _output;
        private readonly List<string> _warnings = [];
        private double _now;

        public Session(SimulationEngine engine, RecordingArchive archive, double startedSeconds, string callsign, ITestOutputHelper output)
        {
            Engine = engine;
            Archive = archive;
            StartedSeconds = startedSeconds;
            _now = startedSeconds;
            _callsign = callsign;
            _output = output;
            engine.WarningEmitted += (cs, warning) =>
            {
                if (cs == callsign)
                {
                    output.WriteLine($"warning t={_now:F0}: {warning}");
                    _warnings.Add(warning);
                }
            };
        }

        public SimulationEngine Engine { get; }

        public RecordingArchive Archive { get; }

        public double StartedSeconds { get; }

        public AircraftState Aircraft() => Engine.FindAircraft(_callsign) ?? throw new InvalidOperationException($"{_callsign} is not in the world");

        public void StepTo(int seconds)
        {
            while (_now < seconds)
            {
                Engine.ReplayOneSecond();
                _now++;
            }
        }

        /// <summary>One bare second: physics and phases, no recorded actions.</summary>
        public void Tick()
        {
            Engine.TickOneSecond();
            _now++;
        }

        /// <summary>Ticks bare seconds until <paramref name="until"/> holds or <paramref name="maxSeconds"/> pass.</summary>
        public void TickUntil(Func<AircraftState, bool> until, int maxSeconds)
        {
            for (int second = 0; (second < maxSeconds) && !until(Aircraft()); second++)
            {
                Engine.TickOneSecond();
                _now++;
            }

            Describe("ticked");
        }

        /// <summary>Sends <paramref name="command"/> and asserts it was cleared in place: accepted, read back as "Cleared …".</summary>
        public void SendInPlace(string command)
        {
            CommandResult result = Engine.SendCommand(_callsign, command);
            _output.WriteLine($"{command} -> success={result.Success} message={result.Message}");
            Assert.True(result.Success, result.Message);
            Assert.StartsWith("Cleared", result.Message ?? "", StringComparison.Ordinal);
            Describe($"after {command}");
        }

        public void AssertNoCancelledWarning() => Assert.DoesNotContain(_warnings, w => w.Contains(CancelledBy, StringComparison.Ordinal));

        public void Describe(string label)
        {
            AircraftState aircraft = Aircraft();
            string phases = aircraft.Phases is { } pl
                ? string.Join(">", pl.Phases.Select(p => ReferenceEquals(p, pl.CurrentPhase) ? $"[{p.Name}]" : p.Name))
                : "none";
            ApproachClearance? clearance = aircraft.Phases?.ActiveApproach;
            _output.WriteLine(
                $"{label} t={_now:F0} {aircraft.Callsign}: phases={phases} apch={clearance?.ApproachId} "
                    + $"lateralOnly={clearance?.LateralInterceptOnly} "
                    + $"expected={aircraft.Approach.Expected} route=[{string.Join(",", aircraft.Targets.NavigationRoute.Select(n => n.Name))}] "
                    + $"alt={aircraft.Altitude:F0} hdg={aircraft.TrueHeading.Degrees:F1}"
            );
        }
    }
}
