using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// An action host with no room: the one remaining slot captured, every consumer counted or ignored. Attendance is
/// engine state (<see cref="AttendanceTestSupport.Attend"/>), not something the host answers.
/// </summary>
public sealed class AttendanceActionHost : IActionHost
{
    public int ConsolidationChanges { get; private set; }

    public int WeatherChanges { get; private set; }

    public List<string> SpawnedCallsigns { get; } = [];

    public List<string> DeletedCallsigns { get; } = [];

    public List<string> HiddenLiveTraffic { get; } = [];

    public List<string> OverlaysRemoved { get; } = [];

    public List<(string ConnectionId, string Callsign, List<string> Lines)> ShownQueues { get; } = [];

    public void OnConsolidationChanged() => ConsolidationChanges++;

    public List<RecordedAsdexSafetyLogicChange> AsdexSafetyLogicChanges { get; } = [];

    public void ApplyRecordedAsdexSafetyLogic(RecordedAsdexSafetyLogicChange change) => AsdexSafetyLogicChanges.Add(change);

    public void OnAircraftSpawned(AircraftState aircraft) => SpawnedCallsigns.Add(aircraft.Callsign);

    public void OnAircraftDeleted(string callsign, AircraftState? lastState) => DeletedCallsigns.Add(callsign);

    /// <summary>
    /// Runs inside <see cref="OnLiveTrafficHidden"/>, before the callsign is recorded: a test that cares *when* the
    /// consumer fires reads the world through this rather than after the applier has finished. Null unless a test sets it.
    /// </summary>
    public Action<string>? WhenLiveTrafficHidden { get; set; }

    public void OnLiveTrafficHidden(string callsign)
    {
        WhenLiveTrafficHidden?.Invoke(callsign);
        HiddenLiveTraffic.Add(callsign);
    }

    public List<(string ConnectionId, TrackOwner Owner, string TcpCode)> SelectedPositions { get; } = [];

    public void OnPositionSelected(string connectionId, TrackOwner owner, string tcpCode) => SelectedPositions.Add((connectionId, owner, tcpCode));

    public void OnGhostOverlayRemoved(string callsign) => OverlaysRemoved.Add(callsign);

    /// <summary>Every callsign an ASDE-X terminate handed over, in order — the live room's one-shot delete marker.</summary>
    public List<string> AsdexTerminations { get; } = [];

    public void OnAsdexTrackTerminated(string callsign) => AsdexTerminations.Add(callsign);

    /// <summary>The SAID twin of <see cref="AsdexTerminations"/>.</summary>
    public List<string> SaidTerminations { get; } = [];

    public void OnSaidTrackTerminated(string callsign) => SaidTerminations.Add(callsign);

    public void OnQueuedCommandsShown(string connectionId, string callsign, IReadOnlyList<string> lines) =>
        ShownQueues.Add((connectionId, callsign, lines.ToList()));

    /// <summary>Every TDLS change set the router or the spine drained into this host, in order.</summary>
    public List<TdlsChangeSet> TdlsChanges { get; } = [];

    public void OnTdlsChanged(TdlsChangeSet changes) => TdlsChanges.Add(changes);

    /// <summary>Every strip change set the router or the spine drained into this host, in order.</summary>
    public List<StripChangeSet> StripChanges { get; } = [];

    public void OnStripsChanged(StripChangeSet changes) => StripChanges.Add(changes);

    /// <summary>How many times a drain reported the coordination lists changed.</summary>
    public int CoordinationChanges { get; private set; }

    public void OnCoordinationChanged() => CoordinationChanges++;

    /// <summary>How many times a drain reported the shared timeline bookmarks changed.</summary>
    public int BookmarkChanges { get; private set; }

    public void OnBookmarksChanged() => BookmarkChanges++;

    /// <summary>How many times a drain reported the session clock changed.</summary>
    public int SimStateChanges { get; private set; }

    public void OnSimStateChanged() => SimStateChanges++;

    /// <summary>How many times a drain reported the ERAM CRR groups changed.</summary>
    public int EramCrrGroupChanges { get; private set; }

    public void OnEramCrrGroupsChanged() => EramCrrGroupChanges++;

    public void OnTimersChanged() { }

    public void OnHeldDeparturesChanged() { }

    public void OnWeatherChanged() => WeatherChanges++;
}
