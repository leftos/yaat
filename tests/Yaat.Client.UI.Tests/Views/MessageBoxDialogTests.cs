using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MsBox.Avalonia;
using MsBox.Avalonia.Base;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Pins the MessageBox.Avalonia package to a build that loads against the Avalonia the client
/// ships. The 3.x line targets Avalonia 11: under Avalonia 12 its params types reference the
/// removed <c>Avalonia.Controls.SystemDecorations</c>, so every <c>MessageBoxManager</c> call
/// throws a TypeLoadException that the desktop dispatcher swallows — the favorites import in
/// GitHub #437 silently did nothing for that reason. Both box kinds are driven to a button click
/// here so a mismatched package fails this test instead of a user's dialog.
/// </summary>
public class MessageBoxDialogTests
{
    [AvaloniaFact]
    public async Task CustomBox_ShowsAsDialog_AndReturnsTheClickedButtonName()
    {
        var owner = new Window { Width = 300, Height = 200 };
        owner.ShowAndRunLayout();

        IMsBox<string> box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ButtonDefinitions = [new ButtonDefinition { Name = "Add to existing" }, new ButtonDefinition { Name = "Cancel", IsCancel = true }],
                ContentTitle = "Import Favorites",
                ContentMessage = "Add or replace?",
            }
        );
        Task<string> result = box.ShowWindowDialogAsync(owner);

        ClickDialogButton(owner, "Add to existing");

        Assert.Equal("Add to existing", await result);
    }

    [AvaloniaFact]
    public async Task StandardBox_ShowsAsDialog_AndReturnsOk()
    {
        var owner = new Window { Width = 300, Height = 200 };
        owner.ShowAndRunLayout();

        Task<ButtonResult> result = MessageBoxManager
            .GetMessageBoxStandard("Import Favorites", "Imported 452 new favorite(s).")
            .ShowWindowDialogAsync(owner);

        // The standard view keeps every button in the tree and shows only the ones its ButtonEnum names.
        ClickDialogButton(owner, b => b.IsEffectivelyVisible);

        Assert.Equal(MsBox.Avalonia.Enums.ButtonResult.Ok, await result);
    }

    private static void ClickDialogButton(Window owner, string content) =>
        ClickDialogButton(owner, b => string.Equals(b.Content as string, content, StringComparison.Ordinal));

    private static void ClickDialogButton(Window owner, Predicate<Button> match)
    {
        Dispatcher.UIThread.RunJobs();
        Window dialog = Assert.Single(owner.OwnedWindows, w => w.GetType().Name == "MsBoxWindow");
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Button button = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), match);
        Point? center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), dialog);
        Assert.NotNull(center);

        dialog.MouseDown(center.Value, MouseButton.Left);
        dialog.MouseUp(center.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
