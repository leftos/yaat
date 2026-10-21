# VATSIM OAuth & access control

YAAT authenticates controllers with **VATSIM Connect (OAuth2)** instead of self-asserted CID/initials.
The server is the identity authority; clients never hold the OAuth secret or the VATSIM token.

> Read this before touching `src/Yaat.Server/Auth/`, the training-hub connection gate, the desktop
> `VatsimAuthClient`, or the vStrips/vTDLS sign-in pages.

## Why

Replaces manual CID/initials/ARTCC entry and the old per-ARTCC bcrypt training keys with verified
VATSIM identity. Goals: prove who the user is, gate scenario access by real controller rating, and keep
sub-mentor ratings (OBS/S1 and non-instructor non-mentors) off deployed servers.

## Architecture — server-mediated

`yaat-server` is the only registered VATSIM OAuth client (holds `client_secret`), performs the code
exchange + userinfo call, and issues short-lived **YAAT session tokens** (HS256 JWTs). All clients
present a YAAT token to `/hubs/training` — never a VATSIM token. Two other endpoints take a VATSIM access token: `/vnas/auth/refresh` (TowerCab 3D, see below) and `GET /feed/rooms` (see "Feed room list").

**Desktop (system browser + loopback handoff):**
```
client opens browser → GET <server>/auth/vatsim/start?return=http://127.0.0.1:<port>/cb
  server stores {state,return,PKCE} → 302 to auth.vatsim.net/oauth/authorize (redirect_uri = <server>/auth/vatsim/callback)
    user logs in → 302 back to <server>/auth/vatsim/callback?code&state
      server exchanges code (PKCE) → /api/user → VATUSA isMentor → mints YAAT access+refresh
        → stores them behind a single-use exchange code → 302 to http://127.0.0.1:<port>/cb?code=<exchangeCode>
client HttpListener captures the code → POST /auth/exchange → receives tokens+identity
  → stores them (DPAPI-encrypted) in auth-sessions.json → connects /hubs/training with Bearer
```
Only the **server callback** is registered with VATSIM. The loopback is server→desktop, so **no custom
URI scheme / protocol handler is needed** — plain `https://` is enough. The tokens travel via a
single-use POST exchange (RFC 8252 style), never in the loopback redirect URL.

**Web (vStrips/vTDLS — same-origin cookie):**
```
GET /vstrips/ → JS calls /auth/token; 401 → "Sign in with VATSIM" → /auth/vatsim/start?return=/vstrips/
  → auth.vatsim.net → /auth/vatsim/callback → sets HttpOnly yaat_refresh + yaat_session cookies → 302 /vstrips/
WASM boots → connects /hubs/training; the browser sends yaat_session automatically (same origin)
```
The WASM apps need no token plumbing — the cookie rides the same-origin negotiate. `/auth/token` (called
by the sign-in page) re-resolves VATUSA, re-mints the access token, refreshes the `yaat_session` cookie,
and returns the identity (including the resolved `artcc`) so the page can confirm initials; ARTCC is
filled automatically and not prompted for.

## VATSIM / VATUSA endpoints

- Authorize `https://auth.vatsim.net/oauth/authorize`, token `…/oauth/token`, userinfo `…/api/user`.
- Scopes: `full_name vatsim_details` (`vatsim_details` exposes rating + subdivision).
- Userinfo → `data.cid`, `data.personal.name_full`, `data.vatsim.rating.short`, `data.vatsim.subdivision`.
- VATUSA: `GET https://api.vatusa.net/v2/user/{cid}` → `data.isMentor` (MTR role) + `data.facility` (home
  ARTCC, used to auto-fill the controller's ARTCC). Queried at login and re-queried on every token refresh
  (best-effort: a null result — outage or VATUSA disabled — keeps the cached identity, so it never flips a
  live session). Non-VATUSA controllers fall back to the VATSIM `subdivision`.
- The flow is **Authorization Code + PKCE (S256)**. A **public** VATSIM client (no secret) is supported and is the
  expected setup; a **confidential** client additionally sends `client_secret` (set `Yaat:Vatsim:ClientSecret`).

## Connection gate + capability tier (who may connect / do what)

`/hubs/training` carries `[Authorize(Policy = TrainingHubAccessRequirement.PolicyName)]`, which admits
**any authenticated VATSIM controller**. Capability is tiered on the hub methods, not at connect time:

- **Mentors/instructors** (`isMentor` from VATUSA, OR rating instructor-or-above I1/I2/I3/SUP/ADM via
  `ScenarioRatingClassifier.IsInstructorOrAbove`) run sessions: create rooms, load/unload scenarios, kick.
- **Non-mentors get limited access.** They wait in the lobby until a mentor pulls them into a room:
  `PullRpo(connectionId)` records an invite on the room (`TrainingRoom.Invite`) and pushes
  `RoomAvailableForCid`, which the client auto-joins. `JoinRoom` rejects an uninvited non-mentor "main"
  client (`CanJoinRoomCore`); `CreateRoom`, `LoadScenario`, `UnloadScenarioAircraft`/`ConfirmUnloadScenario`,
  and `KickMember` throw `HubException` for non-mentors (`RequireMentorOrInstructor`). The vStrips/vTDLS
  position tools join freely (they follow the main client).
- The mentor's **Room Members** modal lists waiting RPOs (`GetRpoLobbyClients` + the `RpoLobbyChanged`
  broadcast) and CRC clients alongside each other for one-click pull.

CRC trainees on `/hubs/client` are unaffected — they're not rating-gated.

## Scenario gate (what they may load)

A scenario's `minimumRating` is checked against the caller's verified rating via
`ScenarioRatingClassifier.IsRatingSufficient(callerRating, minimumRating)` in
`ScenarioLifecycleService.ResolveGatedJsonAsync` (and `TrainingHub.GetScenarios` for catalog filtering).
The per-ARTCC bcrypt key system is gone — rating is the sole rating gate.

## ARTCC gate (which catalogs they may work)

The room's ARTCC selects the scenario catalog it lists, so the server checks it: `CreateRoom`'s `artccId` must be
in the caller's **permitted ARTCCs** — the token's `artcc` claim (VATUSA home facility, else subdivision) plus any
**operator grant** for the CID — or the hub throws a `HubException`. `GetScenarioJsonById` re-checks the canonical
scenario's `artccId` against the same set *before* the rating gate, so a scenario id lifted from another ARTCC's
public catalog can't be loaded from a room of a different ARTCC. `GetMyPermittedArtccs` returns the set (home
first) so the desktop client shows an ARTCC picker on Create Room only when there is more than one.

Grants are the hand-vetted visiting-mentor list — e.g. a ZOA-home controller the ZMA Training Administrator has
approved to mentor at ZMA. They live in the **private yaat-server repo** at `src/Yaat.Server/Data/artcc-grants.json`
(`yaat-artcc-grants/1`: `grants[].cid`, `artccs`, plus free-text `name`/`note` for audit), ship inside the Docker
image, and are read once at startup (`ArtccGrantList` → `ArtccAccessPolicy`); `Yaat:ArtccGrantsPath` overrides the
path. A missing file means home-ARTCC access only; an unreadable or malformed one is logged as an error and treated
the same — the file can never widen or break home access. A grant widens ARTCC access **only**: the mentor tier
still comes from VATUSA `isMentor` / the rating. Deliberately outside the gate: **joining** — `JoinRoom`/`PullRpo`
never look at ARTCCs, so a visiting mentor/instructor joins any facility's room and a visiting non-mentor is pulled
into one exactly as at home (the joiner's member ARTCC is display-only; a joiner without a grant simply can't *load*
that ARTCC's catalog scenarios — the host does); the Local Files tab (`LoadScenario` raw JSON — custom scenarios for
any ARTCC still load); and rooms restored from a checkpoint (validated when they were created).

## Tokens

`YaatTokenService` mints HS256 JWTs (claims `sub`=cid, `name`, `rating`, `subdivision`, `artcc`,
`is_mentor`, `token_use`; refresh tokens additionally carry a `jti`). `artcc` is the resolved ARTCC
(VATUSA home facility, else the subdivision) and is re-resolved from VATUSA on each refresh so a
controller's transfer or mentor-role change takes effect within the access-token lifetime (~1h). Access token ~1h (hub Bearer / `yaat_session`
cookie); refresh token ~30d (`yaat_refresh` cookie / desktop `auth-sessions.json`) re-mints access via
`/auth/refresh` (desktop) and `/auth/token` (web). JwtBearer reads the token from the `access_token`
query (desktop) or the `yaat_session` cookie (web) for `/hubs/training` only; no other path has its token lifted from the query or cookie.

## Feed room list

`GET /feed/rooms` lets a feed consumer (a tool that reads a room's traffic feed) list the rooms its user may watch. It is not a hub, takes no YAAT token and has no authorization policy; it reads its own `Authorization: Bearer <VATSIM Connect access token>`, which the consumer forwards from its user's sign-in.

- **Verification.** The token is looked up at VATSIM's userinfo endpoint (`VatsimAuthService.FetchUserByAccessTokenAsync`), as at `/vnas/auth/refresh`. Only when production refuses the token and `Yaat:Vatsim:SandboxUserInfoUrl` is set (appsettings.Development.json sets it; a deployed server leaves it unset) is it asked of the VATSIM Connect sandbox (`FetchUserBySandboxAccessTokenAsync`), so a development server accepts sandbox tokens and a deployed one production tokens only; a VATSIM outage never falls through, and the sandbox call is a VATSIM call of its own against the limits below. The ARTCC is resolved through VATUSA as at sign-in. A bearer that names this server's issuer is refused 401 without being forwarded to VATSIM (`YaatTokenService.CarriesThisIssuer`, which reads the issuer without validating, so an expired token or one signed with a since-rotated key is refused too, as are a YAAT access and a refresh token). The verified CID and ARTCC are cached 5 minutes under a SHA-256 of the token (at most 1024 entries); failed verifications are not cached, and a token is never logged.
- **VATSIM call limits.** Only a cache miss reaches VATSIM. Each client IP (an IPv6 client per /64) may make 5 misses per 5 minutes (`VatsimCallLimiter.PerClientLimit`, 429 with `Retry-After`), checked first so one client cannot spend the shared budget; then every miss draws on one bucket of 60 calls per minute across all clients (`VatsimCallLimiter.TokenLimit`, 503 with `Retry-After` when empty). A client refused by the empty bucket has still spent its own permit; one refused its own limit takes nothing from the bucket. `VatsimCallLimiter` covers room-list traffic only: sign-in and `/vnas` calls to VATSIM do not draw on it.
- **Refusals.** 401 with `WWW-Authenticate: Bearer` and a JSON `reason` for a missing or non-Bearer header, a token VATSIM rejects (userinfo 400, 401 or 403), or a token naming this server's issuer; 429 after 30 requests per client IP per fixed one-minute window, or when the client's own VATSIM-call limit above is spent; 503 with `Retry-After` when the shared bucket is empty or VATSIM could not be asked (see "VATSIM outage"). Every response is `Cache-Control: no-store`.
- **Listing.** 200 `{"rooms":[{"roomId","artccId","creatorInitials","scenarioName","hasScenario","memberCount","aircraftCount","isPaused","feedUrl"}]}` sorted by `artccId` then `roomId`. `artccId` is the room's loaded scenario's ARTCC, else its creator's, upper-cased (`TrainingRoomManager.ListingArtccOf`). A room is listed when its creator's or its loaded scenario's ARTCC is in the caller's permitted set (`ArtccAccessPolicy.PermitsRoomArtcc`), the same home-plus-grant set as the ARTCC gate above, or when the caller's CID is a member of the room; a room with no known ARTCC is not listed on that basis, and a caller with no home, no grant and no room gets `{"rooms":[]}`. With `RequireVatsimAuth=false` every room is listed (the token is still verified). `TrainingRoomManager.GetListedRoomInfos` applies the rule under the registry lock and reads each entry's `memberCount`, `aircraftCount` and `isPaused` with it; `hasScenario` is whether `scenarioName` is non-null. Entries carry no member CIDs or connection ids.
- **Same rule on the desktop.** The training hub's `GetActiveRooms` applies the same rule (`TrainingRoomManager.GetListedRoomInfos`), so the desktop room list shows only rooms of the caller's home or granted ARTCCs and rooms the caller is already a member of (every room with VATSIM auth off). Each `TrainingRoomInfoDto` carries `ArtccId`, the scenario-else-creator label above, which the Join Room list shows; `RoomStateDto.ArtccId` (from `TrainingHub.BuildRoomState`) carries the same label, which the joined room's title shows and keeps in step with (`ScenarioLoadedDto.ArtccId`, `ScenarioUnloadedDto.ArtccId`; see [training-hub-contract.md](training-hub-contract.md)). Joining by id, by invite or by a mentor's pull is not filtered.

## VATSIM outage

Where an endpoint asks VATSIM Connect who a user is, a VATSIM failure is told from a bad token. `VatsimAuthService.FetchUserByAccessTokenAsync` returns null only when userinfo answers 400, 401 or 403 (or its body lacks the details); any other non-success status (a 5xx, a 429, a 404) or a network, TLS, timeout or non-JSON failure throws, and `VatsimOutage.IsOutage` classifies the throw as an outage. The token endpoint counts too: `VatsimAuthService.ExchangeCodeAsync` throws on a status of 500 or above or a 429, while any other token 4xx is a refused code and stays `login_failed`. `/vnas/auth/refresh`, the vEDST login (`/vnas/auth/login`) and `GET /feed/rooms` then answer 503 with `Retry-After: 30` (`VatsimOutage.RetryAfterSeconds`) instead of a 500: the vNAS endpoints with the usual `{error, message}` body and code `vatsim_unavailable`, the room list with a JSON `reason`. The browser sign-in callback (`GET /auth/vatsim/callback`, `AuthEndpoints.CallbackAsync`) answers no 503 and no `Retry-After`: it redirects 302 to the return URL with `?error=vatsim_unavailable` (desktop loopback) or `?auth_error=vatsim_unavailable` (web apps), and the pages show their generic failure text for it. A token VATSIM rejects stays 401 (or `login_failed`).

## Security model

The OAuth/PKCE/state core, the connection gate, and hub claim-trust are deliberately fail-closed
(unknown/blank ratings deny; claims come only from the signed token, never client arguments). Beyond
that:

- **Access-only at the hub.** Access and refresh tokens share the signing key, issuer, and audience, so
  JwtBearer's `OnTokenValidated` rejects any token whose `token_use` ≠ `access`. A refresh token can't
  be replayed as a hub credential. HS256 is pinned via `ValidAlgorithms` (alg-confusion defense).
- **Refresh rotation + revocation.** `/auth/refresh` issues a new refresh token and revokes the
  consumed one's `jti` (`RefreshTokenRegistry`); `/auth/logout` (and desktop sign-out) revoke the
  presented token. The registry is in-memory and cleared on restart — a restarted single-node server
  re-trusts not-yet-expired tokens, which is acceptable and far better than the prior no-revocation
  state. The web `/auth/token` path does **not** rotate (a single browser session makes concurrent
  vStrips+vTDLS calls); its refresh cookie is revoked only on logout.
- **Exchange-code desktop handoff.** The loopback receives a single-use code, not the tokens; the
  client POSTs it to `/auth/exchange` (`AuthExchangeStore`, 2-min TTL). Desktop tokens are then stored
  DPAPI-encrypted (Windows, per-user) at `auth-sessions.json`.
- **Secure cookies behind the proxy.** Caddy terminates TLS and proxies plain HTTP, so `UseForwardedHeaders`
  honors `X-Forwarded-Proto` — otherwise `Request.IsHttps` is false and `yaat_session`/`yaat_refresh`
  never get their `Secure` flag. It also honors `X-Forwarded-For` (one hop), so logs and `RemoteIpAddress` (the feed room list's rate limits) carry the real client IP. The headers are trusted only from a peer in loopback or a private range (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `fc00::/7`, where Caddy's docker-network address lies), so a client reaching the port directly cannot choose its own address.
- **Dev endpoint double-gated.** `/auth/dev` (which mints an arbitrary identity from query input) is
  mounted only when `RequireVatsimAuth=false` **and** the host is in the Development environment, so a
  single misconfigured knob in a real deployment can't expose it.

## Configuration (per server)

VATSIM allows **one redirect URI per OAuth client**, so **each deployed server registers its own VATSIM
client** with its own callback. Config (secrets via env / `appsettings.Local.json`, never committed):

| Key | Meaning |
|-----|---------|
| `Yaat:Vatsim:ClientId` / `ClientSecret` | This server's VATSIM Connect client (`ClientSecret` blank for a public/PKCE client) |
| `Yaat:Vatsim:CallbackUrl` | This server's registered redirect — in Docker, derived as `https://<YAAT_DOMAIN>/auth/vatsim/callback` |
| `Yaat:Vatusa:Enabled` / `ApiKey` | VATUSA mentor lookup (disable for non-US; ApiKey optional) |
| `Yaat:Vnas:ClientId` / `ClientSecret` | The server's second VATSIM Connect client, for vEDST (env `VNAS_VATSIM_CLIENT_ID` / `VNAS_VATSIM_CLIENT_SECRET`); only `/vnas/login` and `/vnas/auth/login` need it. Setup for the `Yaat:Vnas` keys: [vedst-sign-in.md](vedst-sign-in.md) |
| `Yaat:Vnas:LoginReturnUrl` | Where `/vnas/login` sends the browser with the code (vEDST's `/login` page, e.g. `http://localhost:3000/login`); absolute http(s), validated at startup |
| `Yaat:Vnas:PublicBaseUrl` | The server's public origin for the `/vnas/configuration` URLs and the expected vEDST redirect; compose sets `https://<YAAT_DOMAIN>`, blank derives it from the request |
| `Yaat:Vnas:AllowedOrigins` | Exact browser origins allowed by the `vnas` CORS policy (env `VNAS_ALLOWED_ORIGINS`, comma-separated); blank is `http://localhost:3000` in Development and nothing in Production; must include `LoginReturnUrl`'s origin |
| `Yaat:Vnas:RefreshTokenLifetimeHours` | Lifetime of a vEDST refresh token (default 24) |
| `Yaat:Auth:RequireVatsimAuth` | `true` (default, fail-secure). `false` enables `/auth/dev` for local dev |
| `Yaat:Auth:JwtSigningKey` | HS256 key (≥32 bytes). Required in Production; a fixed dev key is used if blank in Development |
| `Yaat:ArtccGrantsPath` | Operator ARTCC grants file (visiting mentors); blank → the committed `Data/artcc-grants.json` in the image |

### Multi-domain Docker deployment

`docker-compose.yml` derives the callback URL and Caddy's TLS site address from a single `YAAT_DOMAIN`
variable, so one repo serves yaat1, yaat2, or any other domain by swapping the env file. Put each
deployment's values in a gitignored `.env.<target>` file (copied from `.env.example`):

```dotenv
# .env.yaat1
YAAT_DOMAIN=yaat1.leftos.dev
VATSIM_CLIENT_ID=1883
VATSIM_CLIENT_SECRET=        # blank for a public/PKCE client
VATUSA_API_KEY=
JWT_SIGNING_KEY=             # openssl rand -base64 48 (unique per server, stable across restarts)
ADMIN_PASSWORD=
REQUIRE_VATSIM_AUTH=true
```

Each VATSIM client's registered redirect must equal the server URL that receives its code: `https://<YAAT_DOMAIN>/auth/vatsim/callback` for the YAAT client (`Yaat:Vatsim`), `https://<YAAT_DOMAIN>/vnas/login` for the vEDST client (`Yaat:Vnas`). The clients registered in the owner's VATSIM Connect dashboard:

| Client ID | Name | Redirect URL | Used by |
|---|---|---|---|
| 1883 | yaat1 | `https://yaat1.leftos.dev/auth/vatsim/callback` | `Yaat:Vatsim:ClientId` on the yaat1 target |
| 1985 | yaat1 vedst | `https://yaat1.leftos.dev/vnas/login` | `Yaat:Vnas:ClientId` on the yaat1 target (confidential, with a secret: [vedst-sign-in.md](vedst-sign-in.md)) |
| 1728 | towercab-3d | `tc3d://oauth/callback` | not YAAT (the owner's Tower Cab 3D app) |

How vEDST uses its client, and the `VNAS_*` keys that enable it: [vedst-sign-in.md](vedst-sign-in.md).

Copy `Caddyfile.example` to `Caddyfile` once (it reads `{$YAAT_DOMAIN}`, so it needs no per-domain edit). Deploy/update a target with `./update.sh <target>` (e.g. `./update.sh yaat1`), which runs every `docker compose` command with `--env-file .env.<target>` (no argument falls back to `.env`).

By default it pulls the CI-built ghcr image with the `docker-compose.image.yml` overlay; self-hosters without ghcr access pass `--build` to build from source instead. For leftos deployments prefer the yaat repo's `deploy-to-droplet.ps1`, which builds the image in CI first.

## Local development

`dotnet run` is Development (it binds `http://localhost:5130`), and `appsettings.Development.json` sets `RequireVatsimAuth=false`. In that mode the server exposes `/auth/dev`, and the desktop `VatsimAuthClient` (and the GuideCapture in-process host) mint a dev session (rating `I1`) without any VATSIM round-trip.

**Under dev auth the ARTCC gate is off**: nobody logged in with VATSIM, so `CreateRoom` and `GetScenarioJsonById` accept any ARTCC (the hub checks `RequireVatsimAuth` before `ArtccAccessPolicy`, which itself stays fail-secure).

Why the gate would bite: `VatsimAuthClient` passes `?artcc=` to `/auth/dev` only when it mints a session, a stored dev session is refreshed rather than re-minted for the refresh token's lifetime, and VATUSA 404s for CID 0000001, so a dev token minted without the ARTCC stays ARTCC-less forever.

`GetMyPermittedArtccs` is untouched, so the Create Room ARTCC picker stays hidden for an ARTCC-less dev token and the client falls back to its preferred ARTCC (MAIN.md backlog). To test the real flow locally, register an `http://localhost:5130/auth/vatsim/callback` redirect on a dedicated dev VATSIM client and set `RequireVatsimAuth=true` + the `Vatsim:*` config.

## vEDST sign-in (`/vnas`)

vEDST's sign-in (the `/vnas` endpoints, its VATSIM client, setup on a deployed or local server, troubleshooting and its accepted risks) is in [vedst-sign-in.md](vedst-sign-in.md).

## CRC hub socket: direct and joined clients

**Accepting a CRC hub socket** (`CrcWebSocketHandler.DecideAcceptAsync`, a pure decision over the `?id=`, the `?access_token=` and the token store). `NegotiateHandler` records every connection token it issues in `CrcNegotiateTokenStore`, with the Bearer fsd-jwt's unverified `sub` as its CID when one is sent and none otherwise; a token lives 60 s and is consumed once, and lapsed entries are swept at most once a second. Then:

| `?id=` | `?access_token=` | Outcome |
|---|---|---|
| none | valid | **direct** (vEDST): the token's CID, no connection token |
| none | missing or invalid | 401 |
| issued, live, unused | none (or blank) | **negotiated** (CRC): the store's CID, possibly none; may be dev-auto-bound |
| issued, live, unused | valid | **negotiated joiner** (Tower Cab 3D): direct, the access token's CID (the store's is ignored), the `?id=` as its connection token, so it can register for UDP |
| issued, live, unused | invalid | 401 (the id is spent) |
| not issued, lapsed or already used | any | 401 |

Every refusal is a bare 401 before the upgrade, logged as a warning that names the reason and the remote address and never a token. A direct connection or a joiner may call only the hub methods in `CrcClientState.DirectConnectionTargets` (`GetServerConfiguration` among them); `JoinSession` needs a CRC primary session with the same CID. An access token is checked once, at connect: its 60-minute expiry does not close an open socket, as on the training hub.

Tower Cab 3D (a third-party vNAS client) connects as a negotiated joiner over the JSON hub protocol: it lists its CID's CRC sessions with `GetSessions`, joins one with `JoinSession`, and subscribes to `TowerCabAircraft`. A CID with no CRC session is instead offered a synthetic `room:<roomId>` session for the room it joined most recently (YAAT main, vStrips, vTDLS or a CRC bind), so a Tower Cab 3D with no CRC joins the room directly and is listed in Room Members (`TowerCabRooms`); it hears `HandleSessionStarted` when its CID joins a room and `HandleSessionEnded` when the CID leaves, switches room, is kicked or the room closes.

A kick removes the whole CID from the room, this viewer included, and bans the CID from the room until an instructor pulls it back in (`PullRpo`/`PullCrcClient`); a banned CID is neither offered nor attached to the room.

Tower Cab 3D signs in like a real vNAS client: its own VATSIM Connect client yields a VATSIM access token, which it sends as `/vnas/auth/refresh?vatsimToken=`.

The room session's rules (`TowerCabRooms`, `CrcClientState.TowerCab.cs`):

- **Offer.** The session is an observer session in the room's ARTCC (the scenario's, else the room's own `CreatorArtccId` with a logged warning; when neither names one, a warning and no offer) whose one position names the scenario's primary airport when it is a Tower Cab airport of that ARTCC, else the ARTCC's first Tower Cab airport (warned).
- **Following.** While it has joined no CRC session and is attached to, or was offered, a room session, it keeps following its CID's rooms (`FollowsRoomSessions`): a CRC session of the CID binding into a lobby leaves it alone, one binding into another room D moves it to D's room session (`HandleSessionEnded`, then `HandleSessionStarted`), and one started in its own room leaves it where it is.

  A CRC session start offers the CID's waiting Tower Cab its session before `StartSession` answers, so a bind that follows can never be overtaken by a late offer of the lobby session.
- **Leaving.** `LeaveSession` on the room session is a quiet detach: the viewer is unlisted without `HandleSessionEnded`, forgets the offer, and records the room as declined, so the room is not offered again until its CID next joins a room.
- **No position.** With no CRC session joined it holds no position, so it may call only `GetServerConfiguration`, `Subscribe`, `Unsubscribe`, `GetSessions`, `JoinSession` and `LeaveSession`; any other method gets `RefuseDirectInvocation`'s error completion.
- **Acks.** A repeat `JoinSession` to the session it is already in is a plain acknowledgement; for Tower Cab 3D that ack, like an accepted join's answer, is queued behind its earlier session pushes rather than written directly.

The refresh endpoint therefore accepts two kinds of token: a YAAT refresh token (vEDST) is validated as before and never forwarded anywhere, revoked or not; any other token that is not a YAAT access token is looked up at VATSIM's userinfo endpoint (`VatsimAuthService.FetchUserByAccessTokenAsync`), and a user with the `vatsim_details` scope gets a YAAT access token. A VATSIM outage during that lookup answers 503, not 500 (see "VATSIM outage").

Against a Development server with `RequireVatsimAuth` off, TowerCab signs in through `/vnas/auth/dev-login?cid=` instead. After `GetServerConfiguration` (`{"udpPort":6809}`) it registers its negotiate id on the UDP entity socket and receives Tower Cab positions there, as on vNAS; new aircraft, removals and the periodic full set still arrive over the hub. A TowerCab build that skips negotiate connects as a plain direct client and gets every position over the hub.

## Key files

- Server: `src/Yaat.Server/Auth/*` (`VatsimAuthService`, `VatusaService`, `YaatTokenService`,
  `AuthEndpoints`, `AuthStateStore`, `AuthExchangeStore`, `RefreshTokenRegistry`,
  `TrainingHubAccessRequirement`, `VatsimUser`, `ArtccGrantList`, `ArtccAccessPolicy`), `ServerApp.cs` (JwtBearer +
  `token_use` gate + forwarded headers + policy wiring + grants load), `Hubs/TrainingHub.cs` (gate + claim reading +
  ARTCC gate), `Simulation/ScenarioLifecycleService.cs` (ARTCC + rating scenario gate), `YaatOptions.cs`,
  `Data/artcc-grants.json`; `Feeds/VatsimRoomListEndpoints.cs` (`GET /feed/rooms`, `VatsimIdentityCache`, `VatsimCallLimiter`),
  `Simulation/TrainingRoomManager.cs` (`ListingArtccOf`, `GetListedRoomInfos`), `Auth/VatsimAuthService.cs` (`VatsimOutage`).
- Shared: `src/Yaat.Sim/Scenarios/ScenarioRatingClassifier.cs`.
- Desktop: `src/Yaat.Client.Core/Services/VatsimAuthClient.cs`, `ServerConnection.cs`.
- Web: `tools/Yaat.VStrips.Web/wwwroot/index.html`, `tools/Yaat.VTdls.Web/wwwroot/index.html`,
  `src/Yaat.Client.Strips/Services/BrowserStripsTransport.cs`, `…Tdls/BrowserTdlsTransport.cs`.

## Residual / follow-ups

- **CRC fsd-jwt is unverifiable, by design of VATSIM's network.** CRC obtains a genuine VATSIM fsd-jwt
  from a hardcoded `https://auth.vatsim.net/api/fsd-jwt` and presents it to `/hubs/client`.
  `NegotiateHandler` reads its `sub` CID **without** verifying the signature, because VATSIM does not
  publish the fsd-jwt signing key to non-sanctioned operators (the from-scratch openfsd server has to
  redirect clients to its *own* fsd-jwt endpoint for exactly this reason). Verifying against a key we
  can't obtain would reject every legitimate CRC client. The exposure is contained: a CID only resolves
  to a room (`TrainingRoomManager.GetRoomForCid`) when that CID joined via the OAuth-gated training hub,
  so a forged CID can at most bind to a room whose owner's CID it already knows — one room's STARS/ERAM
  feed and CRC mutations, no cross-room/server escalation. CRC is rating-exempt regardless. The
  negotiate connection token uses `RandomNumberGenerator`, lives 60 s and is consumed once; a socket
  presenting an `?id=` the server did not issue is refused. A proper fix (e.g. lobby-only binding with
  an explicit instructor pull) is tracked for a future design pass.
