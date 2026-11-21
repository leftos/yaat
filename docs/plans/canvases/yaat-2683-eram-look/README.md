# YAAT-2683 mock: how the Scope draws ERAM tracks

The question: how should the YAAT Scope draw ERAM-template tracks (target, history, leader, datablocks) so they read apart from STARS tracks at a glance, in the Mixed template above all?

`index.html` draws the same small Mixed scene in every lane: two STARS tracks drawn as today, and six ERAM tracks (correlated Mode C with a full datablock, ident, coasting, VFR 1200, uncorrelated beacon, uncorrelated primary). The ERAM glyphs are YAAT's own drawings from descriptions of CRC's shapes; no CRC font or bitmap is copied.

## Options and answers

| Question | Options | Answer |
|---|---|---|
| Overall look (Section A) | A1 today; A2 ERAM faithful (glyph per state plus track symbol, glyph history, CRC leader, pixel font, ERAM yellow); A3 ERAM glyphs with YAAT ownership colours; A4 lighter touch (glyph only, today's layout, yellow) | A2 |
| Datablock font (Section B) | VT323 pixel-snapped, B612 Mono, Share Tech Mono, JetBrains Mono (Consolas Bold as today's reference) | VT323, pixel-snapped |
| Symbol set (Section C) | C1 full set (target glyph plus track-position symbol); C2 target glyph only; C3 beacon, primary and VFR only | C1 |
| Where it applies | every ERAM track; Mixed only; a per-profile toggle | every ERAM-template track, Mixed and the ERAM profile alike |

A second page, `sizes.html`, asked about sizes and a realistic STARS font after the first answers: it lays CRC's ERAM and STARS text size tables beside VT323, Terminus TTF Bold (drawn from its bitmap strikes), Spleen, B612 Mono, Share Tech Mono and Relief SingleLine.

| Question (sizes.html) | Options | Answer |
|---|---|---|
| Realistic ERAM font | Terminus Bold strikes; VT323 smoothed; VT323 crisp only | Terminus Bold strikes (replaces the first page's VT323) |
| Realistic STARS font | Terminus Bold strikes; B612 Mono Bold; Share Tech Mono; Spleen 8x16 | Terminus Bold strikes |
| Default sizes | CRC's defaults (ERAM 1, STARS 2); ERAM one step smaller; both one step larger | CRC's defaults, each with its own size setting |
| The text setting | per profile, default realistic; per profile, default compact; one global setting | per profile, default compact |

The answers are recorded in YAAT-2683's description.

## Viewing

The page loads its fonts from `fonts/` beside it, which is not committed. To view it, put these files there, each with its licence (all SIL OFL 1.1):

- `VT323-Regular.ttf` and `OFL-VT323.txt` from https://github.com/google/fonts/tree/main/ofl/vt323
- `B612Mono-Regular.ttf`, `B612Mono-Bold.ttf` and `OFL-B612Mono.txt` from https://github.com/google/fonts/tree/main/ofl/b612mono
- `ShareTechMono-Regular.ttf` and `OFL-ShareTechMono.txt` from https://github.com/google/fonts/tree/main/ofl/sharetechmono
- `JetBrainsMono-Regular.ttf`, `JetBrainsMono-Bold.ttf` and `OFL-JetBrainsMono.txt` copied from `src/Yaat.Client.Strips/Resources/Fonts/`

`sizes.html` also needs `TerminusTTF-Bold.ttf` (version 4.49.3) with `OFL-TerminusTTF.txt` (https://files.ax86.net/terminus-ttf/), `spleen-8x16.otf` with `LICENSE-Spleen.txt` (https://github.com/fcambus/spleen) and `ReliefSingleLineOutline-Regular.otf` with `OFL-ReliefSingleLine.txt` (https://github.com/isdat-type/Relief-SingleLine). Then open `index.html` or `sizes.html` at 100 % browser zoom on a 100 % display scale; a red banner shows when either is off, or when a font failed to load. A one-file copy with the fonts embedded comes from `uv run --no-project python ~/.claude/tools/mock-embed/embed_media.py docs/plans/canvases/yaat-2683-eram-look/index.html .tmp/mocks/yaat-2683-eram-look`.
