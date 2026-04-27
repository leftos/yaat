# YAAT Installation Guide

There are two ways to install YAAT. Most users want the first option.

- **[Install a prebuilt release](#install-a-prebuilt-release)** — download an installer or portable archive. No terminal, no Git, no .NET SDK. Takes a couple of minutes.
- **[Building from source](#building-from-source)** — clone the repositories and build locally. Use this if you want to host a YAAT server, run against a nightly build, or contribute code changes.

If you're joining a training session hosted by an instructor, the prebuilt release is all you need.

## Install a prebuilt release

### Step 1: Download

Open the [Releases page](https://github.com/leftos/yaat/releases/latest) and grab the asset that matches your platform.

| Platform | Installer (recommended) | Portable |
|----------|-------------------------|----------|
| Windows  | `YaatClient-<ver>-win-Setup.exe` | `YaatClient-<ver>-win-Portable.zip` |
| Linux    | `YaatClient-<ver>-linux.AppImage` | (the AppImage is itself portable) |
| macOS    | `YaatClient-<ver>-osx-Setup.pkg` | `YaatClient-<ver>-osx-Portable.zip` |

The **installers** register YAAT with your OS and keep themselves up to date automatically — when a new version ships, YAAT downloads it in the background and applies it the next time you launch.

The **portable** archives unzip to a folder containing the YAAT executable and its native dependencies (SkiaSharp, Avalonia, LM-Kit). They don't install anything, don't auto-update, and are handy for USB sticks or locked-down machines. Unzip the folder anywhere and run the executable inside. On Linux the AppImage already runs without install, so it's both the recommended installer *and* the portable form.

A separate **YAAT Flight Strips** download (`YaatVStrips-*`) is also available — it's a standalone flight-strips UI for students who want to replace vStrips without installing the full trainer.

### Step 2: Run the installer (or the portable)

**Windows installer:** double-click `YaatClient-<ver>-win-Setup.exe`. Windows SmartScreen may warn that the installer is unsigned — click **More info** → **Run anyway**. YAAT appears in the Start menu when the install finishes.

**Linux AppImage:** mark it executable and run it.

```bash
chmod +x YaatClient-<ver>-linux.AppImage
./YaatClient-<ver>-linux.AppImage
```

On some distros you'll also need `libfontconfig1` and `libfreetype6` for text rendering — see [Linux prerequisites](#linux-prerequisites) below.

**macOS package:** double-click `YaatClient-<ver>-osx-Setup.pkg`. macOS Gatekeeper may block the first launch; if so, right-click the installed app → **Open** → **Open** again.

**Windows portable:** the zip contains flat files (the executable plus its native dependencies). Create an empty folder, extract the zip into it, then double-click `Yaat.Client.exe`. Don't extract into Downloads — the app expects all its sibling files in the same folder.

**macOS portable:** the zip contains `Yaat.Client.app`. Unzip it, then double-click the `.app` (or drag it to `/Applications`). Gatekeeper may block the first launch — right-click → **Open** → **Open** to allow it.

### Step 3: Connect and run a scenario

YAAT opens to an empty main window. Head to **[Getting Started](GETTING_STARTED.md)** to configure your identity, connect to a server, create a room, and load your first scenario.

### NVIDIA GPU acceleration (Windows, optional)

The installer ships with CPU and Vulkan backends out of the box — that's enough for YAAT's speech recognition and LLM features. If you have an NVIDIA card and want CUDA 13 acceleration, open **Settings → Speech → Acceleration** and click **Download CUDA 13 runtime**. YAAT fetches ~534 MB of CUDA libraries into `%LOCALAPPDATA%\yaat\backends\cuda13\` and activates them on the next launch. You can uninstall them from the same screen to reclaim the disk space.

This is opt-in because the CUDA runtime would have added ~1.5 GB to the base installer; most users don't need it.

### Linux prerequisites

SkiaSharp needs system fonts to render text. Install these packages before launching YAAT:

**Debian/Ubuntu:**

```bash
sudo apt install libfontconfig1 libfreetype6 fonts-dejavu-core
```

**Fedora:**

```bash
sudo dnf install fontconfig freetype fonts-dejavu-core
```

**Arch:**

```bash
sudo pacman -S fontconfig freetype2 ttf-dejavu
```

**Wayland note:** Avalonia supports Wayland, but window position save/restore does not work — the app opens in a default position each time. Everything else works normally.

---

## Building from source

Use this path if you want to:

- Run the server yourself instead of connecting to a hosted one
- Track the `main` branch between releases
- Contribute code changes

### Step 1: Install Git

Git is a tool that downloads and tracks code from GitHub.

**Windows:**

1. Go to [https://git-scm.com/downloads/win](https://git-scm.com/downloads/win)
2. Download the **64-bit Git for Windows Setup** installer
3. Run the installer — the default options are fine, just click **Next** through each screen

**macOS:**

Git is included with the Xcode Command Line Tools. Install them by running:

```bash
xcode-select --install
```

Alternatively, install via [Homebrew](https://brew.sh/): `brew install git`

**Linux:**

```bash
# Debian/Ubuntu
sudo apt install git

# Fedora
sudo dnf install git

# Arch
sudo pacman -S git
```

**Verify:** open a terminal (PowerShell on Windows) and run:

```
git --version
```

You should see something like `git version 2.47.1`.

### Step 2: Install .NET 10 SDK

.NET is the framework YAAT is built with. You need the SDK (Software Development Kit) to build and run from source.

1. Go to [https://dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Under **SDK**, download the installer for your platform (Windows x64, macOS, or Linux)
3. Run the installer and follow the prompts (on Linux, you can also install via your package manager — see [Microsoft's instructions](https://learn.microsoft.com/en-us/dotnet/core/install/linux))

To verify, open a terminal and run:

```
dotnet --version
```

You should see a version starting with `10.`.

### Step 3: Download the code

You need two repositories (code projects): the client and the server.

1. Open a terminal (PowerShell on Windows, Terminal on macOS/Linux)
2. Navigate to a folder where you want to keep the code. For example:

```bash
# Windows (PowerShell)
mkdir C:\dev
cd C:\dev

# macOS / Linux
mkdir -p ~/dev
cd ~/dev
```

3. Download both repositories:

```bash
git clone https://github.com/leftos/yaat.git
git clone https://github.com/leftos/yaat-server.git
```

This creates two folders side by side (`yaat` and `yaat-server`). Both folders must be in the same parent directory — the server references shared simulation code from the client repo.

4. Set up git hooks in yaat-server (keeps the shared code reference in sync automatically):

```bash
cd yaat-server
git config core.hooksPath .githooks
```

### Step 4: Build and run

#### Option A: Use the start script (recommended)

The start script builds both projects and launches the server and client together:

```powershell
cd yaat

.\start.ps1          # Windows (PowerShell)
```

```bash
cd yaat

./start.sh           # macOS / Linux
```

The script will:
1. Build both the YAAT client and yaat-server
2. Start the server on `http://localhost:5000`
3. Launch the YAAT client, which connects automatically

Press **Ctrl+C** to stop everything.

**Windows only:** if you see an error like "cannot be loaded because running scripts is disabled," run this once in PowerShell and try again:

```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```

#### Option B: Join a remote server pinned to a specific commit

If you're connecting to a hosted YAAT server and want to track exactly the commit it was built from, the `--sync` flag automatically:

1. Checks out the exact client version the remote server was built with (ensures compatibility)
2. Builds the client
3. Launches it and connects to the remote server

```powershell
.\start.ps1 -Sync https://yaat1.leftos.dev    # Windows (PowerShell)
```

```bash
./start.sh --sync https://yaat1.leftos.dev     # macOS / Linux
```

Your git working tree must be clean (no uncommitted changes). After you're done, return to the latest code with:

```bash
git checkout main
```

If you don't need to track a specific commit, the prebuilt release installer is a simpler way to connect to a hosted server.

#### Option C: Run manually

If you prefer to run each piece separately (useful for troubleshooting):

**Terminal 1 — Server:**

```bash
cd yaat-server
dotnet run --project src/Yaat.Server
```

Wait until you see it's listening (usually on `http://localhost:5000`).

**Terminal 2 — Client:**

```bash
cd yaat
dotnet run --project src/Yaat.Client
```

### Updating to the latest source

When there's a new version on `main`:

```powershell
cd yaat

.\start.ps1 -Pull          # Windows (PowerShell)
```

```bash
cd yaat

./start.sh --pull           # macOS / Linux
```

The `-Pull` / `--pull` flag downloads the latest code for both repos before building. Alternatively, update manually:

```bash
cd yaat
git pull

cd ../yaat-server
git pull
```

Then build and run as usual.

---

## Next steps

Once the client window opens, head to **[Getting Started](GETTING_STARTED.md)** to configure your identity and run your first training session.

## Troubleshooting

### "dotnet" is not recognized (source builds only)

Close and reopen your terminal after installing .NET. If it still doesn't work, the installer may not have added itself to your PATH — try restarting your computer. This doesn't apply to the prebuilt installers, which bundle their own .NET runtime.

### "git" is not recognized (source builds only)

Close and reopen your terminal after installing Git. Same as above — a restart may be needed.

### Build errors mentioning .NET version (source builds only)

Make sure you installed the **.NET 10 SDK**, not an older version or just the Runtime. Run `dotnet --list-sdks` to check.

### Execution policy error on start.ps1 (Windows, source builds only)

Run this once in PowerShell:

```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```

### Windows SmartScreen blocks the installer

The installer is not code-signed (signing certificates are expensive and YAAT is a free project). Click **More info** → **Run anyway** to proceed. The contents come from the GitHub Actions build pipeline — you can verify the build via the [Actions tab](https://github.com/leftos/yaat/actions/workflows/release.yml) if you want to audit what went in.

### Client can't connect to server

- If you're self-hosting, make sure the server is running and you see log output from it
- The client connects to `http://localhost:5000` by default when launched without arguments. To point at a different server, either pass `--autoconnect http://<server>:<port>` on the command line, or enter the URL in **File → Connect** after launch

### Client crashes or shows no text on Linux

Make sure `libfontconfig1` and `libfreetype6` (or your distro's equivalent) are installed. SkiaSharp needs these for font rendering — without them, the app may crash on startup or render blank text.

### Where are the log files?

- **Client log:**
  - Windows: `%LOCALAPPDATA%\yaat\yaat-client.log` (paste this path into File Explorer's address bar)
  - Linux: `~/.local/share/yaat/yaat-client.log`
  - macOS: `~/Library/Application Support/yaat/yaat-client.log`
- **Server log** (self-hosted only): `yaat-server/src/Yaat.Server/bin/Debug/net10.0/yaat-server.log` (relative to where you cloned it)
