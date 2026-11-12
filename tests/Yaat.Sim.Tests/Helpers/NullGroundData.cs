using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>An <see cref="IAirportGroundData"/> that has no layouts — the layout-less airport case.</summary>
internal sealed class NullGroundData : IAirportGroundData
{
    /// <summary>Every airport loaded, with no map.</summary>
    public GroundDataRead Read(string airportId) => new(true, null, null);
}
