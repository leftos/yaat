# YAAT-1220: crc_launch on an injected clock, with an event-ended pipe reader

The design for making yaat-server's client-driver `crc_launch` / `crc_stop` path testable with no wall-clock bound, and for the output-loss defects found while reproducing its flaky tests. Paths are yaat-server's; line numbers are at yaat-server 14fe529e.

## Why

Under starvation of the launch scripts' `pwsh` processes, the product kills the script at `ScriptTimeout` (`tools/Yaat.ClientDriver.Mcp/Crc/CrcSession.cs:346`) before pwsh runs its first statement (`CrcToolsTests.cs:508`, 3 s) or before it exits (`:403`, `:310`, 20 s). With a 300 s limit the same starvation passes at 25.4 s: late, not stuck (suspend-rig reproduction, 0 s of burn).

The owner's rule (user-level `load-proof-tests`, "No test asserts a time"): tests wait on events; product timeouts under test run on an injected clock; the only wall-clock bound in a test is a hang guard of minutes that fails as "hung".

## Owner rulings

- Split by goal. YAAT-1220 is the clock seam, the new pipe reader, the timeout-message fix and the `crc_launch` test rewrites. The other defects below are their own issues.
- The pipe reader may poll on one fixed `TimeProvider.System` interval, named as such: it only bounds how stale a read is and never ends or fails anything. Tests never assert on it.

## Invariants (each one gets a named test)

- **INV-1. The caller's cancel wins.** A cancel of the caller's token before `LaunchAsync` returns throws `OperationCanceledException`, never a timeout `McpException`, and kills the script tree. Every catch tests the caller's own token first, then the CRC-exit token, then the deadline: `CrcSession.cs:356-359` (keep that order), `CrcPipeClient.cs:178`, `:187-191`, and the rewritten pipe wait. The drain takes the caller's token (`CrcSession.cs:316`, `:374`).
- **INV-1b. No `OperationCanceledException` from a product timer escapes.** Each one becomes an `McpException` (or a `TimeoutException` inside `CrcPipeClient`): `CrcSession.cs:360-366`, `:610-612`, `:650-653`; `CrcPipeClient.cs:178-186`; the new untimed connect.
- **INV-2. A clock that is never advanced never makes the launch fail.** Every bounded wait is a provider timer, never an elapsed-time comparison: `CrcSession.cs:345-346`, `:376-392`, `:586-587`, `:596`, `:606`, `:252`, `:263-264`, `:645`; `CrcPipeClient.cs:89`, `:170-171`. When a launch returns, every timer it armed is disposed (the test asserts the clock has none pending).
- **INV-3. The timeout message holds every line the script wrote before it died**, stderr included: kill, wait for the root's exit (`Process.Kill` is asynchronous), the same drain as a normal exit, then the snapshot (`CrcSession.cs:353-366`; today the snapshot at `:361` follows the kill at `:355` directly, and only stdout is in the message at `:365`).
- **INV-4. The launch never waits for end of output.** It ends on the script's exit plus what the pipes held then (`CrcSession.cs:315`, `:374-393`, `:322-331`).
- **INV-5. No line written before the script exited is lost**, whatever the readers' scheduling, the `CRC pid` line included (`CrcSession.cs:374-393`, `:396-412`, `:334`).
- **INV-6. The "pid line must be last" verdict depends only on what the script wrote** (`CrcSession.cs:334`, `:441-446`).
- **INV-7. Nothing outlives the launch.** The readers end and the read ends are closed before `RunLaunchScriptAsync` returns (today they end only when CRC exits, `CrcSession.cs:321-331`).
- **INV-8. The pipe wait ends as soon as CRC exits**, with the exit message: the exit as an event, not a 250 ms sample (`CrcSession.cs:589`); order caller cancel, then exit, then deadline, as `ProcessTools.cs:382` does.
- **INV-9 (unchanged).** Every kill of CRC passes the identity check, and the gate serialises launch, stop and send (`CrcSession.cs:531-552`, `:622`, `:667`, `:92`).

## The pipe reader: "exit, then empty"

CRC inherits the MCP's stdout/stderr pipe ends (`Process.Start` passes every inheritable handle; `tools/Yaat.CrcDriverHook/live-check.ps1:546`), so the readers never see end of output while CRC runs, and today every production launch ends its drain on the 500 ms quiet window (`DrainQuiet`, `CrcSession.cs:83`). The replacement never blocks in a read:

1. Each round: sample `launcher.HasExited`, then `PeekNamedPipe` each read end.
2. Read exactly the peeked byte count from the raw handle, unbuffered (never through `StandardOutput` or a buffered `FileStream`, whose buffer can hold bytes Peek no longer sees); decode with a stateful `Decoder`, split on LF, strip CR. `ERROR_BROKEN_PIPE` from Peek is end of stream.
3. If the script had exited at the start of the round, flush the partial line after reading what that round's Peek reported, and stop. Exact, because the script's bytes are all in the pipe once its process is signalled; bounded, because the last read takes only the peeked count.
4. Between rounds, wait for the first of the launcher's exit, the fixed `TimeProvider.System` read interval (owner ruling above), or the caller's token.
5. When the loop ends, close the read ends, so a descendant writing later gets a broken pipe instead of blocking on a full buffer.

The reader itself must call Peek: `PeekNamedPipe` can block on a synchronous handle while another thread sits in a blocking `ReadFile`, so the blocking-read design cannot be patched this way. Replaces `CrcSession.cs:308-311`, `:316`, `:318-331`, `:374-393`, `:396-412`; deletes `DrainQuiet` and `DrainLimit` (`:83`, `:86`); the timeout path joins the same loop before its snapshot (`:355-361`); `PeekNamedPipe` is a `LibraryImport` beside `:511-513`.

A line a descendant writes after the script exits is never read (today it is, inside the 500 ms window). Lines the script writes before exiting are all read, so `CrcLaunch_PidLineNotLast_Refused` becomes deterministic.

Also fix with it: the comments that claim CRC holds none of the caller's handles (`tools/Yaat.CrcDriverHook/launch-crc.ps1:79-80`, `CrcSession.cs:283-286`).

## Clock seam

A required `TimeProvider` primary-constructor parameter on `CrcSession` (`Program.cs:22` already registers `TimeProvider.System`, `:28` builds `CrcSession` from the container). `CrcPipeClient.ConnectAsync` already takes 6 positional parameters (`CrcPipeClient.cs:77-84`): fold its timings and the clock into a record (call sites `CrcSession.cs:268`, `CrcToolsTests.cs:728`, `CrcPipeClientTests.cs:46`, `:148`); the client keeps the clock for later `SendAsync` calls.

| Wait | Today | Rewrite |
|---|---|---|
| ScriptTimeout | linked CTS + `CancelAfter` (`CrcSession.cs:345-346`) | `new CancellationTokenSource(ScriptTimeout, clock)` linked with the caller's token, armed before `Process.Start` (`:303`) |
| Script / CRC exit | `WaitForExitAsync` under a token | always through a token; StopWait as `new CancellationTokenSource(StopWait, clock)` (`:645`); after a kill that threw (`:457`), skip the wait |
| DrainQuiet / DrainLimit | `Stopwatch` + `Task.Delay` (`:376-392`) | removed by the reader above |
| PipeWait | `Stopwatch` (`:586-587`) | `new CancellationTokenSource(PipeWait, clock)` |
| Connect attempt | `ConnectAsync(int)` on the pipe's own tick count (`CrcPipeClient.cs:89`) | `pipe.ConnectAsync(token)` (infinite timeout) linked with `new CancellationTokenSource(connectTimeout, clock)`; that timer firing (caller's token not cancelled) becomes `TimeoutException` (keeps `CrcPipeClient.cs:75`); the pipe wait passes `Timeout.InfiniteTimeSpan` and links the PipeWait and CRC-exit tokens; the 250 ms attempts and the `TimeoutException` catch (`CrcSession.cs:598-601`) go |
| Hello TIMEOUT retry | `Task.Delay` (`:606`) | `Task.Delay(PipePollInterval, clock, token)` |
| Read timeouts | `CancelAfter` (`CrcPipeClient.cs:170-171`) | provider CTS linked with the caller's token |
| Reconnect / BUSY delay | `:252`, `:264` / `:263` | the connect rewrite / `Task.Delay(BusyRetryDelay, clock, ct)` |

Test clock: `HeldClock` (`tests/Yaat.ClientDriver.Mcp.Tests/RecordingFakes.cs:44-148`), the project's existing pattern (see an event, assert `Pending`, `Fire`, as `BatchToolsTests.cs:212-230`), with two additions: a `WhenArmed(due)` task so a test waits on an event rather than polling `Pending`, and distinct timer durations in the test options, since `Fire` matches by duration and `ShortWaits`' PipeWait (15 s) equals the hello read timeout (15 s, `CrcSession.cs:51`).

## Tests

Break the rule today: `CrcToolsTests.cs:337` (`elapsed < ScriptTimeout`, Stopwatch `:314`) and `:520` (`elapsed < 20 s`, Stopwatch `:512`; the name at `:508` says "Within"). Depend on real-time product timeouts: `:510` (3 s ScriptTimeout), `ShortWaits` (`:75-76`) under `:310`, `:343`, `:372`, `:403`, `:414`; `:263-272` and `:274-282` (stub delays raced against read timeouts, `StubCrcHook.cs:236`); `:227-249` (BUSY delay, reconnect timeout); the `:727-728` helper (5 s connect + hello, under every `Driven` test); `ProbePwshExe` (`:684-703`, a 30 s poll; `QueryFullProcessImageName` on the started pwsh answers at once, `CrcSession.cs:496-509`). Child scripts wait on a named event the test sets instead of `Start-Sleep 60` (`:39`, `:394`). Hang guards are minutes and fail as "hung" (the project uses 60 s today, `BatchToolsTests.cs:27`).

Per invariant:
- INV-1: cancel while the script runs (the script writes its pid, sets an event, sleeps) → `OperationCanceledException`, then await the script's exit; cancel during the pipe wait (after `WhenArmed(PipeWait)`) → the same, child exits; cancel then `Fire(ScriptTimeout)` → `OperationCanceledException`, not `McpException`.
- INV-1b: `Fire(PipeWait)` with a child that never serves → `McpException` "opened no pipe", child killed (await its exit).
- INV-2: `:310`, `:343`, `:372`, `:403`, `:414`, `:228`, `:240` on `HeldClock`, firing only the timer under test, each ending with `Assert.Empty(clock.Pending)`; `:263` and `:274` move to a stub that answers when the test releases it.
- INV-3: `:508` rewritten: the script writes two stdout lines and one stderr line, sets an event; the test fires ScriptTimeout and asserts every line is in the message.
- INV-4: `:310` without `:314`/`:337`; the child holds the handles, nothing is fired; finishing under the hang guard is the proof.
- INV-5: the script writes more than ~4 KB, then the pid line, with a child holding the handles. A red-first version needs the reader loop factored out and run over an in-process pipe with "exited" set before it reads.
- INV-6: keep `:403`, plus a variant with a handle-holding child running.
- INV-7: after the launch the child writes when released and records the error → broken pipe.
- INV-8: the child exits 3 without serving → "exited with code 3 before its driver pipe answered", nothing fired.

## Booked separately

Each its own Linear issue, naming YAAT-1220: the stdin leak (YAAT-1232, first), the MCP stdout inheritance (YAAT-1233), the orphaned CRC on cancel (YAAT-1234), the hello timeout's early kill (YAAT-1235), `crc_stop`'s untokened gate wait (YAAT-1236), the disposal gaps (YAAT-1237), the opaque reconnect timeout and misleading pipe log (YAAT-1238), and `launch_yaat`'s same wall-clock pattern (YAAT-1239). YAAT-1187 (bounding the start-up wait) overlaps.
