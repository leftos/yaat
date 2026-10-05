using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>What kind of stop a spawn's timed TAXI preset ends at.</summary>
public enum PresetTaxiStopKind
{
    /// <summary>A ramp spot: the TAXI's <c>$spot</c> destination, or a trailing <c>$spot</c> hold short.</summary>
    Spot = 0,

    /// <summary>A taxiway hold short (<c>HS C</c>, or <c>HS C@T41W</c> naming the taxiway it holds on).</summary>
    TaxiwayHoldShort = 1,

    /// <summary>The end of a TAXI with no destination: the last taxiway of its path.</summary>
    RouteEnd = 2,
}

/// <summary>
/// The stop a spawn's timed TAXI preset ends at, recorded at scenario load (<see cref="Scenarios.InitialCallupClassifier"/>)
/// for an <see cref="InitialCallupPlan.AfterTaxiArrival"/> aircraft: its call fires only when the aircraft comes to rest
/// there, so a controller's TAXI elsewhere never triggers it.
/// </summary>
/// <param name="Kind">The kind of stop.</param>
/// <param name="Name">The spot's name (no <c>$</c>), the hold-short taxiway, or the route's last taxiway.</param>
/// <param name="OnTaxiway">For a hold short whose command names the taxiway it holds on (<c>HS C@T41W</c>), that taxiway; else null.</param>
public sealed record PresetTaxiStop(PresetTaxiStopKind Kind, string Name, string? OnTaxiway)
{
    public PresetTaxiStopDto ToSnapshot() =>
        new()
        {
            Kind = Kind,
            Name = Name,
            OnTaxiway = OnTaxiway,
        };

    public static PresetTaxiStop FromSnapshot(PresetTaxiStopDto dto) => new(dto.Kind, dto.Name, dto.OnTaxiway);
}
