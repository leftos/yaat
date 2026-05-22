using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E: S2-OAK-4 bug bundle — IFR departures on NIMI6 / OAK6 RV SIDs from KOAK 28R with bare CTO
/// must hold the published radar-vectors heading after liftoff, not turn direct to the
/// first enroute fix (SAC, MOD, SYRAH, …).
/// </summary>
public class S2Oak4RvSidCtoTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak4-rv-sid-cto-recording.yaat-bug-report-bundle.zip";
    private const double NimiRvHeadingMag = 315.0;
    private const double Oak6RvHeadingMag28R = 278.2;
    private const double FieldElevation = 9.0;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("InitialClimbPhase", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void N436MS_CtoDuringTaxi_Nimi6_Stores315OnClearanceAndInitialClimb()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Route filed as NIMI6 at t=10 after early CTO with an empty route; pending InitialClimb
        // must pick up the 315° RV heading on amend.
        engine.Replay(recording, 10);
        var ac = engine.FindAircraft("N436MS");
        Assert.NotNull(ac);

        var pendingClimb = ac.Phases?.Phases.OfType<InitialClimbPhase>().FirstOrDefault(p => p.Status == PhaseStatus.Pending);
        Assert.NotNull(pendingClimb);
        Assert.Equal(NimiRvHeadingMag, pendingClimb.SidDepartureHeadingMagnetic);

        engine.Replay(recording, 75);
        ac = engine.FindAircraft("N436MS");
        Assert.NotNull(ac);
        var climb = ac.Phases?.Phases.OfType<InitialClimbPhase>().FirstOrDefault();
        Assert.NotNull(climb);
        Assert.Equal(NimiRvHeadingMag, climb.SidDepartureHeadingMagnetic);
        Assert.False(ac.HasLeftStudentFrequency);
        Assert.Empty(ac.Targets.NavigationRoute);

        output.WriteLine(
            $"t=75: hdg={ac.TrueHeading.Degrees:F1} tgtHdg={ac.Targets.TargetTrueHeading?.Degrees:F1} route={string.Join(",", ac.Targets.NavigationRoute.Select(t => t.Name))}"
        );
    }

    [Fact]
    public void N346G_CtoFromHoldShort_Nimi6_Stores315OnClearanceAndInitialClimb()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 130);
        var ac = engine.FindAircraft("N346G");
        Assert.NotNull(ac);

        var pendingClimb = ac.Phases?.Phases.OfType<InitialClimbPhase>().FirstOrDefault(p => p.Status == PhaseStatus.Pending);
        Assert.NotNull(pendingClimb);
        Assert.Equal(NimiRvHeadingMag, pendingClimb.SidDepartureHeadingMagnetic);

        engine.Replay(recording, 195);
        ac = engine.FindAircraft("N346G");
        Assert.NotNull(ac);
        var climb = ac.Phases?.Phases.OfType<InitialClimbPhase>().FirstOrDefault();
        Assert.NotNull(climb);
        Assert.Equal(NimiRvHeadingMag, climb.SidDepartureHeadingMagnetic);
        Assert.Empty(ac.Targets.NavigationRoute);

        output.WriteLine(
            $"t=195: hdg={ac.TrueHeading.Degrees:F1} tgtHdg={ac.Targets.TargetTrueHeading?.Degrees:F1} route={string.Join(",", ac.Targets.NavigationRoute.Select(t => t.Name))}"
        );
    }

    [Fact]
    public void OAK6_CtoDuringTaxi_Stores278HeadingFor28R()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        var ac = engine.FindAircraft("N436MS");
        Assert.NotNull(ac);

        ac.FlightPlan.Route = "OAK6 OAK SYRAH";
        ac.Phases!.DepartureClearance = null;

        var result = engine.SendCommand("N436MS", "CTO");
        Assert.True(result.Success, result.Message);

        Assert.NotNull(ac.Phases.DepartureClearance);
        Assert.Equal(Oak6RvHeadingMag28R, ac.Phases.DepartureClearance!.SidDepartureHeadingMagnetic);
    }
}
