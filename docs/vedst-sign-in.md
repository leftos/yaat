# vEDST sign-in

vEDST (the web ERAM client, [`vFlightDataSystems/VATSIM_EDST_frontend`](https://github.com/vFlightDataSystems/VATSIM_EDST_frontend)) signs in to a yaat server as if the server were vNAS: yaat-server serves vNAS-shaped endpoints under `/vnas` (`src/Yaat.Server/Auth/VnasCompatEndpoints.cs` in yaat-server) and answers them with YAAT-signed tokens. This page is the setup, step by step. The server's own VATSIM sign-in and token model are in [vatsim-auth.md](vatsim-auth.md), the VATSIM Connect clients registered for each server are in its [registered-clients table](vatsim-auth.md#multi-domain-docker-deployment), and the rest of the vEDST integration (JSON hub protocol, joined sessions) is in [vedst.md](vedst.md).

## How it works

1. vEDST loads its configuration from `VITE_VNAS_CONFIG_URL`, the server's `/vnas/configuration`. It lists one environment, `YAAT`, whose `apiBaseUrl` is `<server>/vnas` and whose `clientHubUrl` is `<server>/hubs/client`; `<server>` is `Yaat:Vnas:PublicBaseUrl`, or the request's own scheme and host when that is blank.
2. **Login with VATSIM** builds the VATSIM authorize URL in the browser. The host is hard-coded, `https://auth.vatsim.net/oauth/authorize` (upstream `src/login/Login.tsx:15`), with `client_id` = `VITE_VATSIM_CLIENT_ID`, `redirect_uri` = `<VITE_DOMAIN>/login` and `scope=vatsim_details`, and no PKCE and no `state`. With `VITE_DOMAIN=<server>/vnas` the redirect is the server's `/vnas/login`, the redirect registered on the server's vEDST client.
3. VATSIM sends the browser to `<server>/vnas/login?code=…`, which answers 302 to `Yaat:Vnas:LoginReturnUrl` carrying the code and nothing else. No request parameter can change the target.
4. vEDST's `/login` page, back on its own origin, redeems the code at `<server>/vnas/auth/login?code&redirectUrl&clientId`, a cross-origin fetch with credentials, so its origin must be in `Yaat:Vnas:AllowedOrigins`. The server requires `clientId` to equal `Yaat:Vnas:ClientId` and `redirectUrl` to equal `<server>/vnas/login`, exchanges the code with the vEDST client's id and secret, applies VATUSA, and answers `{nasToken, vatsimToken}`: a YAAT access token, and a YAAT refresh token that lives `Yaat:Vnas:RefreshTokenLifetimeHours` (default 24). vEDST keeps `vatsimToken` in localStorage under `vatsim-token`.
5. vEDST trades the refresh token at `<server>/vnas/auth/refresh?vatsimToken=…` for an access token (plain text; the refresh token is not rotated), opens the CRC hub socket at `clientHubUrl` directly (`skipNegotiation`) with that token as `?access_token=`, and joins the CRC session of the same CID (`GetSessions` → `JoinSession`). The socket takes the token's CID and no room of its own; a missing or invalid token gets HTTP 401 before the upgrade, and the token is never logged. How the hub decides which sockets to accept is in [vatsim-auth.md](vatsim-auth.md#crc-hub-socket-direct-and-joined-clients).

`LoginReturnUrl` is one value per server, so every vEDST user of a server returns to the same URL: a server set up for `http://localhost:3000/login` serves developers running vEDST locally, and a hosted vEDST needs a server whose `LoginReturnUrl` is its own `/login`.

The other endpoints: `/vnas/artccs/{id}` and `/vnas/airports/{id}` pass through to the vNAS data API with the upstream status, and `/vnas/auth/dev-login` mints a token pair without VATSIM on a Development server ([Against a local yaat-server](#against-a-local-yaat-server)). Only `/vnas/login` and `/vnas/auth/login` need the vEDST client configured; the configuration document, the passthroughs, the refresh and the dev login work without it.

## Enable vEDST sign-in on a deployed server (server owner)

1. **Register a VATSIM Connect client for vEDST**, separate from the server's own (`Yaat:Vatsim`), with the redirect URL `https://<YAAT_DOMAIN>/vnas/login`. It must be a **confidential** client with a secret, not one marked "Public client" in the VATSIM dashboard: vEDST sends no PKCE verifier, so the code exchange is authenticated by the secret alone, and the server treats vEDST sign-in as unconfigured while `Yaat:Vnas:ClientSecret` is blank (`VnasOptions.IsConfigured`, yaat-server `YaatOptions.cs:159-160`). yaat1 uses client 1985 ("yaat1 vedst"). Add a new client to the [registered-clients table](vatsim-auth.md#multi-domain-docker-deployment).
2. **Add the vEDST keys to the target's env file on the droplet**, `/home/yaat/yaat-server/.env.<target>` (the target's `RemoteEnvFile` in the yaat repo's `deploy-targets.ps1`; `.env.yaat1` for yaat1):
   ```dotenv
   VNAS_VATSIM_CLIENT_ID=1985
   VNAS_VATSIM_CLIENT_SECRET=                      # the client's secret, entered by the owner
   VNAS_LOGIN_RETURN_URL=http://localhost:3000/login
   VNAS_ALLOWED_ORIGINS=http://localhost:3000
   ```
   - `VNAS_LOGIN_RETURN_URL` is vEDST's `/login` page: `http://localhost:3000/login` for developers running vEDST from source, or the hosted vEDST's `/login`. It must be an absolute http(s) URL.
   - `VNAS_ALLOWED_ORIGINS` lists the vEDST origins, comma-separated, each an exact origin with no path and no wildcard; it must include `VNAS_LOGIN_RETURN_URL`'s origin. Blank allows no origin in Production.
   - Nothing else is needed: compose sets `Yaat__Vnas__PublicBaseUrl` to `https://${YAAT_DOMAIN}`. The four variables map to `Yaat__Vnas__ClientId`, `ClientSecret`, `LoginReturnUrl` and `AllowedOrigins` in yaat-server `docker-compose.yml:34-40`.
   - Instead of editing on the droplet, the keys can go into the local `..\yaat-server\.env.<target>` and be pushed with `.\deploy-secrets.ps1 -Target <target>` (run it with `-DryRun` first).
3. **Recreate the container** so it reads the new values: on the droplet, `./update.sh <target>` in `/home/yaat/yaat-server`, or from Windows `.\deploy-to-droplet.ps1 -Target <target>`. The server checks the `Yaat:Vnas` values at startup and stops with the offending key named when one is malformed.
4. **Verify**:
   - `curl -i https://<domain>/vnas/configuration -H "Origin: http://localhost:3000"` answers 200 with `Access-Control-Allow-Origin: http://localhost:3000` (use each origin you allowed).
   - `curl -i "https://<domain>/vnas/login?code=test"` answers 302 with `Location: <LoginReturnUrl>?code=test`. A 500 with `not_configured` means the client id, the secret or the return URL is missing.

## Connect a vEDST checkout to a deployed server (vEDST developer)

1. Clone [`vFlightDataSystems/VATSIM_EDST_frontend`](https://github.com/vFlightDataSystems/VATSIM_EDST_frontend) and run `npm install` (the repo ships `package-lock.json`; it needs Node 18 or later).
2. Create `.env.local` in the checkout:
   ```dotenv
   VITE_VNAS_CONFIG_URL=https://<domain>/vnas/configuration
   VITE_DOMAIN=https://<domain>/vnas
   VITE_VATSIM_CLIENT_ID=<the server's vEDST client id>
   ```
   The client id is the server's `VNAS_VATSIM_CLIENT_ID` (1985 on yaat1). These two `VITE_DOMAIN`/`VITE_VATSIM_CLIENT_ID` keys apply to the dev server only; a production build reads `VITE_PROD_DOMAIN` and `VITE_PROD_VATSIM_CLIENT_ID` instead (upstream `src/utils/constants.ts:25-26`).
3. Run `npm start`, which starts Vite on port 3000 (`vite.config.ts`), and open `http://localhost:3000`. The server must allow `http://localhost:3000` and return to `http://localhost:3000/login`; ask its owner if sign-in fails ([Troubleshooting](#troubleshooting)).
4. Pick the `YAAT` environment (the only one listed) and press **Login with VATSIM**. After VATSIM, the browser comes back to `/login`, which shows "Waiting for vNAS Connection..." until there is a session to join.
5. Sign in to CRC on the same server with the same CID and open an ERAM position, so vEDST has a session to join (CRC against a YAAT server: [USER_GUIDE.md](../USER_GUIDE.md#connecting-crc-for-students), [crc-first-session.md](crc-first-session.md)). vEDST then joins it and opens.

## Against a local yaat-server

Run the server from source in the yaat-server repo, `dotnet run --project src/Yaat.Server`, which binds `http://localhost:5130` in Development. Development already allows the origin `http://localhost:3000` (`appsettings.Development.json`) and leaves `PublicBaseUrl` blank, so the server's URLs come from the request.

**Option A, no VATSIM.** `appsettings.Development.json` turns `RequireVatsimAuth` off, which mounts the dev login.

1. In the vEDST checkout, `.env.local` needs only `VITE_VNAS_CONFIG_URL=http://localhost:5130/vnas/configuration`; run `npm start`.
2. Mint a token pair: `GET http://localhost:5130/vnas/auth/dev-login?cid=<cid>` answers `{nasToken, vatsimToken}`. Use the CID CRC signs in with. Optional query parameters: `name` (default `Dev User`), `rating` (default `I1`), `subdivision`, `artcc` (default the subdivision), `isMentor=true`.
3. In the browser console on `http://localhost:3000`, run `localStorage.setItem("vatsim-token", "<vatsimToken>")` and reload. vEDST skips the VATSIM login and waits for the CRC session.
4. Sign in to CRC on the local server with that CID and open an ERAM position (`Setup-CrcEnvironment.ps1 -Servers @(@{Name="YAAT Local";Url="http://localhost:5130"})`, [crc-first-session.md](crc-first-session.md)).

**Option B, real VATSIM.**

1. Register your own confidential VATSIM Connect client (with a secret, for the reason in [step 1 above](#enable-vedst-sign-in-on-a-deployed-server-server-owner)) whose redirect is `http://localhost:5130/vnas/login`. The server source says VATSIM will not redirect to `localhost` (`VnasCompatEndpoints.cs`, the class summary); if the dashboard refuses the URL, use Option A.
2. Configure the server with `Yaat:Vnas:ClientId`, `ClientSecret` and `LoginReturnUrl=http://localhost:3000/login`, through the environment (`Yaat__Vnas__ClientId`, `Yaat__Vnas__ClientSecret`, `Yaat__Vnas__LoginReturnUrl`) or the gitignored `appsettings.Local.json`, and restart it.
3. In vEDST's `.env.local`: `VITE_VNAS_CONFIG_URL=http://localhost:5130/vnas/configuration`, `VITE_DOMAIN=http://localhost:5130/vnas`, `VITE_VATSIM_CLIENT_ID=<your client id>`. Then continue from step 3 of [the developer steps](#connect-a-vedst-checkout-to-a-deployed-server-vedst-developer).

## Troubleshooting

| Symptom | Cause |
|---|---|
| `/vnas/login` or `/vnas/auth/login` answers 500 `not_configured` | `Yaat:Vnas:ClientId`, `ClientSecret` or `LoginReturnUrl` is blank (`VNAS_VATSIM_CLIENT_ID`, `VNAS_VATSIM_CLIENT_SECRET`, `VNAS_LOGIN_RETURN_URL`). |
| No `Access-Control-Allow-Origin` header; vEDST shows "Failed to load vNAS config" or the login fetch fails in the browser | The vEDST origin is not in `VNAS_ALLOWED_ORIGINS` (blank allows none in Production), or `VITE_VNAS_CONFIG_URL` is wrong. |
| The container stops at startup naming `Yaat:Vnas:AllowedOrigins` or `LoginReturnUrl` | An origin has a path or a wildcard, the return URL is not absolute http(s), or the origin list leaves out the return URL's origin. |
| `/vnas/auth/login` answers 400 `invalid_client` | vEDST's `VITE_VATSIM_CLIENT_ID` differs from the server's `Yaat:Vnas:ClientId`. |
| `/vnas/auth/login` answers 400 `invalid_redirect` | vEDST's `VITE_DOMAIN` + `/login` differs from `<server>/vnas/login`; the message names the URL the server expects. |
| `/vnas/auth/login` answers 401 `login_failed` | VATSIM refused the code exchange: a wrong secret, or a code already used or expired. Sign in again. |
| `/vnas/airports/{id}` answers 404 | Normal: vNAS answers 404 for an airport it has no entry for, and vEDST treats it as no info. A 502 `upstream_unavailable` means the vNAS data API could not be reached. |
| vEDST stays on "Waiting for vNAS Connection..." | Signed in, but no CRC session with that CID on this server: sign in to CRC with the same CID and open an ERAM position. |

## Accepted risks

Both are set by vEDST's protocol; closing them needs a vEDST change.

- **Login codes are bearer secrets.** vEDST sends neither PKCE nor `state`, so whoever reads a code (browser history, a log, a referrer) can redeem it at `/vnas/auth/login`, and a crafted `/vnas/login?code=` link can sign a victim in as someone else. The server keeps the flow vEDST expects.
- **The refresh token travels in a query string** (`?vatsimToken=`), and vEDST never rotates or revokes it. vEDST refresh tokens therefore live 24 hours, not the desktop's 30 days. Keep `Microsoft.AspNetCore` logging at Warning or above (the server warns at startup otherwise), and use the commented filter in `Caddyfile.example` if Caddy access logs are turned on; it deletes `vatsimToken` and `code` from logged URLs.
