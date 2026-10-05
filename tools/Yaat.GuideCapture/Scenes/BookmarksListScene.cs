using Avalonia.Controls;
using Avalonia.VisualTree;
using Yaat.Client.ViewModels;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Simulation Controls > Bookmarks. The timeline-playback state
// with the timeline bar's Bookmarks list open, showing both bookmarks with
// their jump, rename and delete buttons.
internal sealed class BookmarksListScene : TimelineSceneBase
{
    public override string Name => "bookmarks-list";

    protected override int TabIndex => 0;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        await StagePlaybackAsync(vm, ctx);

        Control list = await SceneActions.OpenButtonFlyoutAsync(window, "Bookmarks ▾", TimeSpan.FromSeconds(5));
        await SceneActions.WaitUntilAsync(
            () =>
                list.GetVisualDescendants().OfType<ListBoxItem>().Count(item => item.IsArrangeValid && (item.Bounds.Width > 0)) == vm.Bookmarks.Count,
            TimeSpan.FromSeconds(5),
            $"the Bookmarks list to show its {vm.Bookmarks.Count} bookmarks"
        );
    }
}
