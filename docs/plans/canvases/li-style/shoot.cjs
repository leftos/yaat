// Usage: node shoot.cjs <dir> <name>... — screenshots <dir>/<name>.html at two fixed framings.
const path = require("path");
const os = require("os");
const { chromium } = require(path.join(os.homedir(), ".claude/tools/mock-three/node_modules/playwright"));

const VIEWS = [
  // overview: centred between the KAI ramp and the 28L hold short
  { suffix: "overview", lat: 37.7262, lon: -122.2112, scale: 2.7 },
  // zoom: the C / ramp junction beside the KAI ramp, with a 28R hold short below it
  { suffix: "zoom", node: 1096, scale: 12.0 },
];

(async () => {
  const [dir, ...names] = process.argv.slice(2);
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  for (const name of names) {
    const file = "file:///" + path.resolve(dir, name + ".html").replace(/\\/g, "/");
    await page.goto(file, { waitUntil: "networkidle" });
    await page.evaluate(() => document.fonts.ready);
    for (const v of VIEWS) {
      await page.evaluate((v) => {
        scale = v.scale;
        if (v.node !== undefined) {
          v.lat = nodeMap[v.node].lat;
          v.lon = nodeMap[v.node].lon;
        }
        const x0 = ((v.lon - minLon) / (maxLon - minLon)) * W;
        const y0 = (1 - (v.lat - minLat) / (maxLat - minLat)) * H;
        panX = W / 2 - x0;
        panY = H / 2 - y0;
        draw();
      }, v);
      const out = path.resolve(dir, `${name}-${v.suffix}.png`);
      await page.screenshot({ path: out });
      console.log("wrote", out);
    }
  }
  await browser.close();
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
