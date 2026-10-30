# server-image

Builds the yaat-server container image on this machine with the .NET SDK's container publish, with no Docker engine, and pushes it to `ghcr.io/leftos/yaat-server`. It's the fallback when GitHub Actions can't build the image (`docker-image.yml` in yaat-server). The usual way in is `deploy-to-droplet.ps1 -BuildImageLocally`, alone or with `-BuildImageOnly`.

| File | What it does |
|---|---|
| `build.ps1` | Checks both repos out at the commits named into detached worktrees under `.tmp/server-image-build/`, publishes the two browser apps, then publishes `Yaat.Server` for `linux-x64` with `-t:PublishContainer`. Pushes with `-Tags`, or writes an archive and prints the image's configuration and layers with `-ArchivePath`. |
| `container.targets` | The image: base `aspnet:10.0`, user `root`, working directory `/app/`, port 5000, `ASPNETCORE_URLS` and the two commit variables `/api/version` reports. It mirrors the runtime stage of yaat-server's `src/Yaat.Server/Dockerfile`, and a change to one needs the same change in the other. |
| `web-apps.proj` | Publishes vStrips and vTDLS side by side in one MSBuild run, into the yaat-server worktree's `wwwroot`. |

## Running it

Through the gate, from the yaat repo root. A push needs a token with `write:packages`, forwarded by name so it never appears on a command line:

```bash
export SDK_CONTAINER_REGISTRY_UNAME=leftos SDK_CONTAINER_REGISTRY_PWORD="$(gh auth token)"
pwsh tools/gate.ps1 run --log .tmp/server-image-build.log --timeout 2400 --slot heavy \
  --env SDK_CONTAINER_REGISTRY_UNAME --env SDK_CONTAINER_REGISTRY_PWORD \
  -- pwsh -NoProfile -File tools/server-image/build.ps1 -ServerSha <full sha> -ClientSha <full sha> -Tags "latest,<server7>-<client7>"
```

To inspect an image without pushing, pass `-ArchivePath .tmp/<name>.tar` instead of `-Tags`. A cold build takes about 8 minutes, nearly all of it the two WebAssembly publishes.

## Footguns

- **Never build the working checkouts.** Their git-ignored files would be published into the image. `appsettings.Local.json` is loaded after the environment (`ServerApp.cs`), so it would override production settings, the admin password among them. `build.ps1` always builds clean worktrees and refuses a publish that holds an `appsettings.Local.json`.
- `ContainerWorkingDirectory` needs its trailing slash. Without it the SDK writes the entrypoint as `dotnet /appYaat.Server.dll`.
- The SDK writes a plain tar for `ContainerArchiveOutputPath`, even when the file is named `.tar.gz`.
- `pwsh -File build.ps1 -Tags a,b` arrives as one string, so `build.ps1` splits it on commas.
- The tags copy `docker-image.yml`: `latest` and `<server>-<client>` with 7-character short hashes.
