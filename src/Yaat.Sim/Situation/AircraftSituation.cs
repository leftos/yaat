namespace Yaat.Sim.Situation;

/// <summary>
/// The controller-facing situation of an aircraft: what a controller is most likely to do with it next.
/// Computed by <see cref="SituationClassifier"/> and sent on the training hub as a number, so the values
/// are fixed: add new members at the end and never renumber.
/// </summary>
public enum AircraftSituation
{
    /// <summary>No situation could be determined; the client falls back to its full menu.</summary>
    Unknown = 0,
    AtParking = 1,
    PushingBack = 2,
    HoldingOnGround = 3,
    Taxiing = 4,
    HoldingShort = 5,
    LinedUp = 6,
    Departing = 7,
    IfrEnroute = 8,
    IfrArrival = 9,
    VfrFlightFollowing = 10,
    Approach = 11,
    Holding = 12,
    Pattern = 13,
    Final = 14,
    RolloutExit = 15,
    GoAround = 16,
    LiveTraffic = 17,
    VfrArrivalInbound = 18,
    VfrDeparting = 19,
}
