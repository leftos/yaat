using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// EuroScope heading mode on the tag's AHDG field: a click opens the list, only a deliberate drag opens the elastic
// vector. A release within 10 px of the press, or still on the AHDG field, never confirms a heading, so a slip while
// clicking leaves the mode armed for the click-to-confirm path instead of sending a heading nobody picked.
public class RadarHeadingModeTests
{
    private const string Callsign = "UAL238";

    [AvaloniaFact]
    public void AhdgPress_WithASmallSlip_SendsNoHeading()
    {
        var rig = HeadingModeRig.Show();
        try
        {
            Point p = rig.AhdgPoint;
            Point slipped = new(p.X + 6, p.Y);
            rig.Down(p);
            rig.Move(slipped);
            rig.Up(slipped);

            Assert.Empty(rig.Sent);
            Assert.True(rig.Canvas.IsHeadingModeActive);
        }
        finally
        {
            rig.Close();
        }
    }

    [AvaloniaFact]
    public void AhdgPress_DragOutAndBackToThePress_SendsNoHeading()
    {
        var rig = HeadingModeRig.Show();
        try
        {
            Point p = rig.AhdgPoint;
            rig.Down(p);
            rig.Move(new Point(p.X + 40, p.Y));
            Point back = new(p.X + 3, p.Y);
            rig.Move(back);
            rig.Up(back);

            Assert.Empty(rig.Sent);
            Assert.True(rig.Canvas.IsHeadingModeActive);
        }
        finally
        {
            rig.Close();
        }
    }

    [AvaloniaFact]
    public void AhdgPress_DeliberateDrag_SendsTheHeadingAtRelease()
    {
        var rig = HeadingModeRig.Show();
        try
        {
            Point p = rig.AhdgPoint;
            Point away = new(p.X + 40, p.Y);
            rig.Down(p);
            rig.Move(away);
            rig.Up(away);

            Assert.Single(rig.Sent);
            Assert.Equal(Callsign, rig.Sent[0].Callsign);
            // The release point sits 80 px east and 30 px north of the aircraft; the press point is 40 px east and
            // 30 px north of it, a bearing far enough away that a heading computed from the press fails this value.
            Assert.Equal(75, rig.Sent[0].Heading);
            Assert.False(rig.Canvas.IsHeadingModeActive);
        }
        finally
        {
            rig.Close();
        }
    }

    [AvaloniaFact]
    public void AhdgPress_ReleasedExactlyTenPixelsFromThePress_SendsNoHeading()
    {
        var rig = HeadingModeRig.Show();
        try
        {
            Point p = rig.AhdgPoint;
            rig.Down(p);
            rig.Move(new Point(p.X + 40, p.Y));
            Point boundary = new(p.X + 10, p.Y);
            rig.Move(boundary);
            rig.Up(boundary);

            Assert.Empty(rig.Sent);
            Assert.True(rig.Canvas.IsHeadingModeActive);
        }
        finally
        {
            rig.Close();
        }
    }

    [AvaloniaFact]
    public void AhdgPress_ReleasedElevenPixelsFromThePress_SendsTheHeading()
    {
        var rig = HeadingModeRig.Show();
        try
        {
            Point p = rig.AhdgPoint;
            rig.Down(p);
            rig.Move(new Point(p.X + 40, p.Y));
            Point away = new(p.X + 11, p.Y);
            rig.Move(away);
            rig.Up(away);

            Assert.Single(rig.Sent);
            Assert.Equal(Callsign, rig.Sent[0].Callsign);
            Assert.False(rig.Canvas.IsHeadingModeActive);
        }
        finally
        {
            rig.Close();
        }
    }

    [AvaloniaFact]
    public void AhdgPress_ThenClickOnTheMap_SendsTheHeading()
    {
        var rig = HeadingModeRig.Show();
        try
        {
            Point p = rig.AhdgPoint;
            rig.Down(p);
            rig.Up(p);
            Assert.Empty(rig.Sent);

            Point target = new(p.X, p.Y - 60);
            rig.Down(target);
            rig.Up(target);

            Assert.Single(rig.Sent);
            Assert.Equal(Callsign, rig.Sent[0].Callsign);
            Assert.False(rig.Canvas.IsHeadingModeActive);
        }
        finally
        {
            rig.Close();
        }
    }

    /// <summary>
    /// A radar canvas showing one airborne aircraft with heading mode entered through real pointer input. Headless
    /// drawing never runs the canvas's draw op, so no tag rects exist to hit-test: a tunnel press handler stands in for
    /// <c>RadarCanvas.OnPointerPressed</c> → <c>EuroScopeFieldClicked</c> → <c>RadarView</c>, entering heading mode at
    /// the press point and marking it handled the way the AHDG field click does.
    /// </summary>
    private sealed class HeadingModeRig
    {
        private readonly Window _window;

        private HeadingModeRig(RadarCanvas canvas, Window window, Point ahdgPoint)
        {
            Canvas = canvas;
            _window = window;
            AhdgPoint = ahdgPoint;
        }

        public RadarCanvas Canvas { get; }

        public Point AhdgPoint { get; }

        public List<(string Callsign, int Heading)> Sent { get; } = [];

        public static HeadingModeRig Show()
        {
            var canvas = new RadarCanvas { EuroScopeMode = true };
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

            // The viewport learns its pixel size only once the layout has run, so the aircraft is placed afterwards
            // and sits exactly on the symbol point every gesture in these tests is measured from.
            var symbol = new Point(300, 300);
            (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)symbol.X, (float)symbol.Y);
            canvas.Aircraft =
            [
                new AircraftModel
                {
                    Callsign = Callsign,
                    AircraftType = "B738",
                    FiledAircraftType = "B738",
                    FlightRules = "IFR",
                    TransponderMode = "C",
                    Altitude = 5000,
                    GroundSpeed = 250,
                    Position = new LatLon(lat, lon),
                    Heading = new TrueHeading(90),
                },
            ];
            RenderFrame();

            var rig = new HeadingModeRig(canvas, window, new Point(symbol.X + 40, symbol.Y - 30));
            bool armed = true;
            window.AddHandler(
                InputElement.PointerPressedEvent,
                (_, e) =>
                {
                    if (armed)
                    {
                        armed = false;
                        canvas.EnterHeadingMode(Callsign, e.GetPosition(canvas));
                        e.Handled = true;
                    }
                },
                RoutingStrategies.Tunnel
            );
            canvas.HeadingModeConfirmed += (callsign, heading) => rig.Sent.Add((callsign, heading));
            return rig;
        }

        public void Down(Point canvasPoint)
        {
            _window.MouseDown(ToWindow(canvasPoint), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Move(Point canvasPoint)
        {
            _window.MouseMove(ToWindow(canvasPoint), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }

        public void Up(Point canvasPoint)
        {
            _window.MouseUp(ToWindow(canvasPoint), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Close()
        {
            _window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        private Point ToWindow(Point canvasPoint)
        {
            Point? inWindow = Canvas.TranslatePoint(canvasPoint, _window);
            Assert.NotNull(inWindow);
            return inWindow.Value;
        }

        // Headless frames are drawn only on a render-timer tick.
        private static void RenderFrame()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
