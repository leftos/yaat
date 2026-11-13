using CommunityToolkit.Mvvm.ComponentModel;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The rewind overlay and the room's "a jump is in flight" state. A jump is this client's own <c>RewindTo</c> call
/// (<see cref="RequestAsync"/>) or another member's, announced by <c>RewindStarted</c> (<see cref="BeginJump"/>) and
/// ended by its own final <c>RewindProgress</c> (<see cref="ApplyProgress"/>) or the next room state. Every notice
/// carries the room's id for its jump, and jumps are told apart by that id alone, never by target: the start sent to
/// this client says whether the jump is its own. The server sends without waiting, so a jump's notices may reach this
/// client out of order; another member's jump announced while this client's own runs is shown once the own one ends,
/// and one whose final notice came first never enters flight.
/// </summary>
/// <remarks>
/// The overlay opens only once the shown jump has run <see cref="ShowDelay"/>, so a quick jump never flashes it.
/// Progress for an id no jump in flight has, or after its jump ended, is dropped. While this client's own jump runs,
/// further requests coalesce (the latest target is sent when the call returns, one call in flight at a time); while
/// another member's runs, requests are refused. Every method runs on the UI thread.
/// </remarks>
public partial class RewindOverlayViewModel : ObservableObject
{
    /// <summary>How long a jump runs before the overlay opens.</summary>
    public static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>How many ended jump ids are remembered for a start or final notice that arrives late.</summary>
    private const int EndedJumpIdLimit = 16;

    private readonly List<RemoteJump> _remoteJumps = [];
    private readonly List<long> _endedJumpIds = [];
    private int _shownId;
    private bool _isHidden;
    private bool _isOwnInFlight;
    private long? _ownJumpId;
    private PendingRequest? _pending;

    /// <summary>The wait before the overlay opens; a test seam.</summary>
    internal Func<TimeSpan, Task> Delay { get; set; } = wait => Task.Delay(wait);

    /// <summary>Whether the overlay is shown.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>"Rewinding to {mm:ss} — by {initials}".</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>The share of the shown jump's re-run done, 0 to 1.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>True until the shown jump's first progress arrives.</summary>
    [ObservableProperty]
    private bool _isIndeterminate = true;

    /// <summary>A jump is running in the room, this client's or another member's: the timeline is disabled.</summary>
    [ObservableProperty]
    private bool _isJumpInFlight;

    /// <summary>The initials of the member whose jump blocks this client's (the first one announced); null when none does.</summary>
    public string? RemoteJumpBy => _remoteJumps.Count > 0 ? _remoteJumps[0].ByName : null;

    /// <summary>The refusal of a jump asked for while <paramref name="who"/>'s runs.</summary>
    public static string RefusalFor(string who) => $"Rewind unavailable while {who}'s jump runs";

    /// <summary>
    /// Runs this client's jump to <paramref name="targetSeconds"/> through <paramref name="send"/>. While one is already in
    /// flight the target is held instead, replacing any target held before it, and sent when the running call returns;
    /// every held request completes with that call's outcome. While another member's jump runs it is refused.
    /// </summary>
    public Task<RewindOutcome> RequestAsync(double targetSeconds, string byName, Func<double, Task<RewindOutcome>> send)
    {
        if (_isOwnInFlight)
        {
            TaskCompletionSource<RewindOutcome> waiting = _pending?.Waiting ?? new TaskCompletionSource<RewindOutcome>();
            _pending = new PendingRequest(targetSeconds, byName, send, waiting);
            return waiting.Task;
        }

        if (RemoteJumpBy is { } who)
        {
            return Task.FromResult(new RewindOutcome(false, RefusalFor(who)));
        }

        return RunOwnAsync(targetSeconds, byName, send);
    }

    /// <summary>
    /// A <c>RewindStarted</c> for jump <paramref name="jumpId"/>. One marked <paramref name="isOwn"/> is this client's own
    /// jump: while its call runs the id is kept for that jump's progress unless a higher one already is (ids rise in run
    /// order, so a lower one is an earlier call's late notice); a lower id, or one arriving once the call has returned (the
    /// result overtook the notice), is remembered as ended. Another member's jump whose final notice already came is
    /// ignored; any other is held, shown at once when nothing else is, else once the jumps before it end.
    /// </summary>
    public void BeginJump(long jumpId, double targetSeconds, string byName, bool isOwn)
    {
        if (isOwn)
        {
            if (_isOwnInFlight && ((_ownJumpId is null) || (jumpId > _ownJumpId)))
            {
                _ownJumpId = jumpId;
            }
            else
            {
                RememberEnded(jumpId);
            }

            return;
        }

        if (_endedJumpIds.Remove(jumpId))
        {
            return;
        }

        _remoteJumps.Add(new RemoteJump(jumpId, targetSeconds, byName));
        if (!_isOwnInFlight && (_remoteJumps.Count == 1))
        {
            Show(targetSeconds, byName);
        }

        UpdateInFlight();
    }

    /// <summary>
    /// A <c>RewindProgress</c> for jump <paramref name="jumpId"/>: moves the bar of the shown jump, and on a final one (every
    /// second done) ends that jump only. This client's own jump ends on its result; its final notice just hides the overlay.
    /// A final notice for an id no jump holds is remembered, so a start arriving after it never puts that jump in flight.
    /// </summary>
    public void ApplyProgress(long jumpId, int doneSeconds, int totalSeconds)
    {
        bool isFinal = doneSeconds >= totalSeconds;
        if (TryApplyOwnProgress(jumpId, doneSeconds, totalSeconds, isFinal))
        {
            return;
        }

        int index = _remoteJumps.FindIndex(jump => jump.JumpId == jumpId);
        if (index < 0)
        {
            if (isFinal)
            {
                RememberEnded(jumpId);
            }

            return;
        }

        bool isShown = !_isOwnInFlight && (index == 0);
        if (isShown)
        {
            ShowProgress(doneSeconds, totalSeconds);
        }

        if (isFinal)
        {
            _remoteJumps.RemoveAt(index);
            if (isShown)
            {
                ShowNextOrHide();
            }

            UpdateInFlight();
        }
    }

    /// <summary>
    /// The room's state arrived (a join or reconnect): other members' jumps whose final notice was lost end, and the ended
    /// ids remembered for late notices are forgotten.
    /// </summary>
    public void ApplyRoomState()
    {
        _remoteJumps.Clear();
        _endedJumpIds.Clear();
        if (!_isOwnInFlight)
        {
            Hide();
        }

        UpdateInFlight();
    }

    /// <summary>
    /// Progress for this client's own running jump moves its bar, and its final one hides the overlay (the jump itself ends
    /// on the call's result). Returns false when the notice is not for the own running jump.
    /// </summary>
    private bool TryApplyOwnProgress(long jumpId, int doneSeconds, int totalSeconds, bool isFinal)
    {
        if (!_isOwnInFlight || (jumpId != _ownJumpId))
        {
            return false;
        }

        ShowProgress(doneSeconds, totalSeconds);
        if (isFinal)
        {
            _isHidden = true;
            IsOpen = false;
        }

        return true;
    }

    /// <summary>Remembers <paramref name="jumpId"/> as ended, keeping only the <see cref="EndedJumpIdLimit"/> most recent.</summary>
    private void RememberEnded(long jumpId)
    {
        if (_endedJumpIds.Contains(jumpId))
        {
            return;
        }

        _endedJumpIds.Add(jumpId);
        if (_endedJumpIds.Count > EndedJumpIdLimit)
        {
            _endedJumpIds.RemoveAt(0);
        }
    }

    /// <summary>
    /// Runs one own call. Its jump id is unknown until the room's start notice marked as this client's own arrives, and is
    /// forgotten when the call returns.
    /// </summary>
    private async Task<RewindOutcome> RunOwnAsync(double targetSeconds, string byName, Func<double, Task<RewindOutcome>> send)
    {
        _isOwnInFlight = true;
        _ownJumpId = null;
        Show(targetSeconds, byName);
        UpdateInFlight();
        try
        {
            return await send(targetSeconds);
        }
        finally
        {
            _isOwnInFlight = false;
            _ownJumpId = null;
            StartPending();
            if (!_isOwnInFlight)
            {
                ShowNextOrHide();
            }

            UpdateInFlight();
        }
    }

    private void StartPending()
    {
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;
        _ = CompleteAsync(pending.Waiting, RunOwnAsync(pending.TargetSeconds, pending.ByName, pending.Send));
    }

    private static async Task CompleteAsync(TaskCompletionSource<RewindOutcome> waiting, Task<RewindOutcome> run)
    {
        try
        {
            waiting.SetResult(await run);
        }
        catch (Exception ex)
        {
            waiting.SetException(ex);
        }
    }

    private void ShowProgress(int doneSeconds, int totalSeconds)
    {
        if (totalSeconds > 0)
        {
            IsIndeterminate = false;
            Progress = Math.Clamp((double)doneSeconds / totalSeconds, 0, 1);
        }
    }

    private void ShowNextOrHide()
    {
        if (_remoteJumps.Count > 0)
        {
            Show(_remoteJumps[0].TargetSeconds, _remoteJumps[0].ByName);
        }
        else
        {
            Hide();
        }
    }

    private void Show(double targetSeconds, string byName)
    {
        int shownId = ++_shownId;
        _isHidden = false;
        Title = $"Rewinding to {FormatTime(targetSeconds)} — by {byName}";
        Progress = 0;
        IsIndeterminate = true;
        IsOpen = false;
        _ = OpenAfterDelayAsync(shownId);
    }

    private void Hide()
    {
        _shownId++;
        IsOpen = false;
        Progress = 0;
        IsIndeterminate = true;
    }

    private void UpdateInFlight() => IsJumpInFlight = _isOwnInFlight || (_remoteJumps.Count > 0);

    private async Task OpenAfterDelayAsync(int shownId)
    {
        await Delay(ShowDelay);
        if ((shownId == _shownId) && !_isHidden)
        {
            IsOpen = true;
        }
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }

    private sealed record RemoteJump(long JumpId, double TargetSeconds, string ByName);

    private sealed record PendingRequest(
        double TargetSeconds,
        string ByName,
        Func<double, Task<RewindOutcome>> Send,
        TaskCompletionSource<RewindOutcome> Waiting
    );
}
