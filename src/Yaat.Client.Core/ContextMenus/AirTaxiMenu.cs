namespace Yaat.Client.ContextMenus;

/// <summary>What an Air taxi or Land destination row names, which picks its marker and its tab.</summary>
public enum AirTaxiDestinationKind
{
    /// <summary>A helipad node, sent as <c>@NAME</c>; on the Pads · parking tab.</summary>
    Helipad,

    /// <summary>A parking position, sent as <c>@NAME</c>; on the Pads · parking tab.</summary>
    Parking,

    /// <summary>A taxiway spot, sent as <c>$NAME</c>; on the Spots tab.</summary>
    Spot,

    /// <summary>A runway end's holding position, sent as the bare designator; on the Runways tab (Air taxi only).</summary>
    Runway,
}

/// <summary>A runway end's other holding position, offered as a chip under the end's row.</summary>
/// <param name="Taxiway">The taxiway the bar is on, the chip's text: <c>E</c>.</param>
/// <param name="Token">The destination as command text: <c>28R@E</c>.</param>
public sealed record AirTaxiChip(string Taxiway, string Token);

/// <summary>One destination of the Air taxi / Land flyout.</summary>
/// <param name="Name">What the row names: <c>HELI2</c>, <c>I8R</c>, or <c>RWY 28R</c> for a runway end.</param>
/// <param name="Kind">The destination's kind.</param>
/// <param name="DistanceFt">The straight-line distance from the aircraft, rounded to 50 ft.</param>
/// <param name="Token">The destination as command text after the verb: <c>@HELI2</c>, <c>$I8R</c>, <c>28R</c>.</param>
/// <param name="BarTaxiway">
/// A runway row's taxiway: the one whose located form names the same bar the bare designator does; null when no taxiway
/// does, and for every other kind.
/// </param>
/// <param name="Chips">A runway row's other bars, nearest the threshold first; empty for every other kind.</param>
public sealed record AirTaxiDestination(
    string Name,
    AirTaxiDestinationKind Kind,
    double DistanceFt,
    string Token,
    string? BarTaxiway,
    IReadOnlyList<AirTaxiChip> Chips
)
{
    /// <summary>
    /// A runway row whose bar the aircraft already holds at: the row sends nothing and only its chips do. The host leaves
    /// such a row out when it has no chips.
    /// </summary>
    public bool IsHeldAt { get; init; }
}

/// <summary>
/// The host's answer for the Air taxi / Land flyout (<see cref="IMenuHost.GetAirTaxiChoices"/>): one airport's destinations,
/// each group nearest first.
/// </summary>
/// <param name="AirportId">The airport the destinations are at, empty for <see cref="Empty"/>.</param>
/// <param name="Pads">The helipads.</param>
/// <param name="Parking">The parking positions.</param>
/// <param name="Spots">The taxiway spots.</param>
/// <param name="Runways">One row per runway end, for its holding position nearest the threshold.</param>
public sealed record AirTaxiMenu(
    string AirportId,
    IReadOnlyList<AirTaxiDestination> Pads,
    IReadOnlyList<AirTaxiDestination> Parking,
    IReadOnlyList<AirTaxiDestination> Spots,
    IReadOnlyList<AirTaxiDestination> Runways
)
{
    /// <summary>No airport and no destination: the aircraft is unknown or no layout is loaded.</summary>
    public static AirTaxiMenu Empty { get; } = new("", [], [], [], []);
}
