// diag-colores.mjs — ¿el color de material dominante de cada elemento coincide con producción?  node diag-colores.mjs <R2.tbv> <candidato.tbv>
import fs from 'node:fs';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';
console.warn = () => {};
const ld = (p) => { const b = fs.readFileSync(p); return parseTbv(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength)); };
const [pa, pb] = process.argv.slice(2); const A = ld(pa), B = ld(pb);
const dom = (d) => { const m = new Map(); for (const i of d.instances) { const mt = d.materials[i.matIdx]; const k = mt ? [mt.r, mt.g, mt.b].join(',') : '?'; const e = m.get(i.elemId) || new Map(); e.set(k, (e.get(k) || 0) + d.meshes[i.meshIdx].tc); m.set(i.elemId, e); } const o = new Map(); for (const [id, e] of m) o.set(id, [...e.entries()].sort((a, b) => b[1] - a[1])[0][0]); return o; };
const da = dom(A), db = dom(B); let n = 0, ok = 0, cerca = 0; const ej = {};
const d3 = (x, y) => { const a = x.split(',').map(Number), b = y.split(',').map(Number); return Math.max(...a.map((v, i) => Math.abs(v - b[i]))); };
for (const [id, c] of da) { if (!db.has(id)) continue; n++; const c2 = db.get(id); if (c === c2) ok++; else { if (d3(c, c2) <= 20) cerca++; const k = c + ' ← ' + c2; ej[k] = (ej[k] || 0) + 1; } }
console.log(`elementos comunes ${n} · color dominante igual ${ok} (${(100 * ok / n).toFixed(1)} %) · parecido (≤20/255) ${cerca}`);
console.log('diferencias más frecuentes (R2 ← candidato):', JSON.stringify(Object.entries(ej).sort((a, b) => b[1] - a[1]).slice(0, 8)));
