---
paths:
  - "src/Yaat.Sim/Data/Vnas/**"
  - "reference/cifp/**"
  - "tools/Yaat.CifpInspector/**"
---

# CIFP / ARINC 424 reference parsers

Two open-source CIFP parsers are cloned (git-untracked) into `reference/cifp/` as authoritative references for ARINC 424 column offsets, field meanings, and approach/SID/STAR record handling. Read these before changing `src/Yaat.Sim/Data/Vnas/CifpParser.cs`:

- **`reference/cifp/cifparse/`** — [misterrodg/cifparse](https://github.com/misterrodg/cifparse) — Python parser. The canonical source for column widths. Procedure leg widths are in `src/cifparse/records/procedure/widths.py` (`PrimaryIndices` class).
- **`reference/cifp/parseCifp/`** — [rstory1/parseCifp](https://github.com/rstory1/parseCifp) — Perl parser used by ZOA reference tooling.

If a column offset in YAAT's parser disagrees with `cifparse`, **trust cifparse**. YAAT's parser had a systematic +0/-1 off-by-one in procedure leg fields (arc_radius, theta, rho, course, dist_time, alt_1, alt_2) — see git log for the fix. Re-clone with:

```bash
mkdir -p reference/cifp && cd reference/cifp
git clone --depth 1 https://github.com/misterrodg/cifparse.git
git clone --depth 1 https://github.com/rstory1/parseCifp.git
```

Use `tools/Yaat.CifpInspector` to inspect parsed CIFP procedures from the command line — useful for diagnosing extraction bugs without writing throwaway scratch code.
