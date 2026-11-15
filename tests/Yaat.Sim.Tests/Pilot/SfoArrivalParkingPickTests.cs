using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// SFO's sidecar <c>gaOperators</c> moves a listed business-jet operator out of the airline pool, so the stand XOJ712
/// parks on is one the layout offers general aviation rather than an airline gate (YAAT-609). Real SFO layout, navdata
/// and shipped ZOA sidecar.
/// </summary>
public class SfoArrivalParkingPickTests(ITestOutputHelper output)
{
    [Fact]
    public void Pick_ListedGaOperator_LeavesTheGatePool()
    {
        TestVnasData.EnsureInitialized();
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        AirportSidecarCatalog sidecars = NavigationDatabase.Instance.AirportSidecars;
        Assert.Contains("XOJ", sidecars.GetGaOperators("KSFO"));

        var aircraft = new AircraftState { Callsign = "XOJ712", AircraftType = "CL30" };
        string? pick = ArrivalParkingPicker.Pick(aircraft, layout, [], 0, sidecars);
        output.WriteLine($"{aircraft.Callsign} with SFO's gaOperators: {pick}");

        Assert.NotNull(pick);
        Assert.False(ArrivalParkingPicker.IsGateName(pick), pick);
        Assert.Contains(pick, NonGateStands(layout));
    }

    [Fact]
    public void Pick_WithoutTheGaOperatorList_TakesAGate()
    {
        TestVnasData.EnsureInitialized();
        if (SfoLayout() is not { } layout)
        {
            return;
        }

        var aircraft = new AircraftState { Callsign = "XOJ712", AircraftType = "CL30" };
        string? pick = ArrivalParkingPicker.Pick(aircraft, layout, [], 0, AirportSidecarCatalog.Empty);
        output.WriteLine($"{aircraft.Callsign} with an empty gaOperators: {pick}");

        Assert.NotNull(pick);
        Assert.True(ArrivalParkingPicker.IsGateName(pick), pick);
    }

    /// <summary>The shipped SFO ground layout, or null when the test data carries none.</summary>
    private static AirportGroundLayout? SfoLayout() => new TestAirportGroundData().GetLayout("SFO");

    /// <summary>The layout's parking names a general-aviation arrival is pooled over: every name that is not a gate.</summary>
    private static IReadOnlyList<string> NonGateStands(AirportGroundLayout layout) =>
        [
            .. layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is { Length: > 0 }))
                .Select(n => n.Name!.Trim())
                .Where(n => !ArrivalParkingPicker.IsGateName(n))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
}
