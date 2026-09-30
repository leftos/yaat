# vEDST on yaat-server

## Context

The vEDST team (the ERAM EDST web client, github.com/vFlightDataSystems/VATSIM_EDST_frontend) wants to point vEDST at yaat-server instead of vNAS, for ERAM training in YAAT rooms. Jonah confirmed three things. vEDST uses the SignalR **JSON** hub protocol. It attaches to CRC's session with **GetSessions → JoinSession**, because only CRC may call StartSession. It needs **CORS for http://localhost:3000** while they test.

What yaat-server has today:

- `/hubs/client` is a hand-rolled SignalR-over-WebSocket hub that speaks only MessagePack. `CrcClientState.RunAsync` fakes the handshake and never reads `"protocol"`.
- Dispatch matches method names exactly. vEDST sends `joinSession`, `subscribe` and `amendFlightPlan` in camelCase.
- `GetSessions`, `JoinSession` and `LeaveSession` are `NilAckStub` (`CrcClientState.cs:442-444`).
- vEDST opens the WebSocket directly with `?access_token=` and `skipNegotiation: true`. That bypasses the negotiate/`?id=` path the hub uses to find the caller's CID (`CrcWebSocketHandler.cs:37-50`).
- vEDST gets its token from vNAS-shaped endpoints. It reads environments from a config URL, then calls `{apiBaseUrl}/auth/login?code&redirectUrl&clientId` → `{nasToken, vatsimToken}` and `{apiBaseUrl}/auth/refresh?vatsimToken` → token.
- There is no CORS setup.

Every callback vEDST registers is already sent: `ReceiveFlightPlans`, `ReceiveEramTracks`, `ReceiveOpenPositions`, the matching `Delete*`, `HandleSessionStarted`/`Ended`, `SetSessionActive` and `HandleFsdConnectionStateChanged`. So is every method it invokes except GetSessions and JoinSession: `Subscribe`, `GenerateFrd`, `AmendFlightPlan`, `Set`/`DeleteHoldAnnotations`, `SendPrivateMessage`, `ProcessEramMessage`. The JS client matches handler names case-insensitively.

Decisions made: **transcode JSON at the socket boundary** rather than refactor all ~200 MessagePack write sites; **serve vNAS-shaped auth endpoints**, signed with YAAT's own tokens.

All code is in yaat-server (`D:\yaat-server`). yaat gets only docs and plan edits.

## Steps

Shipped: step 2 (direct WebSocket auth), steps 3–4 (JSON handshake, framing and transcoder), step 6 (`/vnas` auth, config and CORS) and step 5's direct connections plus `JoinSession`/`LeaveSession` with the allowlist (B1, `CrcClientState.Join.cs`); open: the fan-out (B2, below), `GenerateFrd`, aircraft-addressed private messages. The Decisions section below overrides the steps where they differ. The vEDST source referred to below is `github.com/vFlightDataSystems/VATSIM_EDST_frontend`, cloned as the sibling `..\vedst` (`src/contexts/HubContext.tsx`, `src/api/vNasDataApi.ts`, `src/login/Login.tsx`, `src/redux/slices/authSlice.ts`).

### 2. Direct WebSocket auth
In `CrcWebSocketHandler.Handle`, when there is no `?id=` negotiate token but there is an `?access_token=`, validate it with `YaatTokenService` (access tokens only, matching the `OnTokenValidated` check in `ServerApp.cs:124`). Take the CID from the validated `sub`, then resolve the room with `GetRoomForCid` as today. An invalid token closes the socket with a policy-violation status and a log line. The existing negotiate path is unchanged, so CRC keeps working.

### 3. Protocol-aware handshake and framing (`CrcClientState.RunAsync`)
- Parse the handshake JSON and record `_protocol` (`messagepack` | `json`). Reply `{}\x1e`. Reject an unknown protocol with `{"error":"..."}\x1e` and close.
- Replace the single 64 KB `ReceiveAsync` with a loop that reassembles a message until `EndOfMessage`. Both protocols benefit.
- For JSON, split frames on `\x1e`.
- `SendAsync(byte[])` stays the single outbound choke point. For a JSON connection it hands the framed MessagePack to the transcoder (step 4) and sends the result as a `Text` message.

### 4. `CrcJsonTranscoder` (new, `src/Yaat.Server/Protocol/`)
Built on System.Text.Json with camelCase names, a `JsonStringEnumConverter` that also accepts integers on read, and a `Topic` converter for `{category, facilityId, subset?, sectorId?}`.

- **Inbound.** Parse the JSON message: type 1 invocation, 6 ping, 7 close. Resolve `target` case-insensitively against a **method map**: target → the parameter types of that method's arguments. Deserialize each argument to its DTO, re-serialize the argument array with MessagePack (the options the handlers already read with, including `TopicFormatter`), and build the same `InvocationMessage` the MessagePack parser produces. Pass it to the existing `DispatchInvocationAsync`, which stays untouched. Record `invocationId → canonical target` so the completion can be typed.
  - Methods in the map: `GetSessions`, `JoinSession(JoinSessionDto)`, `LeaveSession`, `Subscribe(Topic)`, `Unsubscribe(Topic)`, `GenerateFrd`, `AmendFlightPlan`, `SetHoldAnnotations`, `DeleteHoldAnnotations`, `SendPrivateMessage`, `ProcessEramMessage`.
  - Any other target gets a SignalR error completion (`{"type":3,"invocationId":…,"error":"Method X is not supported over JSON"}`) and a warning log.
- **Outbound.** Split the framed MessagePack. For each frame:
  - An invocation (type 1): look up the **callback map** (target → argument types), deserialize the arguments with MessagePack to typed DTOs, and write a JSON invocation.
  - A completion (type 3): type the result from the recorded method.
  - A ping: `{"type":6}`.
  - A callback with no mapping is dropped with a once-per-target warning, which keeps the ERAM-only client quiet about STARS/ASDEX traffic it never subscribed to.
  - Callbacks in the map: `HandleSessionStarted(SessionInfoDto)`, `HandleSessionEnded(string,bool)`, `SetSessionActive(bool)`, `HandleFsdConnectionStateChanged(bool)`, `ReceiveFlightPlans(Topic, FlightPlanDto[])`, `DeleteFlightPlans(Topic,string[])`, `ReceiveEramTracks`, `DeleteEramTracks`, `ReceiveOpenPositions`, `DeleteOpenPositions`, plus anything a mapped method pushes (to be confirmed by reading those handlers).
- The DTO types are the existing `[MessagePackObject]` classes in `Dtos/CrcDtos*.cs`. Their PascalCase properties become camelCase. Enums marked `StringEnumFormatter` become strings. `GeoRegion` and other positional oddities get a converter only where vEDST's TS types show a different shape; to be checked against `EramTrackDto.ts` and `apiFlightplan.ts`.

### 5. GetSessions / JoinSession / LeaveSession (joined secondary connection)
- **`CrcClientManager.FindPrimarySessionsByCid(cid)`.** Returns clients with a started session (non-null `_artccId`, a registry entry) and no `_primary`. `FindClientByCid` and `FindAnyClientByCid` skip joined clients.
- **`BuildSessionInfo()`.** Factored out of `HandleStartSession` and given the live `IsActive`.
  - `GetSessions` returns `List<SessionInfoDto>` for the caller's CID.
- **`JoinSession(JoinSessionDto)`.**
  - Find the session with `FindByClientId`. It must belong to the same CID, and must not be this connection or a joined one; otherwise send an error completion.
  - Set `_primary` on the joiner. The joiner reads `_artccId`, `_currentPositionId`, `_role`, `_realName`, `_isActive` and `_roomEngine` through `_primary ?? this`, so `ResolveEramSectorScope`/`ActingEramSector` and `HandleSubscribe` work unchanged.
  - Do not register a `CrcPositionEntry`, so attendance and OpenPositions are not double-counted.
  - Push `HandleFsdConnectionStateChanged(true)`, `HandleSessionStarted(info)` and `SetSessionActive(isActive)` using the helpers at `Session.cs:763-799`.
- **Fan-out from the primary** (B2, shipped): as built, described in [crc-display-state.md](../crc-display-state.md) (the joined-session paragraph). An unbind, room move or kick ends the joiners' session (`Session left the room`) rather than rebinding them; vEDST rejoins through `GetSessions`/`JoinSession`.
- **`LeaveSession`** detaches.
- Room-member and lobby DTOs exclude joined connections. The joiner's disconnect removes it from its primary's `_joined`.
- **UDP entity-history path.** ERAM target histories go out by UDP, looked up through the client's `ConnectionToken` (`CrcBroadcastService.cs:2364`). A direct-WebSocket client has no negotiate token, so it must degrade cleanly: skip it and log once. Check whether vEDST needs histories at all.

### 6. vNAS-shaped auth and config (`src/Yaat.Server/Auth/VnasCompatEndpoints.cs`, new)
- `GET /vnas/configuration` returns `{artccBoundariesUrl, artccAoisUrl, environments:[{name:"YAAT", apiBaseUrl:"<base>/vnas", clientHubUrl:"<base>/hubs/client", isSweatbox:false}]}`. The two URLs are copied from the live vNAS config at `https://configuration.vnas.vatsim.net/`, fetched and cached. The vEDST source (`authSlice.ts`, `App.tsx`) reads nothing else.
- `GET /vnas/artccs/{id}` and `GET /vnas/airports/{id}` pass through to `https://data-api.vnas.vatsim.net/api/...` and keep the upstream status, so a 404 means "no info" as vEDST expects. vEDST reads `facility.neighboringFacilityIds` and `eramConfiguration.nasId` from these. If an existing ARTCC-config fetch helper can be reused, use it.
- **OAuth redirect bounce `GET /vnas/login?code`.** VATSIM Connect will not register a `localhost` redirect URI, so vEDST's redirect goes through yaat-server.
  - vEDST builds `redirect_uri` as `${VITE_DOMAIN}/login`, and `DOMAIN` is used nowhere else (`constants.ts`, `Login.tsx`). With `VITE_DOMAIN=https://yaat1.leftos.dev/vnas`, VATSIM sends the browser to `https://yaat1.leftos.dev/vnas/login?code=…`, an https URI on YAAT's own domain.
  - This endpoint redirects to the configured `Yaat:Vnas:LoginReturnUrl` (e.g. `http://localhost:3000/login`), carrying `code` through unchanged.
  - vEDST's `/login` page then reads `code` and calls `/vnas/auth/login` with `redirectUrl` = the yaat URI, which is exactly the `redirect_uri` VATSIM needs for the exchange.
  - vEDST sends no `state`, so the return target is a single configured value, never taken from the request. That keeps the endpoint from being an open redirect.
- `GET /vnas/auth/login?code&redirectUrl&clientId`:
  - vEDST logs in through its own VATSIM Connect client, **separate from YAAT's own** (`Yaat:Vatsim`). On yaat1 that is client 1974, "yaat1 vedst", whose redirect is `/vnas/login`.
    - New options: `Yaat:Vnas:ClientId`, `Yaat:Vnas:ClientSecret` and `Yaat:Vnas:LoginReturnUrl`.
    - Check `clientId` against `Yaat:Vnas:ClientId`, and `redirectUrl` against `<base>/vnas/login`.
    - The exchange uses the vnas client's id and secret. `VatsimAuthService.ExchangeCodeAsync` therefore takes the client credentials as well as the redirect URI, or a second configured instance is used; decide which in implementation.
    - The endpoints return 500 "not configured" when the `Yaat:Vnas` options are empty.
  - Exchange the code. `VatsimAuthService.ExchangeCodeAsync` gains required `redirectUri` and nullable `codeVerifier` parameters, and the existing callers pass their own values.
  - Apply VATUSA.
  - Return `{nasToken: access, vatsimToken: refresh}` as YAAT-signed tokens. vEDST keeps only `vatsimToken` and decodes its `exp`; the YAAT refresh JWT has one.
  - Errors come back as a JSON body, because vEDST calls `.json()` even on a non-OK response.
  - vEDST does not use PKCE (`Login.tsx`), so its exchange passes `codeVerifier: null` and depends on `client_secret`.
- `GET /vnas/auth/refresh?vatsimToken` validates the refresh token with the revocation check and returns a fresh access token as **plain text**, which vEDST reads with `r.text()`. It does not rotate, following the reasoning in `IssueAccessFromCookieAsync`.
- **CORS.** Add a named policy from `Yaat:Cors:AllowedOrigins`, set to `http://localhost:3000` in dev config. List the exact origins (no wildcard) and allow credentials, because `/auth/login` is fetched with `credentials: "include"`. Apply it to the `/vnas/*` endpoints only. The hub is a WebSocket, so CORS does not apply to it.
- **Manual steps.**
  - Done: VATSIM Connect client 1974 ("yaat1 vedst") exists, with redirect `https://yaat1.leftos.dev/vnas/login`.
  - Ships with the endpoint:
    - `docker-compose.yml` maps `Yaat__Vnas__ClientId=${VNAS_VATSIM_CLIENT_ID:-}`, `Yaat__Vnas__ClientSecret=${VNAS_VATSIM_CLIENT_SECRET:-}` and `Yaat__Vnas__LoginReturnUrl=${VNAS_LOGIN_RETURN_URL:-}`, and `.env.example` gains all three.
    - `.env.yaat1` gets `VNAS_VATSIM_CLIENT_ID=1974` and `VNAS_LOGIN_RETURN_URL=http://localhost:3000/login`.
    - The user adds the secret, `VNAS_VATSIM_CLIENT_SECRET=…`, by hand; an agent never reads it.
    - Then `deploy-secrets.ps1 -Target yaat1 -DryRun`, then the real run with the user's OK.
  - Jonah: set `VITE_VNAS_CONFIG_URL=https://yaat1.leftos.dev/vnas/configuration`, `VITE_VATSIM_CLIENT_ID=1974` and `VITE_DOMAIN=https://yaat1.leftos.dev/vnas`. No vEDST code changes.

### 7. Tests (`tests/Yaat.Server.Tests`, TDD per step)
- `CrcJsonTranscoderTests`:
  - Inbound camelCase `subscribe`/`joinSession` turn into the MessagePack args the handlers read.
  - An outbound `ReceiveEramTracks`/`ReceiveFlightPlans` frame turns into the expected camelCase JSON with string enums.
  - An unknown method gets an error completion.
  - The frame splitter handles several frames and a partial frame.
- `CrcJoinSessionTests`, in the `CrcBindTickGateTests` harness style:
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
- yaat-server: a section on the JSON path, joined sessions and the `/vnas` endpoints in `docs/crc-display-state.md`, a replacement for line 144 ("Wire encoding is MessagePack"), and `docs/vatsim-auth.md`.
- yaat: `docs/architecture.md`, the CHANGELOG line, a glossary entry for "joined session".

## Decisions and corrections

The exploration against the code and vEDST's source (`vFlightDataSystems/VATSIM_EDST_frontend`, main) settles these; where they differ from the steps above, they win.

- **Build order, three independent tracks:** W1 auth primitives then `/vnas` endpoints and CORS (`Auth/*`, `YaatOptions.cs`, appsettings, compose, `ServerApp.cs` services and middleware); W2 the JSON transcoder then handshake and framing (`Protocol/*`, new `Dtos/*`, `CrcClientState.cs` RunAsync/SendAsync) — in progress; W3 direct-client hygiene and `GetSessions`, then `JoinSession`/fan-out (`Hubs/*` in a new `CrcClientState.Join.cs`, the client-list consumers in `Simulation/*` and `TrainingHub.cs`). Direct WebSocket auth follows W1 and precedes the join step (both edit `CrcWebSocketHandler.cs`).
- **Joiner rights (user):** a whitelist enforced in `DispatchInvocationAsync` for every joined connection, MessagePack or JSON: `Subscribe`, `Unsubscribe`, `GetSessions`, `JoinSession`, `LeaveSession`, `GenerateFrd`, `AmendFlightPlan`, `SetHoldAnnotations`, `DeleteHoldAnnotations`, `SendPrivateMessage`, `ProcessEramMessage`. Start/End/Activate/Deactivate, `ChangeActive*`, `Secondary*` and `KillClient` are refused.
- **`JoinSession` pushes only `HandleFsdConnectionStateChanged(true)` and `SetSessionActive`**, never `HandleSessionStarted`: vEDST's `HandleSessionStarted` handler calls `joinSession` again. A repeat join to the same primary is an idempotent ack.
- **A waiting connection hears about a later session:** `StartSession` also pushes `HandleSessionStarted` to same-CID direct connections that have not joined. A direct connection with no room is allowed, and is kept out of the lobby list, `DevCrcAutoBind`, `HasBoundCrcClient`, `TrainingBroadcastService` and the `TrainingHub` client lists from connect time.
- **Joiners act as their primary:** `ResolveIdentity`, `GetPositionCallsign`, `SendStarsReadoutAreaAsync` and `BuildOwnPositionPayload` read the primary's registry entry.
- **`GenerateFrd` and aircraft-addressed private messages are implemented in this item (user):** `GenerateFrd` answers a fix/radial/distance from the navigation database (`FrdResolver`'s inverse), and `SendPrivateMessage(aircraftId, text)` reaches the aircraft's pilot, i.e. the RPO terminal line for that aircraft (the brief's design pass settles the exact surface).
- **CORS (user):** a configured list, `Yaat:Vnas:AllowedOrigins` bound from a comma-separated `VNAS_ALLOWED_ORIGINS`, defaulting to `http://localhost:3000`; applied to `/vnas` and the CRC hub path.
- **Config:** `Yaat:Vnas:ClientId`, `ClientSecret`, `LoginReturnUrl`, `PublicBaseUrl` (compose sets `https://${YAAT_DOMAIN}`; request-derived in development). `ExchangeCodeAsync` takes the client credentials, the redirect URI and the code verifier as required parameters (one shared `HttpClient`). The return target is the single configured `LoginReturnUrl`, validated at startup, never taken from the request. Endpoints are always mapped and answer 500 "not configured" when the keys are missing. `YaatTokenService` gains `ValidateAccessTokenAsync`.
- **Direct auth (shipped):** a direct connection authenticates with its `?access_token=` (`CrcWebSocketHandler.AuthenticateDirectAsync`) and takes that CID with no room of its own; a missing, blank or invalid token gets HTTP 401 before `AcceptWebSocketAsync`, logged without the token; an unknown hub protocol gets `{"error":…}\x1e` and close 1002.
- **Transcoder:** closed-world over the mapped DTO graphs, unmapped callbacks dropped with a once-per-target warning; new `JoinSessionDto`, `CreateOrAmendFlightPlanDto`, `ProcessEramMessageDto`, `EramMessageProcessingResultDto`; inbound enums accept numbers; every JSON frame is a Text message.
- **Direct connections (shipped as the first half of the join track):** a connection without the negotiate `?id=` is *direct* (`CrcClientState.IsDirect`); the manager's CID and room lookups skip it, `GetSessions` lists the caller's non-direct sessions oldest first, and a non-direct `StartSession` tells waiting same-CID direct connections. For the join step: the joiner allowlist applies to **every** direct connection, not only joined ones (a direct connection is never a primary), so a direct `StartSession` cannot register a position that `FindByPositionCallsign` would return ahead of the real CRC session; a joined connection carries no room of its own and reads it through `_primary` (the unbind paths `TrainingBroadcastService.EvictRoomClientsAsync`, `TrainingHub.UnbindAllCrcClients` and `KickCrcClient` see only non-direct clients); and `CrcBroadcastService.BroadcastToCrcClientsAsync` matches `targetCallsign` and `excludeClientId` on the connection itself, so decide there whether a callsign-targeted message reaches the joiner and whether a command sent from vEDST excludes its primary.
- **Several primaries per CID:** `GetSessions` returns all, oldest connection first.
- **ERAM pushes reach the joiner:** ERAM C3's `SM`/`SW` push private messages to the ERAM sessions of a sector or facility through `CrcClientManager.GetClientsForRoom`, which skips direct connections. The join step makes a joined connection receive whatever private message its primary receives (the `FanOutToJoined` helper). vEDST registers no `ReceivePrivateMessage` handler today and the JSON callback map lacks it, so the fan-out happens but no transcoder mapping is added until vEDST consumes it.
- **B1 rulings (shipped):** fields read through the primary are `_roomEngine`, `_artccId`, `_currentPositionId` and `_displayName` (a joiner never reaches `_realName`, `_role` or `_isActive`); identity lookups use the primary's client id; the session-log redaction skips joined connections only (a room-bound direct connection keeps its own redaction); the join ack and its two pushes go out in one buffer; switching primaries clears subscriptions; a direct connection's disconnect only leaves its session.
- **B3 rulings (user 2026-09-30):** `HandleGenerateFrd` (`CrcClientState.Session.cs` ~:713, a stub returning `""` today) answers `FrdResolver.ToFrd(lat, lon, _navDb.GetFixTuples())` with the default 50 nm radius the ERAM route code uses (`CrcClientState.Eram.Route.cs` ~:282), `""` when no fix is in range (CRC and vEDST both treat `""` as failure); it reads no room, so a room-less direct connection gets an answer, and CRC gets a real FRD too (changelog it). vEDST calls it from its route, previous-route and template menus (`..\vedst\src\hooks\useHubActions.ts` ~:61). `HandleSendPrivateMessage` (`CrcClientState.Messaging.cs` ~:27) resolves the recipient as an aircraft in the room first: a hit writes a **Chat** terminal entry on that aircraft's callsign (`BroadcastTerminalEntry`, recorded in the terminal log), sender the primary's position callsign, display-only (no pilot inbox, no reply path; vEDST registers no `ReceivePrivateMessage`); else the controller path as today; a recipient that is neither an aircraft nor a known controller position returns an **error completion**. vEDST's `sendPrivateMessage` has no UI call site yet (its uplink menu entries are commented out), so the harness is its only caller.
- **B2, the fan-out (shipped):** a `FanOutToJoined` helper on the primary and a `SendSessionActiveAsync` wrapper over `BuildSetSessionActivePayload`. Fan out: `SetSessionActive` on Activate/Deactivate (`Session.cs` ~:330-360); `HandleSessionEnded(reason, false)` from `HandleEndSession`, `ForceDisconnectAsync` (`Info.cs`, `isForcible` true there) and the primary's disconnect, then detach (vEDST stops its hub on it); `HandleSessionEnded` on the primary's unbind from its room (the joiner has no room to read); per-joiner `FlightPlansForViewerChange` and conflict resend on `ChangeActiveEramPosition` (`Secondary.cs` ~:189-250, `wasEramViewer` captured per joiner); the bind replay (`SendInitialDataForSubscriptionsAsync`, exposed apart from `TryBindToRoom`) when the primary binds; C3's private-message pushes; `BroadcastToCrcClientsAsync`'s `targetCallsign` match on `(client.Primary ?? client).ClientId` (a callsign-targeted message reaches the joiner), with `excludeClientId` excluding only the sender. Replies to the connection's own call (`SendInfoReplyAsync`, `SendServerMessageAsync`, `SendServerErrorAsync`) are not fanned out.
- **Local end to end:** a development-only `/vnas/auth/dev-login`, gated like `/auth/dev`.
- **CORS test:** a policy-builder unit test; no new test-host package.
- The UDP history step is moot: vEDST subscribes only FlightPlans, EramTracks and OpenPositions, and the existing negotiate-token guard already skips a direct client.

## Verification
- `pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 30 -Slot heavy -- dotnet test ... --filter-class` for each new class, then `pwsh tools/test-all.ps1`.
- End to end: run yaat-server locally (5130), connect CRC to a room with an ERAM position, then run vEDST from source (`npm run dev` at :3000) with its config URL pointed at `http://localhost:5130/vnas/configuration`. Log in, check that vEDST joins and shows flight plans and tracks, amend a flight plan, and confirm CRC shows the amendment.
- `csharp-reviewer` on the diff. The aviation review is not owed, because no sim behaviour changes.
