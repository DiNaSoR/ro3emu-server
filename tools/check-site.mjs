import assert from 'node:assert/strict';
import { readFileSync, existsSync, statSync } from 'node:fs';
import { dirname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const site = resolve(root, 'docs/site');
const html = readFileSync(resolve(site, 'index.html'), 'utf8');
const canonical = 'https://akreao.github.io/ro3emu-server/';
assert.match(html, /<html lang="en">/);
assert.equal([...html.matchAll(/<h1\b/g)].length, 1, 'Use one descriptive H1');
assert.ok(html.includes(`<link rel="canonical" href="${canonical}">`));
assert.match(html, /<meta name="description" content="[^"]{70,220}">/);
const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(m => m[1]);
assert.equal(new Set(ids).size, ids.length, 'Duplicate HTML id');
let checked = 0;
for (const [, url] of html.matchAll(/\b(?:href|src)="([^"]+)"/g)) {
  if (/^(?:https?:|mailto:|data:)/.test(url)) continue;
  const [path, hash] = url.split('#');
  if (!path && hash) assert.ok(ids.includes(hash), `Missing anchor ${url}`);
  if (path) {
    const target = resolve(site, path === './' ? 'index.html' : path);
    assert.ok(target.startsWith(site + sep), `Local link leaves website: ${url}`);
    assert.ok(existsSync(target), `Missing website file: ${url}`);
  }
  checked++;
}
const structured = JSON.parse(html.match(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/)?.[1] ?? 'null');
assert.equal(structured?.codeRepository, 'https://github.com/akreao/ro3emu-server');
assert.equal(structured?.url, canonical);
assert.ok(!('aggregateRating' in structured), 'Do not invent a rating');
const png = readFileSync(resolve(site, 'social-preview.png'));
assert.equal(png.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
assert.equal(png.readUInt32BE(16), 1280);
assert.equal(png.readUInt32BE(20), 640);
assert.ok(statSync(resolve(site, 'social-preview.png')).size < 1_000_000, 'Social preview must fit GitHub limit');
assert.ok(readFileSync(resolve(site, 'sitemap.xml'), 'utf8').includes(`<loc>${canonical}</loc>`));
assert.match(html, /Builds server code\. Playing requires exported client data\./);
console.log(`Website checks passed: ${checked} local links, canonical, JSON-LD, sitemap, 1280x640 social image.`);
