"use strict";
/*
 * Yaat.LayoutInspector — interactive HTML behavior.
 * Embedded into the rendered HTML by HtmlRenderer at build time and run
 * after the data blob (window.D) is in scope. Pulls all rendering colors
 * from the same Editorial Forensic palette as inspector.css so the canvas
 * matches the chrome.
 */

// Canvas paint constants mirror inspector.css :root variables. Canvas2D can't
// read CSS custom properties cheaply at draw time, so we keep a JS copy.
const PAINT = {
  bg: "#0c0d10",
  surface: "#15171a",
  hairline: "#1f2126",
  hairlineStrong: "#2a2d33",
  textFaint: "#4a4e57",
  accent: "#f0883e",
  accentSoft: "rgba(240, 136, 62, 0.25)",
  legendTwy: "#3fb950",
  legendRwy: "#58a6ff",
  legendArc: "#8957e5",
  legendRamp: "#8b949e",
  legendHs: "#f85149",
  legendRoute: "#f0883e",
  rwSurface: "#16181c",
  rwSurfaceHl: "#23262c",
  rwCenterline: "#262a31",
  rwCenterlineHl: "#3a3f48",
  measureRuler: "#e6db74",
};

const FONT_MONO = "'JetBrains Mono', ui-monospace, 'SF Mono', Consolas, monospace";

const D = window.D;
const canvas = document.getElementById("c");
const ctx = canvas.getContext("2d");
const tip = document.getElementById("tooltip");
const hud = document.getElementById("hud");

const nodeMap = {};
D.nodes.forEach((n) => {
  nodeMap[n.id] = n;
});
const routeSet = new Set(D.route);

// --- Runtime highlight state (mutable; UI in #hlpanel toggles entries) ---
// Initialized from the CLI-baked flags so the page comes up matching the
// command line, but the user can add/remove highlights live without
// re-running LI. Each Set normalizes its keys: taxiways/runways uppercase,
// node ids as numbers.
const state = {
  taxiways: new Set(D.hlTaxiways.map((s) => s.toUpperCase())),
  runways: new Set(D.hlRunways.map((s) => s.toUpperCase())),
  nodes: new Set(D.nodes.filter((n) => n.hl).map((n) => n.id)),
};
function hasHl() {
  return state.taxiways.size > 0 || state.runways.size > 0 || state.nodes.size > 0;
}

function twyMatch(name) {
  if (!name) return false;
  const u = name.toUpperCase();
  if (state.taxiways.has(u)) return true;
  return name.split(" · ").some((p) => state.taxiways.has(p.toUpperCase()));
}

function runwayDesignatorMatch(rwyName, designator) {
  if (!rwyName || !designator) return false;
  const parts = rwyName.toUpperCase().split("/");
  return parts.includes(designator.toUpperCase());
}

function isHlEdge(e) {
  if (!hasHl()) return true;
  if (state.nodes.has(e.a) || state.nodes.has(e.b)) return true;
  if (e.twy && twyMatch(e.twy)) return true;
  if (e.rwy && e.twy) {
    for (const r of state.runways) {
      if (runwayDesignatorMatch(e.twy, r)) return true;
    }
  }
  return false;
}

function isHlArc(a) {
  if (!hasHl()) return true;
  if (state.nodes.has(a.a) || state.nodes.has(a.b)) return true;
  if (a.names && a.names.some(twyMatch)) return true;
  return false;
}

function isHlRunway(r) {
  if (state.runways.size === 0) return false;
  for (const designator of state.runways) {
    if (runwayDesignatorMatch(r.name, designator)) return true;
  }
  return false;
}

function isHlNode(n) {
  return state.nodes.has(n.id);
}

// Bounds
let minLat = 1e9,
  maxLat = -1e9,
  minLon = 1e9,
  maxLon = -1e9;
D.nodes.forEach((n) => {
  if (n.lat < minLat) minLat = n.lat;
  if (n.lat > maxLat) maxLat = n.lat;
  if (n.lon < minLon) minLon = n.lon;
  if (n.lon > maxLon) maxLon = n.lon;
});
const padLat = (maxLat - minLat) * 0.05 + 0.0001;
const padLon = (maxLon - minLon) * 0.05 + 0.0001;
minLat -= padLat;
maxLat += padLat;
minLon -= padLon;
maxLon += padLon;

let W,
  H,
  scale = 1,
  panX = 0,
  panY = 0;
let dragging = false,
  dragX = 0,
  dragY = 0;

// --- View persistence via URL hash ---
// Pan/zoom + highlight Sets are mirrored into location.hash so refreshing
// the page lands on the same view + same selection. Rapid wheel/drag events
// are coalesced with a short debounce. replaceState avoids history spam.
let _saveViewTimer = null;
function saveView() {
  if (_saveViewTimer !== null) return;
  _saveViewTimer = setTimeout(() => {
    _saveViewTimer = null;
    const parts = ["z=" + scale.toFixed(3), "px=" + panX.toFixed(2), "py=" + panY.toFixed(2)];
    if (state.taxiways.size > 0) parts.push("tw=" + Array.from(state.taxiways).join(","));
    if (state.runways.size > 0) parts.push("rw=" + Array.from(state.runways).join(","));
    if (state.nodes.size > 0) parts.push("nd=" + Array.from(state.nodes).join(","));
    const h = "#" + parts.join(",");
    try {
      history.replaceState(null, "", h);
    } catch (_) {
      location.hash = h;
    }
  }, 120);
}
function loadView() {
  const h = location.hash || "";
  const z = h.match(/(?:^|[#,])z=([-\d.]+)/);
  const px = h.match(/(?:^|[#,])px=([-\d.]+)/);
  const py = h.match(/(?:^|[#,])py=([-\d.]+)/);
  if (z && px && py) {
    const zv = parseFloat(z[1]),
      xv = parseFloat(px[1]),
      yv = parseFloat(py[1]);
    if (Number.isFinite(zv) && Number.isFinite(xv) && Number.isFinite(yv)) {
      scale = Math.max(0.5, Math.min(80, zv));
      panX = xv;
      panY = yv;
    }
  }
  // Highlight state — only override if URL specified them, so the CLI-baked
  // initial state still wins on a fresh load.
  const tw = h.match(/(?:^|[#,])tw=([^,]+(?:,[^,]+)*?)(?=,(?:[a-z]+=)|$)/);
  const rw = h.match(/(?:^|[#,])rw=([^,]+(?:,[^,]+)*?)(?=,(?:[a-z]+=)|$)/);
  const nd = h.match(/(?:^|[#,])nd=([^,]+(?:,[^,]+)*?)(?=,(?:[a-z]+=)|$)/);
  if (tw) {
    state.taxiways.clear();
    tw[1].split(",").forEach((t) => state.taxiways.add(t.toUpperCase()));
  }
  if (rw) {
    state.runways.clear();
    rw[1].split(",").forEach((r) => state.runways.add(r.toUpperCase()));
  }
  if (nd) {
    state.nodes.clear();
    nd[1].split(",").forEach((n) => {
      const i = parseInt(n, 10);
      if (Number.isFinite(i)) state.nodes.add(i);
    });
  }
}

function resize() {
  W = canvas.width = window.innerWidth;
  H = canvas.height = window.innerHeight;
  draw();
}
window.addEventListener("resize", resize);

function toScreen(lat, lon) {
  const x = ((lon - minLon) / (maxLon - minLon)) * W;
  const y = (1 - (lat - minLat) / (maxLat - minLat)) * H;
  return [(x + panX) * scale + (W * (1 - scale)) / 2, (y + panY) * scale + (H * (1 - scale)) / 2];
}

function fromScreen(sx, sy) {
  const x = (sx - (W * (1 - scale)) / 2) / scale - panX;
  const y = (sy - (H * (1 - scale)) / 2) / scale - panY;
  return [(1 - y / H) * (maxLat - minLat) + minLat, (x / W) * (maxLon - minLon) + minLon];
}

function drawLine(x1, y1, x2, y2) {
  ctx.beginPath();
  ctx.moveTo(x1, y1);
  ctx.lineTo(x2, y2);
  ctx.stroke();
}

function draw() {
  ctx.clearRect(0, 0, W, H);
  ctx.fillStyle = PAINT.bg;
  ctx.fillRect(0, 0, W, H);

  // 1. Runway surfaces
  D.runways.forEach((r) => {
    if (r.coords.length < 2) return;
    const [x1, y1] = toScreen(r.coords[0][0], r.coords[0][1]);
    const c = r.coords;
    const [x2, y2] = toScreen(c[c.length - 1][0], c[c.length - 1][1]);
    const len = Math.hypot(x2 - x1, y2 - y1);
    const widthPx = Math.max(4, (r.widthFt * len) / 6000);
    const rwHl = isHlRunway(r);
    ctx.strokeStyle = rwHl ? PAINT.rwSurfaceHl : PAINT.rwSurface;
    ctx.lineWidth = widthPx;
    ctx.lineCap = "round";
    drawLine(x1, y1, x2, y2);
    ctx.strokeStyle = rwHl ? PAINT.rwCenterlineHl : PAINT.rwCenterline;
    ctx.lineWidth = 1.5;
    ctx.setLineDash([12, 8]);
    drawLine(x1, y1, x2, y2);
    ctx.setLineDash([]);
    // Label
    ctx.fillStyle = PAINT.textFaint;
    ctx.font = "10px " + FONT_MONO;
    ctx.textAlign = "center";
    const mx = (x1 + x2) / 2,
      my = (y1 + y2) / 2;
    ctx.save();
    ctx.translate(mx, my - widthPx / 2 - 6);
    let ang = Math.atan2(y1 - y2, x2 - x1);
    if (ang > Math.PI / 2) ang -= Math.PI;
    if (ang < -Math.PI / 2) ang += Math.PI;
    ctx.rotate(-ang);
    ctx.fillText(r.name, 0, 0);
    ctx.restore();
  });

  const haveAnyHl = hasHl();

  // 2. Non-highlighted edges
  ctx.lineCap = "butt";
  D.edges.forEach((e) => {
    if (isHlEdge(e) && haveAnyHl) return;
    const na = nodeMap[e.a],
      nb = nodeMap[e.b];
    if (!na || !nb) return;
    const [x1, y1] = toScreen(na.lat, na.lon);
    const [x2, y2] = toScreen(nb.lat, nb.lon);
    ctx.globalAlpha = haveAnyHl ? 0.18 : 0.32;
    ctx.strokeStyle = e.rwy ? PAINT.legendRwy : e.ramp ? PAINT.legendRamp : PAINT.legendTwy;
    ctx.lineWidth = 0.7;
    if (e.rwy) ctx.setLineDash([6, 4]);
    else ctx.setLineDash([]);
    drawLine(x1, y1, x2, y2);
    ctx.setLineDash([]);
    ctx.globalAlpha = 1;
  });

  // 3. Non-highlighted arcs
  D.arcs.forEach((a) => {
    if (isHlArc(a) && haveAnyHl) return;
    ctx.globalAlpha = haveAnyHl ? 0.18 : 0.32;
    ctx.strokeStyle = PAINT.legendArc;
    ctx.lineWidth = 0.7;
    ctx.setLineDash([4, 3]);
    ctx.beginPath();
    a.pts.forEach((p, i) => {
      const [x, y] = toScreen(p[0], p[1]);
      if (i === 0) {
        ctx.moveTo(x, y);
      } else {
        ctx.lineTo(x, y);
      }
    });
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.globalAlpha = 1;
  });

  // 4. Highlighted edges (only when something is actively highlighted)
  if (haveAnyHl) {
    D.edges.filter(isHlEdge).forEach((e) => {
      const na = nodeMap[e.a],
        nb = nodeMap[e.b];
      if (!na || !nb) return;
      const [x1, y1] = toScreen(na.lat, na.lon);
      const [x2, y2] = toScreen(nb.lat, nb.lon);
      ctx.strokeStyle = e.rwy ? "#79c0ff" : "#56d364";
      ctx.lineWidth = 2;
      if (e.rwy) ctx.setLineDash([6, 4]);
      else ctx.setLineDash([]);
      drawLine(x1, y1, x2, y2);
      ctx.setLineDash([]);
    });

    // 5. Highlighted arcs
    D.arcs.filter(isHlArc).forEach((a) => {
      ctx.strokeStyle = a.rwyJunction ? "#bc8cff" : "#d2a8ff";
      ctx.lineWidth = 2;
      ctx.setLineDash([4, 3]);
      ctx.beginPath();
      a.pts.forEach((p, i) => {
        const [x, y] = toScreen(p[0], p[1]);
        if (i === 0) {
          ctx.moveTo(x, y);
        } else {
          ctx.lineTo(x, y);
        }
      });
      ctx.stroke();
      ctx.setLineDash([]);
    });
  }

  // 6. Route overlay
  if (D.route.length >= 2) {
    function routePath() {
      ctx.beginPath();
      let first = true;
      for (let i = 0; i < D.route.length - 1; i++) {
        const fid = D.route[i],
          tid = D.route[i + 1];
        const arc = D.arcs.find(
          (a) => (a.a === fid && a.b === tid) || (a.a === tid && a.b === fid),
        );
        if (arc) {
          const pts = arc.a !== fid ? [...arc.pts].reverse() : arc.pts;
          pts.forEach((p) => {
            const [x, y] = toScreen(p[0], p[1]);
            if (first) {
              ctx.moveTo(x, y);
              first = false;
            } else {
              ctx.lineTo(x, y);
            }
          });
        } else {
          const na = nodeMap[fid],
            nb = nodeMap[tid];
          if (na && nb) {
            const [x1, y1] = toScreen(na.lat, na.lon);
            const [x2, y2] = toScreen(nb.lat, nb.lon);
            if (first) {
              ctx.moveTo(x1, y1);
              first = false;
            }
            ctx.lineTo(x2, y2);
          }
        }
      }
      ctx.stroke();
    }
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.strokeStyle = "rgba(240, 136, 62, 0.25)";
    ctx.lineWidth = 6;
    routePath();
    ctx.strokeStyle = PAINT.legendRoute;
    ctx.lineWidth = 2.5;
    routePath();

    // Numbered markers
    D.route.forEach((id, i) => {
      const n = nodeMap[id];
      if (!n) return;
      const [x, y] = toScreen(n.lat, n.lon);
      ctx.beginPath();
      ctx.arc(x, y, 7, 0, Math.PI * 2);
      ctx.fillStyle = PAINT.legendRoute;
      ctx.fill();
      ctx.strokeStyle = PAINT.bg;
      ctx.lineWidth = 1.5;
      ctx.stroke();
      ctx.fillStyle = PAINT.bg;
      ctx.font = "bold 8px " + FONT_MONO;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(i.toString(), x, y);
    });
  }

  // 7. Nodes
  D.nodes.forEach((n) => {
    if (routeSet.has(n.id)) return;
    const [x, y] = toScreen(n.lat, n.lon);
    const isHS = n.type === "RunwayHoldShort";
    const isSpot = n.type === "Spot";
    const isPark = n.type === "Parking";
    const explicitlyHl = isHlNode(n);
    const isImportant = isHS || isSpot || isPark || explicitlyHl;
    const onHlTwy = n.edges && n.edges.some((e) => twyMatch(e.twy));
    if (haveAnyHl && !isImportant && !onHlTwy && !n.ann) return;

    const r = isImportant ? (explicitlyHl ? 5 : 3) : 1.5;
    const color = isHS ? PAINT.legendHs : isSpot ? PAINT.legendTwy : isPark ? PAINT.legendRwy : "#d29922";
    ctx.globalAlpha = isImportant || onHlTwy ? 0.9 : 0.25;
    ctx.beginPath();
    ctx.arc(x, y, Math.max(r, (r * Math.min(scale, 3)) / 3), 0, Math.PI * 2);
    if (isImportant) {
      ctx.fillStyle = color;
      ctx.fill();
    } else {
      ctx.strokeStyle = color;
      ctx.lineWidth = 0.8;
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
  });

  hud.textContent = "ZOOM " + scale.toFixed(1) + "x   •   " + D.airportId;

  // 8. Tick overlay — multiple aircraft, each with their own trail + silhouette
  if (D.ticks && D.ticks.length > 0 && typeof currentTime !== "undefined") {
    const trailWindowSec = 30;
    const minTrailTime = currentTime - trailWindowSec;
    const aircraftMeta = D.aircraft || [];

    aircraftMeta.forEach((meta) => {
      const callsign = meta.callsign;
      const color = meta.color || PAINT.legendRoute;
      const trailTicks = D.ticks.filter(
        (t) => t.callsign === callsign && t.t >= minTrailTime && t.t <= currentTime,
      );
      if (trailTicks.length === 0) return;

      // Trail line
      ctx.globalAlpha = 0.45;
      ctx.beginPath();
      trailTicks.forEach((t, i) => {
        const [x, y] = toScreen(t.lat, t.lon);
        if (i === 0) ctx.moveTo(x, y);
        else ctx.lineTo(x, y);
      });
      ctx.strokeStyle = color;
      ctx.lineWidth = 2;
      ctx.stroke();
      ctx.globalAlpha = 1;

      // Trail dots, fading toward the past
      const span = trailTicks.length;
      trailTicks.forEach((t, i) => {
        if (i === span - 1) return;
        const [x, y] = toScreen(t.lat, t.lon);
        ctx.globalAlpha = 0.15 + (0.5 * (i + 1)) / span;
        ctx.beginPath();
        ctx.arc(x, y, 2, 0, Math.PI * 2);
        ctx.fillStyle = color;
        ctx.fill();
      });
      ctx.globalAlpha = 1;

      // Silhouette at the most recent tick
      const tick = trailTicks[trailTicks.length - 1];
      drawAircraft(tick, meta, color);

      // Bearing line to nav target
      if (tick.nav && tick.nav.targetNodeId && nodeMap[tick.nav.targetNodeId]) {
        const [ax, ay] = toScreen(tick.lat, tick.lon);
        const tn = nodeMap[tick.nav.targetNodeId];
        const [tx, ty] = toScreen(tn.lat, tn.lon);
        ctx.setLineDash([4, 4]);
        ctx.strokeStyle = color + "60";
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(tx, ty);
        ctx.stroke();
        ctx.setLineDash([]);
        ctx.beginPath();
        ctx.arc(tx, ty, 5, 0, Math.PI * 2);
        ctx.strokeStyle = color;
        ctx.lineWidth = 1.5;
        ctx.stroke();
      }
    });
  }
}

function drawAircraft(tick, meta, color) {
  const [ax, ay] = toScreen(tick.lat, tick.lon);
  const hdgRad = ((tick.hdg - 90) * Math.PI) / 180;
  ctx.save();
  ctx.translate(ax, ay);
  ctx.rotate(hdgRad);

  // Project 100ft east of aircraft to derive pixels-per-foot at the current
  // pan/zoom. Falls back to a small fixed silhouette when no dimensions.
  let fuselagePx = 0,
    wingspanPx = 0;
  if (meta.lengthFt && meta.wingspanFt) {
    const [px0, py0] = toScreen(tick.lat, tick.lon);
    const dLat = 100.0 / (60.0 * 6076.12);
    const [px1, py1] = toScreen(tick.lat + dLat, tick.lon);
    const pxPerFt = Math.hypot(px1 - px0, py1 - py0) / 100.0;
    fuselagePx = meta.lengthFt * pxPerFt;
    wingspanPx = meta.wingspanFt * pxPerFt;
  }
  if (fuselagePx < 10) fuselagePx = 10;
  if (wingspanPx < 8) wingspanPx = 8;

  const halfL = fuselagePx / 2;
  const halfW = wingspanPx / 2;
  ctx.fillStyle = color;
  ctx.strokeStyle = PAINT.bg;
  ctx.lineWidth = 1;
  const fuselageWidth = Math.max(3, wingspanPx * 0.12);
  ctx.beginPath();
  ctx.moveTo(halfL, 0);
  ctx.lineTo(halfL - fuselageWidth, -fuselageWidth * 0.6);
  ctx.lineTo(-halfL, -fuselageWidth * 0.5);
  ctx.lineTo(-halfL, fuselageWidth * 0.5);
  ctx.lineTo(halfL - fuselageWidth, fuselageWidth * 0.6);
  ctx.closePath();
  ctx.fill();
  ctx.stroke();
  // Wings
  const wingX = halfL * 0.1;
  const wingChord = Math.max(2, fuselagePx * 0.12);
  ctx.beginPath();
  ctx.moveTo(wingX + wingChord * 0.5, -halfW);
  ctx.lineTo(wingX - wingChord * 0.5, -halfW * 0.95);
  ctx.lineTo(wingX - wingChord * 0.5, halfW * 0.95);
  ctx.lineTo(wingX + wingChord * 0.5, halfW);
  ctx.closePath();
  ctx.fill();
  ctx.stroke();
  // Tail
  const tailX = -halfL + fuselageWidth;
  const tailspan = wingspanPx * 0.35;
  const tailChord = Math.max(1, fuselagePx * 0.08);
  ctx.beginPath();
  ctx.moveTo(tailX + tailChord * 0.5, -tailspan / 2);
  ctx.lineTo(tailX - tailChord * 0.5, -tailspan / 2);
  ctx.lineTo(tailX - tailChord * 0.5, tailspan / 2);
  ctx.lineTo(tailX + tailChord * 0.5, tailspan / 2);
  ctx.closePath();
  ctx.fill();
  ctx.stroke();
  ctx.restore();

  // Callsign label above the silhouette (with halo so it stays legible)
  ctx.save();
  ctx.font = "bold 11px " + FONT_MONO;
  ctx.textAlign = "center";
  ctx.fillStyle = PAINT.bg;
  ctx.fillText(meta.callsign, ax, ay - Math.max(halfL, halfW) - 6);
  ctx.fillText(meta.callsign, ax + 1, ay - Math.max(halfL, halfW) - 5);
  ctx.fillText(meta.callsign, ax - 1, ay - Math.max(halfL, halfW) - 5);
  ctx.fillStyle = color;
  ctx.fillText(meta.callsign, ax, ay - Math.max(halfL, halfW) - 5);
  ctx.restore();
}

// --- Interaction ---
canvas.addEventListener(
  "wheel",
  (e) => {
    e.preventDefault();
    const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
    // Zoom toward mouse position
    const [lat, lon] = fromScreen(e.clientX, e.clientY);
    scale = Math.max(0.5, Math.min(80, scale * factor));
    // Adjust pan so lat/lon stays under mouse
    const [nx, ny] = toScreen(lat, lon);
    panX += (e.clientX - nx) / scale;
    panY += (e.clientY - ny) / scale;
    draw();
    saveView();
  },
  { passive: false },
);

let dragStartX = 0,
  dragStartY = 0;
canvas.addEventListener("mousedown", (e) => {
  if (measureMode) return;
  dragging = true;
  dragX = e.clientX;
  dragY = e.clientY;
  dragStartX = e.clientX;
  dragStartY = e.clientY;
});
window.addEventListener("mouseup", () => {
  dragging = false;
});
canvas.addEventListener("click", (e) => {
  if (measureMode) return; // Handled by measure click handler
  // Only copy if the mouse didn't move (not a drag) and tooltip is visible
  const dist = Math.hypot(e.clientX - dragStartX, e.clientY - dragStartY);
  if (dist < 4 && tip.style.display === "block" && tip.textContent) {
    navigator.clipboard.writeText(tip.textContent).then(() => {
      const orig = tip.style.borderColor;
      tip.style.borderColor = PAINT.accent;
      setTimeout(() => {
        tip.style.borderColor = orig;
      }, 300);
    });
  }
});

canvas.addEventListener("mousemove", (e) => {
  if (dragging) {
    panX += (e.clientX - dragX) / scale;
    panY += (e.clientY - dragY) / scale;
    dragX = e.clientX;
    dragY = e.clientY;
    draw();
    saveView();
    return;
  }
  updateTooltip(e.clientX, e.clientY);
});

canvas.addEventListener("mouseleave", () => {
  dragging = false;
  tip.style.display = "none";
});

function pointToSegmentDist(px, py, x1, y1, x2, y2) {
  const dx = x2 - x1,
    dy = y2 - y1;
  const lenSq = dx * dx + dy * dy;
  if (lenSq === 0) return Math.hypot(px - x1, py - y1);
  const t = Math.max(0, Math.min(1, ((px - x1) * dx + (py - y1) * dy) / lenSq));
  return Math.hypot(px - (x1 + t * dx), py - (y1 + t * dy));
}

function showTip(mx, my) {
  tip.style.display = "block";
  tip.style.left = mx + 16 + "px";
  tip.style.top = my - 10 + "px";
  const r = tip.getBoundingClientRect();
  if (r.right > window.innerWidth) tip.style.left = mx - r.width - 8 + "px";
  if (r.bottom > window.innerHeight) tip.style.top = my - r.height - 8 + "px";
}

function addLine(text) {
  const el = document.createElement("span");
  el.className = "t-edge";
  el.textContent = text;
  tip.appendChild(el);
}

function updateTooltip(mx, my) {
  const NODE_THRESH = 18,
    EDGE_THRESH = 6;

  // --- Node proximity ---
  const [lat, lon] = fromScreen(mx, my);
  const cosLat = Math.cos((lat * Math.PI) / 180);
  let closestNode = null,
    closestNodeDist = Infinity;
  D.nodes.forEach((n) => {
    const d = Math.hypot((n.lat - lat) * 60, (n.lon - lon) * 60 * cosLat);
    if (d < closestNodeDist) {
      closestNodeDist = d;
      closestNode = n;
    }
  });
  let nodeScreenDist = Infinity;
  if (closestNode) {
    const [sx, sy] = toScreen(closestNode.lat, closestNode.lon);
    nodeScreenDist = Math.hypot(sx - mx, sy - my);
  }

  // --- Arc proximity (16 segments per arc) ---
  let closestArc = null,
    closestArcDist = Infinity;
  D.arcs.forEach((a) => {
    for (let i = 0; i < a.pts.length - 1; i++) {
      const [x1, y1] = toScreen(a.pts[i][0], a.pts[i][1]);
      const [x2, y2] = toScreen(a.pts[i + 1][0], a.pts[i + 1][1]);
      const d = pointToSegmentDist(mx, my, x1, y1, x2, y2);
      if (d < closestArcDist) {
        closestArcDist = d;
        closestArc = a;
      }
    }
  });

  // --- Edge proximity ---
  let closestEdge = null,
    closestEdgeDist = Infinity;
  D.edges.forEach((e) => {
    const na = nodeMap[e.a],
      nb = nodeMap[e.b];
    if (!na || !nb) return;
    const [x1, y1] = toScreen(na.lat, na.lon);
    const [x2, y2] = toScreen(nb.lat, nb.lon);
    const d = pointToSegmentDist(mx, my, x1, y1, x2, y2);
    if (d < closestEdgeDist) {
      closestEdgeDist = d;
      closestEdge = e;
    }
  });

  // --- Tick proximity (only consider current frame's silhouettes) ---
  let closestTick = null,
    closestTickDist = Infinity;
  if (D.ticks && typeof currentTime !== "undefined") {
    (D.aircraft || []).forEach((meta) => {
      const candidates = D.ticks.filter((t) => t.callsign === meta.callsign && t.t <= currentTime);
      if (candidates.length === 0) return;
      const t = candidates[candidates.length - 1];
      const [sx, sy] = toScreen(t.lat, t.lon);
      const d = Math.hypot(sx - mx, sy - my);
      if (d < closestTickDist) {
        closestTickDist = d;
        closestTick = t;
      }
    });
  }
  const hasTick = closestTick && closestTickDist <= NODE_THRESH;

  const hasArc = closestArc && closestArcDist <= EDGE_THRESH;
  const hasEdge = closestEdge && closestEdgeDist <= EDGE_THRESH;
  const hasNode = closestNode && nodeScreenDist <= NODE_THRESH;

  if (!hasArc && !hasEdge && !hasNode && !hasTick) {
    tip.style.display = "none";
    return;
  }

  tip.textContent = "";

  if (hasTick) {
    const titleEl = document.createElement("span");
    titleEl.className = "t-title";
    titleEl.textContent = closestTick.callsign + " @ t=" + closestTick.t;
    tip.appendChild(titleEl);
    const lines = [
      "hdg=" + closestTick.hdg.toFixed(1) + "°  gs=" + closestTick.gs.toFixed(1) + "kt",
      "phase=" + closestTick.phase + (closestTick.twy ? "  twy=" + closestTick.twy : ""),
    ];
    if (closestTick.speedLimit != null) {
      lines.push("speedLimit=" + closestTick.speedLimit.toFixed(1) + "kt");
    }
    const nav = closestTick.nav;
    if (nav && nav.targetNodeId != null) {
      lines.push("target=#" + nav.targetNodeId + "  dist=" + ((nav.distNm || 0) * 6076).toFixed(0) + "ft");
      lines.push(
        "brg=" + (nav.brgDeg || 0).toFixed(1) + "°  targetSpd=" + (nav.targetSpdKts || 0).toFixed(1) + "kt",
      );
      lines.push(
        "brakeLimit=" + (nav.brakeLimitKts || 0).toFixed(1) + "kt  nodeReq=" + (nav.nodeReqSpdKts || 0).toFixed(1) + "kt",
      );
      if (nav.onArc) lines.push("ON ARC  arcLimit=" + (nav.arcLimitKts || 0).toFixed(1) + "kt");
    }
    lines.forEach((l) => {
      const el = document.createElement("span");
      el.className = "t-edge";
      el.textContent = l;
      tip.appendChild(el);
    });
  }

  if (hasArc) {
    const titleEl = document.createElement("span");
    titleEl.className = "t-title";
    titleEl.textContent = "Arc · " + closestArc.twy;
    tip.appendChild(titleEl);
    addLine(closestArc.a + " → " + closestArc.b + "  (" + closestArc.ft + "ft)");
    if (closestArc.names && closestArc.names.length > 0) {
      addLine("names: [" + closestArc.names.join(", ") + "]");
    }
    addLine("radius=" + closestArc.radius + "ft  maxSafe=" + closestArc.maxSafe + "kt  turn=" + closestArc.turnAngle + "°");
    addLine("bearings: node0=" + closestArc.bearing0 + "°  node1=" + closestArc.bearing1 + "°");
    if (closestArc.origin) {
      addLine("origin: " + closestArc.origin);
    }
  }

  if (hasEdge) {
    if (hasArc) {
      addLine("───");
    }
    const na = nodeMap[closestEdge.a],
      nb = nodeMap[closestEdge.b];
    const titleEl = document.createElement("span");
    titleEl.className = "t-title";
    titleEl.textContent = "Edge · " + closestEdge.twy;
    tip.appendChild(titleEl);
    addLine((na ? na.id : closestEdge.a) + " → " + (nb ? nb.id : closestEdge.b) + "  (" + closestEdge.ft + "ft)");
    if (closestEdge.origin) {
      addLine("origin: " + closestEdge.origin);
    }
  }

  if (hasArc || hasEdge || hasTick) {
    showTip(mx, my);
    if (!hasNode) return;
  }

  if (!hasNode) {
    tip.style.display = "none";
    return;
  }

  // Build node tooltip content safely using DOM
  tip.textContent = "";
  const titleEl = document.createElement("span");
  titleEl.className = "t-title";
  titleEl.textContent = "#" + closestNode.id;
  tip.appendChild(titleEl);

  const typeEl = document.createElement("span");
  typeEl.className = "t-type";
  typeEl.textContent = " " + closestNode.type;
  tip.appendChild(typeEl);

  if (closestNode.name) {
    const b = document.createElement("b");
    b.textContent = " " + closestNode.name;
    tip.appendChild(b);
  }
  if (closestNode.rwyId) {
    tip.appendChild(document.createTextNode(" rwy=" + closestNode.rwyId));
  }

  if (closestNode.edges && closestNode.edges.length > 0) {
    closestNode.edges.forEach((e) => {
      const el = document.createElement("span");
      el.className = "t-edge";
      el.textContent = (e.arc ? "arc " : "") + e.twy + " → #" + e.to + " (" + e.ft + "ft)" + (e.rwy ? " [RWY]" : "");
      tip.appendChild(el);
    });
  }

  if (closestNode.ann) {
    const annEl = document.createElement("span");
    annEl.className = "t-ann";
    annEl.textContent = closestNode.ann;
    tip.appendChild(annEl);
  }

  showTip(mx, my);
}

// --- Tick animation player (time-axis based; multi-aircraft) ---
let currentTime = 0;
let timeFrames = []; // sorted unique tick timestamps
let timeIdx = 0;
let playing = false;
let playTimer = null;

if (D.ticks && D.ticks.length > 0) {
  const playerEl = document.getElementById("player");
  playerEl.style.display = "flex";
  // Lift the legend above the player so they don't overlap. Player vertical
  // size depends on how many aircraft are in the recording (one row each).
  const adjustLegendOffset = () => {
    document.getElementById("legend").style.bottom = playerEl.getBoundingClientRect().height + 24 + "px";
  };
  const scrub = document.getElementById("scrub");
  const playBtn = document.getElementById("playBtn");
  const tickClock = document.getElementById("tickClock");
  const tickAircraft = document.getElementById("tickAircraft");

  // Build the unified time axis (one slider step per distinct tick second)
  const timeSet = new Set();
  D.ticks.forEach((t) => timeSet.add(t.t));
  timeFrames = Array.from(timeSet).sort((a, b) => a - b);
  scrub.max = timeFrames.length - 1;
  currentTime = timeFrames[0];

  // Pre-bucket ticks by callsign for fast latest-tick lookups during playback.
  const ticksByCallsign = {};
  D.ticks.forEach((t) => {
    if (!ticksByCallsign[t.callsign]) ticksByCallsign[t.callsign] = [];
    ticksByCallsign[t.callsign].push(t);
  });
  Object.values(ticksByCallsign).forEach((arr) => arr.sort((a, b) => a.t - b.t));

  // Render the per-aircraft details as a table so columns line up across
  // multiple aircraft.
  const tickColumns = ["callsign", "phase", "twy", "hdg", "gs", "lim", "navTarget"];
  const tickColLabels = {
    callsign: "CS",
    phase: "phase",
    twy: "twy",
    hdg: "hdg",
    gs: "gs",
    lim: "lim",
    navTarget: "navTgt",
  };

  function buildTickTable() {
    while (tickAircraft.firstChild) tickAircraft.removeChild(tickAircraft.firstChild);
    const table = document.createElement("table");
    const thead = document.createElement("thead");
    const trh = document.createElement("tr");
    tickColumns.forEach((c) => {
      const th = document.createElement("th");
      th.textContent = tickColLabels[c];
      trh.appendChild(th);
    });
    thead.appendChild(trh);
    table.appendChild(thead);
    const tbody = document.createElement("tbody");
    (D.aircraft || []).forEach((meta) => {
      const tr = document.createElement("tr");
      tr.dataset.callsign = meta.callsign;
      tickColumns.forEach((c) => {
        const td = document.createElement("td");
        td.dataset.col = c;
        tr.appendChild(td);
      });
      // Color the callsign cell to match the aircraft silhouette
      tr.firstChild.style.color = meta.color || "";
      tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    tickAircraft.appendChild(table);
  }

  function findLatestTickFor(callsign, t) {
    const arr = ticksByCallsign[callsign];
    if (!arr || arr.length === 0 || arr[0].t > t) return null;
    let lo = 0,
      hi = arr.length - 1,
      best = -1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (arr[mid].t <= t) {
        best = mid;
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }
    return best >= 0 ? arr[best] : null;
  }

  function updateTickInfo() {
    tickClock.textContent = "T " + currentTime;
    const rows = tickAircraft.querySelectorAll("tr[data-callsign]");
    rows.forEach((tr) => {
      const cs = tr.dataset.callsign;
      const t = findLatestTickFor(cs, currentTime);
      const cells = {};
      tr.querySelectorAll("td[data-col]").forEach((td) => {
        cells[td.dataset.col] = td;
      });
      if (!t) {
        cells.callsign.textContent = cs;
        ["phase", "twy", "hdg", "gs", "lim", "navTarget"].forEach((c) => (cells[c].textContent = "–"));
        return;
      }
      cells.callsign.textContent = cs;
      cells.phase.textContent = t.phase || "";
      cells.twy.textContent = t.twy || "";
      cells.hdg.textContent = t.hdg.toFixed(0) + "°";
      cells.gs.textContent = t.gs.toFixed(0) + "kt";
      cells.lim.textContent = t.speedLimit != null ? t.speedLimit.toFixed(0) + "kt" : "";
      cells.navTarget.textContent = t.nav && t.nav.targetNodeId ? "#" + t.nav.targetNodeId : "";
    });
  }

  function setFrame(i) {
    timeIdx = Math.max(0, Math.min(i, timeFrames.length - 1));
    currentTime = timeFrames[timeIdx];
    scrub.value = timeIdx;
    updateTickInfo();
    draw();
  }

  scrub.addEventListener("input", () => {
    setFrame(parseInt(scrub.value));
    if (playing) togglePlay();
  });

  function togglePlay() {
    playing = !playing;
    playBtn.textContent = playing ? "⏸" : "▶";
    if (playing) {
      playTimer = setInterval(() => {
        if (timeIdx >= timeFrames.length - 1) {
          togglePlay();
          return;
        }
        setFrame(timeIdx + 1);
      }, 100);
    } else {
      clearInterval(playTimer);
      playTimer = null;
    }
  }

  playBtn.addEventListener("click", togglePlay);

  document.addEventListener("keydown", (e) => {
    if (e.key === " ") {
      e.preventDefault();
      togglePlay();
    }
    if (e.key === "ArrowRight") {
      e.preventDefault();
      setFrame(timeIdx + 1);
    }
    if (e.key === "ArrowLeft") {
      e.preventDefault();
      setFrame(timeIdx - 1);
    }
  });

  buildTickTable();
  updateTickInfo();
  adjustLegendOffset();
  window.addEventListener("resize", adjustLegendOffset);
}

// --- Measuring tool ---
let measureMode = false;
let measureStart = null; // {lat, lon, sx, sy}
let measureMouse = null; // {lat, lon, sx, sy}
const rulers = []; // [{lat1,lon1,lat2,lon2,distFt}]

function distFt(lat1, lon1, lat2, lon2) {
  const dlat = (lat2 - lat1) * 60 * 6076.12;
  const dlon = (lon2 - lon1) * Math.cos((lat1 * Math.PI) / 180) * 60 * 6076.12;
  return Math.sqrt(dlat * dlat + dlon * dlon);
}

function drawRuler(lat1, lon1, lat2, lon2, ft, color, dashed) {
  const [x1, y1] = toScreen(lat1, lon1);
  const [x2, y2] = toScreen(lat2, lon2);
  ctx.save();
  ctx.strokeStyle = color;
  ctx.lineWidth = 2;
  if (dashed) ctx.setLineDash([6, 4]);
  ctx.beginPath();
  ctx.moveTo(x1, y1);
  ctx.lineTo(x2, y2);
  ctx.stroke();
  ctx.setLineDash([]);
  // Label at midpoint
  const mx = (x1 + x2) / 2,
    my = (y1 + y2) / 2;
  const label = ft.toFixed(1) + "ft";
  ctx.font = "bold 12px " + FONT_MONO;
  ctx.textAlign = "center";
  ctx.fillStyle = PAINT.bg;
  ctx.fillText(label, mx, my - 6);
  ctx.fillText(label, mx + 1, my - 5);
  ctx.fillText(label, mx - 1, my - 5);
  ctx.fillStyle = color;
  ctx.fillText(label, mx, my - 5);
  // Endpoint dots
  [toScreen(lat1, lon1), toScreen(lat2, lon2)].forEach(([x, y]) => {
    ctx.beginPath();
    ctx.arc(x, y, 4, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
  });
  ctx.restore();
}

// Patch draw() to render rulers
const origDraw = draw;
draw = function () {
  origDraw();
  // Permanent rulers
  rulers.forEach((r) => drawRuler(r.lat1, r.lon1, r.lat2, r.lon2, r.distFt, PAINT.measureRuler, false));
  // Active measurement
  if (measureMode && measureStart && measureMouse) {
    const ft = distFt(measureStart.lat, measureStart.lon, measureMouse.lat, measureMouse.lon);
    drawRuler(measureStart.lat, measureStart.lon, measureMouse.lat, measureMouse.lon, ft, PAINT.measureRuler, true);
  }
  // Cursor indicator
  if (measureMode) {
    hud.textContent =
      "ZOOM " + scale.toFixed(1) + "x   •   " + D.airportId + "   •   MEASURE (M to exit, right-click to delete)";
  }
};

document.addEventListener("keydown", (e) => {
  if (e.key === "m" || e.key === "M") {
    if (e.target.tagName === "INPUT") return;
    measureMode = !measureMode;
    measureStart = null;
    measureMouse = null;
    canvas.style.cursor = measureMode ? "crosshair" : "grab";
    draw();
  }
});

canvas.addEventListener("mousemove", (e) => {
  if (!measureMode || !measureStart) return;
  const [lat, lon] = fromScreen(e.clientX, e.clientY);
  measureMouse = { lat, lon };
  draw();
});

canvas.addEventListener("click", (e) => {
  if (!measureMode) return;
  const [lat, lon] = fromScreen(e.clientX, e.clientY);
  if (!measureStart) {
    measureStart = { lat, lon };
    measureMouse = null;
  } else {
    const ft = distFt(measureStart.lat, measureStart.lon, lat, lon);
    rulers.push({ lat1: measureStart.lat, lon1: measureStart.lon, lat2: lat, lon2: lon, distFt: ft });
    measureStart = null;
    measureMouse = null;
    draw();
  }
});

canvas.addEventListener("contextmenu", (e) => {
  e.preventDefault();
  if (!measureMode || rulers.length === 0) return;
  // Delete nearest ruler to click
  const [lat, lon] = fromScreen(e.clientX, e.clientY);
  let bestIdx = -1,
    bestDist = Infinity;
  rulers.forEach((r, i) => {
    const midLat = (r.lat1 + r.lat2) / 2,
      midLon = (r.lon1 + r.lon2) / 2;
    const d = distFt(lat, lon, midLat, midLon);
    if (d < bestDist) {
      bestDist = d;
      bestIdx = i;
    }
  });
  if (bestIdx >= 0) {
    rulers.splice(bestIdx, 1);
    draw();
  }
});

// --- Highlight search panel ---
const hlinput = document.getElementById("hlinput");
const hloptions = document.getElementById("hloptions");
const hlchips = document.getElementById("hlchips");
const hlclearBtn = document.getElementById("hlclear");

function buildSearchIndex() {
  // Returns a list of {label, kind, value} entries used to populate the datalist.
  // 'value' is what the input echoes; on submit we re-parse to figure out kind+key.
  const entries = [];
  // Nodes — only parking, spots, hold-shorts, and named nodes get a friendly
  // label. Generic intersection nodes are still searchable by typing the bare id.
  D.nodes.forEach((n) => {
    const tag =
      n.type === "Parking"
        ? "parking"
        : n.type === "Spot"
        ? "spot"
        : n.type === "RunwayHoldShort"
        ? "hold-short"
        : null;
    if (tag !== null || n.name) {
      let label = "#" + n.id + " (" + (tag || "node");
      if (n.name) label += ' "' + n.name + '"';
      if (n.rwyId) label += " rwy=" + n.rwyId;
      label += ")";
      entries.push({ label, kind: "node", value: "#" + n.id });
    }
  });
  // Taxiways — collect distinct names from edges. "B - C" arc names split into both.
  const twySet = new Set();
  D.edges.forEach((e) => {
    if (e.twy && !e.rwy && !e.ramp) twySet.add(e.twy);
  });
  D.arcs.forEach((a) => {
    (a.names || []).forEach((n) => twySet.add(n));
  });
  Array.from(twySet)
    .sort()
    .forEach((t) => {
      entries.push({ label: "T " + t, kind: "taxiway", value: "T:" + t });
    });
  // Runways — designators on each side of "/".
  const rwySet = new Set();
  D.runways.forEach((r) => {
    (r.name || "").split("/").forEach((s) => {
      if (s) rwySet.add(s.toUpperCase());
    });
  });
  Array.from(rwySet)
    .sort()
    .forEach((r) => {
      entries.push({ label: "R " + r, kind: "runway", value: "R:" + r });
    });
  return entries;
}

const searchEntries = buildSearchIndex();

function refreshDatalist() {
  while (hloptions.firstChild) hloptions.removeChild(hloptions.firstChild);
  searchEntries.forEach((e) => {
    const opt = document.createElement("option");
    opt.value = e.value;
    opt.label = e.label;
    hloptions.appendChild(opt);
  });
}

function parseHighlightToken(raw) {
  // Returns { kind, key } or null. Rules (case-insensitive):
  //   "#1234"  / "1234"            → node id
  //   "T:NAME" / "T NAME"          → taxiway
  //   "R:DESIG" / "R DESIG"        → runway designator
  //   "NAME"                        → ambiguous; matches taxiway then runway
  const s = (raw || "").trim();
  if (!s) return null;
  if (s.startsWith("#")) {
    const id = parseInt(s.slice(1), 10);
    return Number.isFinite(id) && nodeMap[id] ? { kind: "node", key: id } : null;
  }
  const m = s.match(/^([trTR])[:\s]+(.+)$/);
  if (m) {
    const kind = m[1].toLowerCase() === "t" ? "taxiway" : "runway";
    return { kind, key: m[2].trim().toUpperCase() };
  }
  // Bare token: try numeric (node), then taxiway, then runway
  if (/^\d+$/.test(s)) {
    const id = parseInt(s, 10);
    return nodeMap[id] ? { kind: "node", key: id } : null;
  }
  const u = s.toUpperCase();
  if (searchEntries.some((e) => e.kind === "taxiway" && e.value === "T:" + u)) {
    return { kind: "taxiway", key: u };
  }
  if (searchEntries.some((e) => e.kind === "runway" && e.value === "R:" + u)) {
    return { kind: "runway", key: u };
  }
  return null;
}

function addHighlight(kind, key) {
  if (kind === "node") state.nodes.add(key);
  else if (kind === "taxiway") state.taxiways.add(key);
  else if (kind === "runway") state.runways.add(key);
  refreshChips();
  draw();
  saveView();
}

function removeHighlight(kind, key) {
  if (kind === "node") state.nodes.delete(key);
  else if (kind === "taxiway") state.taxiways.delete(key);
  else if (kind === "runway") state.runways.delete(key);
  refreshChips();
  draw();
  saveView();
}

function clearHighlights() {
  state.nodes.clear();
  state.taxiways.clear();
  state.runways.clear();
  refreshChips();
  draw();
  saveView();
}

function refreshChips() {
  while (hlchips.firstChild) hlchips.removeChild(hlchips.firstChild);
  const entries = [];
  Array.from(state.nodes)
    .sort((a, b) => a - b)
    .forEach((id) => entries.push({ kind: "node", key: id, label: "#" + id }));
  Array.from(state.taxiways)
    .sort()
    .forEach((t) => entries.push({ kind: "taxiway", key: t, label: "T " + t }));
  Array.from(state.runways)
    .sort()
    .forEach((r) => entries.push({ kind: "runway", key: r, label: "R " + r }));
  entries.forEach((e) => {
    const chip = document.createElement("span");
    chip.className = "chip";
    chip.dataset.kind = e.kind;
    const lbl = document.createElement("span");
    lbl.className = "label";
    lbl.textContent = e.label;
    chip.appendChild(lbl);
    const btn = document.createElement("button");
    btn.textContent = "×";
    btn.title = "Remove";
    btn.addEventListener("click", () => removeHighlight(e.kind, e.key));
    chip.appendChild(btn);
    hlchips.appendChild(chip);
  });
  hlclearBtn.disabled = entries.length === 0;
}

hlinput.addEventListener("keydown", (e) => {
  if (e.key === "Enter") {
    e.preventDefault();
    const parsed = parseHighlightToken(hlinput.value);
    if (parsed) {
      addHighlight(parsed.kind, parsed.key);
      hlinput.value = "";
    } else {
      const orig = hlinput.style.borderColor;
      hlinput.style.borderColor = "var(--danger)";
      setTimeout(() => {
        hlinput.style.borderColor = orig;
      }, 400);
    }
  }
});
// 'change' fires when the user picks an option from the datalist via mouse.
hlinput.addEventListener("change", () => {
  const parsed = parseHighlightToken(hlinput.value);
  if (parsed) {
    addHighlight(parsed.kind, parsed.key);
    hlinput.value = "";
  }
});

hlclearBtn.addEventListener("click", clearHighlights);

refreshDatalist();
refreshChips();

loadView();
// loadView may have changed the highlight Sets from the URL hash — refresh
// the chip UI so it reflects the loaded state.
refreshChips();
resize();
