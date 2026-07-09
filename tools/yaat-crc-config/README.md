# yaat-crc-config

Tiny standalone tool that adds YAAT server entries to CRC's `DevEnvironments.json`. Equivalent to `Tools → Configure CRC Environments` in the full YAAT client, but distributed as a single small binary so students who only need to point CRC at YAAT don't have to install the full client or vStrips.

## What it does

When you run it, the tool:

1. Locates CRC's per-user config directory:
   - **Windows**: registry `HKCU\Software\CRC\Install_Dir`, then `%LOCALAPPDATA%\CRC`
   - **macOS**: `~/Library/Application Support/CRC`
   - **Linux**: `~/.config/CRC`
2. Reads `DevEnvironments.json` (creates it if missing).
3. Adds (or updates) one entry:
   - `YAAT1` → `https://yaat1.leftos.dev`
4. Writes the file back, preserving any unrelated entries that were already there.

The tool is **idempotent**: running it twice is safe — the second run will tell you the entries are already present and exit without changes.

If you run a YAAT server yourself, add its entry to `DevEnvironments.json` by hand (see [Reverting](#reverting) for the file's location) — the tool only configures the hosted server.

The full YAAT Client (`Tools → Configure CRC Environments`) and the `Setup-CrcEnvironment.ps1` PowerShell script all use the **same** entry list, sourced from `docs/crc-environments.json` in this repo.

## Download

Latest binaries are attached to the [crc-config-v\* releases](https://github.com/leftos/yaat/releases?q=crc-config-v) on GitHub:

| Platform | File |
|----------|------|
| Windows  | `yaat-crc-config-windows-x86_64.exe` |
| macOS (Universal: Intel + Apple Silicon) | `yaat-crc-config-macos-universal.dmg` |
| Linux x86_64 | `yaat-crc-config-linux-x86_64` |

`SHA256SUMS.txt` is published alongside each release.

## Running it

### macOS
Open `yaat-crc-config-macos-universal.dmg` and double-click **`yaat-crc-config.app`** (or drag it to Applications first). The app is signed with a Developer ID certificate and notarized by Apple, so on first launch just click **Open** in the standard macOS download prompt and it runs.

### Windows
Run `yaat-crc-config-windows-x86_64.exe`. If SmartScreen appears, click **More info** → **Run anyway** (it remembers your choice afterward).

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
3. Delete the entry with `"name": "YAAT1"`.
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
