using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Pins the MessageBox.Avalonia package to a build that loads against the Avalonia the client
/// ships. The 3.x line targets Avalonia 11: under Avalonia 12 its params types reference the
/// removed <c>Avalonia.Controls.SystemDecorations</c>, so every message box throws a
/// TypeLoadException that the desktop dispatcher swallows — the favorites import in GitHub #437
/// silently did nothing for that reason. Both box kinds are driven through
/// <see cref="MessageBoxPresenter"/>, the client's only route to the package, to a button click
/// here so a mismatched package fails this test instead of a user's dialog.
/// </summary>
public class MessageBoxDialogTests
{
    [AvaloniaFact]
    public async Task CustomBox_ShowsAsDialog_AndReturnsTheClickedButtonName()
    {
        var owner = new Window { Width = 300, Height = 200 };
        owner.ShowAndRunLayout();

        Task<string> result = MessageBoxPresenter.ShowCustomAsync(
            owner,
            new MessageBoxCustomParams
            {
                ButtonDefinitions = [new ButtonDefinition { Name = "Add to existing" }, new ButtonDefinition { Name = "Cancel", IsCancel = true }],
                ContentTitle = "Import Favorites",
                ContentMessage = "Add or replace?",
            }
        );

        ClickDialogButton(owner, "Add to existing");

        Assert.Equal("Add to existing", await result);
    }

    [AvaloniaFact]
    public async Task StandardBox_ShowsAsDialog_AndReturnsOk()
    {
        var owner = new Window { Width = 300, Height = 200 };
        owner.ShowAndRunLayout();

        Task<ButtonResult> result = MessageBoxPresenter.ShowStandardAsync(owner, "Import Favorites", "Imported 452 new favorite(s).", ButtonEnum.Ok);

        // The standard view keeps every button in the tree and shows only the ones its ButtonEnum names.
        ClickDialogButton(owner, b => b.IsEffectivelyVisible);

        Assert.Equal(ButtonResult.Ok, await result);
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
