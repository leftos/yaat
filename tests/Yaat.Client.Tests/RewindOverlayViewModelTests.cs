using Xunit;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// The rewind overlay: shown only once a jump has run past <see cref="RewindOverlayViewModel.ShowDelay"/>, hidden on the
/// jump's result or final progress, deaf to progress that arrives after it, and the source of "a jump is in flight" that
/// disables the timeline. Jumps are told apart by the room's jump id, never by target. The show delay is the injected
/// seam, released by hand; no test waits on the wall clock.
/// </summary>
public class RewindOverlayViewModelTests
{
    /// <summary>A show delay the test releases: each request queues one pending task, completed in order.</summary>
    private sealed class ManualDelay
    {
        private readonly Queue<TaskCompletionSource> _pending = new();

        public List<TimeSpan> Requested { get; } = [];

        public Task Delay(TimeSpan delay)
        {
            Requested.Add(delay);
            var tcs = new TaskCompletionSource();
            _pending.Enqueue(tcs);
            return tcs.Task;
        }

        public void ElapseNext() => _pending.Dequeue().SetResult();
    }

    /// <summary>A rewind call the test answers: records each target and returns once completed.</summary>
    private sealed class ManualSend
    {
        private readonly Queue<TaskCompletionSource<RewindOutcome>> _pending = new();

        public List<double> Targets { get; } = [];

        public Task<RewindOutcome> Send(double target)
        {
            Targets.Add(target);
            var tcs = new TaskCompletionSource<RewindOutcome>();
            _pending.Enqueue(tcs);
            return tcs.Task;
        }

        public void CompleteNext(double target) => _pending.Dequeue().SetResult(new RewindOutcome(true, $"Rewound to {target}"));
    }

    [Fact]
    public async Task AJumpEndingBeforeTheDelayNeverShows()
    {
        var delay = new ManualDelay();
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = delay.Delay };

        Task<RewindOutcome> jump = vm.RequestAsync(2530, "TS", send.Send);
        Assert.Equal([RewindOverlayViewModel.ShowDelay], delay.Requested);
        send.CompleteNext(2530);
        await jump;
        delay.ElapseNext();

        Assert.False(vm.IsOpen);
        Assert.False(vm.IsJumpInFlight);
    }

    [Fact]
    public void AJumpPastTheDelayShowsWithItsTargetAndWhoAndTheProgress()
    {
        var delay = new ManualDelay();
        var vm = new RewindOverlayViewModel { Delay = delay.Delay };

        vm.BeginJump(1, 2530, "JD", false);
        Assert.False(vm.IsOpen);
        delay.ElapseNext();

        Assert.True(vm.IsOpen);
        Assert.Equal("Rewinding to 42:10 — by JD", vm.Title);
        Assert.True(vm.IsIndeterminate);

        vm.ApplyProgress(1, 30, 75);
        Assert.False(vm.IsIndeterminate);
        Assert.Equal(0.4, vm.Progress, 6);
    }

    [Fact]
    public async Task TheOwnResultHidesTheOverlayAndDropsLateProgress()
    {
        var delay = new ManualDelay();
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = delay.Delay };

        Task<RewindOutcome> jump = vm.RequestAsync(2530, "TS", send.Send);
        vm.BeginJump(1, 2530, "TS", true);
        delay.ElapseNext();
        vm.ApplyProgress(1, 30, 75);
        Assert.True(vm.IsOpen);

        send.CompleteNext(2530);
        await jump;
        Assert.False(vm.IsOpen);

        vm.ApplyProgress(1, 60, 75);
        Assert.False(vm.IsOpen);
        Assert.False(vm.IsJumpInFlight);
        Assert.Equal(0.0, vm.Progress);
    }

    [Fact]
    public void AnotherMembersFinalProgressEndsTheirJump()
    {
        var delay = new ManualDelay();
        var vm = new RewindOverlayViewModel { Delay = delay.Delay };

        vm.BeginJump(1, 2530, "JD", false);
        delay.ElapseNext();
        Assert.True(vm.IsJumpInFlight);

        vm.ApplyProgress(1, 75, 75);

        Assert.False(vm.IsOpen);
        Assert.False(vm.IsJumpInFlight);
    }

    [Fact]
    public void OtherMembersJumpsWhoseFinalNoticeWasLostEndOnTheNextRoomState()
    {
        var vm = new RewindOverlayViewModel { Delay = new ManualDelay().Delay };

        vm.BeginJump(1, 2530, "JD", false);
        vm.BeginJump(2, 100, "AB", false);
        vm.ApplyRoomState();

        Assert.False(vm.IsJumpInFlight);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public async Task AnotherMembersStartDuringTheOwnJumpBeginsWhenTheOwnJumpEnds()
    {
        var delay = new ManualDelay();
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = delay.Delay };

        Task<RewindOutcome> jump = vm.RequestAsync(100, "TS", send.Send);
        vm.BeginJump(1, 100, "TS", true);
        vm.BeginJump(2, 200, "JD", false);
        Assert.Equal("Rewinding to 01:40 — by TS", vm.Title);

        send.CompleteNext(100);
        await jump;

        Assert.True(vm.IsJumpInFlight);
        Assert.Equal("Rewinding to 03:20 — by JD", vm.Title);
        delay.ElapseNext();
        delay.ElapseNext();
        Assert.True(vm.IsOpen);

        vm.ApplyProgress(2, 50, 50);
        Assert.False(vm.IsJumpInFlight);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public async Task AnotherMembersJumpWhoseFinalArrivedDuringTheOwnJumpNeverBegins()
    {
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = new ManualDelay().Delay };

        Task<RewindOutcome> jump = vm.RequestAsync(100, "TS", send.Send);
        vm.BeginJump(2, 200, "JD", false);
        vm.ApplyProgress(2, 50, 50);
        Assert.True(vm.IsJumpInFlight);

        send.CompleteNext(100);
        await jump;

        Assert.False(vm.IsJumpInFlight);
    }

    /// <summary>
    /// Two other members' jumps: a final notice ends only its own jump, so the jump still running keeps this client locked
    /// whichever order the notices arrive in.
    /// </summary>
    [Fact]
    public void AFinalNoticeEndsOnlyItsOwnJump()
    {
        var vm = new RewindOverlayViewModel { Delay = new ManualDelay().Delay };

        vm.BeginJump(1, 100, "AA", false);
        vm.ApplyProgress(1, 40, 40);
        vm.BeginJump(2, 200, "BB", false);
        vm.ApplyProgress(1, 40, 40); // a repeated or late final for A
        Assert.True(vm.IsJumpInFlight);
        Assert.Equal("Rewinding to 03:20 — by BB", vm.Title);

        vm.BeginJump(3, 300, "CC", false);
        vm.ApplyProgress(3, 10, 10);
        Assert.True(vm.IsJumpInFlight);
        Assert.Equal("Rewinding to 03:20 — by BB", vm.Title);

        vm.ApplyProgress(2, 70, 70);
        Assert.False(vm.IsJumpInFlight);
    }

    [Fact]
    public async Task ARequestWhileAnotherMembersJumpRunsIsRefusedAndSendsNothing()
    {
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = new ManualDelay().Delay };

        vm.BeginJump(1, 2530, "JD", false);
        RewindOutcome outcome = await vm.RequestAsync(100, "TS", send.Send);

        Assert.False(outcome.Rewound);
        Assert.Equal("Rewind unavailable while JD's jump runs", outcome.Status);
        Assert.Empty(send.Targets);
        Assert.True(vm.IsJumpInFlight);
    }

    [Fact]
    public async Task AJumpIsInFlightFromTheOwnCallOrAnotherMembersStartNotice()
    {
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = new ManualDelay().Delay };

        vm.BeginJump(1, 2530, "JD", false);
        Assert.True(vm.IsJumpInFlight);
        vm.ApplyProgress(1, 75, 75);
        Assert.False(vm.IsJumpInFlight);

        Task<RewindOutcome> jump = vm.RequestAsync(100, "TS", send.Send);
        Assert.True(vm.IsJumpInFlight);
        send.CompleteNext(100);
        await jump;
        Assert.False(vm.IsJumpInFlight);
    }

    /// <summary>The own jump's final progress hides the overlay but the jump stays in flight until the call returns.</summary>
    [Fact]
    public async Task TheOwnFinalProgressHidesTheOverlayButKeepsTheJumpInFlight()
    {
        var delay = new ManualDelay();
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = delay.Delay };

        Task<RewindOutcome> jump = vm.RequestAsync(2530, "TS", send.Send);
        vm.BeginJump(1, 2530, "TS", true);
        vm.ApplyProgress(1, 75, 75);
        delay.ElapseNext();

        Assert.False(vm.IsOpen);
        Assert.True(vm.IsJumpInFlight);
        send.CompleteNext(2530);
        await jump;
    }

    [Fact]
    public async Task TwoScrubsDuringOneJumpSendTheFirstThenOnlyTheLatest()
    {
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = new ManualDelay().Delay };

        Task<RewindOutcome> first = vm.RequestAsync(100, "TS", send.Send);
        Task<RewindOutcome> second = vm.RequestAsync(200, "TS", send.Send);
        Task<RewindOutcome> latest = vm.RequestAsync(300, "TS", send.Send);
        Assert.Equal([100.0], send.Targets);

        send.CompleteNext(100);
        await first;
        Assert.Equal([100.0, 300.0], send.Targets);
        Assert.True(vm.IsJumpInFlight);

        send.CompleteNext(300);
        RewindOutcome[] outcomes = await Task.WhenAll(second, latest);

        Assert.Equal([100.0, 300.0], send.Targets);
        Assert.All(outcomes, o => Assert.Equal("Rewound to 300", o.Status));
        Assert.False(vm.IsJumpInFlight);
    }

    [Fact]
    public async Task ProgressForAnotherJumpIsDropped()
    {
        var delay = new ManualDelay();
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = delay.Delay };

        Task<RewindOutcome> jump = vm.RequestAsync(300, "TS", send.Send);
        vm.BeginJump(1, 300, "TS", true);
        delay.ElapseNext();
        vm.ApplyProgress(2, 30, 100);

        Assert.True(vm.IsIndeterminate);
        Assert.Equal(0.0, vm.Progress);
        send.CompleteNext(300);
        await jump;
    }

    /// <summary>
    /// Another member jumps to the same target as this client's own jump, its start arriving after the own echo: the own
    /// jump is known by its id, so the other one is held, shown when the own call returns, and ended by its own final.
    /// </summary>
    [Fact]
    public async Task SameTargetRemoteStartAfterOwnEcho_EndsBothJumps()
    {
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = new ManualDelay().Delay };

        Task<RewindOutcome> jump = vm.RequestAsync(0, "AB", send.Send);
        vm.BeginJump(1, 0, "AB", true);
        vm.BeginJump(2, 0, "CD", false);
        vm.ApplyProgress(1, 10, 10);
        send.CompleteNext(0);
        await jump;

        Assert.True(vm.IsJumpInFlight);
        Assert.Contains("CD", vm.Title);

        vm.ApplyProgress(2, 10, 10);
        Assert.False(vm.IsJumpInFlight);
    }

    /// <summary>As <see cref="SameTargetRemoteStartAfterOwnEcho_EndsBothJumps"/>, the other member's start arriving first.</summary>
    [Fact]
    public async Task SameTargetRemoteStartBeforeOwnEcho_EndsBothJumps()
    {
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = new ManualDelay().Delay };

        Task<RewindOutcome> jump = vm.RequestAsync(0, "AB", send.Send);
        vm.BeginJump(2, 0, "CD", false);
        vm.BeginJump(1, 0, "AB", true);
        vm.ApplyProgress(1, 10, 10);
        send.CompleteNext(0);
        await jump;

        Assert.True(vm.IsJumpInFlight);
        Assert.Contains("CD", vm.Title);

        vm.ApplyProgress(2, 10, 10);
        Assert.False(vm.IsJumpInFlight);
    }

    /// <summary>A jump's final notice overtook its start: the late start never puts the jump in flight.</summary>
    [Fact]
    public void FinalProgressBeforeItsStart_LeavesNoJumpInFlight()
    {
        var vm = new RewindOverlayViewModel { Delay = new ManualDelay().Delay };

        vm.ApplyProgress(7, 30, 30);
        vm.BeginJump(7, 60, "CD", false);

        Assert.False(vm.IsJumpInFlight);
        Assert.False(vm.IsOpen);
    }

    /// <summary>
    /// The start of an earlier own call arrives after the coalesced call that followed it has been announced: ids rise in
    /// run order, so the lower id is an ended jump and its final notice does not close the running call's overlay.
    /// </summary>
    [Fact]
    public async Task LateOwnStartOfAnEarlierCall_DoesNotTakeOverTheCoalescedCall()
    {
        var delay = new ManualDelay();
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = delay.Delay };

        Task<RewindOutcome> first = vm.RequestAsync(100, "TS", send.Send);
        Task<RewindOutcome> second = vm.RequestAsync(200, "TS", send.Send);
        send.CompleteNext(100);
        await first;
        Assert.Equal([100.0, 200.0], send.Targets);

        vm.BeginJump(2, 200, "TS", true);
        vm.BeginJump(1, 100, "TS", true);
        vm.ApplyProgress(1, 10, 10);
        delay.ElapseNext();
        delay.ElapseNext();

        Assert.True(vm.IsOpen);
        Assert.True(vm.IsJumpInFlight);

        send.CompleteNext(200);
        await second;
        Assert.False(vm.IsJumpInFlight);
    }

    /// <summary>The own call's result overtook the room's echo of its start: the late echo and its progress change nothing.</summary>
    [Fact]
    public async Task OwnEchoAfterOwnResult_IsIgnored()
    {
        var send = new ManualSend();
        var vm = new RewindOverlayViewModel { Delay = new ManualDelay().Delay };

        Task<RewindOutcome> jump = vm.RequestAsync(0, "AB", send.Send);
        send.CompleteNext(0);
        await jump;

        vm.BeginJump(3, 0, "AB", true);
        vm.ApplyProgress(3, 5, 5);

        Assert.False(vm.IsJumpInFlight);
    }
}
