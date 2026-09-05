using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Phases;

/// <summary>Phase families that place an airborne aircraft inside the tower's (local control's) jurisdiction.</summary>
public static class TowerCabPhases
{
    /// <summary>
    /// The arrival and pattern side of local control: on final, landing, in the traffic pattern (including the exit
    /// leg of a pattern departure), going around, or flying a tower-issued maneuver on final. An aircraft here never
    /// initiates contact with approach or center — the transferring controller hands it over before it enters the next
    /// jurisdiction (7110.65 §2-1-17.a), and the tower keeps it through the go-around and the pattern. Departures
    /// (takeoff roll, initial climb, departure procedure) are deliberately outside this family: they do check in with
    /// departure control once the tower sends them (AIM 5-2-8).
    /// </summary>
    public static bool IsArrivalSide(Phase? phase) =>
        phase
            is FinalApproachPhase
                or LandingPhase
                or HelicopterLandingPhase
                or HelicopterApproachPhase
                or GoAroundPhase
                or LowApproachPhase
                or TouchAndGoPhase
                or StopAndGoPhase
                or STurnPhase
                or MakeTurnPhase
                or PatternEntryPhase
                or UpwindPhase
                or CrosswindPhase
                or DownwindPhase
                or BasePhase
                or MidfieldCrossingPhase
                or TeardropReentryPhase
                or VfrFollowPhase
                or PatternExitPhase;
}
