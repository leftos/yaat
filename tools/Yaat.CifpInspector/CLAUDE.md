# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Yaat.CifpInspector is a CLI tool for inspecting parsed CIFP (Coded Instrument Flight Procedures) approach data from FAA ARINC 424 records. It uses `CifpParser` from Yaat.Sim to parse FAACIFP18 files and display approach procedures, leg tables, final-approach-course analysis, and side-by-side comparisons. Used for diagnosing CIFP extraction bugs without writing throwaway scratch code.

## Build & Run

```bash
# Build (from repo root or this directory)
dotnet build

# Run — defaults to current AIRAC CIFP (FAA cache download once per cycle; see CifpPathResolver)
dotnet run --project tools/Yaat.CifpInspector -- --airport KSFO --list-approaches
dotnet run --project tools/Yaat.CifpInspector -- --airport KSFO --approach R10L
dotnet run --project tools/Yaat.CifpInspector -- --airport KCCR --final-course S19R
dotnet run --project tools/Yaat.CifpInspector -- --airport KSFO --compare R10L I28R

# Custom CIFP file
dotnet run --project tools/Yaat.CifpInspector -- --cifp /path/to/FAACIFP18.dat --airport KOAK --list-approaches

# JSON output (pipe to jq, save to file, etc.)
dotnet run --project tools/Yaat.CifpInspector -- --airport KSFO --approach R10L --json
```

## Architecture

Single-file tool (`Program.cs`) with no tests of its own. References `Yaat.Sim` for `CifpParser`, `CifpApproachProcedure`, `CifpLeg`, and related CIFP types. All parsing logic lives in Yaat.Sim — this tool only formats and displays results.

Key subcommands:
- `--list-approaches` — tabular summary of all parsed approaches at an airport
- `--approach <id>` — full leg table (common legs, transitions, missed approach, hold-in-lieu)
- `--final-course <id>` — compares four FAC extraction strategies (extractor/MAP-leg, before-MAP, RW-fix, before-CA) for debugging offset approach course issues
- `--compare <id1> <id2>` — side-by-side common-leg comparison

The `--final-course` command mirrors the strategies in `FinalApproachCourseExtractor` (production code in Yaat.Sim). When debugging FAC issues, use this tool to see which strategy each approach matches.

## CIFP File Resolution

If `--cifp` is not provided, the tool uses `CifpPathResolver` (current AIRAC cycle from `%LOCALAPPDATA%/yaat/cache/cifp/`, downloading from FAA if missing). Pass `--offline` to use only the bundled `TestData/FAACIFP18.gz` and existing cache. Set `YAAT_SKIP_CIFP_DOWNLOAD=1` in tests for the same offline behavior.
