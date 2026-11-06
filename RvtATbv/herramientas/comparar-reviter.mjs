#!/usr/bin/env node
// comparar-reviter.mjs — mide la auditoría JSON de reviter (lectura del .rvt SIN Revit) contra el .tbv de R2, elemento por elemento.
//   node comparar-reviter.mjs <auditoria.json> <referencia.tbv> [--salida informe.json]
// Eje: .tbv = (X, Z, -Y) de Revit, metros. Reviter da cajas en pies, con origen propio: el desplazamiento se estima con la mediana
// de los elementos comunes (no se asume).
import fs from 'node:fs';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';
console.warn = () => {};
const [pj, pt] = process.argv.slice(2);
const out = process.argv.includes('--salida') ? process.argv[process.argv.indexOf('--salida') + 1] : null;
const FT = 0.3048;
const aud = JSON.parse(fs.readFileSync(pj, 'utf8'));
const b = fs.readFileSync(pt);
const d = parseTbv(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength));

// verdad (R2)
const ref = new Map();
for (const i of d.instances) {
    let e = ref.get(i.elemId);
    if (!e) { e = { id: i.elemId, cat: i.catId, fam: d.names[i.famIdx], tipo: d.names[i.typIdx], min: [1e9, 1e9, 1e9], max: [-1e9, -1e9, -1e9] }; ref.set(i.elemId, e); }
    for (let k = 0; k < 3; k++) { e.min[k] = Math.min(e.min[k], i.gmin[k]); e.max[k] = Math.max(e.max[k], i.gmax[k]); }
}
// reviter: sólo elementos dibujados
const rv = new Map();
for (const e of aud.elementManifest.elements) {
    if (!e.displayed || !e.geometry || !e.geometry.boundsFeet) continue;
    const bf = e.geometry.boundsFeet;
    // Revit (x,y,z) pies → tbv (X, Y=z, Z=-y) metros, sin desplazamiento todavía
    const mn = [bf.min.x * FT, bf.min.z * FT, -bf.max.y * FT];
    const mx = [bf.max.x * FT, bf.max.z * FT, -bf.min.y * FT];
    rv.set(e.elementId, { id: e.elementId, cat: e.category, tipo: e.type, src: e.geometry.source, min: mn, max: mx });
}
const comunes = [...ref.keys()].filter((k) => rv.has(k));
const mediana = (a) => { const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)] ?? 0; };
const off = [0, 1, 2].map((k) => mediana(comunes.map((id) => (ref.get(id).min[k] + ref.get(id).max[k]) / 2 - (rv.get(id).min[k] + rv.get(id).max[k]) / 2)));
let ok1 = 0, ok10 = 0, ok50 = 0;
const peor = [];
for (const id of comunes) {
    const a = ref.get(id), r = rv.get(id);
    let dm = 0;
    for (let k = 0; k < 3; k++) dm = Math.max(dm, Math.abs(a.min[k] - (r.min[k] + off[k])), Math.abs(a.max[k] - (r.max[k] + off[k])));
    if (dm <= 0.01) ok1++; if (dm <= 0.10) ok10++; if (dm <= 0.5) ok50++;
    peor.push({ id, dm: +dm.toFixed(3), cat: a.cat, fam: a.fam, tipo: a.tipo, fuente: r.src });
}
peor.sort((x, y) => y.dm - x.dm);
const pct = (n, t) => t ? +(100 * n / t).toFixed(1) : null;
const porCat = {};
for (const id of ref.keys()) { const c = ref.get(id).cat; porCat[c] = porCat[c] || { ref: 0, hallados: 0, caja10cm: 0 }; porCat[c].ref++; if (rv.has(id)) porCat[c].hallados++; }
for (const p of peor) if (p.dm <= 0.10) porCat[p.cat].caja10cm++;
const inf = {
    referencia_R2: { elementos: ref.size, triangulos: d.meshes.reduce((n, m) => n + m.tc, 0) },
    reviter: { elementos_dibujados: rv.size, triangulos: aud.stats.triangleCount, version_detectada: aud.decoderCoverage.revitVersion },
    desplazamiento_estimado_m: off.map((x) => +x.toFixed(3)),
    elementos_de_R2_encontrados_por_reviter: { n: comunes.length, pct: pct(comunes.length, ref.size) },
    en_comunes: { caja_1cm: pct(ok1, comunes.length), caja_10cm: pct(ok10, comunes.length), caja_50cm: pct(ok50, comunes.length) },
    sobrantes_de_reviter_que_R2_no_tiene: [...rv.keys()].filter((k) => !ref.has(k)).length,
    por_categoria_R2: porCat,
    peores: peor.slice(0, 10)
};
if (out) fs.writeFileSync(out, JSON.stringify(inf, null, 2));
console.log(JSON.stringify(inf, null, 2));
