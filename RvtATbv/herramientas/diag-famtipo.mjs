// diag-famtipo.mjs — diferencias de familia/tipo/categoría/guid entre un .tbv de referencia (producción) y uno generado, agrupadas.
//   node diag-famtipo.mjs <ref.tbv> <generado.tbv> [--max 25]
import fs from 'node:fs';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';
const [refP, genP] = process.argv.slice(2).filter((a) => !a.startsWith('--'));
const max = Number((process.argv.find((a) => a.startsWith('--max=')) || '--max=25').split('=')[1]);
function cargar(p) {
    const b = fs.readFileSync(p), d = parseTbv(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength));
    const m = new Map();
    for (const i of d.instances) if (!m.has(i.elemId)) m.set(i.elemId, { fam: d.names[i.famIdx] ?? '', tipo: d.names[i.typIdx] ?? '', cat: i.catId, guid: i.guid });
    return m;
}
const R = cargar(refP), G = cargar(genP);
const grupos = new Map(); let comunes = 0, igual = 0;
for (const [id, r] of R) {
    const g = G.get(id); if (!g) continue; comunes++;
    if (r.fam === g.fam && r.tipo === g.tipo) { igual++; continue; }
    const k = `[${r.fam} | ${r.tipo}]  →  [${g.fam} | ${g.tipo}]`;
    if (!grupos.has(k)) grupos.set(k, []);
    grupos.get(k).push(id);
}
console.log(`comunes ${comunes} · familia+tipo iguales ${igual} (${(100 * igual / comunes).toFixed(1)} %)`);
for (const [k, ids] of [...grupos].sort((a, b) => b[1].length - a[1].length).slice(0, max)) console.log(String(ids.length).padStart(5), k, '· ej. id', ids[0]);
