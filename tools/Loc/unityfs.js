// Minimal UnityFS reader (LZ4/LZ4HC/none) + SerializedFile (v22) object table + typetree reader.
const fs = require('fs');

function lz4Block(src, dstSize) {
  const dst = Buffer.alloc(dstSize); let s = 0, d = 0;
  while (s < src.length) {
    const tok = src[s++];
    let lit = tok >> 4;
    if (lit === 15) { let b; do { b = src[s++]; lit += b; } while (b === 255); }
    src.copy(dst, d, s, s + lit); s += lit; d += lit;
    if (s >= src.length) break;
    const off = src[s] | (src[s + 1] << 8); s += 2;
    let ml = tok & 15;
    if (ml === 15) { let b; do { b = src[s++]; ml += b; } while (b === 255); }
    ml += 4;
    let m = d - off;
    for (let i = 0; i < ml; i++) dst[d++] = dst[m++];
  }
  return dst;
}

function readCStr(b, o) { let e = o; while (b[e] !== 0) e++; return [b.toString('utf8', o, e), e + 1]; }

// returns [{path, data}]
function readUnityFS(buf) {
  let o = 0;
  const [sig, o1] = readCStr(buf, o); o = o1;
  if (sig !== 'UnityFS') throw new Error('not UnityFS: ' + sig);
  const ver = buf.readUInt32BE(o); o += 4;
  [, o] = readCStr(buf, o); [, o] = readCStr(buf, o);
  const size = Number(buf.readBigInt64BE(o)); o += 8;
  const cbi = buf.readUInt32BE(o), ubi = buf.readUInt32BE(o + 4), flags = buf.readUInt32BE(o + 8); o += 12;
  if (ver >= 7) o = (o + 15) & ~15;
  let biRaw;
  if (flags & 0x80) biRaw = buf.subarray(buf.length - cbi); else { biRaw = buf.subarray(o, o + cbi); o += cbi; }
  const comp = flags & 0x3f;
  let bi = comp === 0 ? biRaw : (comp === 2 || comp === 3) ? lz4Block(biRaw, ubi) : null;
  if (!bi) throw new Error('blocksinfo compression ' + comp);
  let p = 16; const nb = bi.readInt32BE(p); p += 4; const blocks = [];
  for (let i = 0; i < nb; i++) { blocks.push({ u: bi.readUInt32BE(p), c: bi.readUInt32BE(p + 4), f: bi.readUInt16BE(p + 8) }); p += 10; }
  const nn = bi.readInt32BE(p); p += 4; const nodes = [];
  for (let i = 0; i < nn; i++) {
    const off = Number(bi.readBigInt64BE(p)), sz = Number(bi.readBigInt64BE(p + 8)), fl = bi.readUInt32BE(p + 16); p += 20;
    const [path, np] = readCStr(bi, p); p = np; nodes.push({ off, sz, fl, path });
  }
  if (flags & 0x200) o = (o + 15) & ~15;
  const total = blocks.reduce((a, b) => a + b.u, 0); const data = Buffer.alloc(total); let d = 0;
  for (const b of blocks) {
    const raw = buf.subarray(o, o + b.c); o += b.c;
    const c = b.f & 0x3f;
    if (c === 0) raw.copy(data, d); else if (c === 2 || c === 3) lz4Block(raw, b.u).copy(data, d); else throw new Error('block compression ' + c);
    d += b.u;
  }
  return nodes.map(n => ({ path: n.path, flags: n.fl, data: data.subarray(n.off, n.off + n.sz) }));
}

const COMMON = 'AABB\0AnimationClip\0AnimationCurve\0AnimationState\0Array\0Base\0BitField\0bitset\0bool\0char\0ColorRGBA\0Component\0data\0deque\0double\0dynamic_array\0FastPropertyName\0first\0float\0Font\0GameObject\0Generic Mono\0GradientNEW\0GUID\0GUIStyle\0int\0list\0long long\0map\0Matrix4x4f\0MdFour\0MonoBehaviour\0MonoScript\0m_ByteSize\0m_Curve\0m_EditorClassIdentifier\0m_EditorHideFlags\0m_Enabled\0m_ExtensionPtr\0m_GameObject\0m_Index\0m_IsArray\0m_IsStatic\0m_MetaFlag\0m_Name\0m_ObjectHideFlags\0m_PrefabInternal\0m_PrefabParentObject\0m_Script\0m_StaticEditorFlags\0m_Type\0m_Version\0Object\0pair\0PPtr<Component>\0PPtr<GameObject>\0PPtr<Material>\0PPtr<MonoBehaviour>\0PPtr<MonoScript>\0PPtr<Object>\0PPtr<Prefab>\0PPtr<Sprite>\0PPtr<TextAsset>\0PPtr<Texture>\0PPtr<Texture2D>\0PPtr<Transform>\0Prefab\0Quaternionf\0Rectf\0RectInt\0RectOffset\0second\0set\0short\0size\0SInt16\0SInt32\0SInt64\0SInt8\0staticvector\0string\0TextAsset\0TextMesh\0Texture\0Texture2D\0Transform\0TypelessData\0UInt16\0UInt32\0UInt64\0UInt8\0unsigned int\0unsigned long long\0unsigned short\0vector\0Vector2f\0Vector3f\0Vector4f\0m_ScriptingClassIdentifier\0Gradient\0Type*\0int2_storage\0int3_storage\0BoundsInt\0m_CorrespondingSourceObject\0m_PrefabInstance\0m_PrefabAsset\0FileSize\0Hash128\0RenderingLayerMask\0';
const COMMON_MAP = new Map(); { let off = 0; for (const s of COMMON.split('\0')) { COMMON_MAP.set(off, s); off += Buffer.byteLength(s) + 1; } }

function parseSerialized(b) {
  let o = 0;
  let ver = b.readUInt32BE(8);
  let dataOffset = b.readUInt32BE(12); o = 16;
  let le = b[o] === 0; o += 4;
  if (ver >= 22) { o += 4; o += 8; dataOffset = Number(b.readBigInt64BE(o)); o += 8; o += 8; }
  const R = {
    u8: () => b[o++], i16: () => { const v = le ? b.readInt16LE(o) : b.readInt16BE(o); o += 2; return v; },
    i32: () => { const v = le ? b.readInt32LE(o) : b.readInt32BE(o); o += 4; return v; },
    u32: () => { const v = le ? b.readUInt32LE(o) : b.readUInt32BE(o); o += 4; return v; },
    i64: () => { const v = le ? b.readBigInt64LE(o) : b.readBigInt64BE(o); o += 8; return v; },
    cstr: () => { const [s, n] = readCStr(b, o); o = n; return s; },
  };
  const unityVersion = R.cstr(); const platform = R.i32(); const typeTree = R.u8() !== 0;
  const readTypes = (isRef) => {
    const n = R.i32(); const types = [];
    for (let i = 0; i < n; i++) {
      const t = { classID: R.i32() }; t.stripped = R.u8(); t.scriptIndex = R.i16();
      if (isRef || t.classID === 114) o += 16;
      o += 16;
      if (typeTree) {
        const nc = R.i32(), sb = R.i32(); const nodes = [];
        const nstart = o; o += nc * 32; const sbuf = b.subarray(o, o + sb); o += sb;
        const str = (x) => (x & 0x80000000) ? (COMMON_MAP.get(x & 0x7fffffff) ?? ('?c' + (x & 0x7fffffff))) : readCStr(sbuf, x)[0];
        for (let k = 0; k < nc; k++) {
          const p = nstart + k * 32;
          const rd32 = (q) => le ? b.readUInt32LE(q) : b.readUInt32BE(q);
          nodes.push({ level: b[p + 2], flags: b[p + 3], type: str(rd32(p + 4)), name: str(rd32(p + 8)), size: le ? b.readInt32LE(p + 12) : b.readInt32BE(p + 12), meta: rd32(p + 20) });
        }
        t.nodes = nodes;
        if (isRef) { R.cstr(); R.cstr(); R.cstr(); } else { const dn = R.i32(); o += dn * 4; }
      }
      types.push(t);
    }
    return types;
  };
  const types = readTypes(false);
  const nObj = R.i32(); const objects = [];
  for (let i = 0; i < nObj; i++) {
    o = (o + 3) & ~3;
    const pathID = R.i64(); const start = Number(R.i64()); const size = R.u32(); const typeIdx = R.i32();
    objects.push({ pathID, start: dataOffset + start, size, type: types[typeIdx] });
  }
  const nScripts = R.i32(); const scripts = [];
  for (let i = 0; i < nScripts; i++) { const fi = R.i32(); o = (o + 3) & ~3; const id = R.i64(); scripts.push({ fi, id }); }
  const nExt = R.i32(); const externals = [];
  for (let i = 0; i < nExt; i++) { R.cstr(); o += 16; R.i32(); externals.push(R.cstr()); }
  return { ver, le, unityVersion, typeTree, types, objects, scripts, externals, buf: b };
}

// generic typetree value reader
function readTree(sf, obj) {
  const b = sf.buf; const nodes = obj.type.nodes; let o = obj.start; const le = sf.le;
  let i = 0;
  function align(n) { if (n.meta & 0x4000) o = (o + 3) & ~3; }
  function val(idx) {
    const n = nodes[idx];
    // children range
    let end = idx + 1; while (end < nodes.length && nodes[end].level > n.level) end++;
    let v;
    switch (n.type) {
      case 'bool': case 'UInt8': case 'char': v = b[o]; o += 1; break;
      case 'SInt8': v = b.readInt8(o); o += 1; break;
      case 'SInt16': case 'short': v = le ? b.readInt16LE(o) : b.readInt16BE(o); o += 2; break;
      case 'UInt16': case 'unsigned short': v = le ? b.readUInt16LE(o) : b.readUInt16BE(o); o += 2; break;
      case 'SInt32': case 'int': v = le ? b.readInt32LE(o) : b.readInt32BE(o); o += 4; break;
      case 'UInt32': case 'unsigned int': case 'Type*': v = le ? b.readUInt32LE(o) : b.readUInt32BE(o); o += 4; break;
      case 'SInt64': case 'long long': v = le ? b.readBigInt64LE(o) : b.readBigInt64BE(o); o += 8; break;
      case 'UInt64': case 'unsigned long long': case 'FileSize': v = le ? b.readBigUInt64LE(o) : b.readBigUInt64BE(o); o += 8; break;
      case 'float': v = le ? b.readFloatLE(o) : b.readFloatBE(o); o += 4; break;
      case 'double': v = le ? b.readDoubleLE(o) : b.readDoubleBE(o); o += 8; break;
      case 'string': {
        const len = le ? b.readInt32LE(o) : b.readInt32BE(o); o += 4; v = b.toString('utf8', o, o + len); o += len;
        // string node: child Array has align flag
        const arr = nodes[idx + 1]; if (arr && (arr.meta & 0x4000)) o = (o + 3) & ~3;
        align(n); return [v, end];
      }
      default: {
        if (idx + 1 < end && nodes[idx + 1].type === 'Array') {
          // vector/array: child Array{size, data}
          const arrIdx = idx + 1; let aend = arrIdx + 1; while (aend < nodes.length && nodes[aend].level > nodes[arrIdx].level) aend++;
          const cnt = le ? b.readInt32LE(o) : b.readInt32BE(o); o += 4;
          const dataIdx = arrIdx + 2;
          const dn = nodes[dataIdx];
          if (cnt > 10000000) throw new Error('bad count');
          if ((dn.type === 'UInt8' || dn.type === 'char') && dn.size === 1) { v = b.subarray(o, o + cnt); o += cnt; }
          else { v = []; for (let k = 0; k < cnt; k++) { const [x] = val(dataIdx); v.push(x); } }
          if (nodes[arrIdx].meta & 0x4000) o = (o + 3) & ~3;
          align(n); return [v, end];
        }
        if (n.type === 'Array') {
          const cnt = le ? b.readInt32LE(o) : b.readInt32BE(o); o += 4;
          v = []; for (let k = 0; k < cnt; k++) { const [x] = val(idx + 2); v.push(x); }
          align(n); return [v, end];
        }
        v = {}; let c = idx + 1;
        while (c < end) { const [x, ne] = val(c); v[nodes[c].name] = x; c = ne; }
      }
    }
    align(n);
    return [v, end];
  }
  return val(0)[0];
}

module.exports = { lz4Block, readUnityFS, parseSerialized, readTree };
