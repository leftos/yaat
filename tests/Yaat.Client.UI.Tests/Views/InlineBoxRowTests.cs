using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The inline box row the Squawk and Say flyouts share (<see cref="InlineBoxRow"/>): text the row cannot read draws the box's
/// red underline, the reason line under it and <c>can't send</c> in the command column, and Enter sends nothing; text it
/// reads sends on Enter and closes the menu; a blank box shows the verb waiting for its argument and sends nothing.
/// </summary>
public class InlineBoxRowTests
{
    private const string Reason = "Not a word the test reads";

    private static readonly InlineBoxSpec Spec = new()
    {
        Label = "Custom",
        Glyph = null,
        Verb = "SAY",
        Placeholder = "words",
        BoxName = "TestInlineBox",
        BoxWidth = 200,
        Seed = "",
        Read = Read,
    };

    [AvaloniaFact]
    public void TextItCannotRead_ShowsTheUnderlineTheReasonAndCantSend_EnterSendsNothing()
    {
        OpenRow open = Open();
        try
        {
            Type(open.Row, "bad");

            Assert.Contains(RefusableBox.RefusedClass, open.Row.Box.Classes);
            AssertBarShown(open.Row);
            Assert.True(open.Row.Refusal.ReasonLine.IsVisible, "A refused text should show its reason line.");
            Assert.Equal(Reason, open.Row.Refusal.ReasonLine.Text);
            Assert.Equal(InlineBoxRow.CantSend, open.Row.CommandColumn.Text);

            PressEnter(open.Row.Box);

            Assert.Empty(open.Sent);
            Assert.True(open.Menu.IsOpen, "Enter on a refused text should leave the menu open.");
        }
        finally
        {
            open.Close();
        }
    }

    [AvaloniaFact]
    public void TextItReads_EnterSendsTheCommandAndClosesTheMenu()
    {
        OpenRow open = Open();
        try
        {
            Type(open.Row, "  say again ");

            Assert.DoesNotContain(RefusableBox.RefusedClass, open.Row.Box.Classes);
            Assert.False(open.Row.Refusal.ReasonLine.IsVisible, "A readable text should show no reason line.");
            Assert.Equal("SAY say again", open.Row.CommandColumn.Text);

            PressEnter(open.Row.Box);

            Assert.Equal(["SAY say again"], open.Sent);
            Assert.False(open.Menu.IsOpen, "Enter on a readable text should send and close the menu.");
        }
        finally
        {
            open.Close();
        }
    }

    [AvaloniaFact]
    public void BlankBox_ShowsTheVerbWaiting_EnterSendsNothing()
    {
        OpenRow open = Open();
        try
        {
            Type(open.Row, "   ");

            Assert.DoesNotContain(RefusableBox.RefusedClass, open.Row.Box.Classes);
            Assert.False(open.Row.Refusal.ReasonLine.IsVisible, "A blank box should show no reason line.");
            Assert.Equal($"SAY {TakeoffFlyout.Unfilled}", open.Row.CommandColumn.Text);

            PressEnter(open.Row.Box);

            Assert.Empty(open.Sent);
            Assert.True(open.Menu.IsOpen, "Enter on a blank box should leave the menu open.");
        }
        finally
        {
            open.Close();
        }
    }

    /// <summary>A refused text corrected to one the row reads clears the underline and the reason line.</summary>
    [AvaloniaFact]
    public void RefusedThenCorrected_ClearsTheRefusal()
    {
        OpenRow open = Open();
        try
        {
            Type(open.Row, "bad");
            Type(open.Row, "unable");

            Assert.DoesNotContain(RefusableBox.RefusedClass, open.Row.Box.Classes);
            Assert.False(open.Row.Refusal.ReasonLine.IsVisible, "A corrected text should hide the reason line.");
            Assert.Equal("SAY unable", open.Row.CommandColumn.Text);
        }
        finally
        {
            open.Close();
        }
    }

    /// <summary>
    /// A focused box given a code it cannot read keeps its frame as drawn while it reads: the red bar is laid over its bottom
    /// edge rather than replacing its border.
    /// </summary>
    [AvaloniaFact]
    public void FocusedBox_RefusedCode_ShowsTheBar_KeepsTheFrame()
    {
        OpenRow open = OpenSquawk();
        try
        {
            Assert.True(open.Row.Box.Focus(), "The Squawk box should take focus.");
            HeadlessWindowExtensions.PumpDispatcher();
            Thickness frame = BorderElement(open.Row.Box).BorderThickness;
            IBrush? frameBrush = BorderElement(open.Row.Box).BorderBrush;

            Type(open.Row, "8888");
            TopLevel.GetTopLevel(open.Row.Box)!.UpdateLayout();

            Assert.True(open.Row.Box.IsFocused, "Typing should leave the focus in the box.");
            Assert.Contains(RefusableBox.RefusedClass, open.Row.Box.Classes);
            AssertBarShown(open.Row);
            Assert.Equal(frame, BorderElement(open.Row.Box).BorderThickness);
            Assert.Same(frameBrush, BorderElement(open.Row.Box).BorderBrush);
            Border bar = open.Row.Refusal.Underline;
            Point barBottom = Assert.NotNull(bar.TranslatePoint(new Point(0, bar.Bounds.Height), open.Row.Box));
            Assert.Equal(open.Row.Box.Bounds.Height, barBottom.Y, 1);
        }
        finally
        {
            open.Close();
        }
    }

    /// <summary>A refused code corrected to one the row reads hides the bar and the reason line.</summary>
    [AvaloniaFact]
    public void RefusedCode_ClearedToAValidCode_HidesTheBarAndTheReasonLine()
    {
        OpenRow open = OpenSquawk();
        try
        {
            Type(open.Row, "8888");
            Type(open.Row, "1200");

            Assert.False(open.Row.Refusal.Underline.IsVisible, "A valid code should hide the red bar.");
            Assert.False(open.Row.Refusal.ReasonLine.IsVisible, "A valid code should hide the reason line.");
            Assert.DoesNotContain(RefusableBox.RefusedClass, open.Row.Box.Classes);
            Assert.Equal("SQ 1200", open.Row.CommandColumn.Text);
        }
        finally
        {
            open.Close();
        }
    }

    private static void AssertBarShown(InlineBoxRow row)
    {
        Assert.True(row.Refusal.Underline.IsVisible, "A refused text should show the red bar over the box's bottom edge.");
        Assert.Equal(FlyoutPanelParts.RefusalBrush, row.Refusal.Underline.Background);
    }

    /// <summary>The test's reader: blank waits, <c>bad</c> is refused, anything else is said.</summary>
    private static InlineBoxReading Read(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return InlineBoxReading.Blank;
        }

        return (trimmed == "bad") ? InlineBoxReading.Refuses(Reason) : InlineBoxReading.Sends($"SAY {trimmed}");
    }

    private sealed record OpenRow(Window Window, ContextMenu Menu, InlineBoxRow Row, List<string> Sent)
    {
        public void Close()
        {
            Menu.Close();
            Window.Close();
        }
    }

    /// <summary>The test spec's row, opened as <see cref="OpenItem"/> does.</summary>
    private static OpenRow Open()
    {
        List<string> sent = [];
        MenuItem item = InlineBoxRow
            .Create(
                Spec,
                command =>
                {
                    sent.Add(command);
                    return Task.CompletedTask;
                }
            )
            .Item;
        return OpenItem(item, sent);
    }

    /// <summary>The Squawk flyout's code box row, opened as <see cref="OpenItem"/> does; its sends go to a host the tests do not read.</summary>
    private static OpenRow OpenSquawk()
    {
        var host = new RecordingMenuHost("");
        MenuItem item = MenuCatalog.SquawkCodeRow(TestMenuContext.Create("N123AB", "AB", null, false, VfrCommandsForIfr.EnterFinalOnly), host).Item;
        return OpenItem(item, []);
    }

    /// <summary>A context menu holding a submenu whose one item is <paramref name="item"/>, opened on a shown window with the submenu open.</summary>
    private static OpenRow OpenItem(MenuItem item, List<string> sent)
    {
        var anchor = new Border();
        var window = new Window
        {
            Width = 500,
            Height = 300,
            Content = anchor,
        };
        window.ShowAndRunLayout();

        var group = new MenuItem { Header = "Group", Items = { item } };
        var menu = new ContextMenu { Items = { group } };
        menu.Open(window);
        HeadlessWindowExtensions.PumpDispatcher();
        group.IsSubMenuOpen = true;
        HeadlessWindowExtensions.PumpDispatcher();
        return new OpenRow(window, menu, Assert.IsType<InlineBoxRow>(item.Header), sent);
    }

    private static void Type(InlineBoxRow row, string text)
    {
        row.Box.Text = text;
        HeadlessWindowExtensions.PumpDispatcher();
    }

    private static void PressEnter(TextBox box)
    {
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        HeadlessWindowExtensions.PumpDispatcher();
    }

    private static Border BorderElement(TextBox box) =>
        box.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_BorderElement");
}
