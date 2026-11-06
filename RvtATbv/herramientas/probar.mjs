#!/usr/bin/env node
// probar.mjs — pruebas de regresión del conversor .rvt → .tbv sin Revit. Falla (código ≠ 0) si algún umbral no se cumple.
//   node probar.mjs            SO_DU (rápido, ~1 min)
//   node probar.mjs --completo además RI_ARQ (112 MB, ~4 min, necesita ~3 GB de RAM libre)
// Las referencias son los .tbv / _Habitaciones.json que produjo el Revit de producción (bajados de R2 a PruebasInternas\referencia_r2).
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const AQUI = path.dirname(fileURLToPath(import.meta.url));
const TRABAJO = path.join(AQUI, '..', '..', 'PruebasInternas');
const REF = path.join(TRABAJO, 'referencia_r2');
const completo = process.argv.includes('--completo');
let fallos = 0, total = 0;
const ok = (c, m, extra = '') => { total++; if (!c) fallos++; console.log(`${c ? '  ok ' : '  FALLA'}  ${m}${extra ? '  · ' + extra : ''}`); };
const run = (args, opts = {}) => spawnSync('node', args, { encoding: 'utf8', maxBuffer: 1 << 28, ...opts });
const sha = (p) => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');

function caso(nombre, rvt, { habitaciones }) {
    console.log(`\n=== ${nombre} ===`);
    const orig = rvt, antes = sha(orig), mt = fs.statSync(orig).mtimeMs;
    const salida = path.join(TRABAJO, 'salida', nombre, 'prueba');
    fs.rmSync(salida, { recursive: true, force: true });
    const t0 = Date.now();
    const r = run(['--expose-gc', '--max-old-space-size=3200', '--experimental-strip-types', path.join(AQUI, 'rvt-a-tbv.mjs'), orig, '--salida', salida]);
    ok(r.status === 0, 'la herramienta termina sin error', `${((Date.now() - t0) / 1000).toFixed(0)} s`);
    if (r.status !== 0) { console.log((r.stdout || '').slice(-600), (r.stderr || '').slice(-600)); return; }
    ok(sha(orig) === antes && fs.statSync(orig).mtimeMs === mt, 'el original no se modificó (sha256 y fecha)');
    const tbv = path.join(salida, nombre + '_Geometria.tbv');
    const refTbv = path.join(REF, nombre + '_Geometria.tbv');
    // formato válido (mismo parser que el visor)
    const v = run(['-e', `import('file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js').then(m=>{console.warn=()=>{};const b=require('fs').readFileSync(process.argv[1]);const d=m.parseTbv(b.buffer.slice(b.byteOffset,b.byteOffset+b.byteLength));console.log(JSON.stringify({m:d.meshes.length,i:d.instances.length}))})`, tbv]);
    ok(v.status === 0, 'el .tbv lo abre el parser del visor', (v.stdout || '').trim());
    // comparación contra producción
    const c = run([path.join(AQUI, 'comparar-tbv.mjs'), refTbv, tbv, '--salida', salida, '--nombre', 'vs_R2']);
    const J = JSON.parse(fs.readFileSync(path.join(salida, 'vs_R2_comparacion.json'), 'utf8'));
    const e = J.elementos;
    ok(e.pct_de_A_presentes_en_B >= 95, 'elementos de producción presentes', `${e.pct_de_A_presentes_en_B} %`);
    ok(e.misma_categoria === 100, 'misma categoría', `${e.misma_categoria} %`);
    ok(e.mismo_guid >= 99, 'mismo GUID', `${e.mismo_guid} %`);
    ok(e.misma_familia_y_tipo >= (nombre === 'SO_DU' ? 99 : 90), 'misma familia y tipo', `${e.misma_familia_y_tipo} %`);
    if (nombre === 'SO_DU') {
        ok(e.bbox_dentro_de_1cm >= 85, 'caja del elemento dentro de 1 cm', `${e.bbox_dentro_de_1cm} %`);
        const rn = J.render || {};
        ok((rn.planta?.iou_silueta ?? 0) >= 0.99 && (rn.iso?.iou_silueta ?? 0) >= 0.95, 'siluetas planta/iso iguales a producción', `planta ${rn.planta?.iou_silueta} · iso ${rn.iso?.iou_silueta}`);
        const dA = J.A.bbox, dB = J.B.bbox;
        ok([0, 1, 2].every((k) => Math.abs(dA.min[k] - dB.min[k]) < 0.05 && Math.abs(dA.max[k] - dB.max[k]) < 0.05), 'caja de la escena igual (±5 cm)');
    }
    // color del material dominante de cada elemento
    const co = run(['--max-old-space-size=3000', path.join(AQUI, 'diag-colores.mjs'), refTbv, tbv]);
    const pc = Number((/\(([\d.]+) %\)/.exec(co.stdout || '') || [])[1]);
    ok(pc >= (nombre === 'SO_DU' ? 55 : 65), 'color de material dominante igual a producción', `${pc} %`);
    // habitaciones: mismo formato
    const hab = JSON.parse(fs.readFileSync(path.join(salida, nombre + '_Geometria_Habitaciones.json'), 'utf8'));
    const ref = JSON.parse(fs.readFileSync(path.join(REF, nombre + '_Geometria_Habitaciones.json'), 'utf8'));
    ok(['Units', 'AxisSystem', 'Origin', 'ProjectBasePoint', 'Count', 'Rooms'].every((k) => k in hab) && hab.Units === ref.Units && hab.AxisSystem === ref.AxisSystem && hab.Origin === ref.Origin, 'habitaciones: mismo formato y cabecera que producción');
    ok(hab.ProjectBasePoint.every((x, k) => Math.abs(x - ref.ProjectBasePoint[k]) < 1e-4), 'punto base igual al de producción', JSON.stringify(hab.ProjectBasePoint.map((x) => +x.toFixed(5))));
    if (habitaciones) {
        const R = new Map(hab.Rooms.map((r) => [r.ElementId, r]));
        const enc = ref.Rooms.filter((r) => R.has(r.ElementId));
        ok(enc.length / ref.Rooms.length >= 0.95, 'habitaciones de producción encontradas', `${enc.length}/${ref.Rooms.length}`);
        ok(enc.every((r) => R.get(r.ElementId).Name === r.Name), 'nombre de habitación idéntico', `${enc.filter((r) => R.get(r.ElementId).Name === r.Name).length}/${enc.length}`);
        ok(enc.every((r) => R.get(r.ElementId).Department === r.Department), 'departamento idéntico');
        const lv = enc.filter((r) => R.get(r.ElementId).Level === r.Level).length;
        ok(lv / enc.length >= 0.99, 'nivel idéntico', `${lv}/${enc.length}`);
        const cen = enc.filter((r) => Math.max(...[0, 1, 2].map((k) => Math.abs(R.get(r.ElementId).Center[k] - r.Center[k]))) < 0.01).length;
        ok(cen / enc.length >= 0.99, 'centro dentro de 1 cm', `${cen}/${enc.length}`);
    } else ok(hab.Count === ref.Count, 'misma cantidad de habitaciones que producción', `${hab.Count}`);
}

caso('SO_DU', 'C:\\ARQ\\X02_SO\\03_ARQ\\02_RVT\\SO_DU.rvt', { habitaciones: false });
if (completo) caso('RI_ARQ', 'C:\\ARQ\\X03_RI\\03_ARQ\\02_RVT\\RI_ARQ.rvt', { habitaciones: true });
console.log(`\n${total - fallos}/${total} comprobaciones correctas${fallos ? ` · ${fallos} FALLAN` : ''}`);
process.exit(fallos ? 1 : 0);
