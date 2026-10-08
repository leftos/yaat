using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The scenario-load overlay's state machine: which progress events it applies, when its table is final, and when it
/// closes itself or waits for the user.
/// </summary>
public class LoadOverlayViewModelTests
{
    private const string LoadA = "load-a";

    private static LoadStepDto Step(string id, string state, params string[] problems) => new(id, id, state, null, [.. problems]);

    private static ScenarioLoadProgressDto Progress(string loadId, int sequence, bool isComplete, params LoadStepDto[] steps) =>
        new(loadId, sequence, "OAK Ground 7", isComplete, [.. steps]);

    private static LoadScenarioResultDto Result(bool success, params LoadStepDto[] steps) =>
        new(success, "OAK Ground 7", "scenario-7", 0, 0, true, 1, "OAK", [], [], [], [], false) { Steps = [.. steps] };

    private static LoadOverlayViewModel Begun()
    {
        var overlay = new LoadOverlayViewModel();
        overlay.BeginLocal("oak-ground.json");
        return overlay;
    }

    [Fact]
    public void BeginLocal_ShowsReadScenarioRunning()
    {
        LoadOverlayViewModel overlay = Begun();

        Assert.True(overlay.IsOpen);
        Assert.False(overlay.IsComplete);
        Assert.False(overlay.CanClose);
        Assert.True(overlay.IsLoadInFlight);
        Assert.Equal("oak-ground.json", overlay.Title);
        LoadStepViewModel row = Assert.Single(overlay.Steps);
        Assert.Equal("Read scenario", row.Label);
        Assert.Equal("running", row.State);
        Assert.Equal("•", row.Glyph);
    }

    [Fact]
    public void StaleSequence_IsDropped()
    {
        LoadOverlayViewModel overlay = Begun();
        overlay.ApplyProgress(Progress(LoadA, 2, false, Step("read", "done"), Step("artcc", "running")));

        overlay.ApplyProgress(Progress(LoadA, 1, false, Step("read", "running")));
        overlay.ApplyProgress(Progress(LoadA, 2, false, Step("read", "failed")));

        Assert.Equal(["done", "running"], overlay.Steps.Select(s => s.State));
    }

    [Fact]
    public void OtherLoadId_IsIgnored()
    {
        LoadOverlayViewModel overlay = Begun();
        overlay.ApplyProgress(Progress(LoadA, 1, false, Step("read", "done")));

        overlay.ApplyProgress(Progress("load-b", 5, true, Step("read", "failed")));

        Assert.Equal("done", Assert.Single(overlay.Steps).State);
        Assert.False(overlay.IsComplete);
        Assert.True(overlay.IsOpen);
    }

    [Fact]
    public void AllDoneOrNotNeeded_ClosesItself()
    {
        LoadOverlayViewModel overlay = Begun();

        overlay.ApplyProgress(Progress(LoadA, 1, true, Step("read", "done"), Step("weather", "notNeeded")));

        Assert.True(overlay.IsComplete);
        Assert.False(overlay.IsOpen);
        Assert.False(overlay.IsLoadInFlight);
        Assert.False(overlay.CanClose);
    }

    [Fact]
    public void Warning_KeepsOpenUntilClosed()
    {
        LoadOverlayViewModel overlay = Begun();

        overlay.ApplyProgress(Progress(LoadA, 1, true, Step("read", "done"), Step("layouts", "warning", "SQL: no ground map on vNAS.")));

        Assert.True(overlay.IsOpen);
        Assert.True(overlay.CanClose);
        Assert.False(overlay.IsLoadInFlight);
        Assert.Equal(["SQL: no ground map on vNAS."], overlay.Steps[1].Problems);
        Assert.True(overlay.CloseCommand.CanExecute(null));

        overlay.CloseCommand.Execute(null);

        Assert.False(overlay.IsOpen);
    }

    [Fact]
    public void Failed_KeepsOpenUntilClosed()
    {
        LoadOverlayViewModel overlay = Begun();

        overlay.ApplyProgress(Progress(LoadA, 1, true, Step("read", "failed", "The scenario JSON could not be read: bad.")));

        Assert.True(overlay.IsOpen);
        Assert.True(overlay.CanClose);
        Assert.Equal("✗", Assert.Single(overlay.Steps).Glyph);

        overlay.CloseCommand.Execute(null);

        Assert.False(overlay.IsOpen);
    }

    [Fact]
    public void CannotCloseBeforeComplete()
    {
        LoadOverlayViewModel overlay = Begun();

        overlay.ApplyProgress(Progress(LoadA, 1, false, Step("read", "done"), Step("layouts", "warning", "SQL: no ground map on vNAS.")));

        Assert.False(overlay.CanClose);
        Assert.False(overlay.CloseCommand.CanExecute(null));
        Assert.True(overlay.IsOpen);
    }

    [Fact]
    public void ResultSteps_RenderFinalTable_WhenNoCompleteEvent()
    {
        LoadOverlayViewModel overlay = Begun();
        overlay.ApplyProgress(Progress(LoadA, 1, false, Step("read", "done"), Step("aircraft", "running")));

        overlay.ApplyResult(Result(false, Step("read", "done"), Step("aircraft", "done"), Step("populate", "failed", "The room was closed.")));

        Assert.Equal(["done", "done", "failed"], overlay.Steps.Select(s => s.State));
        Assert.True(overlay.IsComplete);
        Assert.True(overlay.IsOpen);
        Assert.True(overlay.CanClose);
    }

    [Fact]
    public void LateEvent_AfterCompleteTable_IsIgnored()
    {
        LoadOverlayViewModel overlay = Begun();
        overlay.ApplyResult(Result(true, Step("read", "done"), Step("layouts", "warning", "SQL: no ground map on vNAS.")));

        overlay.ApplyProgress(Progress(LoadA, 9, false, Step("read", "running")));

        Assert.Equal(["done", "warning"], overlay.Steps.Select(s => s.State));
        Assert.True(overlay.IsComplete);
        Assert.True(overlay.CanClose);
    }

    /// <summary>A complete event's table is final: the RPC result that follows does not replace it.</summary>
    [Fact]
    public void CompleteEvent_ThenResult_KeepsEventTable()
    {
        LoadOverlayViewModel overlay = Begun();
        overlay.ApplyProgress(Progress(LoadA, 2, true, Step("read", "done"), Step("layouts", "warning", "SQL: no ground map on vNAS.")));

        overlay.ApplyResult(Result(false, Step("read", "done"), Step("layouts", "done"), Step("populate", "failed", "The room was closed.")));

        Assert.Equal(["done", "warning"], overlay.Steps.Select(s => s.State));
        Assert.Equal(["SQL: no ground map on vNAS."], overlay.Steps[1].Problems);
        Assert.True(overlay.CanClose);
    }

    [Fact]
    public void Refusal_ClosesOverlay()
    {
        LoadOverlayViewModel overlay = Begun();

        overlay.ApplyRefusal();

        Assert.False(overlay.IsOpen);
        Assert.False(overlay.IsLoadInFlight);
        Assert.False(overlay.CanClose);
    }
}
