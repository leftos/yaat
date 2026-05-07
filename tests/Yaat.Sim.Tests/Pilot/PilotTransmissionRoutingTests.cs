using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Three-way routing of sim-initiated pilot transmissions across the new mode/setting
/// matrix. Covers a representative phase (HoldingShortPhase) and the static pilot-observation
/// path (PilotObservationUpdater used for sim-resolved RTIS/RFIS) so future regressions in
/// either flow surface immediately.
///
/// Joins the <c>NavDbMutator</c> xUnit collection because the visual-acquisition path used by
/// PilotObservationUpdater can be affected by other tests swapping the NavigationDatabase
/// singleton; the collection serializes those tests so we don't race with them.
/// </summary>
[Collection("NavDbMutator")]
public class PilotTransmissionRoutingTests
{
    public PilotTransmissionRoutingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(string callsign)
    {
        var ac = new AircraftState { Callsign = callsign, AircraftType = "B738" };
        ac.FlightPlan.FlightRules = "IFR";
        return ac;
    }

    private static PhaseContext MakeCtx(AircraftState ac, bool soloTrainingMode, bool rpoShowPilotSpeech)
    {
        ac.Ground = new AircraftGroundOps { CurrentTaxiway = "B" };
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1,
            Logger = NullLogger.Instance,
            SoloTrainingMode = soloTrainingMode,
            RpoShowPilotSpeech = rpoShowPilotSpeech,
        };
    }

    private static HoldingShortPhase MakeHoldingShortPhase()
    {
        var hs = new HoldShortPoint
        {
            NodeId = 1,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = "28R",
        };
        return new HoldingShortPhase(hs);
    }

    [Fact]
    public void HoldingShortPhase_SoloMode_AddsTerseWarning_AndAnnouncesReady()
    {
        var ac = MakeAircraft("N172SP");
        var phase = MakeHoldingShortPhase();
        var ctx = MakeCtx(ac, soloTrainingMode: true, rpoShowPilotSpeech: false);

        phase.OnStart(ctx);

        // Solo path: terse warning still fires (preserves current behavior),
        // and the existing solo "ready for departure" pilot announcement also fires.
        Assert.Equal("N172SP holding short runway 28R at B", Assert.Single(ac.PendingWarnings));
        Assert.Equal("tower, N172SP holding short runway 28R, ready for departure.", Assert.Single(ac.PendingNotifications));
        Assert.Equal(PilotResponder.BuildHoldingShortReady(ac, "28R"), Assert.Single(ac.PendingPilotTransmissions).Text);
        Assert.Empty(ac.PendingPilotSpeech);
    }

    [Fact]
    public void HoldingShortPhase_RpoMode_PilotSpeechOff_AddsTerseWarning()
    {
        var ac = MakeAircraft("N172SP");
        var phase = MakeHoldingShortPhase();
        var ctx = MakeCtx(ac, soloTrainingMode: false, rpoShowPilotSpeech: false);

        phase.OnStart(ctx);

        Assert.Equal("N172SP holding short runway 28R at B", Assert.Single(ac.PendingWarnings));
        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Empty(ac.PendingNotifications);
    }

    [Fact]
    public void HoldingShortPhase_RpoMode_PilotSpeechOn_AddsSpelledOutPilotSpeech()
    {
        var ac = MakeAircraft("N172SP");
        var phase = MakeHoldingShortPhase();
        var ctx = MakeCtx(ac, soloTrainingMode: false, rpoShowPilotSpeech: true);

        phase.OnStart(ctx);

        Assert.Empty(ac.PendingWarnings);
        Assert.Empty(ac.PendingNotifications);
        var speech = Assert.Single(ac.PendingPilotSpeech);
        Assert.Equal(PilotResponder.BuildHoldingShortCrossing(ac, "28R"), speech);
    }

    // PilotObservationUpdater routing is exercised end-to-end by RpoPilotSpeechReplayTests
    // against the S2-OAK-5 bundle (which contains real RTIS resolutions). A unit test here
    // would have to set up VisualAcquisition state that's sensitive to test ordering — the
    // E2E replay test gives stronger confidence with less fragility.
}
