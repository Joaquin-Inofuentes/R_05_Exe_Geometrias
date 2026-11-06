// diag-triangulos.mjs — por elemento: triángulos de R2 vs candidato, agrupados por categoría y por fuente de geometría de reviter.
//   node diag-triangulos.mjs <R2.tbv> <candidato.tbv> [auditoria.json]
import fs from 'node:fs';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';
console.warn = () => {};
const [pa, pb, pj] = process.argv.slice(2);
const cargar = (p) => { const b = fs.readFileSync(p); return parseTbv(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength)); };
const por = (d) => { const m = new Map(); for (const i of d.instances) { const e = m.get(i.elemId) || { tris: 0, inst: 0, cat: i.catId, fam: d.names[i.famIdx], tipo: d.names[i.typIdx], mats: new Set() }; e.tris += d.meshes[i.meshIdx].tc; e.inst++; e.mats.add(i.matIdx); m.set(i.elemId, e); } return m; };
const A = por(cargar(pa)), B = por(cargar(pb));
const src = new Map();
if (pj) for (const e of JSON.parse(fs.readFileSync(pj, 'utf8')).elementManifest.elements) src.set(e.elementId, e.geometry && e.geometry.source);
const g = {};
let totA = 0, totB = 0;
for (const [id, a] of A) {
    const b = B.get(id); const t = b ? b.tris : 0;
    totA += a.tris; totB += t;
    const k = `${a.cat} | ${src.get(id) || '?'}`;
    const r = g[k] || (g[k] = { n: 0, trisA: 0, trisB: 0, iguales: 0, menos: 0, mas: 0, instA: 0, instB: 0 });
    r.n++; r.trisA += a.tris; r.trisB += t; r.instA += a.inst; r.instB += b ? b.inst : 0;
    if (t === a.tris) r.iguales++; else if (t < a.tris) r.menos++; else r.mas++;
}
console.log('triángulos R2', totA, 'candidato (sobre los elementos de R2)', totB);
console.table(g);
const ej = [...A].filter(([id, a]) => (B.get(id)?.tris ?? 0) !== a.tris).sort((x, y) => Math.abs(y[1].tris - (B.get(y[0])?.tris ?? 0)) - Math.abs(x[1].tris - (B.get(x[0])?.tris ?? 0))).slice(0, 12);
for (const [id, a] of ej) console.log(id, a.cat, a.fam + '|' + a.tipo, 'R2', a.tris, 'tris/', a.inst, 'inst/', a.mats.size, 'mat → cand', B.get(id)?.tris, 'tris/', B.get(id)?.inst, 'inst', src.get(id));
