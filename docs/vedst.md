# vEDST on yaat-server

Signing vEDST in to a server, step by step (server owner and vEDST developer): [vedst-sign-in.md](vedst-sign-in.md).

## Context

The vEDST team (the ERAM EDST web client, github.com/vFlightDataSystems/VATSIM_EDST_frontend) wants to point vEDST at yaat-server instead of vNAS, for ERAM training in YAAT rooms. Jonah confirmed three things. vEDST uses the SignalR **JSON** hub protocol. It attaches to CRC's session with **GetSessions → JoinSession**, because only CRC may call StartSession. It needs **CORS for http://localhost:3000** while they test.

How yaat-server meets each of those:

- `/hubs/client` is a hand-rolled SignalR-over-WebSocket hub whose native encoding is MessagePack. `CrcClientState.RunAsync` reads the handshake's `"protocol"`, and a JSON connection is transcoded at the socket boundary by `CrcJsonTranscoder`.
- Dispatch matches method names exactly. vEDST sends `joinSession`, `subscribe` and `amendFlightPlan` in camelCase, so the transcoder resolves inbound targets case-insensitively and hands dispatch the canonical name.
- `GetSessions`, `JoinSession` and `LeaveSession` are real handlers (`CrcClientState.Join.cs`).
- vEDST opens the WebSocket directly with `?access_token=` and `skipNegotiation: true`, bypassing the negotiate/`?id=` path the hub uses to find the caller's CID. `CrcWebSocketHandler` authenticates such a *direct* connection from the token (`AuthenticateDirectAsync`).
- vEDST gets its token from vNAS-shaped endpoints. It reads environments from a config URL, then calls `{apiBaseUrl}/auth/login?code&redirectUrl&clientId` → `{nasToken, vatsimToken}` and `{apiBaseUrl}/auth/refresh?vatsimToken` → token. `VnasCompatEndpoints` serves them under `/vnas`.
- CORS is a named policy (`vnas`) from `Yaat:Vnas:AllowedOrigins`, applied to the `/vnas` endpoints and the CRC hub path.

Every callback vEDST registers is sent: `ReceiveFlightPlans`, `ReceiveEramTracks`, `ReceiveOpenPositions`, the matching `Delete*`, `HandleSessionStarted`/`Ended`, `SetSessionActive` and `HandleFsdConnectionStateChanged`. Every method it invokes is handled: `GetSessions`, `JoinSession`, `Subscribe`, `GenerateFrd`, `AmendFlightPlan`, `Set`/`DeleteHoldAnnotations`, `SendPrivateMessage`, `ProcessEramMessage`. The JS client matches handler names case-insensitively.

Decisions made: **transcode JSON at the socket boundary** rather than refactor all ~200 MessagePack write sites; **serve vNAS-shaped auth endpoints**, signed with YAAT's own tokens.

All code is in yaat-server. yaat gets only docs and plan edits.

## Steps

Shipped: step 2 (direct WebSocket auth), steps 3–4 (JSON handshake, framing and transcoder), step 6 (`/vnas` auth, config and CORS) and step 5's direct connections plus `JoinSession`/`LeaveSession` with the allowlist (B1, `CrcClientState.Join.cs`), the fan-out (B2) and `GenerateFrd` plus aircraft-addressed private messages (B3). Open: the end-to-end check under **Verification** against a real vEDST.

The Decisions section below overrides the steps where they differ. The vEDST source referred to below is `github.com/vFlightDataSystems/VATSIM_EDST_frontend`, cloned as the sibling `..\_ext\vFlightDataSystems\VATSIM_EDST_frontend` (under its `src/`: `contexts/HubContext`, `api/vNasDataApi`, `login/Login`, `redux/slices/authSlice`).

### 2. Direct WebSocket auth
In `CrcWebSocketHandler.Handle`, when there is no `?id=` negotiate token but there is an `?access_token=`, validate it with `YaatTokenService` (access tokens only, matching the `OnTokenValidated` check in `ServerApp.cs`). Take the CID from the validated `sub`, then resolve the room with `GetRoomForCid` as today. An invalid token closes the socket with a policy-violation status and a log line. The existing negotiate path is unchanged, so CRC keeps working.

### 3. Protocol-aware handshake and framing (`CrcClientState.RunAsync`)
- Parse the handshake JSON and record `_protocol` (`messagepack` | `json`). Reply `{}\x1e`. Reject an unknown protocol with `{"error":"..."}\x1e` and close.
- Replace the single 64 KB `ReceiveAsync` with a loop that reassembles a message until `EndOfMessage`. Both protocols benefit.
- For JSON, split frames on `\x1e`.
- `SendAsync(byte[])` stays the single outbound choke point. For a JSON connection it hands the framed MessagePack to the transcoder (step 4) and sends the result as a `Text` message.

### 4. `CrcJsonTranscoder` (new, `src/Yaat.Server/Protocol/`)
Built on System.Text.Json with camelCase names, a `JsonStringEnumConverter` that also accepts integers on read, and a `Topic` converter for `{category, facilityId, subset?, sectorId?}`.

- **Inbound.** Parse the JSON message: type 1 invocation, 6 ping, 7 close. Resolve `target` case-insensitively against a **method map**: target → the parameter types of that method's arguments.

  Deserialize each argument to its DTO, re-serialize the argument array with MessagePack (the options the handlers already read with, including `TopicFormatter`), and build the same `InvocationMessage` the MessagePack parser produces. Pass it to the existing `DispatchInvocationAsync`, which stays untouched. Record `invocationId → canonical target` so the completion can be typed.
  - Methods in the map: `GetServerConfiguration`, `GetSessions`, `JoinSession(JoinSessionDto)`, `LeaveSession`, `Subscribe(Topic)`, `Unsubscribe(Topic)`, `GenerateFrd`, `AmendFlightPlan`, `SetHoldAnnotations`, `DeleteHoldAnnotations`, `SendPrivateMessage`, `ProcessEramMessage`.
  - Any other target gets a SignalR error completion (`{"type":3,"invocationId":…,"error":"Method X is not supported over JSON"}`) and a warning log.
- **Outbound.** Split the framed MessagePack. For each frame:
  - An invocation (type 1): look up the **callback map** (target → argument types), deserialize the arguments with MessagePack to typed DTOs, and write a JSON invocation.
  - A completion (type 3): type the result from the recorded method.
  - A ping: `{"type":6}`.
  - A callback with no mapping is dropped with a once-per-target warning, which keeps the ERAM-only client quiet about STARS/ASDEX traffic it never subscribed to.
  - Callbacks in the map: `HandleSessionStarted(SessionInfoDto)`, `HandleSessionEnded(string,bool)`, `SetSessionActive(bool)`, `HandleFsdConnectionStateChanged(bool)`, `ReceiveFlightPlans(Topic, FlightPlanDto[])`, `DeleteFlightPlans(Topic,string[])`, `ReceiveEramTracks`, `DeleteEramTracks`, `ReceiveOpenPositions`, `DeleteOpenPositions`, `ReceiveTowerCabAircrafts`, `DeleteTowerCabAircrafts`.
- The DTO types are the existing `[MessagePackObject]` classes in the `CrcDtos` partial classes in `Dtos/`. Their PascalCase properties become camelCase. Enums marked `StringEnumFormatter` become strings. `GeoRegion` and other positional oddities get a converter only where vEDST's TS types show a different shape; to be checked against vEDST's `EramTrackDto` and `apiFlightplan` TypeScript types.

### 5. GetSessions / JoinSession / LeaveSession (joined secondary connection)
- **`CrcClientManager.FindPrimarySessionsByCid(cid)`.** Returns the CID's non-direct clients with a started session (an ARTCC id, a position and a registry entry) whose connection is not closing, oldest first. `FindClientByCid` and `FindAnyClientByCid` skip direct connections.
- **`BuildSessionInfo()`.** Factored out of `HandleStartSession` and given the live `IsActive`.
  - `GetSessions` returns `List<SessionInfoDto>` for the caller's CID.
- **`JoinSession(JoinSessionDto)`.**
  - Find the session with `FindByClientId`. It must belong to the same CID, and must not be this connection or a joined one; otherwise send an error completion.
  - Set `_primary` on the joiner. The joiner reads `_artccId`, `_currentPositionId`, `_displayName` and `_roomEngine` through the primary, so `ResolveEramSectorScope`/`ActingEramSector` and `HandleSubscribe` work unchanged.
  - Do not register a `CrcPositionEntry`, so attendance and OpenPositions are not double-counted.
  - Push `HandleFsdConnectionStateChanged(true)` and `SetSessionActive(isActive)`, never `HandleSessionStarted` (see Decisions), using the payload builders in `CrcClientState.Session.cs`.
- **Fan-out from the primary** (B2, shipped): as built, described in [crc-display-state.md](crc-display-state.md) (the joined-session paragraph). An unbind, room move or kick ends the joiners' session (`Session left the room`) rather than rebinding them; vEDST rejoins through `GetSessions`/`JoinSession`.
- **`LeaveSession`** detaches.
- Room-member and lobby DTOs exclude joined connections. The joiner's disconnect removes it from its primary's `_joined`.
- **UDP entity-history path.** ERAM target histories go out by UDP, looked up through the client's `ConnectionToken` (`CrcBroadcastService`). A direct-WebSocket client has no negotiate token, so it must degrade cleanly: skip it and log once. Check whether vEDST needs histories at all.

### 6. vNAS-shaped auth and config (`src/Yaat.Server/Auth/VnasCompatEndpoints.cs`, new)
- `GET /vnas/configuration` returns `{artccBoundariesUrl, artccAoisUrl, environments:[{name:"YAAT", apiBaseUrl:"<base>/vnas", clientHubUrl:"<base>/hubs/client", isSweatbox:false}]}`. The two URLs are copied from the live vNAS config at `https://configuration.vnas.vatsim.net/`, fetched and cached. The vEDST source (`authSlice` and `App`) reads nothing else.
- `GET /vnas/artccs/{id}` and `GET /vnas/airports/{id}` pass through to `https://data-api.vnas.vatsim.net/api/...` and keep the upstream status, so a 404 means "no info" as vEDST expects. vEDST reads `facility.neighboringFacilityIds` and `eramConfiguration.nasId` from these. If an existing ARTCC-config fetch helper can be reused, use it.
- **OAuth redirect bounce `GET /vnas/login?code`.** VATSIM Connect will not register a `localhost` redirect URI, so vEDST's redirect goes through yaat-server.
  - vEDST builds `redirect_uri` as `${VITE_DOMAIN}/login`, and `DOMAIN` is used nowhere else (vEDST's `constants` and `login/Login`). With `VITE_DOMAIN=https://yaat1.leftos.dev/vnas`, VATSIM sends the browser to `https://yaat1.leftos.dev/vnas/login?code=…`, an https URI on YAAT's own domain.
  - This endpoint redirects to the configured `Yaat:Vnas:LoginReturnUrl` (e.g. `http://localhost:3000/login`), carrying `code` through unchanged.
  - vEDST's `/login` page then reads `code` and calls `/vnas/auth/login` with `redirectUrl` = the yaat URI, which is exactly the `redirect_uri` VATSIM needs for the exchange.
  - vEDST sends no `state`, so the return target is a single configured value, never taken from the request. That keeps the endpoint from being an open redirect.
- `GET /vnas/auth/login?code&redirectUrl&clientId`:
  - vEDST logs in through its own VATSIM Connect client, **separate from YAAT's own** (`Yaat:Vatsim`). On yaat1 that is client 1985, "yaat1 vedst" (confidential: it needs a secret), whose redirect is `/vnas/login`.
    - New options: `Yaat:Vnas:ClientId`, `Yaat:Vnas:ClientSecret` and `Yaat:Vnas:LoginReturnUrl`.
    - Check `clientId` against `Yaat:Vnas:ClientId`, and `redirectUrl` against `<base>/vnas/login`.
    - The exchange uses the vnas client's id and secret. `VatsimAuthService.ExchangeCodeAsync` therefore takes the client credentials as well as the redirect URI, or a second configured instance is used; decide which in implementation.
    - The endpoints return 500 "not configured" when the `Yaat:Vnas` options are empty.
  - Exchange the code. `VatsimAuthService.ExchangeCodeAsync` gains required `redirectUri` and nullable `codeVerifier` parameters, and the existing callers pass their own values.
  - Apply VATUSA.
  - Return `{nasToken: access, vatsimToken: refresh}` as YAAT-signed tokens. vEDST keeps only `vatsimToken` and decodes its `exp`; the YAAT refresh JWT has one.
  - Errors come back as a JSON body, because vEDST calls `.json()` even on a non-OK response.
  - vEDST does not use PKCE (`login/Login`), so its exchange passes `codeVerifier: null` and depends on `client_secret`.
  - When VATSIM cannot be asked (a 5xx, 429 or 404 from userinfo, a 5xx or 429 from the token endpoint, a network failure or a timeout) the login and refresh answer 503 with `Retry-After: 30` and the same JSON error body (`vatsim_unavailable`), not a 500; see [vatsim-auth.md](vatsim-auth.md) ("VATSIM outage").
- `GET /vnas/auth/refresh?vatsimToken` validates the refresh token with the revocation check and returns a fresh access token as **plain text**, which vEDST reads with `r.text()`. It does not rotate, following the reasoning in `IssueAccessFromCookieAsync`.
- **CORS.** Add a named policy from `Yaat:Cors:AllowedOrigins`, set to `http://localhost:3000` in dev config. List the exact origins (no wildcard) and allow credentials, because `/auth/login` is fetched with `credentials: "include"`. Apply it to the `/vnas/*` endpoints only. The hub is a WebSocket, so CORS does not apply to it.
- **Manual steps.**
  - Done: VATSIM Connect client 1985 ("yaat1 vedst", confidential) exists, with redirect `https://yaat1.leftos.dev/vnas/login`.
  - Ships with the endpoint:
    - `docker-compose.yml` maps `Yaat__Vnas__ClientId=${VNAS_VATSIM_CLIENT_ID:-}`, `Yaat__Vnas__ClientSecret=${VNAS_VATSIM_CLIENT_SECRET:-}`, `Yaat__Vnas__LoginReturnUrl=${VNAS_LOGIN_RETURN_URL:-}`, `Yaat__Vnas__PublicBaseUrl=https://${YAAT_DOMAIN}` and `Yaat__Vnas__AllowedOrigins=${VNAS_ALLOWED_ORIGINS:-}`.
    - The deploy step (the four `VNAS_*` keys in `.env.yaat1`, the container recreate and the checks) is [vedst-sign-in.md, "Enable vEDST sign-in on a deployed server"](vedst-sign-in.md#enable-vedst-sign-in-on-a-deployed-server-server-owner). The user adds the secret, `VNAS_VATSIM_CLIENT_SECRET=…`, by hand; an agent never reads it.
  - Jonah: set `VITE_VNAS_CONFIG_URL=https://yaat1.leftos.dev/vnas/configuration`, `VITE_VATSIM_CLIENT_ID=1985` and `VITE_DOMAIN=https://yaat1.leftos.dev/vnas`. No vEDST code changes.

### 7. Tests (`tests/Yaat.Server.Tests`, TDD per step)
- `CrcJsonTranscoderTests`:
  - Inbound camelCase `subscribe`/`joinSession` turn into the MessagePack args the handlers read.
  - An outbound `ReceiveEramTracks`/`ReceiveFlightPlans` frame turns into the expected camelCase JSON with string enums.
  - An unknown method gets an error completion.
  - The frame splitter handles several frames and a partial frame.
- `CrcJoinSessionTests` (a partial class over several files, the fan-out tests in `CrcJoinFanOutTests.cs`), in the `CrcBindTickGateTests` harness style:
  - `GetSessions` lists the CID's primary, and not another CID's.
  - A FlightPlans/EramTracks subscribe after join returns the primary's ERAM data.
  - Activate, Deactivate, End and disconnect reach the joiner.
  - Joining with a wrong CID or an unknown id is rejected.
  - OpenPositions is not double-counted.
- `VnasCompatEndpointsTests`:
  - A refresh with a revoked or access token is rejected.
  - A redirect not on the allowlist is rejected.
  - The configuration document's shape is correct.
  - The CORS preflight passes for an allowed origin and fails for another.

### 8. Docs
- yaat-server: a section on the JSON path, joined sessions and the `/vnas` endpoints in `docs/crc-display-state.md`, a replacement for its "Wire encoding is MessagePack" statement, and `docs/vatsim-auth.md`.
- yaat: `docs/architecture.md`, the CHANGELOG line, a glossary entry for "joined session".

## Decisions and corrections

The exploration against the code and vEDST's source (`vFlightDataSystems/VATSIM_EDST_frontend`, main) settles these; where they differ from the steps above, they win.

- **Build order, three independent tracks:** W1 auth primitives then `/vnas` endpoints and CORS (`Auth/*`, `YaatOptions.cs`, appsettings, compose, `ServerApp.cs` services and middleware); W2 the JSON transcoder then handshake and framing (`Protocol/*`, new `Dtos/*`, `CrcClientState.cs` RunAsync/SendAsync), shipped.

  W3 is direct-client hygiene and `GetSessions`, then `JoinSession`/fan-out (`Hubs/*` in a new `CrcClientState.Join.cs`, the client-list consumers in `Simulation/*` and `TrainingHub.cs`).

  Direct WebSocket auth follows W1 and precedes the join step (both edit `CrcWebSocketHandler.cs`).
- **Joiner rights (user):** a whitelist enforced in `DispatchInvocationAsync` for every joined connection, MessagePack or JSON: `Subscribe`, `Unsubscribe`, `GetSessions`, `JoinSession`, `LeaveSession`, `GenerateFrd`, `AmendFlightPlan`, `SetHoldAnnotations`, `DeleteHoldAnnotations`, `SendPrivateMessage`, `ProcessEramMessage`. Start/End/Activate/Deactivate, `ChangeActive*`, `Secondary*` and `KillClient` are refused.
- **`JoinSession` pushes only `HandleFsdConnectionStateChanged(true)` and `SetSessionActive`**, never `HandleSessionStarted`: vEDST's `HandleSessionStarted` handler calls `joinSession` again. A repeat join to the same primary is an idempotent ack.
- **A waiting connection hears about a later session:** `StartSession` also pushes `HandleSessionStarted` to same-CID direct connections that have not joined. A direct connection with no room is allowed, and is kept out of the lobby list, `DevCrcAutoBind`, `HasBoundCrcClient`, `TrainingBroadcastService` and the `TrainingHub` client lists from connect time.
- **Joiners act as their primary:** `ResolveIdentity`, `GetPositionCallsign`, `SendStarsReadoutAreaAsync` and `BuildOwnPositionPayload` read the primary's registry entry.
- **`GenerateFrd` and aircraft-addressed private messages are implemented in this item (user):** `GenerateFrd` answers a fix/radial/distance from the navigation database (`FrdResolver`'s inverse), and `SendPrivateMessage(aircraftId, text)` reaches the aircraft's pilot, i.e. the RPO terminal line for that aircraft (the brief's design pass settles the exact surface).
- **CORS (user):** a configured list, `Yaat:Vnas:AllowedOrigins` bound from a comma-separated `VNAS_ALLOWED_ORIGINS`, defaulting to `http://localhost:3000`; applied to `/vnas` and the CRC hub path.
- **Config:** `Yaat:Vnas:ClientId`, `ClientSecret`, `LoginReturnUrl`, `PublicBaseUrl` (compose sets `https://${YAAT_DOMAIN}`; request-derived in development). `ExchangeCodeAsync` takes the client credentials, the redirect URI and the code verifier as required parameters (one shared `HttpClient`).

  The return target is the single configured `LoginReturnUrl`, validated at startup, never taken from the request. Endpoints are always mapped and answer 500 "not configured" when the keys are missing. `YaatTokenService` gains `ValidateAccessTokenAsync`.
- **Direct auth (shipped):** a direct connection authenticates with its `?access_token=` (`CrcWebSocketHandler.AuthenticateDirectAsync`) and takes that CID with no room of its own; a missing, blank or invalid token gets HTTP 401 before `AcceptWebSocketAsync`, logged without the token; an unknown hub protocol gets `{"error":…}\x1e` and close 1002.
- **Transcoder:** closed-world over the mapped DTO graphs, unmapped callbacks dropped with a once-per-target warning; new `JoinSessionDto`, `CreateOrAmendFlightPlanDto`, `ProcessEramMessageDto`, `EramMessageProcessingResultDto`; inbound enums accept numbers; every JSON frame is a Text message.
- **Direct connections (shipped as the first half of the join track):** a connection without the negotiate `?id=` is *direct* (`CrcClientState.IsDirect`); the manager's CID and room lookups skip it, `GetSessions` lists the caller's non-direct sessions oldest first, and a non-direct `StartSession` tells waiting same-CID direct connections.

  For the join step: the joiner allowlist applies to **every** direct connection, not only joined ones (a direct connection is never a primary), so a direct `StartSession` cannot register a position that `FindByPositionCallsign` would return ahead of the real CRC session.

  A joined connection carries no room of its own and reads it through `_primary` (the unbind paths `TrainingBroadcastService.EvictRoomClientsAsync`, `TrainingHub.UnbindAllCrcClients` and `KickCrcClient` see only non-direct clients). And `CrcBroadcastService.BroadcastToCrcClientsAsync` matches `targetCallsign` and `excludeClientId` on the connection itself, so decide there whether a callsign-targeted message reaches the joiner and whether a command sent from vEDST excludes its primary.
- **Several primaries per CID:** `GetSessions` returns all, oldest connection first.
- **ERAM pushes reach the joiner:** ERAM C3's `SM`/`SW` push private messages to the ERAM sessions of a sector or facility through `CrcClientManager.GetClientsForRoom`, which skips direct connections.

  The join step makes a joined connection receive whatever private message its primary receives. vEDST registers no `ReceivePrivateMessage` handler today and the JSON callback map lacks it, so the fan-out happens but no transcoder mapping is added until vEDST consumes it.
- **B1 rulings (shipped):** fields read through the primary are `_roomEngine`, `_artccId`, `_currentPositionId` and `_displayName` (a joiner never reaches `_realName`, `_role` or `_isActive`); identity lookups use the primary's client id; the session-log redaction skips joined connections only (a room-bound direct connection keeps its own redaction).

  The join ack and its two pushes go out in one buffer; switching primaries clears subscriptions; a direct connection's disconnect only leaves its session.
- **B3 rulings:** `HandleGenerateFrd` (`CrcClientState.Session.cs`) answers `FrdResolver.ToFrd(lat, lon, _navDb.GetFixTuples(), _navDb)` with the default 50 nm radius the ERAM route code uses (`CrcClientState.Eram.Route.cs`), `""` when no fix is in range (CRC and vEDST both treat `""` as failure).

  It reads no room, so a room-less direct connection gets an answer, and CRC gets a real FRD too (changelog it). vEDST calls it from its route, previous-route and template menus (its `hooks/useHubActions` in the clone above).

  `HandleSendPrivateMessage` (`CrcClientState.Messaging.cs`) resolves the recipient as an aircraft in the room first: a hit writes a **Chat** terminal entry on that aircraft's callsign (`BroadcastTerminalEntry`, recorded in the terminal log), sender the primary's position callsign, display-only (no pilot inbox, no reply path; vEDST registers no `ReceivePrivateMessage`).

  Otherwise it takes the controller path as today; a recipient that is neither an aircraft nor a known controller position returns an **error completion**. vEDST's `sendPrivateMessage` has no UI call site yet (its uplink menu entries are commented out), so the harness is its only caller.
- **B2, the fan-out (shipped):** the primary pushes through the joined-connection helpers in `CrcClientState.Join.cs` (`PushSessionActiveToJoined`, `EndJoinedSessions`, `SendJoinedEramViewChanges`) and a `SendSessionActiveAsync` wrapper over `BuildSetSessionActivePayload`.

  Fan out: `SetSessionActive` on Activate/Deactivate (`CrcClientState.Session.cs`); `HandleSessionEnded(reason, false)` from `HandleEndSession`, `ForceDisconnectAsync` (`CrcClientState.Info.cs`, `isForcible` true there) and the primary's disconnect, then detach (vEDST stops its hub on it); `HandleSessionEnded` on the primary's unbind from its room (the joiner has no room to read).

  Fan out too: per-joiner `FlightPlansForViewerChange` and conflict resend on `ChangeActiveEramPosition` (`CrcClientState.Secondary.cs`, `wasEramViewer` captured per joiner); the bind replay (`SendInitialDataForSubscriptionsAsync`, exposed apart from `TryBindToRoom`) when the primary binds; C3's private-message pushes.

  Last, `BroadcastToCrcClientsAsync`'s `targetCallsign` match on `(client.Primary ?? client).ClientId` (a callsign-targeted message reaches the joiner), with `excludeClientId` excluding only the sender.

  Replies to the connection's own call (`SendInfoReplyAsync`, `SendServerMessageAsync`, `SendServerErrorAsync`) are not fanned out.
- **Local end to end:** a development-only `/vnas/auth/dev-login`, gated like `/auth/dev`.
- **CORS test:** a policy-builder unit test; no new test-host package.
- The UDP history step is moot: vEDST subscribes only FlightPlans, EramTracks and OpenPositions, and the existing negotiate-token guard already skips a direct client.

## Verification
- `pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 30 -Slot heavy -- dotnet test ... --filter-class` for each new class, then `pwsh tools/test-all.ps1`.
- End to end: run yaat-server locally (5130), connect CRC to a room with an ERAM position, then run vEDST from source (`npm start` at :3000) with its config URL pointed at `http://localhost:5130/vnas/configuration`. Log in, check that vEDST joins and shows flight plans and tracks, amend a flight plan, and confirm CRC shows the amendment.
- `csharp-reviewer` on the diff. The aviation review is not owed, because no sim behaviour changes.
