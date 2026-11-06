// probar-habitaciones.mjs — valida las habitaciones decodificadas del .rvt contra el _Habitaciones.json de producción (R2).
//   node probar-habitaciones.mjs <copia.rvt> <referencia_Habitaciones.json> [--escribir salida.json]
import fs from 'node:fs';
import { abrirRvt, identidades } from './lector-rvt.mjs';
import { extraerHabitaciones, jsonHabitaciones } from './habitaciones.mjs';
const [rvt, refp] = process.argv.slice(2);
const ref = JSON.parse(fs.readFileSync(refp, 'utf8'));
// ProjectBasePoint del JSON de producción está en ejes de Revit (x, y, z), metros
const base = ref.ProjectBasePoint;
const cfb = abrirRvt(rvt);
const idt = identidades(cfb);
const t0 = Date.now();
const { rooms, niveles } = extraerHabitaciones(cfb, idt, { base });
const R = new Map(rooms.map((r) => [r.ElementId, r]));
let ok = { nombre: 0, depto: 0, nivel: 0, centro1cm: 0, centro10cm: 0, guid: 0 }, faltan = [], dif = [];
for (const r of ref.Rooms) {
    const m = R.get(r.ElementId);
    if (!m) { faltan.push(r.ElementId); continue; }
    if (m.Name === r.Name) ok.nombre++; else dif.push(['nombre', r.ElementId, r.Name, m.Name]);
    if (m.Department === r.Department) ok.depto++; else dif.push(['depto', r.ElementId, r.Department, m.Department]);
    if (m.Level === r.Level) ok.nivel++; else dif.push(['nivel', r.ElementId, r.Level, m.Level]);
    const d = Math.max(...[0, 1, 2].map((k) => Math.abs(m.Center[k] - r.Center[k])));
    if (d < 0.01) ok.centro1cm++; if (d < 0.10) ok.centro10cm++; else dif.push(['centro', r.ElementId, d.toFixed(3)]);
    if (m.Guid === r.Guid) ok.guid++;
}
const extra = rooms.filter((r) => !ref.Rooms.some((x) => x.ElementId === r.ElementId));
console.log(`habitaciones R2: ${ref.Rooms.length} · decodificadas: ${rooms.length} · encontradas: ${ref.Rooms.length - faltan.length} · faltan: ${faltan.length} · sobrantes: ${extra.length} · niveles: ${niveles.length} · ${((Date.now() - t0) / 1000).toFixed(0)} s`);
console.log('coinciden:', JSON.stringify(ok));
if (faltan.length) console.log('faltan (ids):', faltan.slice(0, 10).join(','));
if (extra.length) console.log('sobrantes (ejemplos):', JSON.stringify(extra.slice(0, 5)));
if (dif.length) console.log('diferencias:', JSON.stringify(dif.slice(0, 8)));
const w = process.argv.indexOf('--escribir');
if (w > 0) fs.writeFileSync(process.argv[w + 1], jsonHabitaciones(rooms, base));
