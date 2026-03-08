# YAAT Installation Guide

This guide walks you through installing and running YAAT from scratch, assuming no prior experience with Git or .NET development.

## What You Need

- **Windows 10 or later**, **macOS**, or **Linux** (YAAT uses Avalonia UI and runs on all three platforms)
- **An internet connection** (to download tools and code)

## Platform Notes

The instructions below use Windows/PowerShell examples, but the build and run commands (`dotnet build`, `dotnet run`) are identical on all platforms. Adapt paths and shell commands to your OS as needed.

### Linux

Install these system packages before building (SkiaSharp requires them for font rendering):

**Debian/Ubuntu:**

```bash
sudo apt install libfontconfig1 libfreetype6
```

**Fedora:**

```bash
sudo dnf install fontconfig freetype
```

**Arch:**

```bash
sudo pacman -S fontconfig freetype2
```

You also need a monospace font installed. Most distros include `DejaVu Sans Mono` by default; if not, install it (e.g., `sudo apt install fonts-dejavu-core`).

**Wayland note:** Avalonia supports Wayland but window position save/restore may not work (Wayland doesn't allow apps to set their own window position). Everything else works normally.

### macOS

No extra dependencies needed. .NET and Avalonia handle everything.

## Step 1: Install Git

Git is a tool that downloads and tracks code from GitHub.

1. Go to [https://git-scm.com/downloads/win](https://git-scm.com/downloads/win)
2. Download the **64-bit Git for Windows Setup** installer
3. Run the installer — the default options are fine, just click **Next** through each screen
4. When it finishes, you now have Git installed

To verify it worked, open a new **PowerShell** window (search "PowerShell" in the Start menu) and type:

```
git --version
```

You should see something like `git version 2.47.1.windows.1`.

## Step 2: Install .NET 10 SDK

.NET is the framework YAAT is built with. You need the SDK (Software Development Kit) to build and run it.

1. Go to [https://dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Under **SDK**, download the installer for your platform (Windows x64, macOS, or Linux)
3. Run the installer and follow the prompts (on Linux, you can also install via your package manager — see [Microsoft's instructions](https://learn.microsoft.com/en-us/dotnet/core/install/linux))

To verify, open a new PowerShell window and type:

```
dotnet --version
```

You should see a version starting with `10.`.

## Step 3: Download the Code

You need two repositories (code projects): the client and the server.

1. Open PowerShell
2. Navigate to a folder where you want to keep the code. For example, to use `C:\dev`:

```powershell
mkdir C:\dev
cd C:\dev
```

3. Download both repositories:

```powershell
git clone https://github.com/leftos/yaat.git
git clone https://github.com/leftos/yaat-server.git
```

This creates two folders: `C:\dev\yaat` and `C:\dev\yaat-server`.

**Important:** Both folders must be side by side in the same parent directory. The server references shared code from the client repo, so they need to be next to each other.

## Step 4: Build and Run

### Option A: Use the Start Script (Recommended)

The easiest way to run everything is the included start script. It builds and launches both the server and client for you.

1. Open PowerShell
2. Navigate to the yaat folder:

```powershell
cd C:\dev\yaat
```

3. Run the start script:

```powershell
.\start.ps1
```

If you get an error about "execution policies", run this first and try again:

```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```

The script builds both projects and starts them. You'll see log output from both the server and client. Press **Ctrl+C** to stop everything.

### Option B: Run Manually

If you prefer to run each piece separately (useful for troubleshooting):

**Terminal 1 — Server:**

```powershell
cd C:\dev\yaat-server
dotnet run --project src/Yaat.Server
```

Wait until you see it's listening (usually on `http://localhost:5000`).

**Terminal 2 — Client:**

```powershell
cd C:\dev\yaat
dotnet run --project src/Yaat.Client
```

## Step 5: First-Time Setup

Once the client window opens:

1. Open **Settings** (gear icon or menu)
2. Go to the **Identity** tab
3. Fill in:
   - **VATSIM CID** — your VATSIM ID number
   - **Initials** — any two letters (e.g., your initials)
   - **ARTCC ID** — the facility you want to train (e.g., `ZOA`)
4. Go to the **Connection** tab and confirm the server URL is `http://localhost:5000`
5. Close Settings
6. **File > Connect** to connect to the server

You're now ready to create a room and load a scenario. See the [User Guide](USER_GUIDE.md) for detailed usage instructions.

## Step 6: Connect CRC (Optional, Windows Only)

If you want students to connect with [CRC](https://crc.virtualnas.net), you need to configure CRC to point at your YAAT server. See the [User Guide — Connecting CRC](USER_GUIDE.md#connecting-crc-optional) for setup options and connection instructions.

## Updating to the Latest Version

When there's a new version available:

```powershell
cd C:\dev\yaat
.\start.ps1 -Pull
```

The `-Pull` flag downloads the latest code for both repos before building. Alternatively, you can update manually:

```powershell
cd C:\dev\yaat
git pull

cd C:\dev\yaat-server
git pull
```

Then build and run as usual.

## Troubleshooting

### "dotnet" is not recognized

Close and reopen PowerShell after installing .NET. If it still doesn't work, the .NET installer may not have added itself to your PATH — try restarting your computer.

### "git" is not recognized

Close and reopen PowerShell after installing Git. Same as above — a restart may be needed.

### Build errors mentioning .NET version

Make sure you installed the **.NET 10 SDK**, not an older version or just the Runtime. Run `dotnet --list-sdks` to check.

### Execution policy error on start.ps1

Run this once in PowerShell:

```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```

### Client can't connect to server

- Make sure the server is running and you see log output from it
- Check that the server URL in Settings matches (default: `http://localhost:5000`)
- If you changed the server port, update the client's Connection setting to match

### Client crashes or shows no text on Linux

Make sure `libfontconfig1` and `libfreetype6` (or your distro's equivalent) are installed. SkiaSharp needs these for font rendering — without them, the app may crash on startup or render blank text.

### Where are log files?

- **Client log:**
  - Windows: `%LOCALAPPDATA%\yaat\yaat-client.log` (paste this path into File Explorer's address bar)
  - Linux: `~/.local/share/yaat/yaat-client.log`
  - macOS: `~/Library/Application Support/yaat/yaat-client.log`
- **Server log:** `yaat-server/src/Yaat.Server/bin/Debug/net10.0/yaat-server.log` (relative to where you cloned it)
