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
present a YAAT token to `/hubs/training` — never a VATSIM token.

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

## Connection gate (who may connect)

`/hubs/training` carries `[Authorize(Policy = TrainingHubAccessRequirement.PolicyName)]`. A controller
may connect when **`isMentor` (VATUSA) is true, OR their VATSIM rating is instructor-or-above (I1/I2/I3,
SUP, ADM)** — `ScenarioRatingClassifier.IsInstructorOrAbove`. Everyone else (OBS/S1/S2/S3/C1/C3 without a
mentor role) is rejected at connect. CRC trainees on `/hubs/client` are **exempt** — they're the students
being trained and are not rating-gated.

## Scenario gate (what they may load)

A scenario's `minimumRating` is checked against the caller's verified rating via
`ScenarioRatingClassifier.IsRatingSufficient(callerRating, minimumRating)` in
`ScenarioLifecycleService.ResolveGatedJsonAsync` (and `TrainingHub.GetScenarios` for catalog filtering).
The per-ARTCC bcrypt key system is gone — rating is the sole gate.

## Tokens

`YaatTokenService` mints HS256 JWTs (claims `sub`=cid, `name`, `rating`, `subdivision`, `artcc`,
`is_mentor`, `token_use`; refresh tokens additionally carry a `jti`). `artcc` is the resolved ARTCC
(VATUSA home facility, else the subdivision) and is re-resolved from VATUSA on each refresh so a
controller's transfer or mentor-role change takes effect within the access-token lifetime (~1h). Access token ~1h (hub Bearer / `yaat_session`
cookie); refresh token ~30d (`yaat_refresh` cookie / desktop `auth-sessions.json`) re-mints access via
`/auth/refresh` (desktop) and `/auth/token` (web). JwtBearer reads the token from the `access_token`
query (desktop) or the `yaat_session` cookie (web) for `/hubs/training`.

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
  never get their `Secure` flag.
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
| `Yaat:Auth:RequireVatsimAuth` | `true` (default, fail-secure). `false` enables `/auth/dev` for local dev |
| `Yaat:Auth:JwtSigningKey` | HS256 key (≥32 bytes). Required in Production; a fixed dev key is used if blank in Development |

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

Each VATSIM client's registered redirect must equal `https://<YAAT_DOMAIN>/auth/vatsim/callback`. Copy
`Caddyfile.example` to `Caddyfile` once (it reads `{$YAAT_DOMAIN}`, so it needs no per-domain edit).
Deploy/update a target with `./update.sh <target>` (e.g. `./update.sh yaat1`), which runs every
`docker compose` command with `--env-file .env.<target>`. With no argument it falls back to `.env`.

## Local development

`dotnet run` is Development, and `appsettings.Development.json` sets `RequireVatsimAuth=false`. In that
mode the server exposes `/auth/dev`, and the desktop `VatsimAuthClient` (and the GuideCapture in-process
host) mint a dev session (rating `I1`) without any VATSIM round-trip. To test the real flow locally,
register an `http://localhost:5000/auth/vatsim/callback` redirect on a dedicated dev VATSIM client and
set `RequireVatsimAuth=true` + the `Vatsim:*` config.

## Key files

- Server: `src/Yaat.Server/Auth/*` (`VatsimAuthService`, `VatusaService`, `YaatTokenService`,
  `AuthEndpoints`, `AuthStateStore`, `AuthExchangeStore`, `RefreshTokenRegistry`,
  `TrainingHubAccessRequirement`, `VatsimUser`), `YaatHost.cs` (JwtBearer + `token_use` gate +
  forwarded headers + policy wiring), `Hubs/TrainingHub.cs` (gate + claim reading),
  `Simulation/ScenarioLifecycleService.cs` (scenario gate), `YaatOptions.cs`.
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
  negotiate connection token uses `RandomNumberGenerator`. A proper fix (e.g. lobby-only binding with
  an explicit instructor pull) is tracked for a future design pass.
