// diag-conjunto.mjs — elementos que sólo están en producción (A) o sólo en el generado (B), agrupados por categoría y familia|tipo.
//   node diag-conjunto.mjs <R2.tbv> <generado.tbv> [--max=12]
import fs from 'node:fs';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';
console.warn = () => {};
const [pa, pb] = process.argv.slice(2).filter((a) => !a.startsWith('--'));
const max = Number((process.argv.find((a) => a.startsWith('--max=')) || '--max=12').split('=')[1]);
const ld = (p) => { const b = fs.readFileSync(p), d = parseTbv(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength)); const m = new Map(); for (const i of d.instances) { const e = m.get(i.elemId) || { cat: i.catId, fam: d.names[i.famIdx] ?? '', tipo: d.names[i.typIdx] ?? '', tris: 0, min: [1e9, 1e9, 1e9], max: [-1e9, -1e9, -1e9] }; e.tris += d.meshes[i.meshIdx].tc; for (let k = 0; k < 3; k++) { e.min[k] = Math.min(e.min[k], i.gmin[k]); e.max[k] = Math.max(e.max[k], i.gmax[k]); } m.set(i.elemId, e); } return m; };
const A = ld(pa), B = ld(pb);
for (const [titulo, X, Y] of [['SOLO EN PRODUCCIÓN (faltan en el generado)', A, B], ['SOLO EN EL GENERADO (sobran)', B, A]]) {
    const g = new Map();
    for (const [id, e] of X) if (!Y.has(id)) { const k = `${e.cat} | ${e.fam} | ${e.tipo}`; const a = g.get(k) || { n: 0, tris: 0, ej: [] }; a.n++; a.tris += e.tris; if (a.ej.length < 2) a.ej.push(id); g.set(k, a); }
    console.log('\n' + titulo + ': ' + [...g.values()].reduce((s, a) => s + a.n, 0));
    for (const [k, a] of [...g].sort((x, y) => y[1].n - x[1].n).slice(0, max)) console.log(String(a.n).padStart(4), String(a.tris).padStart(6) + ' tris', k, '· ej.', a.ej.join(','));
}
