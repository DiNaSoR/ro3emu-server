// Prints the Ro3Emu.ClientHooks MapModules table entry for a map config module (Config/Map/<name>.lua) that the
// client's recovery manifest does not admit, from the original chunk still stored in the VFS under the same name.
// The chunk is decoded as tools/LuaDecode does (0x1E signature, per-function instruction XOR, chained string XOR,
// permuted opcodes) and its main function is run on the few opcodes a constant table needs.
// usage: node mapmodule.js <name> [<name>...]     e.g. node mapmodule.js sc_pm_gef_dun02_001
const V = require('./vfsget.js');

const OpMap = (() => {
  const m = []; for (let i = 0; i < 128; i++) m[i] = i;
  for (let i = 1; i <= 20; i++) m[i] = 33 + i;   // ADD..CONCAT
  for (let i = 21; i <= 39; i++) m[i] = i - 20;  // LOADI..NEWTABLE
  for (let i = 40; i <= 53; i++) m[i] = i - 20;  // SELF, ADDI..SHLI
  m[83] = 12;                                     // a second GETTABLE
  return m;
})();

// Lua floats are kept apart from integers: the module's subtypes are reproduced as written.
class Float { constructor(v) { this.v = v; } }

function parse(src) {
  const d = Buffer.from(src); let p = 32;
  if (d[0] !== 0x1E || d[4] !== 0x54) throw new Error('not an RO3 Lua 5.4 chunk');
  const vi = () => { let x = 0, b; do { b = d[p++]; x = x * 128 + (b & 0x7f); } while ((b & 0x80) === 0); return x; };
  const str = () => {
    const size = vi(); if (size === 0) return null; const n = size - 1;
    for (let k = n - 1; k >= 1; k--) d[p + k] ^= d[p + k - 1];
    if (n > 0) d[p] ^= n & 0xff;
    const s = d.toString('utf8', p, p + n); p += n; return s;
  };
  function fn() {
    const f = { code: [], k: [], p: [] };
    str(); vi(); vi(); p += 3;
    const sizecode = vi(); const key = ((sizecode << 16) | sizecode) >>> 0;
    for (let i = 0; i < sizecode; i++, p += 4) {
      const w = (d.readUInt32LE(p) ^ key) >>> 0;
      f.code.push(((w & ~0x7f) | OpMap[w & 0x7f]) >>> 0);
    }
    const nk = vi();
    for (let i = 0; i < nk; i++) {
      const tag = d[p++];
      if (tag === 0x00) f.k.push(null);
      else if (tag === 0x01) f.k.push(false);
      else if (tag === 0x11) f.k.push(true);
      else if (tag === 0x03) { f.k.push(Number(d.readBigInt64LE(p))); p += 8; }
      else if (tag === 0x13) { f.k.push(new Float(d.readDoubleLE(p))); p += 8; }
      else if (tag === 0x04 || tag === 0x14) f.k.push(str());
      else throw new Error('constant tag ' + tag);
    }
    const nups = vi(); p += 3 * nups;
    const np = vi(); for (let i = 0; i < np; i++) f.p.push(fn());
    const nlines = vi(); p += nlines;
    const abs = vi(); for (let i = 0; i < abs; i++) { vi(); vi(); }
    const locs = vi(); for (let i = 0; i < locs; i++) { str(); vi(); vi(); }
    const ups = vi(); for (let i = 0; i < ups; i++) str();
    return f;
  }
  const main = fn();
  if (p !== d.length) throw new Error(`trailing bytes: parsed ${p} of ${d.length}`);
  return main;
}

// Runs a constant-table chunk: loads, NEWTABLE, SETFIELD/SETI and the final RETURN; anything else is refused.
function run(f) {
  const R = [];
  for (let pc = 0; pc < f.code.length; pc++) {
    const w = f.code[pc], op = w & 0x7f, A = (w >>> 7) & 0xff, k = (w >>> 15) & 1;
    const B = (w >>> 16) & 0xff, C = (w >>> 24) & 0xff, Bx = w >>> 15, sBx = Bx - 65535;
    const rk = () => (k ? f.k[C] : R[C]);
    switch (op) {
      case 0: R[A] = R[B]; break;                   // MOVE
      case 1: R[A] = sBx; break;                    // LOADI
      case 2: R[A] = new Float(sBx); break;         // LOADF
      case 3: R[A] = f.k[Bx]; break;                // LOADK
      case 17: R[A][B] = rk(); break;               // SETI
      case 18: R[A][f.k[B]] = rk(); break;          // SETFIELD
      case 19: R[A] = {}; pc++; break;              // NEWTABLE (+ EXTRAARG)
      case 81: break;                               // VARARGPREP
      case 70: return R[A];                         // RETURN
      default: throw new Error(`opcode ${op} at ${pc}: not a constant table`);
    }
  }
  throw new Error('no RETURN');
}

const num = (v) => {
  if (v instanceof Float) { const s = String(v.v); return /[.eE]/.test(s) ? s : s + '.0'; }
  if (typeof v === 'number' && Number.isInteger(v)) return String(v);
  throw new Error('not a number: ' + JSON.stringify(v));
};
const keys = (o) => Object.keys(o).join(',');

function entry(name) {
  const meta = entry.meta || (entry.meta = V.loadMeta());
  const r = V.extract(meta, name);
  if (!r.data) throw new Error(name + ': no Lua chunk of that name in the VFS');
  const m = run(parse(r.data));
  if (keys(m) !== 'kMapId,kNavMeshExporter,ItemName,MiniMapInfo') throw new Error(name + ': unexpected fields ' + keys(m));
  if (m.kNavMeshExporter !== `map/bin/${name}.bin` || m.ItemName !== name) throw new Error(name + ': navmesh path or ItemName not derived from the name');
  if (typeof m.kMapId !== 'number') throw new Error(name + ': kMapId is not an integer');
  const cameras = Object.keys(m.MiniMapInfo).map(Number);
  if (cameras.some((c, i) => c !== i)) throw new Error(name + ': MiniMapInfo keys ' + cameras);
  const lines = cameras.map((c) => {
    const cam = m.MiniMapInfo[c];
    if (keys(cam) !== 'CamPixelWidth,CamPixelHeight,P,CameraPos,CameraRotation,CameraOrthographicSize,CameraAspect')
      throw new Error(name + ': camera fields ' + keys(cam));
    const P = Array.from({ length: 16 }, (_, i) => cam.P[i]);
    const parts = [
      [cam.CamPixelWidth, cam.CamPixelHeight], P,
      [cam.CameraPos.x, cam.CameraPos.y, cam.CameraPos.z],
      [cam.CameraRotation.x, cam.CameraRotation.y, cam.CameraRotation.z, cam.CameraRotation.w],
      [cam.CameraOrthographicSize, cam.CameraAspect],
    ];
    return '"' + parts.map((g) => g.map(num).join(' ')).join('  ') + '"';
  });
  return `new MapModule("${name}", ${m.kMapId},\n    ${lines.join(',\n    ')}),`;
}

if (require.main === module) {
  const names = process.argv.slice(2);
  if (names.length === 0) { console.error('usage: node mapmodule.js <name> [<name>...]'); process.exit(1); }
  for (const n of names) console.log(entry(n));
}
module.exports = { parse, run, entry };
