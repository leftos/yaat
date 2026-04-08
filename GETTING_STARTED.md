# Getting Started with YAAT

This guide walks you through your first YAAT session — from launching the app to issuing your first command. It assumes you've already installed everything from the [Installation Guide](INSTALL.md).

## What is YAAT?

YAAT is a tool for ATC (air traffic control) training instructors and RPOs (Remote Pilot Operators). You use it to control simulated aircraft while students view them on their radar scopes in [CRC](#glossary) (Consolidated Radar Client).

**Key concepts:**

- **Room** — an isolated training session. Each room has its own aircraft, weather, and participants. Multiple rooms can run simultaneously on the same server.
- **Scenario** — a predefined set of aircraft with starting positions, flight plans, and spawn timing. Loading a scenario populates the room with traffic.
- **RPO** — a YAAT user who controls simulated aircraft by issuing commands. Multiple RPOs can work the same room.
- **Student** — a trainee using CRC to practice radar/tower operations. Students see the simulated traffic but don't use YAAT directly.

## Step 1: Launch

Start the server and client together using the start script:

```powershell
.\start.ps1          # Windows (PowerShell)
```

```bash
./start.sh           # macOS / Linux
```

Or launch the client manually (requires a running server):

```bash
dotnet run --project src/Yaat.Client
```

The client connects to `http://localhost:5000` by default.

### Joining a remote server

If an instructor is already hosting a YAAT server (e.g., `https://yaat1.leftos.dev`), you don't need to run your own. The `--sync` flag automatically checks out the compatible client version, builds, and connects:

```powershell
.\start.ps1 -Sync https://yaat1.leftos.dev    # Windows
```

```bash
./start.sh --sync https://yaat1.leftos.dev     # macOS / Linux
```

Your git working tree must be clean (no uncommitted changes). After you're done, return to the latest code with `git checkout main`.

## Step 2: Configure Your Identity

Before connecting, open **Settings** (gear icon) and fill in the **Identity** tab:

| Field | What to enter |
|-------|---------------|
| **[VATSIM](#glossary) CID** | Your VATSIM ID number |
| **Initials** | Any two letters (e.g., your initials — "JE", "AB") |
| **[ARTCC](#glossary) ID** | The facility you're training (e.g., `ZOA`, `ZLA`, `ZNY`) |

All three fields are required — you cannot connect without them.

## Step 3: Connect and Create a Room

1. **File > Connect** — connects to the server
2. The **room list** appears. Either:
   - **Create** a new room (give it a name), or
   - **Join** an existing room that another instructor created
3. You're now in a room, ready to load traffic

## Step 4: Load a Scenario

1. **Scenario > Load Scenario...** opens the scenario browser
2. Two tabs:
   - **ARTCC Scenarios** — training scenarios from the [vNAS](#glossary) data API for your ARTCC. Use the Airport filter to narrow results.
   - **Local Files** — browse for ATCTrainer-format JSON scenario files on your machine
3. Select a scenario and click **Load** (or double-click)

Aircraft spawn at their configured starting positions. The window title updates to show the room and scenario name.

## Step 5: Issue Your First Command

With a scenario loaded, you'll see aircraft in the **Aircraft List** (the main grid). Try these:

1. **Select an aircraft** — click a row in the grid, or type a callsign in the command bar and press **Numpad +**
2. **Issue a command** — with an aircraft selected, type a command and press **Enter**:

| Try this | What it does |
|----------|-------------|
| `FH 270` | Fly heading 270 |
| `CM 100` | Climb and maintain 10,000 ft |
| `SPD 250` | Maintain 250 knots |
| `CM 050, FH 090` | Climb to 5,000 ft **and** turn to 090 simultaneously |

You can also prefix any command with a callsign: `UAL123 FH 270`.

The **terminal panel** (below the grid) shows command confirmations, errors, and aircraft responses.

## Step 6: Explore the Views

YAAT has three main views, accessible via tabs or pop-out windows (**View** menu):

- **Aircraft List** — data grid with all aircraft state (altitude, speed, heading, phase, etc.)
- **Ground View** — airport surface map for tower operations. Right-click aircraft or taxiway nodes for context menus (taxi routes, hold short, cross runway).
- **Radar View** — STARS-style scope for approach/departure. Shows targets, video maps, and data blocks. Right-click for heading, altitude, and approach options.

## Step 7: Load Weather (Optional)

Weather affects aircraft performance — headwinds reduce ground speed, tailwinds increase it.

- **Scenario > Load Weather...** — browse ARTCC or local weather profiles
- **Scenario > Load Live Weather** — fetch real-world METARs and winds aloft
- **Scenario > New Weather...** — create a custom weather profile

## Helping Students Connect CRC

If you're setting up a training session with students, see the [CRC Setup section](USER_GUIDE.md#connecting-crc-for-students) in the User Guide for how to configure CRC to connect to your YAAT server.

## Next Steps

- **[Command Reference](COMMANDS.md)** — complete list of every command, alias, and example
- **[User Guide](USER_GUIDE.md)** — detailed documentation of all features, views, settings, and workflows

## Glossary

| Term | Definition |
|------|-----------|
| **ARTCC** | Air Route Traffic Control Center — a facility that manages airspace (e.g., ZOA = Oakland Center, ZLA = Los Angeles Center) |
| **ATC** | Air Traffic Control |
| **CRC** | Consolidated Radar Client — the VATSIM radar client that students use to work scopes |
| **RPO** | Remote Pilot Operator — a YAAT user who controls simulated aircraft |
| **VATSIM** | Virtual Air Traffic Simulation Network — a community for online ATC and pilot simulation |
| **vNAS** | Virtual National Airspace System — VATSIM's infrastructure for facility data, nav data, and scenarios |
