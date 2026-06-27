# RPO Limited-Access Mode + VATUSA ARTCC Auto-Fill

Two coupled changes to the connect/identity flow, both spanning `yaat` + `yaat-server`.

## Status

VATUSA ARTCC auto-fill (Part C) — **shipped** (yaat-server `c62d544c`, yaat `58f0f91e`). RPO limited
access (Parts A + B) + the unified Room Members modal — **implemented**, pending commit. Server: connect
gate relaxed, capability gating (`RequireMentorOrInstructor` on CreateRoom/LoadScenario/Unload*/KickMember),
`CanJoinRoomCore` invite gate, RPO lobby + `PullRpo` + `RpoLobbyChanged`, `TrainingRoom.InvitedCids`.
Client: `IsLimitedRpo` gating, waiting overlay, merged Members + Students into one Room Members modal with
CRC + YAAT lobbies.

## Confirmed decisions

1. **Same desktop app, rating-tiered.** The existing YAAT desktop client admits any signed-in
   VATSIM controller. Mentors/instructors get full powers; non-mentors run a **limited "RPO" mode**.
   No separate RPO app or clientKind.
2. **In-room powers: everything but create/load (plus two footgun exceptions).** Once an RPO is in a
   room they have all in-room controls — commands, pause/resume, sim rate, rewind, weather, auto-*
   settings. Mentor/instructor-only: `CreateRoom`, `LoadScenario`, **`KickMember`/room-retire**, and
   **`UnloadScenarioAircraft`** (an RPO must not evict the host or wipe the loaded scenario).
3. **Mentor pulls from a lobby.** Connected RPOs sit in an RPO lobby (mirroring the CRC lobby). A
   mentor pulls one into their room; the RPO cannot self-join an uninvited room.
4. **Silent home ARTCC, no override.** Resolve ARTCC from VATUSA's home facility, fall back to the
   VATSIM `subdivision` claim, and only prompt when neither resolves. Remove the ARTCC entry field
   from the normal desktop + web flows; not user-editable once resolved.

## Current state (verified)

- Connect gate `TrainingHubAccessHandler` (`yaat-server/src/Yaat.Server/Auth/TrainingHubAccessRequirement.cs:26`)
  rejects non-mentor/non-instructor `main` clients at negotiate; vStrips/vTDLS exempt via `clientKind`.
- Only `CreateRoom` (`TrainingHub.cs:137`) and `LoadScenario` (`TrainingHub.cs:336`) call
  `RequireMentorOrInstructor()` (`TrainingHub.cs:76`). All other in-room methods are ungated beyond
  "must be in a room."
- `RoomMember(Cid, Initials, ArtccId, Kind)` (`TrainingRoom.cs:177`) — no rating/mentor/privilege
  field; creator stored but never checked. No owner concept.
- RPO identity = `AS <tcp>` → `ActivePositionByConnection` (`TrackCommandHandler.cs:96`). No
  structural primary-vs-RPO distinction.
- No desktop "pull": only `PullCrcClient` (`TrainingHub.cs:1328`) exists; desktop clients self-join
  after a `RoomAvailableForCid` hint (`TrainingHub.cs:296`, client `MainViewModel.Rooms.cs:611`).
- Client holds `VatsimIdentity{Cid,Name,Rating,Subdivision,IsMentor}` (`VatsimAuthClient.cs:15`) but
  uses no capability flag — shows Create/Load regardless; only learns "no" via `HubException`.
- `VatusaService.IsMentorAsync` (`VatusaService.cs:35`) calls `GET /v2/user/{cid}` and reads **only**
  `data.isMentor`. The response also carries `data.facility` (home ARTCC) + `data.visiting_facilities`.
- ARTCC today: `UserPreferences.ArtccId`, pre-filled once from `subdivision` if blank
  (`MainViewModel.Rooms.cs:36`); web forms in the two `wwwroot/index.html` files. Server consumes
  `CreatorArtccId` for scenario-catalog fetch + NEXRAD/CRC facility; post-load `scenario.ArtccId`
  governs all aviation logic; `RoomMember.ArtccId` is display-only.

---

## Part A — Connect gate → capability tier (server)

- [ ] **Relax the connect gate.** `TrainingHubAccessHandler` succeeds for **any authenticated**
  principal (the mentor/instructor requirement moves off the connect path). `[Authorize]` still
  requires a valid YAAT session token; `token_use=access` enforcement and dev-bypass are unchanged.
  The `clientKind` vStrips/vTDLS special-case becomes redundant and is removed (subsumed).
- [ ] Add a server-side capability helper already present as `CallerIsMentorOrInstructor`
  (`TrainingHub.cs:72`). Keep `CreateRoom`/`LoadScenario` gated by `RequireMentorOrInstructor()`.
- [ ] **Gate `JoinRoom` for non-mentors:** a non-mentor may join a room only if their CID has been
  **invited** (pulled) to it. Mentors/instructors join freely (unchanged). Unsolicited non-mentor
  `JoinRoom` → `HubException("Wait for an instructor to add you.")`.
- [ ] Gate `KickMember`/room-retire and `UnloadScenarioAircraft` with `RequireMentorOrInstructor()`
  (confirmed) — an RPO must not evict the host or wipe the scenario. Everything else stays open to
  RPOs per decision #2.

## Part B — RPO lobby + pull (server + both clients)

- [ ] **RPO lobby (server).** Track connected non-mentor training-hub clients that aren't in a room.
  Mirror the CRC lobby: `GetRpoLobbyClients()` (mentor-only) + `RpoLobbyChanged` broadcast to room
  mentors. Entry carries `{ConnectionId, Cid, Name, Rating}`.
- [ ] **Invite store.** Per-room set of invited CIDs (parallel to `ConnectedClientIds`). Populated by
  pull, checked by the gated `JoinRoom`, cleared on leave/kick.
- [ ] **`PullRpo(connectionId)` (server, mentor-only).** Records the invite for that CID→room and
  pushes `RoomAvailableForCid(roomId)` to the RPO's connection. Reuses the existing client auto-join
  path. (Symmetric `KickMember` already removes them.)
- [ ] **Mentor UI (desktop).** A "Pull RPO" list in the room view (alongside the existing CRC pull
  list) showing lobby RPOs; clicking pulls. Driven by `GetRpoLobbyClients` + `RpoLobbyChanged`. No
  Student/RPO role distinction — any non-mentor YAAT client pulled in is an RPO with the in-room
  capabilities above. CRC trainees keep their existing separate pull flow.
- [ ] **RPO UI (desktop).** Client computes `IsLimitedRpo = !(identity.IsMentor ||
  ScenarioRatingClassifier.IsInstructorOrAbove(identity.Rating))` from the local `VatsimIdentity`
  (server still enforces). When limited: hide Create Room + Load Scenario + room-list join; show a
  "Connected as RPO — waiting for an instructor to add you" state. On `RoomAvailableForCid`, the
  existing `OnRoomAvailableForCid` auto-joins.

## Part C — VATUSA ARTCC auto-fill (server + both clients + web)

- [ ] **Extend VATUSA read.** Replace `IsMentorAsync` with a single `GetUserAsync(cid)` returning
  `{bool IsMentor, string? HomeArtcc}` from the same `/v2/user/{cid}` call (`data.isMentor` +
  `data.facility`). Fail-closed unchanged (no facility on error).
- [ ] **Resolve + bake into the JWT.** In `AuthEndpoints`, set `artcc = vatusaHome ?? subdivision`.
  Add an `artcc` claim in `YaatTokenService.Create` when non-empty. (Keep `subdivision` claim as-is.)
- [ ] **Surface to clients.** Add `artcc` to the `/auth/token` + `/auth/exchange` session payloads
  (`AuthEndpoints.SessionPayload`) and the desktop `VatsimIdentity`.
- [ ] **Desktop: drop the field.** Remove the ARTCC entry from `ConnectWindow`/Settings. On sign-in,
  set `UserPreferences.ArtccId` from the `artcc` claim (authoritative). Only if `artcc` is absent,
  show a minimal fallback prompt. `SendCommand`/`JoinRoom`/`CreateRoom` keep passing the resolved value.
- [ ] **Web: drop the field.** Remove the ARTCC `<input>` from both `wwwroot/index.html` landing
  forms; use `session.artcc`. Keep a fallback field shown only when `session.artcc` is empty.
- [ ] **Known tradeoff (accepted):** "no override" means a controller can't run another ARTCC's
  scenario catalog (e.g. a ZOA mentor can't load ZLA). Flagged at decision time.

## Part D — Docs & tests

- [ ] Tests: connect-gate now admits non-mentors; non-mentor `JoinRoom` rejected unless invited;
  `PullRpo` invite→join round-trip; `CreateRoom`/`LoadScenario` still rejected for RPOs; VATUSA
  `GetUserAsync` parses `facility`; `artcc` claim minted with VATUSA-home and subdivision-fallback.
- [ ] Update `TrainingHubAccessHandlerTests` for the relaxed gate; cross-repo `test-all.ps1` green
  (hub signature changes touch both repos).
- [ ] Docs: `docs/vatsim-auth.md`, `docs/training-hub-contract.md`, `docs/server-rooms-and-hub.md`,
  `docs/architecture.md` (both repos); `USER_GUIDE.md` (RPO flow + no-ARTCC-prompt); memory note
  `vatsim_oauth_server_mediated.md`. CHANGELOG (client-facing).

## Posture note (confirmed)

Relaxing the deployed-server posture is intended: v0.8.0's "mentors and instructors only" becomes
"any authenticated VATSIM controller may connect, but is limited (lobby-only) until a mentor pulls
them." Footgun exceptions (kick/retire + unload) are locked to mentor/instructor.
