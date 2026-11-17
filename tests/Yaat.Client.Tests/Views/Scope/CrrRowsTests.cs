using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The CRR view's rows (CRC <c>ViewCrr</c>, list mode): each group's label in its colour, in the groups' order, then its
/// members nearest first as <c>{callsign,-7}{distance,7}</c>.
/// </summary>
public class CrrRowsTests
{
    private static AircraftModel At(string callsign, double northNm, string? group)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Callsign = callsign;
        ac.Position = new LatLon(37.0 + (northNm / 60.0), -122.0);
        ac.EramCrrGroup = group;
        return ac;
    }

    [Fact]
    public void EachGroup_ListsItsLabelInItsColour_ThenItsMembersNearestFirst()
    {
        EramCrrGroupHubDto a = new("A", "Green", 37.0, -122.0);
        EramCrrGroupHubDto b = new("B", "Yellow", 37.0, -122.0);
        AircraftModel[] aircraft = [At("UAL1549", 30, "A"), At("SWA12", 5, "A"), At("N123", 2000, "B"), At("DAL1", 1, null)];

        IReadOnlyList<CrrRow> rows = CrrRows.Build([a, b], aircraft);

        Assert.Equal(["A", "SWA12      5.0", "UAL1549   30.0", "B", "N123    >999.9"], rows.Select(r => r.Text));
        Assert.Equal<SKColor?>([ScopeCrrReadout.Green, null, null, ScopeCrrReadout.Yellow, null], rows.Select(r => r.GroupColor));
    }

    [Fact]
    public void AGroupWithNoMembers_ListsOnlyItsLabel() =>
        Assert.Equal(["C"], CrrRows.Build([new EramCrrGroupHubDto("C", "Coral", 37.0, -122.0)], [At("UAL1", 3, "A")]).Select(r => r.Text));

    [Fact]
    public void NoGroups_HasNoRows() => Assert.Empty(CrrRows.Build([], [At("UAL1", 3, "A")]));
}
