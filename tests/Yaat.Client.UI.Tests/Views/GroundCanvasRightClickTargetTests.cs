using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// A ground right-click gathers every target it lands on — each aircraft whose datablock or symbol it hits, and, with an
// aircraft selected, each parking/spot/helipad marker within 10 px — and offers a picker when there are two or more, so a
// stand under a parked aircraft stays reachable. The target the hover label names is option 1: a datablock under the
// cursor, else the stand within the 20 px hover radius, inserted when it is beyond 10 px. One target keeps today's
// direct menu.
public class GroundCanvasRightClickTargetTests
{
    private const double FieldLat = 37.62;
    private const double FieldLon = -122.39;
    private const int StandNodeId = 7;

    [AvaloniaFact]
    public void GroundRightClick_SelectedAircraft_ClickOnParkingMarkerUnderParkedAircraft_ListsHoveredStandFirstThenAircraft()
    {
        GroundCanvas canvas = MakeCanvas();
        Point stand = StandScreenPoint(canvas);
        AircraftModel parked = MakeAircraft(canvas, "UAL238", "B738", stand.X + 3, stand.Y + 3);
        AircraftModel selected = MakeAircraft(canvas, "SWA12", "B737", stand.X - 200, stand.Y);
        canvas.Aircraft = [parked, selected];
        canvas.SelectedAircraft = selected;

        IReadOnlyList<RightClickTarget> targets = canvas.FindRightClickTargets(stand);

        Assert.Equal(["Parking A5", "UAL238 (B738)"], targets.Select(t => t.Label));
        Assert.Equal(StandNodeId, targets[0].NodeId);
        Assert.Equal("UAL238", targets[1].Callsign);
    }

    // On the real KOAK layout: the cursor sits exactly on a stand the hover label names, a parked aircraft's symbol close
    // enough to be hit too. The stand under the cursor is option 1, the aircraft follows.
    [AvaloniaFact]
    public void FindRightClickTargets_CursorOnStandWithAircraftNearby_ListsStandFirst()
    {
        (GroundCanvas canvas, GroundNodeDto stand, Point standPoint) = OakStandCanvas();
        AircraftModel parked = MakeAircraft(canvas, "UAL238", "B738", standPoint.X, standPoint.Y + 15);
        AircraftModel selected = MakeAircraft(canvas, "SWA12", "B737", standPoint.X - 200, standPoint.Y);
        canvas.Aircraft = [parked, selected];
        canvas.SelectedAircraft = selected;
        Assert.Null(canvas.FindDataBlockAtPoint(standPoint));
        Assert.Same(parked, canvas.FindAircraftAtPoint(standPoint));
        Assert.Equal(stand.Id, canvas.FindNodeAtPoint(standPoint)!.Id);

        IReadOnlyList<RightClickTarget> targets = canvas.FindRightClickTargets(standPoint);

        Assert.Equal(2, targets.Count);
        Assert.Equal(stand.Id, targets[0].NodeId);
        Assert.Equal("UAL238", targets[1].Callsign);
    }

    // The hover label names a stand up to 20 px away, the stand hit only 10 px: a stand 15 px from the cursor is not a
    // candidate of its own, but as the hovered target it is inserted as option 1, ahead of the aircraft the click hits.
    [AvaloniaFact]
    public void FindRightClickTargets_StandTenToTwentyPxAway_IsInsertedFirst()
    {
        (GroundCanvas canvas, GroundNodeDto stand, Point standPoint) = OakStandCanvas();
        var click = new Point(standPoint.X + 15, standPoint.Y);
        AircraftModel parked = MakeAircraft(canvas, "UAL238", "B738", click.X, click.Y + 15);
        AircraftModel selected = MakeAircraft(canvas, "SWA12", "B737", standPoint.X - 200, standPoint.Y);
        canvas.Aircraft = [parked, selected];
        canvas.SelectedAircraft = selected;
        Assert.Null(canvas.FindDataBlockAtPoint(click));
        Assert.Same(parked, canvas.FindAircraftAtPoint(click));
        Assert.Equal(stand.Id, canvas.FindNodeAtPoint(click)!.Id);

        IReadOnlyList<RightClickTarget> targets = canvas.FindRightClickTargets(click);

        Assert.Equal(2, targets.Count);
        Assert.Equal(stand.Id, targets[0].NodeId);
        Assert.Equal("UAL238", targets[1].Callsign);
    }

    [AvaloniaFact]
    public void GroundRightClick_NoSelection_ClickOnParkedAircraftAtItsStand_IsAircraftOnly()
    {
        GroundCanvas canvas = MakeCanvas();
        Point stand = StandScreenPoint(canvas);
        canvas.Aircraft = [MakeAircraft(canvas, "UAL238", "B738", stand.X + 3, stand.Y + 3)];

        RightClickTarget target = Assert.Single(canvas.FindRightClickTargets(stand));

        Assert.Equal("UAL238", target.Callsign);
    }

    [AvaloniaFact]
    public void GroundRightClick_ParkingMarkerBeyond10Px_IsNotATarget()
    {
        GroundCanvas canvas = MakeCanvas();
        Point stand = StandScreenPoint(canvas);
        AircraftModel selected = MakeAircraft(canvas, "SWA12", "B737", stand.X - 200, stand.Y);
        canvas.Aircraft = [selected];
        canvas.SelectedAircraft = selected;

        Assert.Empty(canvas.FindRightClickTargets(new Point(stand.X + 11, stand.Y)));
        Assert.Single(canvas.FindRightClickTargets(new Point(stand.X + 9, stand.Y)));
    }

    [AvaloniaFact]
    public void GroundRightClick_TwoOverlappingAircraft_OffersBoth_InDistanceOrder()
    {
        GroundCanvas canvas = MakeCanvas();
        var click = new Point(300, 300);
        AircraftModel far = MakeAircraft(canvas, "SWA12", "B737", click.X - 12, click.Y);
        AircraftModel near = MakeAircraft(canvas, "UAL238", "B738", click.X + 4, click.Y);
        canvas.Aircraft = [far, near];

        IReadOnlyList<RightClickTarget> targets = canvas.FindRightClickTargets(click);

        Assert.Equal(["UAL238", "SWA12"], targets.Select(t => t.Callsign));
    }

    [AvaloniaFact]
    public void GroundRightClick_AircraftHitByDatablockAndIcon_IsOneTarget()
    {
        GroundCanvas canvas = MakeCanvas();
        var state = new GroundDataBlockViewState();
        state.ManualOffsets["UAL238"] = new SKPoint(0, 0);
        canvas.DataBlockState = state;
        AircraftModel ac = MakeAircraft(canvas, "UAL238", "B738", 300, 300);
        canvas.Aircraft = [ac];
        var click = new Point(305, 305);
        Assert.Same(ac, canvas.FindDataBlockAtPoint(click));
        Assert.Same(ac, canvas.FindAircraftAtPoint(click));

        RightClickTarget target = Assert.Single(canvas.FindRightClickTargets(click));

        Assert.Equal("UAL238", target.Callsign);
        Assert.True(target.ViaDataBlock);
    }

    [AvaloniaFact]
    public void GroundRightClick_SingleAircraft_RaisesAircraftRightClickedDirectly()
    {
        (GroundCanvas canvas, Window window) = ShowCanvas();
        try
        {
            Point stand = StandScreenPoint(canvas);
            var click = new Point(stand.X + 60, stand.Y - 60);
            canvas.Aircraft = [MakeAircraft(canvas, "UAL238", "B738", click.X + 2, click.Y)];
            var raised = new List<(string Callsign, Point Pos)>();
            canvas.AircraftRightClicked += (cs, pos) => raised.Add((cs, pos));

            RightClick(window, canvas, click);

            Assert.Equal([("UAL238", click)], raised);
            Assert.Null(canvas.ActiveRightClickPicker);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void RightClickPicker_ChoosingATarget_RaisesItsEventWithTheOriginalPoint()
    {
        (GroundCanvas canvas, Window window) = ShowCanvas();
        try
        {
            // The stand sits at the fitted layout's lower-left corner, so the click lands just up-right of it, inside the canvas.
            Point standNode = StandScreenPoint(canvas);
            var stand = new Point(standNode.X + 4, standNode.Y - 4);
            AircraftModel parked = MakeAircraft(canvas, "UAL238", "B738", stand.X + 2, stand.Y - 2);
            AircraftModel selected = MakeAircraft(canvas, "SWA12", "B737", stand.X + 200, stand.Y - 100);
            canvas.Aircraft = [parked, selected];
            canvas.SelectedAircraft = selected;
            var nodeClicks = new List<(int NodeId, Point Pos)>();
            var aircraftClicks = new List<string>();
            canvas.NodeRightClicked += (id, pos) => nodeClicks.Add((id, pos));
            canvas.AircraftRightClicked += (cs, _) => aircraftClicks.Add(cs);

            RightClick(window, canvas, stand);

            ContextMenu? picker = canvas.ActiveRightClickPicker;
            Assert.NotNull(picker);
            Assert.Empty(nodeClicks);
            Assert.Empty(aircraftClicks);
            MenuItem parkingItem = picker!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Parking A5"));
            parkingItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([(StandNodeId, stand)], nodeClicks);
            Assert.Empty(aircraftClicks);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void GroundRightClick_SelectedAircraftOnItsOwnStand_IsAircraftOnly()
    {
        GroundCanvas canvas = MakeCanvas();
        Point stand = StandScreenPoint(canvas);
        AircraftModel selected = MakeAircraft(canvas, "UAL238", "B738", stand.X + 3, stand.Y + 3);
        canvas.Aircraft = [selected];
        canvas.SelectedAircraft = selected;

        RightClickTarget target = Assert.Single(canvas.FindRightClickTargets(stand));

        Assert.Equal("UAL238", target.Callsign);
    }

    [AvaloniaFact]
    public void GroundRightClick_DataBlockHit_IsListedBeforeACloserCentreHit()
    {
        GroundCanvas canvas = MakeCanvas();
        var click = new Point(300, 300);

        // SWA12's symbol is 40 px away (outside the 28 px symbol radius), but its datablock is dragged over the click;
        // UAL238's symbol is 3 px away.
        var state = new GroundDataBlockViewState();
        state.ManualOffsets["SWA12"] = new SKPoint(35, 0);
        canvas.DataBlockState = state;
        AircraftModel blockOwner = MakeAircraft(canvas, "SWA12", "B737", click.X - 40, click.Y - 5);
        AircraftModel nearSymbol = MakeAircraft(canvas, "UAL238", "B738", click.X + 3, click.Y);
        canvas.Aircraft = [blockOwner, nearSymbol];
        Assert.Same(blockOwner, canvas.FindDataBlockAtPoint(click));
        Assert.Same(nearSymbol, canvas.FindAircraftAtPoint(click));

        IReadOnlyList<RightClickTarget> targets = canvas.FindRightClickTargets(click);

        Assert.Equal(["SWA12", "UAL238"], targets.Select(t => t.Callsign));
        Assert.Equal([true, false], targets.Select(t => t.ViaDataBlock));
    }

    [AvaloniaFact]
    public void RightClickPicker_ClickingAnEntry_RaisesItsEventOnlyAfterThePickerHasClosed()
    {
        (GroundCanvas canvas, Window window) = ShowCanvas();
        try
        {
            Point standNode = StandScreenPoint(canvas);
            var stand = new Point(standNode.X + 4, standNode.Y - 4);
            AircraftModel parked = MakeAircraft(canvas, "UAL238", "B738", stand.X + 2, stand.Y - 2);
            AircraftModel selected = MakeAircraft(canvas, "SWA12", "B737", stand.X + 200, stand.Y - 100);
            canvas.Aircraft = [parked, selected];
            canvas.SelectedAircraft = selected;
            RightClick(window, canvas, stand);
            ContextMenu? picker = canvas.ActiveRightClickPicker;
            Assert.NotNull(picker);
            Assert.True(picker!.IsOpen);

            bool? pickerOpenWhenRaised = null;
            canvas.NodeRightClicked += (_, _) => pickerOpenWhenRaised = picker.IsOpen;

            // A real click on the entry, so the menu closes itself the way it does for a user.
            MenuItem parkingItem = picker.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Parking A5"));
            var itemRoot = TopLevel.GetTopLevel(parkingItem);
            Assert.NotNull(itemRoot);
            Point? itemCentre = parkingItem.TranslatePoint(new Point(parkingItem.Bounds.Width / 2, parkingItem.Bounds.Height / 2), itemRoot!);
            Assert.NotNull(itemCentre);
            itemRoot!.MouseDown(itemCentre!.Value, MouseButton.Left);
            itemRoot.MouseUp(itemCentre.Value, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.False(pickerOpenWhenRaised);
            Assert.Null(canvas.ActiveRightClickPicker);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static GroundLayoutDto StandLayout() =>
        new(
            "SFO",
            [
                new GroundNodeDto(StandNodeId, FieldLat, FieldLon, "Parking", "A5", 280, null),
                new GroundNodeDto(8, FieldLat + 0.01, FieldLon + 0.01, "TaxiwayIntersection", null, null, null),
            ],
            [],
            null,
            null,
            null
        );

    private static GroundCanvas MakeCanvas()
    {
        var canvas = new GroundCanvas();
        canvas.Viewport.CenterLat = FieldLat;
        canvas.Viewport.CenterLon = FieldLon;
        canvas.Viewport.Zoom = 1.0;
        canvas.Viewport.PixelWidth = 800f;
        canvas.Viewport.PixelHeight = 600f;
        canvas.AirportCenterLat = FieldLat;
        canvas.AirportCenterLon = FieldLon;
        canvas.AirportElevation = 0;
        canvas.Layout = StandLayout();
        return canvas;
    }

    private static (GroundCanvas Canvas, Window Window) ShowCanvas()
    {
        var canvas = new GroundCanvas();
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        canvas.AirportCenterLat = FieldLat;
        canvas.AirportCenterLon = FieldLon;
        canvas.AirportElevation = 0;
        canvas.Layout = StandLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (canvas, window);
    }

    /// <summary>
    /// A canvas over the real KOAK layout, centred on its first parking stand and zoomed so the nearest other node is 60 px
    /// away: the stand is the only node within the 20 px hover radius of any point up to 40 px from it.
    /// </summary>
    private static (GroundCanvas Canvas, GroundNodeDto Stand, Point StandPoint) OakStandCanvas()
    {
        GroundLayoutDto layout = MenuGoldenFixtures.OakLayoutForClient;
        GroundNodeDto stand = layout.Nodes.First(n => n.Type == "Parking");
        var standPos = new LatLon(stand.Latitude, stand.Longitude);
        GroundNodeDto neighbour = layout
            .Nodes.Where(n => n.Id != stand.Id)
            .MinBy(n => GeoMath.DistanceNm(standPos, new LatLon(n.Latitude, n.Longitude)))!;
        var canvas = new GroundCanvas();
        canvas.Viewport.CenterLat = stand.Latitude;
        canvas.Viewport.CenterLon = stand.Longitude;
        canvas.Viewport.Zoom = 1.0;
        canvas.Viewport.PixelWidth = 800f;
        canvas.Viewport.PixelHeight = 600f;
        canvas.AirportCenterLat = stand.Latitude;
        canvas.AirportCenterLon = stand.Longitude;
        canvas.AirportElevation = 0;
        canvas.Layout = layout;
        (float sx, float sy) = canvas.Viewport.LatLonToScreen(stand.Latitude, stand.Longitude);
        (float nx, float ny) = canvas.Viewport.LatLonToScreen(neighbour.Latitude, neighbour.Longitude);
        canvas.Viewport.Zoom = 60.0 / Math.Sqrt(((nx - sx) * (nx - sx)) + ((ny - sy) * (ny - sy)));
        (float x, float y) = canvas.Viewport.LatLonToScreen(stand.Latitude, stand.Longitude);
        return (canvas, stand, new Point(x, y));
    }

    private static Point StandScreenPoint(GroundCanvas canvas)
    {
        (float x, float y) = canvas.Viewport.LatLonToScreen(FieldLat, FieldLon);
        return new Point(x, y);
    }

    // An on-ground aircraft whose symbol sits at the given canvas point.
    private static AircraftModel MakeAircraft(GroundCanvas canvas, string callsign, string type, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        return new AircraftModel
        {
            Callsign = callsign,
            AircraftType = type,
            FlightRules = "IFR",
            TransponderMode = "C",
            IsOnGround = true,
            Altitude = 0,
            Position = new LatLon(lat, lon),
        };
    }

    private static void RightClick(Window window, GroundCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Right);
        window.MouseUp(inWindow.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }
}
