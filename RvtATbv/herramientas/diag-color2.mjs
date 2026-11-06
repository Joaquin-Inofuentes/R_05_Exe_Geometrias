// diag-color2.mjs — para los elementos con color distinto: materiales del tipo / asignaciones del elemento con su color vs. el de producción.
//   node diag-color2.mjs <R2.tbv> <generado.tbv> <cache.json> [--max=12]
import fs from 'node:fs';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';
console.warn = () => {};
const [pa, pb, pc] = process.argv.slice(2).filter((a) => !a.startsWith('--'));
const max = Number((process.argv.find((a) => a.startsWith('--max=')) || '--max=12').split('=')[1]);
const C = JSON.parse(fs.readFileSync(pc, 'utf8'));
const mats = new Map(C.mats.map((m) => [m.id, m])), asign = new Map(C.asign.map((a) => [a.id, a.a])), mdt = new Map(C.matsDeTipo), tid = new Map(C.tipoIds);
const col = (m) => m && m.rgb ? m.rgb.join(',') + (m.tr ? '/t' + m.tr.toFixed(2) : '') : '?';
const ld = (p) => { const b = fs.readFileSync(p), d = parseTbv(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength)); const m = new Map(); for (const i of d.instances) { const mt = d.materials[i.matIdx]; const e = m.get(i.elemId) || { cat: i.catId, fam: d.names[i.famIdx], tipo: d.names[i.typIdx], cols: new Map() }; const t = d.meshes[i.meshIdx].tc; const k = mt.r + ',' + mt.g + ',' + mt.b; e.cols.set(k, (e.cols.get(k) || 0) + t); m.set(i.elemId, e); } return m; };
const dom = (e) => [...e.cols].sort((a, b) => b[1] - a[1])[0][0];
const A = ld(pa), B = ld(pb); const g = new Map();
for (const [id, a] of A) { const b = B.get(id); if (!b || dom(a) === dom(b)) continue; const k = `${a.cat} | ${a.fam} | ${a.tipo} | R2 ${[...a.cols.keys()].join(' ; ')} ← gen ${[...b.cols.keys()].join(' ; ')}`; const x = g.get(k) || []; x.push(id); g.set(k, x); }
for (const [k, ids] of [...g].sort((x, y) => y[1].length - x[1].length).slice(0, max)) {
    const id = ids[0], t = tid.get(id);
    console.log(String(ids.length).padStart(4), k, '· ej.', id, '\n       asign:', JSON.stringify((asign.get(id) || []).map((x) => x.materialId + ':' + col(mats.get(x.materialId)))), ' tipo-mats:', JSON.stringify((t ? mdt.get(t) || [] : []).map((x) => x + ':' + col(mats.get(x)))));
}
