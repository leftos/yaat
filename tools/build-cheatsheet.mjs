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
  const intro = (data.intro ?? [])
    .map(s => `<span class="pill">${renderInline(s)}</span>`)
    .join('');
  const sections = data.categories.map(renderCategory).join('\n');
  const profiles = data.profiles ?? [];
  const profileOptions = profiles
    .map(p => `<option value="${escapeAttr(p.id)}">${escapeText(p.label)}</option>`)
    .join('');
  const profilesInline = JSON.stringify(profiles).replace(/</g, '\\u003c');

  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>YAAT Command Cheatsheet</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="generator" content="tools/build-cheatsheet.mjs">
<style>
:root {
  color-scheme: light;
  --bg: #ffffff;
  --fg: #1a1a1a;
  --muted: #555;
  --rule: #e6e6e6;
  --code-bg: #f0f0f0;
  --accent: #0066cc;
  --accent-tint: rgba(0, 102, 204, 0.12);
  --row-alt: rgba(0, 0, 0, 0.025);
}
* { box-sizing: border-box; margin: 0; padding: 0; }
html, body { background: var(--bg); color: var(--fg); }
body {
  font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
  font-size: clamp(11px, 0.55vw + 9px, 14px);
  line-height: 1.4;
}
.topbar {
  position: sticky;
  top: 0;
  z-index: 10;
  display: flex;
  gap: 14px;
  align-items: center;
  padding: 8px 16px;
  background: rgba(255, 255, 255, 0.96);
  backdrop-filter: blur(6px);
  border-bottom: 1px solid #ddd;
}
.topbar h1 {
  font-size: clamp(15px, 1.2vw + 10px, 22px);
  margin: 0;
  white-space: nowrap;
  flex: 0 0 auto;
}
.filter {
  display: flex;
  gap: 10px;
  align-items: center;
  flex: 1;
  min-width: 0;
}
.filter input {
  flex: 1;
  max-width: 480px;
  padding: 5px 10px;
  border: 1px solid #bbb;
  border-radius: 4px;
  font: inherit;
}
.filter input:focus { outline: 2px solid var(--accent); outline-offset: -1px; border-color: var(--accent); }
.filter .count { color: var(--muted); font-size: 0.85em; white-space: nowrap; }
.filter .hint {
  color: var(--muted);
  font-size: 0.78em;
  white-space: nowrap;
}
@media (max-width: 720px) {
  .filter .hint { display: none; }
}
.filter .hint kbd {
  font-family: "Cascadia Mono", Consolas, ui-monospace, monospace;
  border: 1px solid var(--rule);
  border-bottom-width: 2px;
  border-radius: 3px;
  padding: 0 4px;
  font-size: 0.92em;
  background: var(--bg);
}
.filter button,
.filter select {
  padding: 4px 8px;
  border: 1px solid #bbb;
  border-radius: 4px;
  background: transparent;
  color: var(--fg);
  font: inherit;
  font-size: 0.85em;
  cursor: pointer;
  white-space: nowrap;
}
.filter select { padding-right: 4px; }
.filter button:hover,
.filter select:hover { border-color: var(--accent); color: var(--accent); }
.filter select:focus { outline: 2px solid var(--accent); outline-offset: -1px; border-color: var(--accent); }
.sub {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  align-items: center;
  gap: 4px 18px;
  padding: 6px 16px 8px;
  border-bottom: 2px solid #333;
  font-size: 0.85em;
  color: var(--muted);
}
.sub .pill { white-space: nowrap; }
.sub code { font-size: 0.95em; }
.sheet {
  columns: 24rem auto;
  column-gap: 1.5rem;
  padding: 8px 16px;
}
.cat {
  break-inside: avoid;
  margin-bottom: 6px;
  scroll-margin-top: 60px;
}
.cat[hidden] { display: none; }
.cat > summary {
  background: transparent;
  color: var(--fg);
  border-left: 3px solid var(--accent);
  border-bottom: 1px solid var(--rule);
  padding: 2px 6px 2px 8px;
  font-size: 0.86em;
  font-weight: 700;
  text-transform: uppercase;
  margin: 6px 0 3px;
  cursor: pointer;
  list-style: none;
  user-select: none;
  display: flex;
  align-items: center;
  gap: 6px;
}
.cat > summary::-webkit-details-marker { display: none; }
.cat > summary::before {
  content: '';
  display: inline-block;
  width: 0;
  height: 0;
  border-left: 4px solid currentColor;
  border-top: 3px solid transparent;
  border-bottom: 3px solid transparent;
  transition: transform 0.12s;
  flex: 0 0 auto;
}
.cat[open] > summary::before { transform: rotate(90deg); }
.cat-body {
  display: grid;
  grid-template-columns: max-content max-content 1fr;
  column-gap: 8px;
  row-gap: 0;
}
.cat-body--no-alias { grid-template-columns: max-content 1fr; }
.cat-body--no-alias .alias { display: none; }
.row {
  display: grid;
  grid-column: 1 / -1;
  grid-template-columns: subgrid;
  padding: 0 4px;
  border-radius: 2px;
  scroll-margin-top: 60px;
}
.row[hidden] { display: none; }
.row.r-alt { background: var(--row-alt); }
.row:target { outline: 2px solid var(--accent); background: var(--accent-tint); }
.row.kbd-focus { outline: 2px solid var(--accent); outline-offset: 1px; }
.verb {
  font-family: "Cascadia Mono", Consolas, ui-monospace, monospace;
  font-weight: 700;
  white-space: nowrap;
}
.alias {
  font-family: "Cascadia Mono", Consolas, ui-monospace, monospace;
  color: var(--muted);
  white-space: nowrap;
  font-size: 0.92em;
}
.desc { color: #333; }
.desc .ex { color: var(--muted); margin-left: 4px; }
.row.global .verb::after {
  content: 'G';
  display: inline-block;
  margin-left: 5px;
  padding: 0 3px;
  font-size: 0.62rem;
  font-weight: 700;
  font-family: "Segoe UI", system-ui, sans-serif;
  color: var(--accent);
  border: 1px solid var(--accent);
  border-radius: 2px;
  vertical-align: middle;
  letter-spacing: 0.4px;
  line-height: 1.4;
}
mark {
  background: var(--accent-tint);
  color: inherit;
  border-radius: 2px;
  padding: 0 1px;
}
.note {
  grid-column: 1 / -1;
  font-style: normal;
  color: var(--fg);
  font-size: 0.88em;
  padding: 4px 8px;
  margin: 4px 0 2px;
  border-left: 2px solid var(--accent);
  background: var(--code-bg);
  border-radius: 0 2px 2px 0;
  break-inside: avoid;
}
code {
  font-family: "Cascadia Mono", Consolas, ui-monospace, monospace;
  background: var(--code-bg);
  padding: 0 3px;
  border-radius: 2px;
  font-size: 0.92em;
}
.toast {
  position: fixed;
  bottom: 16px;
  right: 16px;
  z-index: 100;
  padding: 6px 12px;
  background: var(--fg);
  color: var(--bg);
  border-radius: 4px;
  font-size: 0.85em;
  font-weight: 500;
  opacity: 0;
  pointer-events: none;
  transform: translateY(8px);
  transition: opacity 0.15s, transform 0.15s;
}
.toast.show { opacity: 1; transform: translateY(0); }
.print-footer { display: none; }

@media (prefers-color-scheme: dark) {
  :root {
    --bg: #1a1a1a;
    --fg: #e6e6e6;
    --muted: #9a9a9a;
    --rule: #2a2a2a;
    --code-bg: #2a2a2a;
    --accent: #4a9eff;
    --accent-tint: rgba(74, 158, 255, 0.18);
    --row-alt: rgba(255, 255, 255, 0.04);
  }
  .topbar { background: rgba(26, 26, 26, 0.96); border-bottom-color: #333; }
  .filter input { background: #222; color: #e6e6e6; border-color: #444; }
  .filter .hint kbd { background: #222; }
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
  .topbar { display: block; position: static; padding: 0; border: 0; background: none; }
  .topbar h1 { text-align: center; font-size: 10pt; margin-bottom: 2px; white-space: normal; }
  .filter, .toast { display: none; }
  .sub { font-size: 6pt; padding: 0 0 3px; margin-bottom: 4px; border-bottom-width: 1.5px; gap: 2px 12px; }
  .sheet { columns: 4; column-gap: 10px; column-rule: 1px solid #ddd; padding: 0; }
  .cat { margin-bottom: 2px; }
  .cat > summary {
    background: var(--code-bg);
    color: var(--fg);
    border-left: 2px solid var(--fg);
    border-bottom: 0;
    font-size: 7pt;
    padding: 1px 4px 1px 6px;
    margin: 3px 0 1px;
    cursor: default;
  }
  .cat > summary::before { display: none; }
  .row { font-size: 6.5pt; padding: 0 2px; outline: 0 !important; background: transparent !important; }
  .row.r-alt { background: rgba(0, 0, 0, 0.04) !important; }
  .verb { font-size: 6.5pt; }
  .alias { font-size: 6pt; }
  .note { font-size: 5.5pt; padding: 1px 4px; margin: 1px 0; }
  code { font-size: 6pt; padding: 0 1.5px; }
  .row.global .verb::after {
    content: 'G';
    border: 0.5pt solid currentColor;
    padding: 0 1.5pt;
    margin-left: 2pt;
    color: inherit;
    font-weight: 700;
    font-size: 5pt;
    font-family: inherit;
    letter-spacing: 0;
    line-height: 1.3;
    border-radius: 1pt;
  }
  mark { background: transparent; color: inherit; padding: 0; }
  .print-footer { display: block; text-align: center; font-size: 5.5pt; color: #888; margin-top: 6pt; }
}
</style>
</head>
<body>

<header class="topbar">
  <h1>YAAT Command Cheatsheet</h1>
  <div class="filter">
    <input id="q" type="search" placeholder="Filter commands…  (try cat:ground)" aria-label="Filter commands" autocomplete="off">
    <span class="hint"><kbd>/</kbd> focus &middot; <kbd>↓↑</kbd> nav &middot; <kbd>Enter</kbd> copy</span>
    <span class="count" id="count"></span>
    <select id="profile" aria-label="Category preset" title="Quick-pick which categories to show for a training role">
      <option value="">Profile…</option>
      ${profileOptions}
    </select>
    <button id="toggle-all" type="button" title="Expand or collapse all categories">Collapse all</button>
  </div>
</header>
<div class="sub">${intro}</div>

<div class="sheet">
${sections}
</div>

<div class="print-footer" aria-hidden="true">YAAT Command Cheatsheet — generated by tools/build-cheatsheet.mjs</div>

<script>
(() => {
  const $ = (s, ctx = document) => ctx.querySelector(s);
  const $$ = (s, ctx = document) => [...ctx.querySelectorAll(s)];
  const input = $('#q');
  const count = $('#count');
  const toggleAll = $('#toggle-all');
  const profileSelect = $('#profile');
  const cats = $$('.cat');
  const allRows = $$('.row');
  const storeKey = 'yaat-cheatsheet-collapsed-v1';
  const profileKey = 'yaat-cheatsheet-profile-v1';
  const profiles = ${profilesInline};
  let collapsed = {};
  try { collapsed = JSON.parse(localStorage.getItem(storeKey) || '{}') || {}; } catch (_) { collapsed = {}; }
  // Restore last-applied profile label (does not re-apply state on load).
  try {
    const lastProfile = localStorage.getItem(profileKey) || '';
    if (lastProfile && profileSelect.querySelector(\`option[value="\${lastProfile}"]\`)) {
      profileSelect.value = lastProfile;
    }
  } catch (_) {}

  // Cache original verb/alias text for highlighting & copy.
  for (const row of allRows) {
    const v = row.querySelector('.verb');
    const a = row.querySelector('.alias');
    if (v) row.dataset.verbText = v.textContent;
    if (a) row.dataset.aliasText = a.textContent;
  }

  // Apply persisted collapsed state on load.
  for (const cat of cats) cat.open = !collapsed[cat.id];

  // Persist user toggles via summary clicks (programmatic .open changes don't fire click).
  for (const cat of cats) {
    const summary = cat.querySelector(':scope > summary');
    if (!summary) continue;
    summary.addEventListener('click', () => {
      setTimeout(() => {
        if (cat.open) delete collapsed[cat.id];
        else collapsed[cat.id] = true;
        try { localStorage.setItem(storeKey, JSON.stringify(collapsed)); } catch (_) {}
        clearActiveProfile();
        refreshToggleAllLabel();
      }, 0);
    });
  }

  // ---- Profiles ----
  function clearActiveProfile() {
    if (profileSelect.value) {
      profileSelect.value = '';
      try { localStorage.removeItem(profileKey); } catch (_) {}
    }
  }

  function applyProfile(id) {
    const profile = profiles.find(p => p.id === id);
    if (!profile) return;
    if (profile.cats === '*') {
      for (const c of cats) { c.open = true; delete collapsed[c.id]; }
    } else {
      const open = new Set(profile.cats);
      for (const c of cats) {
        // Keyboard-shortcut groups are always shown — a training-role profile
        // never collapses them.
        const shouldOpen = open.has(c.id) || c.id.startsWith('keys-');
        c.open = shouldOpen;
        if (shouldOpen) delete collapsed[c.id];
        else collapsed[c.id] = true;
      }
    }
    try {
      localStorage.setItem(storeKey, JSON.stringify(collapsed));
      localStorage.setItem(profileKey, id);
    } catch (_) {}
    update();
  }

  profileSelect.addEventListener('change', () => {
    if (profileSelect.value) applyProfile(profileSelect.value);
  });

  function refreshToggleAllLabel() {
    const anyOpen = cats.some(c => !c.hidden && c.open);
    toggleAll.textContent = anyOpen ? 'Collapse all' : 'Expand all';
  }

  toggleAll.addEventListener('click', () => {
    const anyOpen = cats.some(c => !c.hidden && c.open);
    const target = !anyOpen;
    for (const cat of cats) {
      cat.open = target;
      if (target) delete collapsed[cat.id];
      else collapsed[cat.id] = true;
    }
    try { localStorage.setItem(storeKey, JSON.stringify(collapsed)); } catch (_) {}
    // Toggle-all is equivalent to the "all" profile when expanding; otherwise clear.
    if (target) {
      const allProfile = profiles.find(p => p.cats === '*');
      if (allProfile) {
        profileSelect.value = allProfile.id;
        try { localStorage.setItem(profileKey, allProfile.id); } catch (_) {}
      } else {
        clearActiveProfile();
      }
    } else {
      clearActiveProfile();
    }
    refreshToggleAllLabel();
  });

  // ---- Filter ----

  function parseQuery(raw) {
    const trimmed = raw.trim().toLowerCase();
    const m = trimmed.match(/^cat:(\\S+)\\s*(.*)$/);
    if (m) return { catFilter: m[1], q: m[2].trim() };
    return { catFilter: null, q: trimmed };
  }

  // Tier scoring: lower = stronger. 0 = no match.
  function tierOf(row, q) {
    const verb = (row.dataset.verb || '').toLowerCase();
    const aliases = (row.dataset.aliases || '').toLowerCase().split(' ').filter(Boolean);
    const desc = (row.dataset.desc || '').toLowerCase();
    if (verb.startsWith(q)) return 1;
    if (aliases.includes(q)) return 2;
    if (aliases.some(a => a.startsWith(q))) return 3;
    if (verb.includes(q)) return 4;
    if (desc.includes(q)) return 5;
    return 0;
  }

  function highlightSpan(span, q) {
    const orig = span.dataset.orig ?? span.textContent;
    span.dataset.orig = orig;
    if (!q) { span.textContent = orig; return; }
    const lc = orig.toLowerCase();
    const idx = lc.indexOf(q);
    if (idx === -1) { span.textContent = orig; return; }
    span.textContent = '';
    span.appendChild(document.createTextNode(orig.slice(0, idx)));
    const m = document.createElement('mark');
    m.textContent = orig.slice(idx, idx + q.length);
    span.appendChild(m);
    span.appendChild(document.createTextNode(orig.slice(idx + q.length)));
  }

  function restripeCat(cat) {
    let i = 0;
    for (const row of cat.querySelectorAll('.row')) {
      if (row.hidden) continue;
      row.classList.toggle('r-alt', i % 2 === 1);
      i++;
    }
  }

  function update() {
    const { catFilter, q } = parseQuery(input.value);
    const filtering = !!(q || catFilter);
    let visible = 0, total = 0;

    for (const cat of cats) {
      const rows = [...cat.querySelectorAll('.row')];
      const inCatFilter = !catFilter ||
        cat.id.includes(catFilter) ||
        (cat.dataset.name || '').includes(catFilter);
      const catNameMatch = !!q && (cat.dataset.name || '').includes(q);

      // Tier per row + strongest in this category
      let strongest = Infinity;
      const tiers = rows.map(row => {
        if (!q) return 0;           // no q-filter: keep all
        if (catNameMatch) return 1; // category name matches → all rows top tier
        return tierOf(row, q);
      });
      for (const t of tiers) if (t && t <= 3) strongest = Math.min(strongest, t);

      let hits = 0;
      rows.forEach((row, i) => {
        total++;
        let show;
        if (!inCatFilter) show = false;
        else if (!q) show = true;
        else {
          const t = tiers[i];
          show = !!t && !(strongest <= 3 && t > 3);
        }
        row.hidden = !show;
        if (show) { hits++; visible++; }

        // Mark highlights for visible matches.
        const verbSpan = row.querySelector('.verb');
        const aliasSpan = row.querySelector('.alias');
        const markQ = (show && q) ? q : '';
        if (verbSpan) highlightSpan(verbSpan, markQ);
        if (aliasSpan) highlightSpan(aliasSpan, markQ);
      });

      // Hide cats fully filtered out.
      cat.hidden = !inCatFilter || (filtering && hits === 0 && !catNameMatch);
      // While filtering, force-open showing cats. Restore user state otherwise.
      cat.open = filtering ? !cat.hidden : !collapsed[cat.id];
      restripeCat(cat);
    }

    count.textContent = filtering ? \`\${visible} / \${total} match\` : '';
    refreshToggleAllLabel();
    if (focusedRow && (focusedRow.hidden || !document.body.contains(focusedRow))) {
      focusedRow.classList.remove('kbd-focus');
      focusedRow = null;
    }
  }
  input.addEventListener('input', update);

  // ---- Keyboard nav ----

  let focusedRow = null;
  function visibleRows() {
    return $$('.row:not([hidden])').filter(r => {
      const c = r.closest('.cat');
      return c && !c.hidden && c.open;
    });
  }
  function moveKbdFocus(delta) {
    const rows = visibleRows();
    if (rows.length === 0) return;
    let idx = focusedRow ? rows.indexOf(focusedRow) : -1;
    idx = (idx + delta + rows.length) % rows.length;
    if (focusedRow) focusedRow.classList.remove('kbd-focus');
    focusedRow = rows[idx];
    focusedRow.classList.add('kbd-focus');
    focusedRow.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  }

  function copyVerb(row) {
    const v = row?.dataset.verbText;
    if (!v) return;
    if (navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(v).then(() => showToast('Copied: ' + v)).catch(() => {});
    }
  }

  // Global shortcuts
  document.addEventListener('keydown', e => {
    const tag = e.target.tagName;
    const inField = tag === 'INPUT' || tag === 'TEXTAREA';
    if (!inField && e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey) {
      e.preventDefault();
      input.focus();
      input.select();
    } else if (e.key === 'k' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      input.focus();
      input.select();
    }
  });

  // Input-scoped keys
  input.addEventListener('keydown', e => {
    if (e.key === 'Escape') {
      if (input.value) {
        input.value = '';
        update();
      } else {
        input.blur();
      }
      if (focusedRow) {
        focusedRow.classList.remove('kbd-focus');
        focusedRow = null;
      }
    } else if (e.key === 'ArrowDown') {
      e.preventDefault();
      moveKbdFocus(1);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      moveKbdFocus(-1);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const target = focusedRow || visibleRows()[0];
      if (target) copyVerb(target);
    }
  });

  // Click row to copy verb (subtle interaction; verb is also visible).
  document.addEventListener('click', e => {
    const row = e.target.closest('.row');
    if (!row || row.hidden) return;
    // Allow text selection without triggering copy
    if (window.getSelection().toString()) return;
    if (e.target.closest('a, button, input, summary')) return;
    if (e.target.classList.contains('verb')) copyVerb(row);
  });

  // ---- Toast ----

  let toast = null;
  let toastTimer = null;
  function showToast(text) {
    if (!toast) {
      toast = document.createElement('div');
      toast.className = 'toast';
      document.body.appendChild(toast);
    }
    toast.textContent = text;
    toast.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toast.classList.remove('show'), 1500);
  }

  // ---- Print: temporarily expand all so collapsed content prints ----

  let preprintState = null;
  window.addEventListener('beforeprint', () => {
    preprintState = cats.map(c => c.open);
    for (const c of cats) c.open = true;
  });
  window.addEventListener('afterprint', () => {
    if (!preprintState) return;
    cats.forEach((c, i) => { c.open = preprintState[i]; });
    preprintState = null;
  });

  // ---- :target initial scroll & flash ----
  if (location.hash) {
    const target = document.querySelector(location.hash);
    if (target?.classList.contains('row')) {
      const cat = target.closest('.cat');
      if (cat) cat.open = true;
      target.scrollIntoView({ block: 'center' });
    }
  }

  // Initial pass.
  update();
})();
</script>

</body>
</html>
`;
}

function renderCategory(cat) {
  const allEmptyAliases = cat.rows.every(r => !r.aliases || r.aliases.length === 0);
  const bodyClass = allEmptyAliases ? 'cat-body cat-body--no-alias' : 'cat-body';
  const rows = cat.rows.map((row, i) => renderRow(row, cat.id, i)).join('\n');
  const notes = (cat.notes ?? [])
    .map(n => `<div class="note">${renderInline(n)}</div>`)
    .join('\n');
  return `<details class="cat" id="${escapeAttr(cat.id)}" data-name="${escapeAttr(cat.name.toLowerCase())}" open>
<summary>${escapeText(cat.name)}</summary>
<div class="${bodyClass}">
${rows}${notes ? '\n' + notes : ''}
</div>
</details>`;
}

function renderRow(row, catId, idx) {
  const aliasList = row.aliases ?? [];
  const aliases = aliasList.join(' ');
  const examples = (row.examples ?? [])
    .map(e => `<code>${escapeText(e)}</code>`)
    .join(' ');
  const desc = examples
    ? `${escapeText(row.description)}: <span class="ex">${examples}</span>`
    : escapeText(row.description);
  const cls = row.global ? 'row global' : 'row';
  const slug = slugRow(row.verb) || `i${idx}`;
  const id = `cmd-${catId}-${slug}`;
  const dataVerb = row.verb.toLowerCase();
  const dataAliases = aliasList.join(' ').toLowerCase();
  const dataDesc = [row.description, ...(row.examples ?? [])].join(' ').toLowerCase();
  return `<div class="${cls}" id="${escapeAttr(id)}" data-verb="${escapeAttr(dataVerb)}" data-aliases="${escapeAttr(dataAliases)}" data-desc="${escapeAttr(dataDesc)}"><span class="verb">${escapeText(row.verb)}</span><span class="alias">${escapeText(aliases)}</span><span class="desc">${desc}</span></div>`;
}

function slugRow(verb) {
  return String(verb)
    .toLowerCase()
    .replace(/\s*\/\s*/g, '-')
    .replace(/[^a-z0-9-]+/g, '-')
    .replace(/^-+|-+$/g, '');
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
