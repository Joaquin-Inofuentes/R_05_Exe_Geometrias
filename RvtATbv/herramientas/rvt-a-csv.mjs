#!/usr/bin/env node
// rvt-a-csv.mjs — de un .rvt a tres CSV (crudo, profesional con medidas reales, catálogo de variantes), SIN Revit y sin tocar el original.
//   node rvt-a-csv.mjs <ruta.rvt> [--salida <dir>] [--trabajo <dir>]
// Pasos: rvt-a-tbv (copia segura + .tbv + auditoría) → tbv-a-csv → extraer-parametros → tbv-a-csv (con parámetros y nombres de datos\nombres_parametros.json).
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';

const AQUI = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : d; };
const rvt = args[0];
if (!rvt || rvt.startsWith('--') || !fs.existsSync(rvt)) { console.error('Uso: node rvt-a-csv.mjs <ruta.rvt> [--salida dir] [--trabajo dir]'); process.exit(2); }
const nombre = path.basename(rvt, '.rvt').replace(/[^A-Za-z0-9_.-]/g, '_');
const trabajo = path.resolve(opt('--trabajo', path.join(AQUI, '..', '..', 'PruebasInternas')));
const salida = path.resolve(opt('--salida', path.join(trabajo, 'salida', nombre)));
const aud = path.join(salida, 'auditoria'), csv = path.join(salida, 'csv');
const copia = path.join(trabajo, 'copias_rvt', nombre + '.rvt');
const nodo = (script, a, heap = 4000) => {
    const r = spawnSync(process.execPath, ['--expose-gc', `--max-old-space-size=${heap}`, '--experimental-strip-types', path.join(AQUI, script), ...a], { stdio: 'inherit' });
    if (r.status !== 0) { console.error(`falló ${script} (código ${r.status})`); process.exit(r.status || 1); }
};
console.log('1/4 copia segura + .tbv + auditoría'); nodo('rvt-a-tbv.mjs', [path.resolve(rvt), '--auditoria', '--salida', aud, '--trabajo', trabajo], 6000);
const tbv = path.join(aud, nombre + '_Geometria.tbv'), auditoria = path.join(aud, nombre + '_auditoria.json');
console.log('2/4 CSV base'); nodo('tbv-a-csv.mjs', [tbv, csv, auditoria]);
console.log('3/4 parámetros de instancia y de tipo'); const pjson = path.join(salida, 'parametros_instancia.json');
nodo('extraer-parametros.mjs', [copia, path.join(csv, nombre + '_2_profesional.csv'), pjson, auditoria]);
console.log('4/4 CSV final'); nodo('tbv-a-csv.mjs', [tbv, csv, auditoria, pjson, path.join(AQUI, 'datos', 'nombres_parametros.json')]);
console.log('Listo: ' + csv);
