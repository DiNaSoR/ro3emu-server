// Scan unluac output for string literals containing CJK; decode \ddd escapes; attach callee context.
// usage: node luascan.js <lua-src> <out.json>
const fs = require('fs');
const path = require('path');
const root = process.argv[2];
const out = process.argv[3];
const CJK = /[㐀-鿿豈-﫿　-〿＀-￯]/;
const HAN = /[㐀-鿿豈-﫿]/;

function walk(d, acc) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    const p = path.join(d, e.name);
    if (e.isDirectory()) walk(p, acc);
    else if (e.name.endsWith('.lua')) acc.push(p);
  }
  return acc;
}

// parse all double/single-quoted literals on a line; returns [{start,end,value}]
function literals(line) {
  const res = [];
  let i = 0;
  while (i < line.length) {
    const c = line[i];
    if (c === '-' && line[i + 1] === '-') break;
    if (c === '"' || c === "'") {
      const q = c; const bytes = []; let j = i + 1;
      while (j < line.length && line[j] !== q) {
        if (line[j] === '\\') {
          const n = line[j + 1];
          if (/[0-9]/.test(n)) {
            let m = /^[0-9]{1,3}/.exec(line.slice(j + 1))[0];
            bytes.push(parseInt(m, 10)); j += 1 + m.length; continue;
          }
          const map = { n: 10, r: 13, t: 9, a: 7, b: 8, f: 12, v: 11, '\\': 92, '"': 34, "'": 39 };
          if (n in map) { bytes.push(map[n]); j += 2; continue; }
          if (n === 'x') { bytes.push(parseInt(line.substr(j + 2, 2), 16)); j += 4; continue; }
          bytes.push(n.charCodeAt(0)); j += 2; continue;
        }
        const cp = line.codePointAt(j);
        const b = Buffer.from(String.fromCodePoint(cp), 'utf8');
        for (const x of b) bytes.push(x);
        j += cp > 0xffff ? 2 : 1;
      }
      res.push({ start: i, end: j, value: Buffer.from(bytes).toString('utf8') });
      i = j + 1; continue;
    }
    i++;
  }
  return res;
}

const files = walk(root, []);
const rows = [];
for (const f of files) {
  const rel = path.relative(root, f).replace(/\\/g, '/');
  if (/^Localization_/.test(rel)) continue;
  const lines = fs.readFileSync(f, 'latin1').split('\n');
  // track current function start for context
  let fnName = '';
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const fm = /^\s*(?:local\s+)?function\s+([\w.:]+)/.exec(line);
    if (fm) {
      // name assigned after "end": find next "X.Y = Lname" after function end is expensive; keep raw
      fnName = fm[1];
    }
    if (line.indexOf('"') < 0 && line.indexOf("'") < 0) continue;
    for (const lit of literals(line)) {
      if (!CJK.test(lit.value)) continue;
      // context: register assigned
      const am = /^\s*([\w.\[\]"]+)\s*=\s*$/.exec(line.slice(0, lit.start));
      let reg = am ? am[1] : null;
      let use = null, callee = null, sink = null;
      if (reg && /^L\d+_\d+$/.test(reg)) {
        let r = reg;
        for (let k = i + 1; k < Math.min(lines.length, i + 40); k++) {
          const l = lines[k].trim();
          // concatenation / reassignment flows
          const cm = new RegExp('^(L\\d+_\\d+) = (.*)$').exec(l);
          const callRe = /^(?:([\w, ]+) = )?(L\d+_\d+)\((.*)\)$/;
          const call = callRe.exec(l);
          const tokens = (s) => s.split(/[^\w]+/);
          if (call && tokens(call[3]).includes(r)) {
            use = l; const F = call[2];
            // find callee assignment chain backwards
            const chain = [];
            for (let b = k - 1; b > Math.max(0, i - 40); b--) {
              const bl = lines[b].trim();
              const m1 = new RegExp('^' + F + ' = (.+)$').exec(bl);
              if (m1) {
                const v = m1[1];
                const dm = new RegExp('^' + F + '\\.(\\w+)$').exec(v);
                if (dm) { chain.unshift(dm[1]); continue; }
                const dm2 = new RegExp('^' + F + '\\[(.+)\\]$').exec(v);
                if (dm2) { chain.unshift('[' + dm2[1] + ']'); continue; }
                chain.unshift(v); break;
              }
            }
            callee = chain.join('.');
            break;
          }
          if (cm && tokens(cm[2]).includes(r)) {
            if (/\.\./.test(cm[2]) || /^L\d+_\d+$/.test(cm[2])) { r = cm[1]; continue; }
            // table constructor or other
            sink = l; break;
          }
          const fieldSet = new RegExp('^([\\w.\\[\\]"]+) = ' + r + '$').exec(l);
          if (fieldSet) { sink = l; break; }
          if (new RegExp('\\b' + r + '\\b').test(l)) { sink = l; break; }
        }
      } else if (reg) {
        sink = reg + ' = <lit>';
      }
      rows.push({ file: rel, line: i + 1, fn: fnName, text: lit.value, callee, use, sink, han: HAN.test(lit.value) });
    }
  }
}
fs.writeFileSync(out, JSON.stringify(rows));
console.log('files', files.length, 'cjk literals', rows.length);
