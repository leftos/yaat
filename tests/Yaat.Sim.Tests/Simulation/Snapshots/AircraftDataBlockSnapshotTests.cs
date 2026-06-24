using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// Snapshot round-trip for the Track Reposition datablock state (<see cref="AircraftDataBlock"/>).
/// A parked datablock must survive rewind/reconnect; a legacy snapshot with no DataBlock field must
/// default to <see cref="DataBlockBinding.Bound"/> (the pre-feature behavior).
/// </summary>
public class AircraftDataBlockSnapshotTests
{
    private static AircraftState MakeAircraft()
    {
        return new AircraftState
        {
            Callsign = "N100",
            AircraftType = "C172",
            Position = new LatLon(37.7295, -122.2261),
            TrueHeading = new TrueHeading(280),
            Altitude = 2500,
            Transponder = new AircraftTransponder { Code = 1200, Mode = "C" },
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
        };
    }

    [Fact]
    public void ParkedDataBlock_RoundTrips()
    {
        var ac = MakeAircraft();
        ac.DataBlock = new AircraftDataBlock
        {
            Binding = DataBlockBinding.Parked,
            Latitude = 37.7100,
            Longitude = -122.1900,
            DetachedId = "RPOSN100",
            CreatedBy = TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O"),
        };

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), null);

        Assert.Equal(DataBlockBinding.Parked, restored.DataBlock.Binding);
        Assert.Equal(37.7100, restored.DataBlock.Latitude!.Value, 6);
        Assert.Equal(-122.1900, restored.DataBlock.Longitude!.Value, 6);
        Assert.Equal("RPOSN100", restored.DataBlock.DetachedId);
        Assert.NotNull(restored.DataBlock.CreatedBy);
        Assert.Equal("OAK_TWR", restored.DataBlock.CreatedBy!.Callsign);
    }

    [Fact]
    public void BoundDataBlock_RoundTrips_WithNullFields()
    {
        var restored = AircraftState.FromSnapshot(MakeAircraft().ToSnapshot(), null);

        Assert.Equal(DataBlockBinding.Bound, restored.DataBlock.Binding);
        Assert.Null(restored.DataBlock.DetachedId);
        Assert.Null(restored.DataBlock.CreatedBy);
    }

    [Fact]
    public void LegacySnapshot_NoDataBlockField_DefaultsToBound()
    {
        var dto = MakeAircraft().ToSnapshot();
        var json = JsonSerializer.Serialize(dto);
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("DataBlock");
        var legacy = JsonSerializer.Deserialize<AircraftSnapshotDto>(node.ToJsonString())!;

        var restored = AircraftState.FromSnapshot(legacy, null);

        Assert.Equal(DataBlockBinding.Bound, restored.DataBlock.Binding);
    }
}
