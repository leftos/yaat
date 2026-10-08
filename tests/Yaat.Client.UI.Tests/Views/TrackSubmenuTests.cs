using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Track submenu's shape: only the items the track's own state calls for, each state's group in its own place, a
/// separator between shown groups and none lead, trail or double.
/// </summary>
public class TrackSubmenuTests
{
    private const string SeparatorText = "---";

    private static MenuContext Context() => TestMenuContext.Create("N123AB", "AB", null, false, VfrCommandsForIfr.None);

    /// <summary>The submenu's items as header text, "---" for a separator.</summary>
    private static List<string> Shape(AircraftModel? aircraft)
    {
        MenuItem menu = SharedMenuGroups.Track(aircraft, Context(), new RecordingMenuHost(""));
        List<string> shape = [];
        foreach (object? item in menu.Items)
        {
            shape.Add(
                item switch
                {
                    Separator => SeparatorText,
                    MenuItem menuItem => menuItem.Header as string ?? "",
                    _ => "",
                }
            );
        }

        return shape;
    }

    private static AircraftModel Track(
        string? owner,
        string? ownerSectorCode,
        string? handoffPeer,
        string? handoffPeerSectorCode,
        string? pointoutStatus
    ) =>
        new()
        {
            Callsign = "N123AB",
            FlightRules = "IFR",
            CurrentPhase = "",
            Situation = AircraftSituation.IfrEnroute,
            IsOnGround = false,
            Owner = owner,
            OwnerSectorCode = ownerSectorCode,
            HandoffPeer = handoffPeer,
            HandoffPeerSectorCode = handoffPeerSectorCode,
            PointoutStatus = pointoutStatus,
        };

    [AvaloniaFact]
    public void Untracked_ShowsOnlyInitiateTrack() => Assert.Equal(["Initiate Track"], Shape(Track(null, null, null, null, null)));

    [AvaloniaFact]
    public void NullAircraft_ShowsOnlyInitiateTrack() => Assert.Equal(["Initiate Track"], Shape(null));

    [AvaloniaFact]
    public void Owned_ShowsInitiateHandoffPointOutAndDrop() =>
        Assert.Equal(["Initiate handoff…", "Point out…", "Drop track"], Shape(Track(null, "NCT", null, null, null)));

    /// <summary>The owner's callsign alone still counts as owned when no sector code resolves for it.</summary>
    [AvaloniaFact]
    public void OwnerWithoutASectorCode_CountsAsOwned() =>
        Assert.Equal(["Initiate handoff…", "Point out…", "Drop track"], Shape(Track("NCT", null, null, null, null)));

    [AvaloniaFact]
    public void HandoffInProgress_ShowsAcceptAndCancel() =>
        Assert.Equal(["Accept handoff", "Cancel handoff"], Shape(Track(null, null, "OAK_3", null, null)));

    /// <summary>A handoff to an unstaffed sector has no peer callsign, only its sector code.</summary>
    [AvaloniaFact]
    public void HandoffToAnUnstaffedSector_ShowsAcceptAndCancel() =>
        Assert.Equal(["Accept handoff", "Cancel handoff"], Shape(Track(null, null, "", "2B", null)));

    [AvaloniaFact]
    public void PointoutPending_ShowsAcknowledge() => Assert.Equal(["Acknowledge pointout"], Shape(Track(null, null, null, null, "Pending")));

    [AvaloniaFact]
    public void PointoutAccepted_CountsAsNone() => Assert.Equal(["Initiate Track"], Shape(Track(null, null, null, null, "Accepted")));

    [AvaloniaFact]
    public void OwnedWithHandoffInProgress_ShowsBothGroupsUnderOneSeparator() =>
        Assert.Equal(
            ["Initiate handoff…", "Point out…", "Drop track", SeparatorText, "Accept handoff", "Cancel handoff"],
            Shape(Track(null, "NCT", "OAK_3", null, null))
        );

    [AvaloniaFact]
    public void OwnedWithPointoutPending_ShowsThePointoutGroupUnderASeparator() =>
        Assert.Equal(
            ["Initiate handoff…", "Point out…", "Drop track", SeparatorText, "Acknowledge pointout"],
            Shape(Track(null, "NCT", null, null, "Pending"))
        );

    [AvaloniaTheory]
    [InlineData(null, null, null, null, null)]
    [InlineData("NCT", null, null, null, null)]
    [InlineData(null, "NCT", null, null, null)]
    [InlineData(null, null, "OAK_3", null, null)]
    [InlineData(null, null, "", "2B", null)]
    [InlineData(null, null, null, null, "Pending")]
    [InlineData(null, "NCT", "OAK_3", null, null)]
    [InlineData(null, "NCT", null, null, "Pending")]
    public void EveryState_HasNoLeadingTrailingOrDoubleSeparator(
        string? owner,
        string? ownerSectorCode,
        string? handoffPeer,
        string? handoffPeerSectorCode,
        string? pointoutStatus
    )
    {
        List<string> shape = Shape(Track(owner, ownerSectorCode, handoffPeer, handoffPeerSectorCode, pointoutStatus));

        Assert.NotEmpty(shape);
        Assert.NotEqual(SeparatorText, shape[0]);
        Assert.NotEqual(SeparatorText, shape[^1]);
        for (int i = 1; i < shape.Count; i++)
        {
            Assert.False((shape[i - 1] == SeparatorText) && (shape[i] == SeparatorText), $"double separator in [{string.Join(" | ", shape)}]");
        }
    }
}
