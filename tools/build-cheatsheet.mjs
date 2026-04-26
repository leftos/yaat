#!/usr/bin/env node
// Generates docs/command-cheatsheet.html from docs/command-cheatsheet.json.
// Run plain to regenerate. Run with --check to fail when the HTML is stale (CI guard).

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { argv, exit } from 'node:process';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..');
const jsonPath = resolve(repoRoot, 'docs/command-cheatsheet.json');
const htmlPath = resolve(repoRoot, 'docs/command-cheatsheet.html');

const data = JSON.parse(readFileSync(jsonPath, 'utf8'));
const html = render(data);

const checkMode = argv.includes('--check');
if (checkMode) {
  let existing = '';
  try {
    existing = readFileSync(htmlPath, 'utf8');
  } catch {
    /* missing file is treated as out-of-date below */
  }
  if (existing !== html) {
    console.error(
      `command-cheatsheet.html is out of date.\nRun: node tools/build-cheatsheet.mjs`,
    );
    exit(1);
  }
  console.log('command-cheatsheet.html is up to date.');
} else {
  writeFileSync(htmlPath, html);
  console.log(`Wrote ${htmlPath}`);
}

function render(data) {
  const intro = (data.intro ?? []).map(renderInline).join('  &nbsp;|&nbsp;  ');
  const sections = data.categories.map(renderCategory).join('\n');

  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>YAAT Command Cheatsheet</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="generator" content="tools/build-cheatsheet.mjs">
<style>
:root { color-scheme: light; --bg: #ffffff; --fg: #1a1a1a; --muted: #555; --rule: #f0f0f0; --hd: #2a2a2a; --code-bg: #f0f0f0; --accent: #0066cc; }
* { box-sizing: border-box; margin: 0; padding: 0; }
html, body { background: var(--bg); color: var(--fg); }
body {
  font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
  font-size: clamp(11px, 0.55vw + 9px, 14px);
  line-height: 1.4;
  padding: 16px;
}
.filter {
  position: sticky;
  top: 0;
  z-index: 10;
  background: rgba(255, 255, 255, 0.96);
  backdrop-filter: blur(6px);
  margin: -16px -16px 12px;
  padding: 10px 16px;
  border-bottom: 1px solid #ddd;
  display: flex;
  gap: 12px;
  align-items: center;
}
.filter input {
  flex: 1;
  max-width: 480px;
  padding: 6px 10px;
  border: 1px solid #bbb;
  border-radius: 4px;
  font: inherit;
}
.filter input:focus { outline: 2px solid var(--accent); outline-offset: -1px; border-color: var(--accent); }
.filter .count { color: var(--muted); font-size: 0.85em; white-space: nowrap; }
h1 {
  text-align: center;
  font-size: clamp(15px, 1.2vw + 10px, 22px);
  letter-spacing: 0.4px;
  margin-bottom: 4px;
}
.sub {
  text-align: center;
  font-size: 0.85em;
  color: var(--muted);
  margin-bottom: 12px;
  border-bottom: 2px solid #333;
  padding-bottom: 8px;
}
.sub code { font-size: 0.95em; }
.sheet {
  columns: 22rem auto;
  column-gap: 1.5rem;
}
.cat {
  break-inside: avoid;
  margin-bottom: 8px;
}
.cat[hidden] { display: none; }
.cat h2 {
  background: var(--hd);
  color: #fff;
  padding: 2px 6px;
  font-size: 0.85em;
  letter-spacing: 0.4px;
  text-transform: uppercase;
  margin-bottom: 2px;
}
.row {
  display: flex;
  gap: 6px;
  padding: 1px 2px;
  border-bottom: 1px solid var(--rule);
}
.row[hidden] { display: none; }
.verb {
  font-family: "Cascadia Mono", Consolas, ui-monospace, monospace;
  font-weight: 700;
  white-space: nowrap;
  min-width: 5.2em;
}
.alias {
  font-family: "Cascadia Mono", Consolas, ui-monospace, monospace;
  color: var(--muted);
  white-space: nowrap;
  min-width: 4em;
  font-size: 0.92em;
}
.desc { color: #333; flex: 1; }
.desc .ex { color: var(--muted); margin-left: 4px; }
.row.global .verb::after { content: " *"; color: #999; font-weight: 400; }
.note {
  font-style: italic;
  color: var(--muted);
  font-size: 0.88em;
  padding: 2px 0 2px 4px;
  break-inside: avoid;
}
code {
  font-family: "Cascadia Mono", Consolas, ui-monospace, monospace;
  background: var(--code-bg);
  padding: 0 3px;
  border-radius: 2px;
  font-size: 0.92em;
}

@media (prefers-color-scheme: dark) {
  :root { --bg: #1a1a1a; --fg: #e6e6e6; --muted: #9a9a9a; --rule: #2a2a2a; --hd: #2a2a2a; --code-bg: #2a2a2a; --accent: #4a9eff; }
  .filter { background: rgba(26,26,26,0.96); border-bottom-color: #333; }
  .filter input { background: #222; color: #e6e6e6; border-color: #444; }
  .sub { border-bottom-color: #555; }
  .desc { color: #d0d0d0; }
}

@media print {
  @page { size: letter landscape; margin: 0.35in; }
  body {
    padding: 8px;
    font-size: 6.5pt;
    line-height: 1.22;
  }
  .filter { display: none; }
  .sheet { columns: 4; column-gap: 10px; column-rule: 1px solid #ddd; }
  h1 { font-size: 10pt; margin-bottom: 2px; }
  .sub { font-size: 6pt; padding-bottom: 3px; margin-bottom: 4px; border-bottom-width: 1.5px; }
  .cat h2 { font-size: 7pt; padding: 1px 4px; margin: 3px 0 1px 0; }
  .row { font-size: 6.5pt; padding: 0.5px 0; }
  .verb { min-width: 52px; font-size: 6.5pt; }
  .alias { min-width: 42px; font-size: 6pt; }
  .note { font-size: 5.5pt; padding: 0.5px 0 1px 2px; }
  code { font-size: 6pt; padding: 0 1.5px; }
  .row.global .verb::after { content: " *"; }
}
</style>
</head>
<body>

<div class="filter">
  <input id="q" type="search" placeholder="Filter commands…" aria-label="Filter commands">
  <span class="count" id="count"></span>
</div>

<h1>YAAT Command Cheatsheet</h1>
<div class="sub">${intro}</div>

<div class="sheet">
${sections}
</div>

<script>
(() => {
  const input = document.getElementById('q');
  const count = document.getElementById('count');
  const cats = [...document.querySelectorAll('.cat')];
  const update = () => {
    const q = input.value.trim().toLowerCase();
    let visible = 0, total = 0;
    cats.forEach(cat => {
      const rows = [...cat.querySelectorAll('.row')];
      const catMatch = q.length > 0 && (cat.dataset.name || '').includes(q);
      let hits = 0;
      rows.forEach(row => {
        total++;
        const text = row.dataset.search;
        const match = !q || catMatch || text.includes(q);
        row.hidden = !match;
        if (match) { hits++; visible++; }
      });
      cat.hidden = q.length > 0 && !catMatch && hits === 0;
    });
    count.textContent = q ? visible + ' / ' + total + ' match' : '';
  };
  input.addEventListener('input', update);
  update();
})();
</script>

</body>
</html>
`;
}

function renderCategory(cat) {
  const rows = cat.rows.map(renderRow).join('\n');
  const notes = (cat.notes ?? [])
    .map(n => `<div class="note">${renderInline(n)}</div>`)
    .join('\n');
  return `<section class="cat" id="${escapeAttr(cat.id)}" data-name="${escapeAttr(cat.name.toLowerCase())}">
<h2>${escapeText(cat.name)}</h2>
${rows}${notes ? '\n' + notes : ''}
</section>`;
}

function renderRow(row) {
  const aliases = (row.aliases ?? []).join(' ');
  const examples = (row.examples ?? [])
    .map(e => `<code>${escapeText(e)}</code>`)
    .join(' ');
  const desc = examples
    ? `${escapeText(row.description)}: <span class="ex">${examples}</span>`
    : escapeText(row.description);
  const cls = row.global ? 'row global' : 'row';
  const search = [row.verb, ...(row.aliases ?? []), row.description, ...(row.examples ?? [])]
    .join(' ')
    .toLowerCase();
  return `<div class="${cls}" data-search="${escapeAttr(search)}"><span class="verb">${escapeText(row.verb)}</span><span class="alias">${escapeText(aliases)}</span><span class="desc">${desc}</span></div>`;
}

// Render a string that may contain backtick-delimited code spans.
function renderInline(s) {
  if (!s) return '';
  let out = '';
  let i = 0;
  while (i < s.length) {
    const tick = s.indexOf('`', i);
    if (tick < 0) {
      out += escapeText(s.slice(i));
      break;
    }
    out += escapeText(s.slice(i, tick));
    const end = s.indexOf('`', tick + 1);
    if (end < 0) {
      out += escapeText(s.slice(tick));
      break;
    }
    out += `<code>${escapeText(s.slice(tick + 1, end))}</code>`;
    i = end + 1;
  }
  return out;
}

function escapeText(s) {
  return String(s)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');
}

function escapeAttr(s) {
  return escapeText(s).replaceAll('"', '&quot;');
}
