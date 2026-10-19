// diag-tipos.mjs — dónde difieren familia/tipo/GUID entre producción (R2) y el candidato, agrupado por categoría.
//   node diag-tipos.mjs <R2.tbv> <candidato.tbv>
import fs from 'node:fs';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';
console.warn = () => {};
const ld = (p) => { const b = fs.readFileSync(p); return parseTbv(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength)); };
const [pa, pb] = process.argv.slice(2);
const A = ld(pa), B = ld(pb);
const por = (d) => { const m = new Map(); for (const i of d.instances) if (!m.has(i.elemId)) m.set(i.elemId, { cat: i.catId, fam: d.names[i.famIdx] ?? '', tipo: d.names[i.typIdx] ?? '', guid: i.guid }); return m; };
const EA = por(A), EB = por(B);
const g = {};
for (const [id, a] of EA) {
    const b = EB.get(id); if (!b) continue;
    const k = String(a.cat);
    const r = g[k] || (g[k] = { n: 0, famTipoOk: 0, guidOk: 0, sinGuid: 0, ejemplos: [] });
    r.n++;
    if (a.fam === b.fam && a.tipo === b.tipo) r.famTipoOk++; else if (r.ejemplos.length < 4) r.ejemplos.push(`${id}: R2 «${a.fam}|${a.tipo}» ← cand «${b.fam}|${b.tipo}»`);
    if (a.guid === b.guid) r.guidOk++; else if (!b.guid) r.sinGuid++;
}
for (const [k, r] of Object.entries(g).sort((x, y) => y[1].n - x[1].n)) console.log(`cat ${k}: ${r.n} elementos · fam/tipo ok ${r.famTipoOk} · guid ok ${r.guidOk} (sin guid ${r.sinGuid})` + (r.ejemplos.length ? '\n    ' + r.ejemplos.join('\n    ') : ''));
