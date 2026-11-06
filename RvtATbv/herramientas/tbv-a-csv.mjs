#!/usr/bin/env node
// tbv-a-csv.mjs — vuelca un .tbv (v4) a dos CSV legibles en Excel (UTF-8 con BOM, separador ';', decimales con coma).
//   node tbv-a-csv.mjs <archivo.tbv> <carpeta_salida>
//   1) <nombre>_1_crudo.csv         una fila por instancia del .tbv, con los nombres tal cual vienen (ids numéricos, sin interpretar)
//   2) <nombre>_2_profesional.csv   una fila por elemento, columnas con nombre claro, medidas en mm, tipología y variante de medida
// Solo lee el .tbv; no toca ningún .rvt.
import fs from 'node:fs';
import path from 'node:path';

const [,, tbv, dir, auditoria, parametrosJson, nombresJson] = process.argv;
if (!tbv || !dir) { console.error('Uso: node tbv-a-csv.mjs <archivo.tbv> <carpeta_salida>'); process.exit(2); }
const b = fs.readFileSync(tbv);
let o = 0;
const i32 = () => { const v = b.readInt32LE(o); o += 4; return v; };
const f32 = () => { const v = b.readFloatLE(o); o += 4; return v; };
const u16 = () => { const v = b.readUInt16LE(o); o += 2; return v; };
if (i32() !== 0x56544254) throw new Error('No es un .tbv');
const ver = i32(); f32();
const nMat = i32(), nMesh = i32(), nInst = i32();
o += 24;                                            // caja global
const mats = []; for (let i = 0; i < nMat; i++) { const id = i32(); const r = b[o], g = b[o + 1], bl = b[o + 2], a = b[o + 3]; o += 4; f32(); f32(); mats.push({ id, r, g, b: bl, a }); }
const nNom = i32(); const nombres = []; for (let i = 0; i < nNom; i++) { const n = u16(); nombres.push(b.toString('utf8', o, o + n)); o += n; }
const tris = [];
for (let i = 0; i < nMesh; i++) { const vc = i32(); const i16 = b[o]; o += 1; const tc = i32(); o += vc * 12 + vc * 3 + tc * 3 * (i16 ? 2 : 4); tris.push(tc); }
const insts = [];
for (let i = 0; i < nInst; i++) {
    const mesh = i32(), elem = i32(), cat = i32(), mat = i32(), fam = i32(), tip = i32(); f32(); f32(); f32();
    const gmin = [f32(), f32(), f32()], gmax = [f32(), f32(), f32()];
    const n = u16(); const guid = b.toString('utf8', o, o + n); o += n;
    insts.push({ mesh, elem, cat, mat, fam: nombres[fam] || '', tip: nombres[tip] || '', gmin, gmax, guid, tris: tris[mesh] });
}

// Nombres de categoría (BuiltInCategory de Revit, en español). Lo que no está aquí sale como "Categoría <id>".
const CAT = {
    [-2000011]: 'Muros', [-2000032]: 'Suelos', [-2000035]: 'Cubiertas', [-2000038]: 'Techos', [-2000919]: 'Tramos de escalera', [-2000920]: 'Descansillos de escalera', [-2000181]: 'Barridos de muro', [-2000946]: 'Barandales superiores', [-2000023]: 'Puertas', [-2000014]: 'Ventanas', [-2000080]: 'Mobiliario',
    [-2001160]: 'Aparatos sanitarios', [-2001060]: 'Aparatos eléctricos', [-2000170]: 'Paneles de muro cortina', [-2000171]: 'Montantes de muro cortina', [-2000126]: 'Escaleras',
    [-2000127]: 'Barandillas', [-2001000]: 'Muebles de obra', [-2001040]: 'Equipos eléctricos', [-2001140]: 'Equipos mecánicos',
    [-2001100]: 'Sistemas de mobiliario', [-2000151]: 'Modelos genéricos', [-2000150]: 'Equipos especializados', [-2000100]: 'Masa',
    [-2001300]: 'Cimentación estructural', [-2001320]: 'Armazón estructural', [-2001330]: 'Pilares estructurales', [-2001350]: 'Suelos estructurales',
    [-2000120]: 'Escaleras', [-2000150]: 'Modelos genéricos', [-2000948]: 'Rampas', [-2000268]: 'Plantación', [-2000400]: 'Topografía',
};
const nomCat = (id) => CAT[id] || ('(sin nombre, código ' + id + ')');
const mm = (m) => Math.round(m * 1000);
const num = (v, d = 3) => (Math.round(v * 10 ** d) / 10 ** d).toString().replace('.', ',');
const q = (s) => { s = String(s ?? ''); return /[;"\n\r]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; };
const hex = (m) => m ? '#' + [m.r, m.g, m.b].map((x) => x.toString(16).padStart(2, '0')).join('').toUpperCase() : '';
const escribir = (ruta, cab, filas) => fs.writeFileSync(ruta, '﻿' + [cab.join(';'), ...filas.map((f) => f.map(q).join(';'))].join('\r\n') + '\r\n', 'utf8');

fs.mkdirSync(dir, { recursive: true });
// Parámetros que reviter sí entrega (solo integrados de Revit: marca, comentarios, altura/desfases de muros). Los de dimensión propios de cada familia NO salen.
const PAR = new Map();
if (auditoria && fs.existsSync(auditoria)) {
    const rep = JSON.parse(fs.readFileSync(auditoria, 'utf8'));
    for (const e of rep.elementManifest.elements) if (e.parameters && e.parameters.length) PAR.set(e.elementId, new Map(e.parameters.map((p) => [p.name, p.value])));
}
const par = (id, n) => { const m = PAR.get(id); return m && m.has(n) ? m.get(n) : ''; };
const parMm = (id, n) => { const v = par(id, n); return v === '' ? '' : Math.round(v * 304.8); };
// Parámetros de instancia leídos del .rvt (parametros.mjs) y nombres de BuiltInParameter (nombres_parametros.json, sacado de Revit una sola vez)
const PINST = parametrosJson && fs.existsSync(parametrosJson) ? JSON.parse(fs.readFileSync(parametrosJson, 'utf8')) : {};
const NOMP = nombresJson && fs.existsSync(nombresJson) ? JSON.parse(fs.readFileSync(nombresJson, 'utf8')) : {};
const ES_MEDIDA = /anchura|ancho|altura|profundidad|longitud|espesor|grosor|elevaci|desfase|antepecho|extremo|baranda|di[aá]metro|radio|largo|marco|hoja|cota|fondo/i;
const REG = { ancho: /^(anchura|ancho)$/i, alto: /^altura$/i, prof: /^profundidad$/i, elev: /^elevaci[oó]n desde el nivel$/i, desf: /^desfase desde el anfitri[oó]n$/i };
const medidaReal = (elem) => {
    const r = { ancho: '', alto: '', prof: '', elev: '', desf: '', lista: '' };
    const o = PINST[elem]; if (!o) return r;
    const items = [];
    const comb = new Map();
    for (const [id, ft] of Object.entries(o.t || {})) comb.set(id, [ft, ' (tipo)']);
    for (const [id, ft] of Object.entries(o.i || {})) comb.set(id, [ft, '']);      // la instancia manda sobre el tipo
    for (const [id, [ft, suf]] of comb) {
        const nombre = NOMP[id] || ('Parametro ' + id), v = Math.round(ft * 304.8);
        for (const k of Object.keys(REG)) if (REG[k].test(nombre) && r[k] === '') r[k] = v;
        if (ES_MEDIDA.test(nombre) && !/comentarios|coeficiente|anal[ií]tic|acabado|construcci/i.test(nombre)) items.push(nombre + suf + '=' + v + ' mm');
    }
    r.lista = items.sort().join(' | ');
    return r;
};
const base = path.basename(tbv).replace(/_Geometria\.tbv$/i, '').replace(/\.tbv$/i, '');

// ── CSV 1: crudo (una fila por instancia del .tbv; ejes del .tbv: X, Y = altura, Z) ──
escribir(path.join(dir, base + '_1_crudo.csv'),
    ['mesh', 'elem', 'cat', 'mat', 'fam', 'tip', 'gmin_x', 'gmin_y', 'gmin_z', 'gmax_x', 'gmax_y', 'gmax_z', 'guid', 'triangulos'],
    insts.map((s) => [s.mesh, s.elem, s.cat, s.mat, s.fam, s.tip, ...s.gmin.map((v) => num(v, 4)), ...s.gmax.map((v) => num(v, 4)), s.guid, s.tris]));

// ── CSV 2: profesional (una fila por elemento; varias instancias del mismo elemento = una por material, se unen) ──
const porElem = new Map();
for (const s of insts) {
    let e = porElem.get(s.elem);
    if (!e) { e = { ...s, gmin: [...s.gmin], gmax: [...s.gmax], tris: 0, mats: new Set(), nMallas: 0 }; porElem.set(s.elem, e); }
    for (let k = 0; k < 3; k++) { e.gmin[k] = Math.min(e.gmin[k], s.gmin[k]); e.gmax[k] = Math.max(e.gmax[k], s.gmax[k]); }
    e.tris += s.tris; e.nMallas++; e.mats.add(s.mat);
    if (!e.fam && s.fam) e.fam = s.fam; if (!e.tip && s.tip) e.tip = s.tip;
}
const matPorId = new Map(); mats.forEach((m, i) => matPorId.set(m.id, m));
const filas = [...porElem.values()].map((e) => {
    const dx = e.gmax[0] - e.gmin[0], dy = e.gmax[1] - e.gmin[1], dz = e.gmax[2] - e.gmin[2];
    const tipologia = (e.fam || '(sin familia)') + ' | ' + (e.tip || '(sin tipo)');
    const medidas = [mm(Math.max(dx, dz)), mm(Math.min(dx, dz)), mm(dy)];                  // largo mayor, largo menor en planta, alto (caja alineada a los ejes)
    const real = medidaReal(e.elem);
    const hayReal = real.ancho !== '' || real.alto !== '' || real.prof !== '';
    const clave = hayReal ? [real.ancho, real.prof, real.alto].join('x') : medidas.join('x');
    return { e, dx, dy, dz, tipologia, real, hayReal, varianteKey: tipologia + ' | ' + clave, medidas };
});
const cuentaTip = new Map(), cuentaVar = new Map();
for (const f of filas) { cuentaTip.set(f.tipologia, (cuentaTip.get(f.tipologia) || 0) + 1); cuentaVar.set(f.varianteKey, (cuentaVar.get(f.varianteKey) || 0) + 1); }
const idVariante = new Map(); let nv = 0;
const idTip = new Map(); let nt = 0;
filas.sort((a, b) => nomCat(a.e.cat).localeCompare(nomCat(b.e.cat), 'es') || a.tipologia.localeCompare(b.tipologia, 'es') || a.e.elem - b.e.elem);
const cab = ['ID_Elemento', 'GUID_Unico', 'Categoria', 'Codigo_Categoria_Revit', 'Familia', 'Tipo', 'Tipologia', 'ID_Tipologia', 'ID_Variante_Medida',
    'Cantidad_Misma_Tipologia', 'Cantidad_Misma_Variante', 'Caja_Mayor_Planta_mm', 'Caja_Menor_Planta_mm', 'Caja_Alto_mm', 'Cota_Inferior_mm', 'Cota_Superior_mm',
    'Centro_Este_m', 'Centro_Norte_m', 'Volumen_Caja_m3', 'Cantidad_Triangulos', 'Cantidad_Materiales', 'Color_Principal_Hex', 'Opacidad_Pct', 'Ancho_Real_mm', 'Profundidad_Real_mm', 'Alto_Real_mm', 'Elevacion_Desde_Nivel_mm', 'Desfase_Anfitrion_mm', 'Origen_Medidas', 'Parametros_Instancia', 'Marca', 'Comentarios', 'Altura_Sin_Conectar_mm', 'Desfase_Base_mm', 'Desfase_Superior_mm', 'Estado_Datos', 'Observacion'];
escribir(path.join(dir, base + '_2_profesional.csv'), cab, filas.map((f) => {
    const e = f.e;
    if (!idTip.has(f.tipologia)) idTip.set(f.tipologia, 'T' + String(++nt).padStart(3, '0'));
    if (!idVariante.has(f.varianteKey)) idVariante.set(f.varianteKey, 'V' + String(++nv).padStart(4, '0'));
    const m = mats[[...e.mats][0]];
    const estado = !e.fam && !e.tip ? 'Sin familia ni tipo' : !e.fam ? 'Sin familia' : !e.tip ? 'Sin tipo' : !e.guid ? 'Sin GUID' : 'Completo';
    const obs = e.cat === -2000011 || e.cat === -2000032 ? 'Elemento de sistema: la forma sale del propio elemento, no de una familia' :
        f.dx > 20 || f.dz > 20 ? 'Caja muy grande: revisar' : '';
    return [e.elem, e.guid, nomCat(e.cat), e.cat, e.fam, e.tip, f.tipologia, idTip.get(f.tipologia), idVariante.get(f.varianteKey),
        cuentaTip.get(f.tipologia), cuentaVar.get(f.varianteKey), f.medidas[0], f.medidas[1], f.medidas[2], mm(e.gmin[1]), mm(e.gmax[1]),
        num((e.gmin[0] + e.gmax[0]) / 2), num(-(e.gmin[2] + e.gmax[2]) / 2), num(f.dx * f.dy * f.dz), e.tris, e.mats.size, hex(m), m ? Math.round(m.a / 2.55) : '', f.real.ancho, f.real.prof, f.real.alto, f.real.elev, f.real.desf, f.hayReal ? 'Parametros del .rvt' : 'Caja envolvente de la malla', f.real.lista, par(e.elem, 'Mark'), par(e.elem, 'Comments'), parMm(e.elem, 'Unconnected Height'), parMm(e.elem, 'Base Offset'), parMm(e.elem, 'Top Offset'), estado, obs];
}));

// ── CSV 3: catálogo de variantes (una fila por variante de medida: lo que habría que exportar una sola vez con Revit si faltara la forma) ──
const vars = new Map();
for (const f of filas) {
    const id = idVariante.get(f.varianteKey);
    if (!vars.has(id)) vars.set(id, { f, n: 0 });
    vars.get(id).n++;
}
escribir(path.join(dir, base + '_3_catalogo_variantes.csv'),
    ['ID_Variante_Medida', 'ID_Tipologia', 'Categoria', 'Familia', 'Tipo', 'Ancho_mm', 'Profundidad_mm', 'Alto_mm', 'Origen_Medidas', 'Cantidad_Instancias', 'Elemento_Representativo', 'GUID_Representativo'],
    [...vars.entries()].map(([id, v]) => {
        const f = v.f, r = f.real;
        const medidas = f.hayReal ? [r.ancho, r.prof, r.alto] : f.medidas;
        return [id, idTip.get(f.tipologia), nomCat(f.e.cat), f.e.fam, f.e.tip, medidas[0], medidas[1], medidas[2], f.hayReal ? 'Parametros del .rvt' : 'Caja envolvente de la malla', v.n, f.e.elem, f.e.guid];
    }));

// resumen por consola
const porCat = new Map();
for (const f of filas) { const c = nomCat(f.e.cat); const r = porCat.get(c) || { n: 0, t: new Set(), v: new Set() }; r.n++; r.t.add(f.tipologia); r.v.add(f.varianteKey); porCat.set(c, r); }
console.log(`.tbv v${ver}: ${nInst} instancias · ${filas.length} elementos · ${nt} tipologías · ${nv} variantes de medida`);
for (const [c, r] of [...porCat].sort((a, b) => b[1].n - a[1].n)) console.log(`  ${c}: ${r.n} elementos · ${r.t.size} tipologías · ${r.v.size} variantes`);
console.log('Sin familia o tipo: ' + filas.filter((f) => !f.e.fam || !f.e.tip).length);
