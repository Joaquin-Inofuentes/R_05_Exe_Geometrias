#!/usr/bin/env node
// rvt-a-tbv.mjs — .rvt → _Geometria.tbv (v4) + _Geometria_Habitaciones.json, SIN Revit. Solo lectura del original.
//
//   node --expose-gc --experimental-strip-types rvt-a-tbv.mjs <ruta_absoluta.rvt> [opciones]
//     --salida <dir>     carpeta de resultados (por defecto <trabajo>\salida\<nombre>\sin_revit)
//     --trabajo <dir>    carpeta de pruebas internas (por defecto ..\..\PruebasInternas); las copias seguras van a <trabajo>\copias_rvt
//     --reviter <dir>    dónde está instalada la librería reviter (lectura de geometría)
//     --base x,y,z       Punto Base del Proyecto en metros, ejes de Revit (por defecto: se decodifica del archivo)
//     --niveles-es       familia de sistema en español (Muro básico, Suelo) — es lo que hace el Revit de producción (por defecto)
//
// Qué hace: (1) copia el .rvt a la carpeta de pruebas y trabaja SOLO sobre la copia (sha256 y fecha del original se comprueban antes y después);
// (2) geometría con reviter; (3) GUID, tipo de sistema, habitaciones, niveles y punto base con lectores propios (ingeniería inversa del formato);
// (4) escribe el .tbv con el mismo formato que Visor3D/core/TbvParser.js y el JSON de habitaciones con el formato del exportador de producción.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { abrirRvt, identidades } from './lector-rvt.mjs';
import { resolverTipos, clasesDeTipo, clasesGenericasDe } from './tipos.mjs';
import { habitacionesDe, jsonHabitaciones, puntoBaseDe } from './habitaciones.mjs';
import { escanear } from './escaneo.mjs';

const AQUI = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : d; };
const rvtOriginal = args[0];
if (!rvtOriginal || rvtOriginal.startsWith('--')) {
    console.error('Uso: node --expose-gc --experimental-strip-types rvt-a-tbv.mjs <ruta_absoluta.rvt> [--salida dir] [--trabajo dir] [--reviter dir] [--base x,y,z]');
    process.exit(2);
}
const FT = 0.3048;
const abs = path.resolve(rvtOriginal);
if (!fs.existsSync(abs) || !/\.rvt$/i.test(abs)) { console.error('No existe el .rvt: ' + abs); process.exit(2); }
const trabajo = path.resolve(opt('--trabajo', path.join(AQUI, '..', '..', 'PruebasInternas')));
const nombre = path.basename(abs, '.rvt').replace(/[^A-Za-z0-9_.-]/g, '_');
const salida = path.resolve(opt('--salida', path.join(trabajo, 'salida', nombre, 'sin_revit')));
const REVITER = path.resolve(opt('--reviter', path.join(trabajo, 'repos_terceros', 'reviter')));
const dentro = (a, b) => (path.resolve(a).toLowerCase() + path.sep).startsWith(path.resolve(b).toLowerCase() + path.sep);
for (const d of [trabajo, salida]) if (dentro(d, 'C:\\ARQ')) { console.error('La carpeta de trabajo y la de salida no pueden estar dentro de C:\\ARQ: ' + d); process.exit(2); }
const log = (m) => console.log(`[${((Date.now() - t0) / 1000).toFixed(0)} s] ${m}`);
const sha = (p) => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const t0 = Date.now();

// ── 1 · copia segura (el original solo se lee) ──
fs.mkdirSync(path.join(trabajo, 'copias_rvt'), { recursive: true });
fs.mkdirSync(salida, { recursive: true });
const shaAntes = sha(abs), mtimeAntes = fs.statSync(abs).mtimeMs;
const copia = path.join(trabajo, 'copias_rvt', nombre + '.rvt');
fs.copyFileSync(abs, copia);
if (sha(copia) !== shaAntes) { console.error('La copia no coincide con el original (sha256).'); process.exit(1); }
log(`1/5 copia segura: ${copia}`);

// ── 2 · geometría (reviter) ──
const { convertRvtBytes } = await import(pathToFileURL(path.join(REVITER, 'lib/reviter/convert.ts')).href);
const { makeReport } = await import(pathToFileURL(path.join(REVITER, 'lib/reviter/export-report.ts')).href);
const buf = fs.readFileSync(copia);
let out = convertRvtBytes(new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength), nombre + '.rvt', {}, null);
if (!out.ok) { console.error('No se pudo leer la geometría:', out.error); process.exit(1); }
const rep = JSON.parse(makeReport(out, null));
const man = new Map(rep.elementManifest.elements.map((e) => [e.elementId, e]));
if (args.includes('--auditoria')) fs.writeFileSync(path.join(salida, nombre + '_auditoria.json'), JSON.stringify(rep));
const tagDe = (n, d) => { const c = rep.schema && rep.schema.taggedClasses.find((x) => x.name === n); return c ? c.tag : d; };
const TAGS = { room: tagDe('RoomElem', 3354), nivel: tagDe('Level', 2199), base: tagDe('BasePoint', 467) };
log(`2/5 geometría leída: ${out.stats.triangleCount} triángulos de ${out.stats.candidatesUsed} elementos (Revit ${out.decoderCoverage.revitVersion})`);

// ── 3 · identidades, tipos de sistema, punto base ──
const cfb = abrirRvt(copia);
const idt = identidades(cfb);
// Familia de sistema localizada (el Revit de producción está en español). Medido contra RI_ARQ (R2): clase del objeto del elemento → familia.
const FAMILIA_SISTEMA_ES = { SWall: 'Muro básico', ArcWall: 'Muro básico', Wall: 'Muro básico', BaseRailing: 'Barandilla', TopRail: 'Tipo de barandal superior', StairsRun: 'Tramo monolítico', StairsLanding: 'Descansillo monolítico', StairsElement: 'Escalera moldeada in situ', Ceiling: 'Techo compuesto' };
const familiaSistema = (clase, cat) => {
    const n = NOM_CLASE.get(clase) || '';
    if (n === 'Floor') return cat === -2001300 ? 'Losa de cimentación' : 'Suelo';
    return FAMILIA_SISTEMA_ES[n] || '';
};
const ES = { [-2000011]: 'Muro básico', [-2000032]: 'Suelo' };      // respaldo por categoría cuando no se encontró el objeto del elemento
const famNombre = new Map((out.nativeFamilyDefinitions || []).map((f) => [f.familyId, f.name]));
const idsElementos = new Set(out.meshes.flatMap((m) => (m.elementIds ? [...new Set(m.elementIds)] : [])));
const sinTipo = [...idsElementos].filter((id) => { const e = man.get(id); return e && e.category && !(e.type && e.type.name && (e.type.familyName || (e.type.familyId != null && famNombre.get(e.type.familyId)))); });
const NOM_CLASE = new Map((rep.schema ? rep.schema.taggedClasses : []).map((c) => [c.tag, c.name]));
const clasesTipo = clasesDeTipo(rep.schema ? rep.schema.taggedClasses : []);
const datos = escanear(cfb, { quiero: sinTipo, idsValidos: idt ? new Set(idt.filas.keys()) : new Set(), claseGElement: tagDe('GElement', 1911), clasesTipo, clasesGenericas: clasesGenericasDe(rep.schema ? rep.schema.taggedClasses : []), idsMateriales: new Set((out.nativeMaterialDefinitions || []).map((d) => d.elementId)), claseRoom: TAGS.room, claseNivel: TAGS.nivel, claseBase: TAGS.base, log });
const resTipos = idt ? resolverTipos(datos, { log }) : { tipos: new Map(), clases: new Map(), tipoIds: new Map(), matsDeTipo: new Map() };
const tiposSis = resTipos.tipos;
if (opt('--volcar-cache')) fs.writeFileSync(opt('--volcar-cache'), JSON.stringify({ sinTipo, claseGElement: tagDe('GElement', 1911), clasesTipo: [...clasesTipo], idsMateriales: (out.nativeMaterialDefinitions || []).map((d) => d.elementId), man: rep.elementManifest.elements.map((e) => ({ elementId: e.elementId, category: e.category, type: e.type })), nativeFamilyDefinitions: out.nativeFamilyDefinitions || [], mats: (out.nativeMaterialDefinitions || []).map((d) => ({ id: d.elementId, name: d.name, rgb: d.appearance && d.appearance.baseColorSrgb, tr: d.appearance && d.appearance.transparency })), matsDeTipo: [...resTipos.matsDeTipo], tipoIds: [...resTipos.tipoIds], asign: [...man.values()].filter((e) => e.materialAssignments && e.materialAssignments.length).map((e) => ({ id: e.elementId, a: e.materialAssignments })) }));
const base = opt('--base') ? opt('--base').split(',').map(Number) : puntoBaseDe(datos);
log(`3/5 identidades: ${idt ? idt.elementos + ' filas legibles (historial 0x' + idt.formato.toString(16) + ')' : 'no soportado'} · tipos de sistema: ${tiposSis.size}/${sinTipo.length} · punto base (m): ${base.map((v) => +v.toFixed(5)).join(', ')}`);

// ── 4 · .tbv ──
const srgb = (c) => { c = Math.max(0, Math.min(1, c)); return Math.round(255 * (c <= 0.0031308 ? 12.92 * c : 1.055 * Math.pow(c, 1 / 2.4) - 0.055)); };
const mats = []; const matIdx = new Map();
// Color de cada material = el del propio elemento Material de Revit (reviter lo decodifica: colorPacked / transparencia), como exporta producción.
const defsMat = new Map((out.nativeMaterialDefinitions || []).filter((d) => d.appearance).map((d) => [d.elementId, d.appearance]));
function material(matId, md) {
    const k = matId >= 0 ? 'id' + matId : 'disp' + (md ? md.name : '');
    if (matIdx.has(k)) return matIdx.get(k);
    const ap = defsMat.get(matId);
    let r, g, b, a;
    if (ap) { [r, g, b] = ap.baseColorSrgb; a = Math.round(255 * (1 - (ap.transparency || 0))); }
    else if (matId === -1) { r = g = b = 190; a = 255; }                            // sin material: gris 190 (igual que producción)
    else { const c = md ? md.baseColorLinear : [0.75, 0.75, 0.75, 1]; r = srgb(c[0]); g = srgb(c[1]); b = srgb(c[2]); a = Math.round(255 * Math.max(0, Math.min(1, c[3] ?? 1))); }
    mats.push({ id: matId, r, g, b, a });
    matIdx.set(k, mats.length - 1);
    return mats.length - 1;
}
// Categorías que el exportador de producción NUNCA entrega (medido en los 25 .tbv de referencia): soportes/accesorios de escalera, piezas (Parts), nodos y vínculos analíticos…; 0 = sin categoría.
const CATEGORIAS_NO_EXPORTADAS = new Set([-2000921, -2003500, -2009630, -2009636, -2008139, 0]);
const vivos = idt && idt.filas.size > 1000 ? idt.filas : null, descartados = new Set();
const grupos = new Map();
for (const m of out.meshes) {
    const P = m.positions, I = m.indices, E = m.elementIds;
    const matLote = m.nativeMaterialElementId ?? -1;
    for (let t = 0; t < I.length / 3; t++) {
        const eid = E ? E[t] : 0;
        if (eid && CATEGORIAS_NO_EXPORTADAS.has(((man.get(eid) || {}).category || {}).id ?? 0)) { descartados.add(eid); continue; }
        if (vivos && eid && !vivos.has(eid)) { descartados.add(eid); continue; }      // id que ya no está en la tabla de elementos = borrado: producción no lo exporta
        let matId = matLote;
        if (matId < 0) { const as = (man.get(eid) || {}).materialAssignments; if (as && as.length) matId = as[as.length - 1].materialId; }
        if (matId < 0) { const tid = resTipos.tipoIds.get(eid), ms = tid ? resTipos.matsDeTipo.get(tid) : null; if (ms && ms.length) matId = ms[0]; }   // material de la primera capa del tipo
        const mi = material(matId, out.materials[m.materialIndex]), k = eid + '|' + mi;
        let g = grupos.get(k);
        if (!g) { g = { elem: eid, mat: mi, tris: [] }; grupos.set(k, g); }
        const tri = [];
        for (let v = 0; v < 3; v++) {
            const o = I[t * 3 + v] * 3;
            // reviter entrega las mallas relativas a su origen (pies); Revit (x,y,z) → .tbv (x, z, -y) en metros, relativo al Punto Base
            const x = (P[o] + out.origin.x) * FT, y = (P[o + 1] + out.origin.y) * FT, z = (P[o + 2] + out.origin.z) * FT;
            tri.push([x - base[0], z - base[2], -(y - base[1])]);
        }
        g.tris.push(tri);
    }
}
const nombres = []; const nomIdx = new Map();
const nom = (s) => { s = s || ''; if (!nomIdx.has(s)) { nomIdx.set(s, nombres.length); nombres.push(s); } return nomIdx.get(s); };
const mallas = []; const insts = [];
const smin = [1e9, 1e9, 1e9], smax = [-1e9, -1e9, -1e9];
for (const g of grupos.values()) {
    const pos = [], nrm = [], idx = []; const mapa = new Map();
    const gmin = [1e9, 1e9, 1e9], gmax = [-1e9, -1e9, -1e9];
    for (const [a, b, c] of g.tris) {
        const e1 = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], e2 = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
        let n = [e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0]];
        const l = Math.hypot(...n) || 1; n = n.map((x) => Math.round(127 * x / l));
        for (const p of [a, b, c]) {
            const key = p.map((x) => Math.round(x * 1e5)).join(',') + '|' + n.join(',');
            let i = mapa.get(key);
            if (i === undefined) { i = pos.length / 3; mapa.set(key, i); pos.push(p[0], p[1], p[2]); nrm.push(n[0], n[1], n[2]); }
            idx.push(i);
            for (let k = 0; k < 3; k++) { gmin[k] = Math.min(gmin[k], p[k]); gmax[k] = Math.max(gmax[k], p[k]); }
        }
    }
    for (let k = 0; k < 3; k++) { smin[k] = Math.min(smin[k], gmin[k]); smax[k] = Math.max(smax[k], gmax[k]); }
    mallas.push({ pos, nrm, idx });
    const me = man.get(g.elem);
    const famDef = me && me.type && me.type.familyId != null ? famNombre.get(me.type.familyId) : '';
    let fam = me && me.type && me.type.familyName ? me.type.familyName : (famDef || (resTipos.clases.has(g.elem) ? familiaSistema(resTipos.clases.get(g.elem), me && me.category ? me.category.id : 0) : '') || (me && me.category ? (ES[me.category.id] || '') : ''));
    const tip = me && me.type && me.type.name ? me.type.name : (tiposSis.get(g.elem) || '');
    const catId = me && me.category ? me.category.id : 0;
    if (!fam) { if (catId === -2000171 && tip) fam = tip; else if (catId === -2000170) fam = 'Panel del sistema'; }      // montantes: la familia lleva el nombre del tipo; paneles de muro cortina (Revit en español)
    insts.push({ mesh: mallas.length - 1, elem: g.elem, cat: me && me.category ? me.category.id : 0, mat: g.mat, fam: nom(fam), tip: nom(tip), gmin, gmax, guid: idt ? idt.guidDe(g.elem) : '' });
}
if (!mallas.length) { smin.fill(0); smax.fill(0); }
const partes = [];
const i32 = (v) => { const b = Buffer.alloc(4); b.writeInt32LE(v); partes.push(b); };
const f32 = (v) => { const b = Buffer.alloc(4); b.writeFloatLE(v); partes.push(b); };
const u16 = (v) => { const b = Buffer.alloc(2); b.writeUInt16LE(v); partes.push(b); };
i32(0x56544254); i32(4); f32(1); i32(mats.length); i32(mallas.length); i32(insts.length);
smin.forEach(f32); smax.forEach(f32);
for (const m of mats) { i32(m.id); partes.push(Buffer.from([m.r, m.g, m.b, m.a])); f32(0); f32(0); }
i32(nombres.length); for (const n of nombres) { const b = Buffer.from(n, 'utf8'); u16(b.length); partes.push(b); }
for (const m of mallas) {
    const vc = m.pos.length / 3, tc = m.idx.length / 3, i16 = vc <= 65535;
    i32(vc); partes.push(Buffer.from([i16 ? 1 : 0])); i32(tc);
    partes.push(Buffer.from(new Float32Array(m.pos).buffer));
    partes.push(Buffer.from(new Int8Array(m.nrm).buffer));
    partes.push(Buffer.from((i16 ? new Uint16Array(m.idx) : new Uint32Array(m.idx)).buffer));
}
for (const s of insts) {
    i32(s.mesh); i32(s.elem); i32(s.cat); i32(s.mat); i32(s.fam); i32(s.tip);
    f32(0); f32(0); f32(0); s.gmin.forEach(f32); s.gmax.forEach(f32);
    const g = Buffer.from(s.guid, 'utf8'); u16(g.length); partes.push(g);
}
const dstTbv = path.join(salida, nombre + '_Geometria.tbv');
fs.writeFileSync(dstTbv, Buffer.concat(partes));
const nTris = mallas.reduce((n, m) => n + m.idx.length / 3, 0);
const sinGuid = insts.filter((i) => !i.guid).length;
if (descartados.size) log(`   ${descartados.size} elementos descartados (no figuran en la tabla de elementos o son de categorías que producción no exporta)`);
log(`4/5 TBV: ${dstTbv}\n    ${mallas.length} mallas · ${insts.length} instancias · ${new Set(insts.map((i) => i.elem)).size} elementos · ${nTris} triángulos · ${mats.length} materiales · ${(fs.statSync(dstTbv).size / 1024).toFixed(0)} KB`);
const resumenGeom = { mallas: mallas.length, instancias: insts.length, elementos: new Set(insts.map((i) => i.elem)).size, triangulos: nTris, materiales: mats.length, instanciasSinGuid: sinGuid, instanciasSinTipo: insts.filter((i) => !nombres[i.tip]).length };

// liberar memoria antes de la pasada de habitaciones
out = null; partes.length = 0; grupos.clear(); mallas.length = 0; insts.length = 0;
if (global.gc) global.gc();

// ── 5 · habitaciones ──
const { rooms, niveles } = habitacionesDe(datos, idt, { base });
const reales = rooms.filter((r) => r.Name !== '' || r.Department !== '');
const dstHab = path.join(salida, nombre + '_Geometria_Habitaciones.json');
fs.writeFileSync(dstHab, jsonHabitaciones(reales, base));
const sinNivel = reales.filter((r) => !r.Level).length;
log(`5/5 habitaciones: ${reales.length} (${sinNivel} sin nombre de nivel resuelto) → ${dstHab}`);

// ── comprobación del original ──
const intacto = sha(abs) === shaAntes && fs.statSync(abs).mtimeMs === mtimeAntes;
log(intacto ? 'Original intacto (sha256 y fecha iguales).' : 'ALERTA: el original cambió durante la corrida.');
const resumen = { original: abs, copia, salida, revit: rep.decoderCoverage.revitVersion, puntoBase_m_ejesRevit: base, geometria: resumenGeom, habitaciones: { total: reales.length, sinNivel: sinNivel, niveles: niveles.length }, segundos: +((Date.now() - t0) / 1000).toFixed(1), originalIntacto: intacto };
fs.writeFileSync(path.join(salida, nombre + '_Resumen.json'), JSON.stringify(resumen, null, 2));
log(`Listo en ${resumen.segundos} s.`);
process.exit(intacto ? 0 : 1);
