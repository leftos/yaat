# YAAT Deployment & Installer Plan

## Context

YAAT currently runs only in local dev mode (`dotnet run`). This plan adds:
1. **Docker image + DigitalOcean deployment** for yaat-server — enabling remote training sessions
2. **Windows installer** for Yaat.Client with an optional bundled local server — enabling easy distribution

## Decisions

- **Docker registry**: GHCR (ghcr.io) — private, free with GitHub plan, native `GITHUB_TOKEN` auth
- **Installer format**: Inno Setup 6 — lightweight, mature, supports optional components
- **Platforms**: Windows x64 only (Avalonia only tested on Windows for this project)
- **Local server**: Optional checkbox in installer; client detects and starts/stops it
- **CI/CD**: GitHub Actions in both repos; build-only (no auto-deploy to DO)
- **Yaat.Sim resolution**: Git submodule for Docker, sibling checkout for installer CI

---

## Part 1: Docker + DigitalOcean (yaat-server repo)

### 1.1 Fix Dockerfile — `src/Yaat.Server/Dockerfile`

Current problems: .NET 8 images, wrong project path (`src/YaatServer/`), wrong DLL name, no layer caching, no volume setup.

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Layer cache: copy project files + proto first, then restore
COPY Directory.Build.props .
COPY yaat-server.slnx .
COPY src/Yaat.Server/Yaat.Server.csproj src/Yaat.Server/
COPY extern/yaat/src/Yaat.Sim/Yaat.Sim.csproj extern/yaat/src/Yaat.Sim/
COPY extern/yaat/src/Yaat.Sim/Proto/ extern/yaat/src/Yaat.Sim/Proto/
RUN dotnet restore src/Yaat.Server/Yaat.Server.csproj

COPY . .
RUN dotnet publish src/Yaat.Server/Yaat.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data/yaat/cache
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENV XDG_DATA_HOME=/data
ENTRYPOINT ["dotnet", "Yaat.Server.dll"]
```

Build context = repo root (so `extern/yaat` submodule is accessible).
`XDG_DATA_HOME=/data` makes .NET's `LocalApplicationData` → `/data`, so VNAS cache → `/data/yaat/cache/`.

### 1.2 Create `.dockerignore` (repo root)

```
**/bin/
**/obj/
**/.git
**/.vs
**/.vscode
.github/
docs/
tests/
docker-compose.yml
*.md
```

### 1.3 Create `docker-compose.yml` (repo root)

```yaml
services:
  yaat-server:
    image: ghcr.io/leftos/yaat-server:latest
    build:
      context: .
      dockerfile: src/Yaat.Server/Dockerfile
    ports:
      - "5000:5000"
    environment:
      - Yaat__AdminPassword=${YAAT_ADMIN_PASSWORD:?Set YAAT_ADMIN_PASSWORD}
      - Yaat__ArtccResourcesPath=/data/artcc-resources
    volumes:
      - yaat-data:/data
      - ./ArtccResources:/data/artcc-resources:ro
    restart: unless-stopped

volumes:
  yaat-data:
```

### 1.4 Create `.github/workflows/docker.yml`

Triggers: push to `master`, tags `v*`.

```yaml
name: Docker
on:
  push:
    branches: [master]
    tags: ['v*']

env:
  REGISTRY: ghcr.io
  IMAGE_NAME: ${{ github.repository }}

jobs:
  build-and-push:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd  # v6.0.2
        with:
          submodules: recursive
          persist-credentials: false

      - uses: docker/login-action@c94ce9fb468520275223c153574b00df6fe4bcc9  # v3.7.0
        with:
          registry: ${{ env.REGISTRY }}
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - id: meta
        uses: docker/metadata-action@c299e40c65443455700f0fdfc63efafe5b349051  # v5.10.0
        with:
          images: ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}
          tags: |
            type=raw,value=latest,enable={{is_default_branch}}
            type=semver,pattern={{version}}
            type=semver,pattern={{major}}.{{minor}}
            type=sha

      - uses: docker/build-push-action@10e90e3645eae34f1e60eeb005ba3a3d33f178e8  # v6.19.2
        with:
          context: .
          file: src/Yaat.Server/Dockerfile
          push: true
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
```

### 1.5 Create `docs/deployment-digitalocean.md`

Sections:
1. Prerequisites (DO account, SSH key, GHCR PAT with `read:packages`)
2. Create droplet (Ubuntu 24.04, 2GB+ RAM, $12/mo)
3. Install Docker (`apt install docker.io docker-compose-v2`)
4. GHCR auth (`echo $PAT | docker login ghcr.io -u leftos --password-stdin`)
5. Deploy: create `/opt/yaat-server/`, `.env` with `YAAT_ADMIN_PASSWORD=...`, `docker-compose.yml`, `docker compose up -d`
6. Firewall: `ufw allow 5000/tcp`
7. Optional reverse proxy (Caddy/Nginx + TLS)
8. Update: `docker compose pull && docker compose up -d`

---

## Part 2: Windows Installer + Local Server (yaat repo)

### 2.1 Create `LocalServerManager.cs` — `src/Yaat.Client/Services/LocalServerManager.cs`

Manages child `Yaat.Server.exe` process:
- `static bool IsServerInstalled` — checks `{AppContext.BaseDirectory}/server/Yaat.Server.exe`
- `Task StartAsync(string adminPassword)` — starts process with `Yaat__AdminPassword` env var, `CreateNoWindow`, redirect stdout/stderr to AppLog
- `Task StopAsync()` — POST `http://localhost:5000/shutdown` (reuse existing endpoint from server `Program.cs:159-167`), fallback `Process.Kill(entireProcessTree: true)` after 5s
- `bool IsRunning` property
- `IDisposable` — kills process on dispose
- Fire-and-forget stdout/stderr logging via `_process.OutputDataReceived`

### 2.2 Update `UserPreferences.cs` — `src/Yaat.Client/Services/UserPreferences.cs`

Add fields:
- `_useLocalServer` (bool, default `false`)
- `_localServerAdminPassword` (string, default `"yaat-local"`)

Add to: constructor, properties, `SetLocalServerSettings()`, `LoadedPrefs`, `SavedPrefs`, `Load()`, `Save()`.

### 2.3 Update `MainViewModel.cs` — `src/Yaat.Client/ViewModels/MainViewModel.cs`

- Add `LocalServerManager _localServerManager` field
- In connect flow: if `UseLocalServer && IsServerInstalled`, start server, poll `GET http://localhost:5000/api/health` with backoff (max 60s), override `ServerUrl` to `http://localhost:5000`
- In disconnect: stop local server if it was started by us
- On app shutdown (wire from MainWindow.OnClosing): `_localServerManager.Dispose()`
- Add `IsLocalServerAvailable` property for settings UI

### 2.4 Update Settings UI

Add local server section to the Connection area in `SettingsWindow.axaml` / `SettingsViewModel.cs`:
- `IsLocalServerAvailable` visibility gate (hidden when server not installed)
- `UseLocalServer` checkbox
- `LocalServerAdminPassword` text field
- Explanatory text: "Start a built-in server on localhost:5000 when connecting."

### 2.5 Create `installer/yaat.iss`

Inno Setup script:
- `[Components]`: `client` (fixed, always installed), `server` (optional, in "full" type)
- `[Files]`: client from `artifacts/client/*` → `{app}`, server from `artifacts/server/*` → `{app}\server`
- `[Icons]`: Start Menu + optional desktop shortcut
- `[Run]`: Post-install launch option
- Version parameterized via `/DMyAppVersion=...`
- Icon: `installer/icon.ico` (pre-generated from `icon.png`, committed to repo)

### 2.6 Create `installer/build-installer.ps1`

Local build script:
1. `dotnet publish` client as self-contained win-x64 → `artifacts/client/`
2. `dotnet publish` server (from sibling `../yaat-server/`) as self-contained win-x64 → `artifacts/server/`
3. Run `ISCC.exe` with version parameter

### 2.7 Generate and commit `installer/icon.ico`

One-time conversion from `src/Yaat.Client/Resources/icon.png` using ImageMagick or GIMP. Multi-resolution: 256, 128, 64, 48, 32, 16px.

### 2.8 Create `.github/workflows/release.yml`

Triggers: push to `master`, tags `v*`.

```yaml
name: Release
on:
  push:
    branches: [master]
    tags: ['v*']

jobs:
  build-installer:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd  # v6.0.2
        with:
          path: yaat
          persist-credentials: false

      - uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd  # v6.0.2
        with:
          repository: leftos/yaat-server
          path: yaat-server
          token: ${{ secrets.SERVER_REPO_PAT }}
          persist-credentials: false

      - uses: actions/setup-dotnet@baa11fbfe1d6520db94683bd5c7a3818018e4309  # v5.1.0
        with:
          dotnet-version: '10.0.x'

      - name: Publish Client
        run: >
          dotnet publish yaat/src/Yaat.Client/Yaat.Client.csproj
          -c Release -r win-x64 --self-contained
          -p:PublishSingleFile=true
          -p:IncludeNativeLibrariesForSelfExtract=true
          -o artifacts/client

      - name: Publish Server
        run: >
          dotnet publish yaat-server/src/Yaat.Server/Yaat.Server.csproj
          -c Release -r win-x64 --self-contained
          -p:PublishSingleFile=true
          -p:IncludeNativeLibrariesForSelfExtract=true
          -o artifacts/server

      - name: Install Inno Setup
        run: choco install innosetup -y

      - name: Determine version
        id: version
        shell: bash
        run: |
          if [[ "$GITHUB_REF" == refs/tags/v* ]]; then
            echo "version=${GITHUB_REF#refs/tags/v}" >> "$GITHUB_OUTPUT"
          else
            echo "version=0.0.0-dev" >> "$GITHUB_OUTPUT"
          fi

      - name: Build Installer
        run: >
          & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
          /DMyAppVersion="${{ steps.version.outputs.version }}"
          yaat/installer/yaat.iss

      - uses: actions/upload-artifact@bbbca2ddaa5d8feaa63e36b76fdaad77386f024f  # v7.0.0
        with:
          name: yaat-installer
          path: yaat/artifacts/yaat-setup-*.exe

      - if: startsWith(github.ref, 'refs/tags/v')
        uses: softprops/action-gh-release@5be0e66d93ac7ed76da52eca8bb058f665c3a5fe  # v2.4.2
        with:
          files: yaat/artifacts/yaat-setup-*.exe
```

Required secret: `SERVER_REPO_PAT` — PAT with `repo` scope to checkout private yaat-server.

---

## Implementation Order

### Phase 1 — Docker (yaat-server repo)
- [ ] Create `.dockerignore`
- [ ] Rewrite `src/Yaat.Server/Dockerfile`
- [ ] Create `docker-compose.yml`
- [ ] Create `.github/workflows/docker.yml`
- [ ] Create `docs/deployment-digitalocean.md`
- [ ] Test: `docker build` and `docker compose up` locally

### Phase 2 — Local Server Manager (yaat repo)
- [ ] Create `src/Yaat.Client/Services/LocalServerManager.cs`
- [ ] Update `UserPreferences.cs` — add `UseLocalServer`, `LocalServerAdminPassword`
- [ ] Update `SettingsViewModel.cs` — add local server properties
- [ ] Update `SettingsWindow.axaml` — add local server UI section
- [ ] Update `MainViewModel.cs` — integrate start/stop in connect/disconnect
- [ ] Test: build, run with local server installed alongside

### Phase 3 — Installer (yaat repo)
- [ ] Generate and commit `installer/icon.ico`
- [ ] Create `installer/yaat.iss`
- [ ] Create `installer/build-installer.ps1`
- [ ] Create `.github/workflows/release.yml`
- [ ] Test: local Inno Setup build, install/uninstall cycle

---

## Verification

### Docker
1. `docker build -t yaat-server -f src/Yaat.Server/Dockerfile .` — builds without error
2. `docker run -e Yaat__AdminPassword=test -p 5000:5000 yaat-server` — starts, VNAS downloads succeed
3. `curl http://localhost:5000/api/health` → `[]`
4. `docker compose up` with `.env` — works with volume persistence
5. Push to GitHub → GHCR image appears

### Installer
1. `installer/build-installer.ps1` produces `artifacts/yaat-setup-*.exe`
2. Install with server component → `{app}\server\Yaat.Server.exe` exists
3. Client detects local server, start/stop works
4. Install without server component → local server UI hidden
5. Uninstall removes everything cleanly

### CI
1. Push to yaat-server master → Docker image at `ghcr.io/leftos/yaat-server:latest`
2. Tag `v0.1.0` on yaat → GitHub Release with `yaat-setup-0.1.0.exe`

## Risks

- **PublishSingleFile + SkiaSharp**: Avalonia's native SkiaSharp libs may not work with single-file. Fallback: remove `PublishSingleFile`, use directory-based install (Inno Setup handles this fine).
- **First-run VNAS download**: Local server takes 10-30s on first start to download NavData. Client polls with backoff up to 60s.
- **Port 5000 conflict**: If something else uses port 5000, local server fails. `LocalServerManager` detects via stderr and reports clearly.
