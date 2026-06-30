# yaat-crc-config

Tiny standalone tool that adds YAAT server entries to CRC's `DevEnvironments.json`. Equivalent to `Tools → Configure CRC Environments` in the full YAAT client, but distributed as a single small binary so students who only need to point CRC at YAAT don't have to install the full client or vStrips.

## What it does

When you run it, the tool:

1. Locates CRC's per-user config directory:
   - **Windows**: registry `HKCU\Software\CRC\Install_Dir`, then `%LOCALAPPDATA%\CRC`
   - **macOS**: `~/Library/Application Support/CRC`
   - **Linux**: `~/.config/CRC`
2. Reads `DevEnvironments.json` (creates it if missing).
3. Adds (or updates) two entries:
   - `YAAT1` → `https://yaat1.leftos.dev`
   - `YAAT Local` → `http://localhost:5000`
4. Writes the file back, preserving any unrelated entries that were already there.

The tool is **idempotent**: running it twice is safe — the second run will tell you the entries are already present and exit without changes.

The full YAAT Client (`Tools → Configure CRC Environments`) and the `Setup-CrcEnvironment.ps1` PowerShell script all use the **same** entry list, sourced from `docs/crc-environments.json` in this repo.

## Download

Latest binaries are attached to the [crc-config-v\* releases](https://github.com/leftos/yaat/releases?q=crc-config-v) on GitHub:

| Platform | File |
|----------|------|
| Windows  | `yaat-crc-config-windows-x86_64.exe` |
| macOS (Universal: Intel + Apple Silicon) | `yaat-crc-config-macos-universal.dmg` |
| Linux x86_64 | `yaat-crc-config-linux-x86_64` |

`SHA256SUMS.txt` is published alongside each release.

## First-run security warnings

The **macOS** download is a signed, notarized `.dmg`, so it opens without a Gatekeeper workaround. The **Windows** and **Linux** binaries are not code-signed and show a one-time warning:

### Windows — SmartScreen "Windows protected your PC"
1. Click **More info**
2. Click **Run anyway**

After running once, SmartScreen remembers it and won't warn again on the same machine.

### macOS
Open `yaat-crc-config-macos-universal.dmg` and double-click **`yaat-crc-config.app`** inside (or drag it to Applications first). The app is signed with a Developer ID certificate and notarized by Apple, so no right-click-to-open or `xattr` step is needed — on first launch just click **Open** in the standard macOS download prompt.

### Linux
```sh
chmod +x yaat-crc-config-linux-x86_64
./yaat-crc-config-linux-x86_64
```

## Reverting

To remove the YAAT entries:
1. Quit CRC.
2. Open `DevEnvironments.json` in a text editor:
   - Windows: `%LOCALAPPDATA%\CRC\DevEnvironments.json`
   - macOS: `~/Library/Application Support/CRC/DevEnvironments.json`
   - Linux: `~/.config/CRC/DevEnvironments.json`
3. Delete the entries with `"name": "YAAT1"` and `"name": "YAAT Local"`.
4. Save and restart CRC.

## Building from source

Requires a Rust toolchain (stable). From the repo root:

```sh
cd tools/yaat-crc-config
cargo build --release
```

Output binary lives at `target/release/yaat-crc-config[.exe]` (under 600 KB on every platform).

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Success — environments added (or user cancelled at the prompt) |
| 1 | CRC config directory not found |
| 2 | YAAT entries already present, no changes made |
| 3 | I/O failure while reading or writing `DevEnvironments.json` |
