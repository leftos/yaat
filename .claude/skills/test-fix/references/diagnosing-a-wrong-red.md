# When the RED is wrong

Load this at Step 3 whenever the test fails for a reason other than its
assertion, and at Step 5 when a GREEN will not come.

The first two questions are always **"does the harness step time correctly?"**
and **"can the subject reach the asserted state at all?"** — answered by
instrumenting the run, not by reading the crash site and not by trusting a prior
hypothesis. A handoff doc's named culprit is a lead, not a fact; reproduce it
with logging before acting on it. One full investigation cycle has been burned
on a phantom bug that was taken on trust.

Worked examples: [`docs/e2e-tdd-issue-debugging.md`](../../../../docs/e2e-tdd-issue-debugging.md) §5.

## Seeing output at all

`dotnet test` swallows `Console` output and reports only summaries and
failures. To watch a run live, use the process form:

```bash
dotnet run --project tests/Yaat.Sim.Tests -c Release -- \
  --filter-method "*<TestName>*" --show-live-output on 2>&1 | tee .tmp/test-live.log
```

For Yaat.Sim logging inside a test, `SimLog` falls back to `NullLoggerFactory`
and swallows everything unless you opt in:

```csharp
SimLogBuilder.CreateForTest(output).EnableCategory("GroundNavigator", LogLevel.Debug).InitializeSimLog();
```

## The `Diag_` probe

For a replay-driven failure, a throwaway diagnostic fact beats walking many
`bug_bundle snapshot --at` calls: snapshots show what the **old** code did,
while a replay shows what the **fixed** code does.

Write a `Diag_<Callsign>` test that replays to the moment of interest, then
loops `ReplayOneSecond()` printing the fields you care about each tick. Delete
it once the real assertion exists.

Do not call `ReplayFromStartTo(t)` in a loop to step time — it re-runs the
whole replay from t=0 on every iteration. A mid-replay declination or
coordinate crash is almost always this driver bug in the test, not a sim bug:
check how the test steps time before reading the crash site.

## The blame setter

When a field takes an unexpected value mid-replay and reasoning from the code
is slow or wrong, make the property name its own mutator:

```csharp
private double _assignedAltitude;
public double AssignedAltitude
{
    get => _assignedAltitude;
    set
    {
        if (DebugBlame) { Console.WriteLine($"AssignedAltitude={value}\n{Environment.StackTrace}"); }
        _assignedAltitude = value;
    }
}
```

One run names the culprit. Remove it by inverting the edit (Step 5a) — never by
`git checkout` on a file that also carries the session's real changes.

## Is the asserted state reachable?

A replay E2E re-simulates every aircraft from t=0, so an unrelated regression
elsewhere can starve the subject: a ground-stack change can leave the test's
departure taxiing forever, and an "is airborne" assertion then fails for a taxi
reason that has nothing to do with the bug under test.

Before re-anchoring the test, probe phase, altitude and taxiway across the
window, and check the scenario's own expectations:

```bash
python tools/bug_bundle.py scenario --aircraft <CALLSIGN> <bundle>
python tools/bug_bundle.py history --callsign <CALLSIGN> <bundle>
```

Use sim-elapsed time from `history`, not wall-clock log timestamps — they do not
map linearly.

## The recording contains the user's own corrections

A recording captures what the controller actually did, including fix-up
commands issued after the bug appeared. Replaying past that point tests the
correction, not the bug. Replay to a cutoff **before** the fix-up command, then
step with `TickOneSecond` from there.
