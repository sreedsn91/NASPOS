// Renders public/favicon.svg into favicon.ico (16/32/48) and apple-touch-icon.png (180) using Playwright's Chromium.
// Run after changing the logo:  node scripts/make-icons.mjs
import { chromium } from '@playwright/test';
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const pub = join(dirname(fileURLToPath(import.meta.url)), '..', 'public');
const svg = readFileSync(join(pub, 'favicon.svg'), 'utf8');

const browser = await chromium.launch();
const page = await browser.newPage();
async function render(size, padding = 0) {
  await page.setViewportSize({ width: size, height: size });
  const inner = size - padding * 2;
  await page.setContent(`<html><body style="margin:0;background:transparent">
    <div style="width:${size}px;height:${size}px;display:grid;place-items:center;background:${padding ? '#ffffff' : 'transparent'}">
    <img style="width:${inner}px;height:${inner}px" src="data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}"></div></body></html>`);
  return page.screenshot({ omitBackground: !padding, clip: { x: 0, y: 0, width: size, height: size } });
}

const pngs = [];
for (const s of [16, 32, 48]) pngs.push({ size: s, data: await render(s) });
// iOS home-screen icon: opaque background, small margin.
writeFileSync(join(pub, 'apple-touch-icon.png'), await render(180, 10));
await browser.close();

// ICO container with PNG-compressed images (supported since Windows Vista and by all browsers).
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(pngs.length, 4);
const dir = Buffer.alloc(16 * pngs.length);
let offset = 6 + dir.length;
pngs.forEach((p, i) => {
  const o = i * 16;
  dir.writeUInt8(p.size >= 256 ? 0 : p.size, o); dir.writeUInt8(p.size >= 256 ? 0 : p.size, o + 1);
  dir.writeUInt8(0, o + 2); dir.writeUInt8(0, o + 3);
  dir.writeUInt16LE(1, o + 4); dir.writeUInt16LE(32, o + 6);
  dir.writeUInt32LE(p.data.length, o + 8); dir.writeUInt32LE(offset, o + 12);
  offset += p.data.length;
});
writeFileSync(join(pub, 'favicon.ico'), Buffer.concat([header, dir, ...pngs.map(p => p.data)]));
console.log('Wrote favicon.ico (16/32/48) and apple-touch-icon.png (180).');
