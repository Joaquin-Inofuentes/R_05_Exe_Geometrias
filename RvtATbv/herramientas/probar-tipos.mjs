// probar-tipos.mjs — resuelve el tipo de los elementos de sistema con un caché de reviter y lo compara con producción.
//   node --expose-gc probar-tipos.mjs <copia.rvt> <cache.json> <R2.tbv>
//   (el caché se crea con: rvt-a-tbv.mjs <rvt> --volcar-cache <cache.json>)
import fs from 'node:fs';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';
import { abrirRvt, identidades } from './lector-rvt.mjs';
import { clasesDeTipo, clasesGenericasDe } from './tipos.mjs';
import { tiposSistema } from './escaneo.mjs';
const [rvt, cachep, r2p, schemap] = process.argv.slice(2);
const C = JSON.parse(fs.readFileSync(cachep, 'utf8'));
const cfb = abrirRvt(rvt), idt = identidades(cfb);
const t0 = Date.now();
const r = tiposSistema(cfb, new Set(idt.filas.keys()), C.sinTipo, { log: console.log, clasesGenericas: schemap ? clasesGenericasDe(JSON.parse(fs.readFileSync(schemap, 'utf8'))) : null, claseGElement: C.claseGElement, clasesTipo: schemap ? clasesDeTipo(JSON.parse(fs.readFileSync(schemap, 'utf8'))) : new Set(C.clasesTipo), idsMateriales: new Set(C.idsMateriales) });
const b = fs.readFileSync(r2p), d = parseTbv(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength));
const ref = new Map(); for (const i of d.instances) if (!ref.has(i.elemId)) ref.set(i.elemId, d.names[i.typIdx] ?? '');
let en = 0, ok = 0, mal = 0, sin = 0; const ej = [];
for (const id of C.sinTipo) { if (!ref.has(id)) continue; en++; const t = r.tipos.get(id); if (t === undefined) sin++; else if (t === ref.get(id)) ok++; else { mal++; if (ej.length < 10) ej.push(`${id}: R2 «${ref.get(id)}» ← «${t}»`); } }
console.log(`sinTipo ${C.sinTipo.length} · en R2 ${en} · correctos ${ok} · distintos ${mal} · sin resolver ${sin} · ${((Date.now() - t0) / 1000).toFixed(0)} s`);
console.log(ej.join('\n'));
// sin resolver agrupados por clase del objeto del elemento
const tags = schemap ? new Map(JSON.parse(fs.readFileSync(schemap, 'utf8')).map((c) => [c.tag, c.name])) : new Map();
const refFam = new Map(); for (const i of d.instances) if (!refFam.has(i.elemId)) refFam.set(i.elemId, d.names[i.famIdx] ?? '');
const g = new Map();
for (const id of C.sinTipo) { if (!ref.has(id) || r.tipos.has(id)) continue; const c = tags.get(r.clases.get(id)) || ('clase ' + r.clases.get(id)); const k = c + ' · ' + refFam.get(id); const a = g.get(k) || []; a.push(id); g.set(k, a); }
console.log('sin resolver por clase · familia R2:');
for (const [k, a] of [...g].sort((x, y) => y[1].length - x[1].length).slice(0, 25)) console.log(String(a.length).padStart(4), k, '· ej.', a[0]);
