// diag-clases.mjs — para cada elemento de R2: clase de su objeto de elemento (marco ≠ GElement) y familia/tipo de producción.
//   node --expose-gc diag-clases.mjs <rvt> <R2.tbv> <auditoria.json|schema.json> [salida.json]
import fs from 'node:fs';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';
import { abrirRvt, inflarTodo } from './lector-rvt.mjs';
console.warn = () => {};
const [rvt, refp, audp, outp] = process.argv.slice(2);
const b0 = fs.readFileSync(refp); const A = parseTbv(b0.buffer.slice(b0.byteOffset, b0.byteOffset + b0.byteLength));
const NOM = new Map(JSON.parse(fs.readFileSync(audp, 'utf8')).schema.taggedClasses.map((c) => [c.tag, c.name]));
const el = new Map();
for (const i of A.instances) if (!el.has(i.elemId)) el.set(i.elemId, { cat: i.catId, fam: A.names[i.famIdx] ?? '', tipo: A.names[i.typIdx] ?? '' });
const quiero = new Set(el.keys());
const cfb = abrirRvt(rvt);
const clases = new Map();
for (const k of [...cfb.streams.keys()].filter((s) => s.startsWith('/partitions/'))) {
    const b = inflarTodo(cfb.leer(k));
    for (let o = 0; o + 16 <= b.length; o++) {
        const v = b.readUInt32LE(o);
        if (!quiero.has(v)) continue;
        const ln = b.readUInt32LE(o + 8);
        if (ln > 16 && ln < 3000000 && o + 16 + ln <= b.length && b.readUInt32LE(o + 12 + ln) === ln) {
            const cl = b.readUInt16LE(o + 12);
            if (cl === 1911) continue;
            if (!clases.has(v)) clases.set(v, new Map());
            clases.get(v).set(cl, (clases.get(v).get(cl) || 0) + 1);
        }
    }
    if (global.gc) global.gc();
}
const tabla = {};
for (const [id, e] of el) {
    const cm = clases.get(id); const cl = cm ? [...cm.entries()].sort((a, b) => b[1] - a[1])[0][0] : null;
    const nombre = cl ? NOM.get(cl) || String(cl) : '(sin marco)';
    const k = `${e.cat} | ${nombre}`;
    const t = tabla[k] || (tabla[k] = { n: 0, familias: {} });
    t.n++; t.familias[e.fam] = (t.familias[e.fam] || 0) + 1;
}
const salida = Object.entries(tabla).sort((a, b) => b[1].n - a[1].n);
for (const [k, t] of salida) console.log(k, t.n, JSON.stringify(Object.entries(t.familias).sort((a, b) => b[1] - a[1]).slice(0, 3)));
if (outp) fs.writeFileSync(outp, JSON.stringify(Object.fromEntries(salida), null, 1));
