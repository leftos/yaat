---
status: accepted
date: 2026-09-02
---

# Host and run profile are two concepts, and "host" means one thing

Replay is currently a mode: flags the engine sets and individual steps consult. A mode flag read deep
inside a step is unenumerable — nobody can answer "how does replay differ from live?" without
grepping the tree. Replay becomes a run kind instead, so every difference is a named member of an
interface.

Two interfaces, not one. The **run profile** says which kind of run this is and what may legitimately
differ; the **host** supplies each step's arguments and consumes its results. Folding them together
would hand every step an object it can ask whether this is a replay, which is how the mode flags got
in and how the next one would.

"Host" is reclaimed to mean exactly that one thing. Six unrelated types use the word today —
`SimulationHostedService`, `HeadlessRoomHost`, `IHeadlessEpisodeHost`, `AiTestHost`, `RuleHost`,
`YaatHost` — and they are renamed to what they actually are. Documenting eight meanings instead would
leave "which host?" a question every reader and every agent has to ask, in the one subsystem where
being precise about which execution path you are on is the entire point.

## Consequences

- A rename spanning both repos and their tests, for a naming benefit rather than a behavioural one.
  Landed 2026-09-02. The names above are what those types were called; this is what they became:

  | was | is |
  |---|---|
  | `SimulationHostedService` | `RoomTickLoopService` |
  | `HeadlessRoomHost` / `HeadlessHostOptions` | `HeadlessRoom` / `HeadlessRoomOptions` |
  | `IHeadlessEpisodeHost` | `IEpisodeRoom` |
  | `YaatHost` / `YaatHostMarker` | `ServerApp` / `ServerAppMarker` |
  | `AiTestHost` | `AiTestFixture` |
  | `RuleHost` | `RuleProbe` |

  Three uses of the word were deliberately kept, because they are not this concept: ASP.NET's own
  vocabulary (`IHostedService` and the three background services that implement it, `builder.WebHost`,
  `HostOptions`), `VStripsSplitHost` (an Avalonia control that hosts a pane layout), and the aviation
  sense in "host ARTCC" and "enroute-hosted IFR plan".
- The eight sim-side replay guards collapse into the run profile. The server's
  `IsBroadcastSuppressed` stays: it suppresses broadcasts, which is a host concern.
- Vocabulary is fixed in [`CONTEXT.md`](../../CONTEXT.md) before the names are minted, not after.
