using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The RUNDOWN entry menu on the Radar view: a right click on a row opens the entry's coordination menu, a right click on the
/// title still opens the map's, and a click on a verb sends it for the entry's aircraft as the position it acts as.
/// </summary>
public class ScopeRundownMenuTests
{
    [AvaloniaFact]
    public void RightClickOnARow_OpensTheEntryMenu()
    {
        (ScopeView view, Window window) = ShowScopeWithRows("Unsent");
        try
        {
            RightClick(window, view.Canvas, CentreOfLine(view.Canvas, 1));

            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
            Assert.Equal(
                ["Acknowledge", "Release", "Recall", "Hold…", "Modify…", "Delete", "Move up", "Move down"],
                menu.Items.OfType<MenuItem>().Select(m => m.Header as string)
            );
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RightClickOnTheTitle_OpensTheMapMenuAsBefore()
    {
        (ScopeView view, Window window) = ShowScopeWithRows("Unsent");
        try
        {
            RightClick(window, view.Canvas, CentreOfLine(view.Canvas, 0));

            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
            Assert.Equal("Settings for this view…", (menu.Items[^1] as MenuItem)?.Header);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => (m.Header as string) == "Recall");
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ClickingRecall_SendsTheCommand()
    {
        (ScopeView view, Window window) = ShowScopeWithRows("Unsent");
        try
        {
            var sent = new List<(string Callsign, string Command)>();
            view.RundownCommandSender = (callsign, command) =>
            {
                sent.Add((callsign, command));
                return Task.CompletedTask;
            };
            RundownRow row = view.Canvas.CurrentFrame.Rundown.Lines[1].Row!;
            RightClick(window, view.Canvas, CentreOfLine(view.Canvas, 1));
            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
            MenuItem recall = Assert.Single(menu.Items.OfType<MenuItem>(), m => (m.Header as string) == "Recall");

            recall.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([(row.Callsign, "AS 3O RDR PFAT")], sent);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void HoldOnARecalledEntry_TypedTextAndEnter_SendsTheHoldWithTheText()
    {
        (ScopeView view, Window window) = ShowScopeWithRows("Recalled");
        try
        {
            List<(string Callsign, string Command)> sent = CaptureSends(view);
            RundownRow row = view.Canvas.CurrentFrame.Rundown.Lines[1].Row!;
            TopLevel prompt = OpenHoldPrompt(window, view);

            prompt.KeyTextInput("RWY 30 ONLY");
            Dispatcher.UIThread.RunJobs();
            prompt.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([(row.Callsign, "AS 3O RDH PFAT RWY 30 ONLY")], sent);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void HoldOnARecalledEntry_Esc_SendsNothing()
    {
        (ScopeView view, Window window) = ShowScopeWithRows("Recalled");
        try
        {
            List<(string Callsign, string Command)> sent = CaptureSends(view);
            TopLevel prompt = OpenHoldPrompt(window, view);

            prompt.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(sent);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task TextPrompt_OnAnAnchorInNoWindow_AnswersNull()
    {
        Task<string?> answer = RundownTextPrompt.AskAsync(new Border(), new Point(10, 10), RundownEntryMenu.HoldHeader);

        Dispatcher.UIThread.RunJobs();

        Assert.True(answer.IsCompleted);
        Assert.Null(await answer);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static List<(string Callsign, string Command)> CaptureSends(ScopeView view)
    {
        var sent = new List<(string Callsign, string Command)>();
        view.RundownCommandSender = (callsign, command) =>
        {
            sent.Add((callsign, command));
            return Task.CompletedTask;
        };
        return sent;
    }

    // Right-clicks the second row, clicks Hold… with the mouse, and returns the top level of the prompt that opens, whose
    // focused element must be the prompt's box.
    private static TopLevel OpenHoldPrompt(Window window, ScopeView view)
    {
        RightClick(window, view.Canvas, CentreOfLine(view.Canvas, 1));
        ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
        MenuItem hold = Assert.Single(menu.Items.OfType<MenuItem>(), m => (m.Header as string) == RundownEntryMenu.HoldHeader);
        Assert.True(hold.IsEnabled);
        ClickOn(hold);
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        IInputElement? focused = window.FocusManager?.GetFocusedElement();
        TextBox box = Assert.IsType<TextBox>(focused);
        return Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(box));
    }

    // Presses and releases the left button over the middle of the control, through its own top level's mouse.
    private static void ClickOn(Control target)
    {
        TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(target));
        Point? centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top);
        Assert.NotNull(centre);
        top.MouseDown(centre.Value, MouseButton.Left);
        top.MouseUp(centre.Value, MouseButton.Left);
    }

    private static (ScopeView View, Window Window) ShowScopeWithRows(string status)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot)
        {
            TemplateId = ScopeTemplates.StarsId,
        };
        main.OpenScope = scope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        main.ApplyCoordinationLists([Pfat(status)]);
        RenderFrame(view.Canvas);
        Assert.Equal(3, view.Canvas.CurrentFrame.Rundown.Lines.Count);
        return (view, window);
    }

    private static CoordinationListHubDto Pfat(string status) =>
        new(
            ListId: "PFAT",
            Title: "Oakland departure releases",
            AutoAcknowledge: false,
            SenderTcps: ["3O"],
            ReceiverTcps: ["4O"],
            Items:
            [
                new CoordinationItemHubDto("POAK-1", "SWA123", status, 7, "B738", "0425", "F", "SJC", "RWY 30", "3O", null),
                new CoordinationItemHubDto("POAK-2", "SWA456", status, 8, "B738", "0426", "F", "SJC", "RWY 30", "3O", null),
            ]
        );

    private static Point CentreOfLine(ScopeCanvas canvas, int index)
    {
        SKRect rect = canvas.CurrentFrame.Rundown.Lines[index].Rect;
        return new Point(rect.MidX, rect.MidY);
    }

    private static void RightClick(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Right);
        window.MouseUp(inWindow.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    // Headless frames are drawn only on a render-timer tick.
    private static void RenderFrame(ScopeCanvas canvas)
    {
        canvas.MarkDirty();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Close(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
