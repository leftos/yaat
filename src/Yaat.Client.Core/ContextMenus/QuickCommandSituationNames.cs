using Yaat.Sim.Situation;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The name the Quick Commands editor shows for each classified <see cref="AircraftSituation"/>, from the default
/// situation table. <see cref="AircraftSituation.Unknown"/> has no quick-command list and so no name.
/// </summary>
public static class QuickCommandSituationNames
{
    private static readonly Dictionary<AircraftSituation, string> Names = new()
    {
        [AircraftSituation.AtParking] = "At parking",
        [AircraftSituation.PushingBack] = "Pushing back",
        [AircraftSituation.HoldingOnGround] = "Holding on ground",
        [AircraftSituation.Taxiing] = "Taxiing",
        [AircraftSituation.HoldingShort] = "Holding short",
        [AircraftSituation.LinedUp] = "Lined up",
        [AircraftSituation.Departing] = "Departing",
        [AircraftSituation.IfrEnroute] = "IFR enroute",
        [AircraftSituation.IfrArrival] = "IFR arrival",
        [AircraftSituation.VfrFlightFollowing] = "VFR flight following",
        [AircraftSituation.Approach] = "Approach",
        [AircraftSituation.Holding] = "Holding",
        [AircraftSituation.Pattern] = "Pattern",
        [AircraftSituation.Final] = "Final",
        [AircraftSituation.RolloutExit] = "Rollout / exit",
        [AircraftSituation.GoAround] = "Go-around",
        [AircraftSituation.LiveTraffic] = "Live traffic",
        [AircraftSituation.VfrArrivalInbound] = "VFR arrival, inbound",
        [AircraftSituation.VfrDeparting] = "VFR departure",
    };

    /// <summary>Every situation that has a name and a quick-command list, in the enum's order.</summary>
    public static IReadOnlyList<AircraftSituation> Classified { get; } =
    [.. Enum.GetValues<AircraftSituation>().Where(situation => situation != AircraftSituation.Unknown)];

    /// <summary>The name <paramref name="situation"/> shows, or null for <see cref="AircraftSituation.Unknown"/>.</summary>
    public static string? NameOf(AircraftSituation situation) => Names.GetValueOrDefault(situation);
}
