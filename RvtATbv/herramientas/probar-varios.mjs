#!/usr/bin/env node
// probar-varios.mjs — corre la herramienta sobre varios .rvt de C:\ARQ (siempre sobre copias) y los mide contra el .tbv / _Habitaciones.json de producción (R2).
//   node probar-varios.mjs [nombre ...]      (sin nombres: la lista completa; nombres entre los de LISTA)
// Escribe un resumen en <trabajo>\salida\varios_<fecha>.txt
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const AQUI = path.dirname(fileURLToPath(import.meta.url));
const TRABAJO = path.join(AQUI, '..', '..', 'PruebasInternas');
const REF = path.join(TRABAJO, 'referencia_r2');
const LISTA = {
    SO_DU: 'C:\\ARQ\\X02_SO\\03_ARQ\\02_RVT\\SO_DU.rvt',
    JU_DU: 'C:\\ARQ\\Y02_JU\\03_ARQ\\02_RVT\\JU_DU.rvt',
    SO_MO: 'C:\\ARQ\\X02_SO\\03_ARQ\\02_RVT\\SO_MO.rvt',
    JU_ARQ: 'C:\\ARQ\\Y02_JU\\03_ARQ\\02_RVT\\JU_ARQ.rvt',
    SO_ARQ: 'C:\\ARQ\\X02_SO\\03_ARQ\\02_RVT\\SO_ARQ.rvt',
    HO_ARQ: 'C:\\ARQ\\Y06_HO\\03_ARQ\\02_RVT\\HO_ARQ.rvt',
    AF_ARQ: 'C:\\ARQ\\X06_AF\\03_ARQ\\02_RVT\\AF_ARQ.rvt',
    RI_ARQ: 'C:\\ARQ\\X03_RI\\03_ARQ\\02_RVT\\RI_ARQ.rvt'
};
const nombres = process.argv.slice(2).filter((a) => !a.startsWith('--'));
const sel = nombres.length ? nombres : Object.keys(LISTA);
const run = (args, opts = {}) => spawnSync('node', args, { encoding: 'utf8', maxBuffer: 1 << 28, ...opts });
const filas = [];
for (const n of sel) {
    const rvt = LISTA[n]; if (!rvt || !fs.existsSync(rvt)) { console.log(n, 'no existe'); continue; }
    const salida = path.join(TRABAJO, 'salida', n, 'varios');
    fs.rmSync(salida, { recursive: true, force: true });
    const t0 = Date.now();
    const mb = (fs.statSync(rvt).size / 1048576).toFixed(0);
    const r = run(['--expose-gc', '--max-old-space-size=' + (Number(mb) > 200 ? 6000 : 4000), '--experimental-strip-types', path.join(AQUI, 'rvt-a-tbv.mjs'), rvt, '--salida', salida]);
    const seg = ((Date.now() - t0) / 1000).toFixed(0);
    const fila = { n, mb, seg, ok: r.status === 0 };
    if (r.status !== 0) { fila.error = ((r.stderr || '') + (r.stdout || '')).slice(-300).replace(/\s+/g, ' '); filas.push(fila); console.log(JSON.stringify(fila)); continue; }
    const refTbv = path.join(REF, n + '_Geometria.tbv'), tbv = path.join(salida, n + '_Geometria.tbv');
    if (fs.existsSync(refTbv) && fs.statSync(refTbv).size > 100) {
        run([path.join(AQUI, 'comparar-tbv.mjs'), refTbv, tbv, '--salida', salida, '--nombre', 'vs']);
        try {
            const J = JSON.parse(fs.readFileSync(path.join(salida, 'vs_comparacion.json'), 'utf8')), e = J.elementos;
            Object.assign(fila, { presentes: e.pct_de_A_presentes_en_B, cat: e.misma_categoria, guid: e.mismo_guid, famtipo: e.misma_familia_y_tipo, bbox1cm: e.bbox_dentro_de_1cm, trisA: J.A.triangulos, trisB: J.B.triangulos, soloB: e.soloEnB, soloA: e.soloEnA, planta: J.render?.planta?.iou_silueta, iso: J.render?.iso?.iou_silueta });
        } catch (_) { fila.comp = 'sin comparación'; }
        const co = run(['--max-old-space-size=3000', path.join(AQUI, 'diag-colores.mjs'), refTbv, tbv]);
        fila.color = Number((/\(([\d.]+) %\)/.exec(co.stdout || '') || [])[1]) || null;
    }
    const refH = path.join(REF, n + '_Geometria_Habitaciones.json'), hab = path.join(salida, n + '_Geometria_Habitaciones.json');
    if (fs.existsSync(refH) && fs.existsSync(hab)) {
        const R = JSON.parse(fs.readFileSync(refH, 'utf8')), H = JSON.parse(fs.readFileSync(hab, 'utf8'));
        const M = new Map(H.Rooms.map((x) => [x.ElementId, x]));
        const enc = R.Rooms.filter((x) => M.has(x.ElementId));
        const d1 = (a, b) => Math.max(...[0, 1, 2].map((k) => Math.abs(a[k] - b[k]))) < 0.01;
        fila.hab = { ref: R.Rooms.length, gen: H.Rooms.length, enc: enc.length, nombre: enc.filter((x) => M.get(x.ElementId).Name === x.Name).length, nivel: enc.filter((x) => M.get(x.ElementId).Level === x.Level).length, centro: enc.filter((x) => d1(M.get(x.ElementId).Center, x.Center)).length, guid: enc.filter((x) => M.get(x.ElementId).Guid === x.Guid).length, base: H.ProjectBasePoint.every((v, k) => Math.abs(v - R.ProjectBasePoint[k]) < 1e-4) };
    }
    filas.push(fila); console.log(JSON.stringify(fila));
}
fs.writeFileSync(path.join(TRABAJO, 'salida', 'varios_ultimo.json'), JSON.stringify(filas, null, 1));
