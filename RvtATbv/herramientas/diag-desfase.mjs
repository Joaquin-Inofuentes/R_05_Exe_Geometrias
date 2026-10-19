// diag-desfase.mjs — desfase mediano (x,y,z) entre las cajas de los elementos comunes de dos .tbv, y cuántos coinciden tras corregirlo.
import fs from 'node:fs';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';
console.warn = () => {};
const [pa, pb] = process.argv.slice(2);
const ld = (p) => { const b = fs.readFileSync(p), d = parseTbv(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength)); const m = new Map(); for (const i of d.instances) { const e = m.get(i.elemId) || { min: [1e9, 1e9, 1e9], max: [-1e9, -1e9, -1e9] }; for (let k = 0; k < 3; k++) { e.min[k] = Math.min(e.min[k], i.gmin[k]); e.max[k] = Math.max(e.max[k], i.gmax[k]); } m.set(i.elemId, e); } return m; };
const A = ld(pa), B = ld(pb);
const med = (a) => a.sort((x, y) => x - y)[a.length >> 1];
const d = [[], [], []];
for (const [id, a] of A) { const b = B.get(id); if (!b) continue; for (let k = 0; k < 3; k++) d[k].push(b.min[k] - a.min[k]); }
const m = d.map(med);
console.log('desfase mediano B−A (m):', m.map((v) => +v.toFixed(4)).join(', '), 'sobre', d[0].length, 'elementos');
for (const corr of [[0, 0, 0], m]) {
    let ok = 0, n = 0;
    for (const [id, a] of A) { const b = B.get(id); if (!b) continue; n++; let good = true; for (let k = 0; k < 3; k++) if (Math.abs(b.min[k] - corr[k] - a.min[k]) > 0.01 || Math.abs(b.max[k] - corr[k] - a.max[k]) > 0.01) good = false; if (good) ok++; }
    console.log('corrección', corr.map((v) => +v.toFixed(3)).join(','), '→ cajas dentro de 1 cm:', ok, '/', n);
}
