#!/usr/bin/env node
// extraer-parametros.mjs — vuelca a JSON los parámetros por instancia de un .rvt (copia), SIN Revit.
//   node --expose-gc --max-old-space-size=4000 extraer-parametros.mjs <copia.rvt> <csv_profesional|ids.txt> <salida.json>
// Lee los ids de elemento de la columna ID_Elemento del CSV profesional. Salida: { "<idElemento>": { "<idParametro>": pies } }
import fs from 'node:fs';
import { abrirRvt } from './lector-rvt.mjs';
import { leerParametros } from './parametros.mjs';

const [,, rvt, lista, salida, auditoria] = process.argv;
if (!rvt || !lista || !salida) { console.error('Uso: node extraer-parametros.mjs <copia.rvt> <csv_profesional> <salida.json>'); process.exit(2); }
const lineas = fs.readFileSync(lista, 'utf8').replace(/^﻿/, '').split(/\r?\n/).filter(Boolean);
const c0 = lineas[0].split(';'), iId = c0.indexOf('ID_Elemento'), iCat = c0.indexOf('Codigo_Categoria_Revit');
// solo instancias de familia cargable: se excluyen muros (-2000011), suelos, techos, escaleras… (elementos de sistema)
const SISTEMA = new Set([-2000011, -2000032, -2000035, -2000038, -2001350, -2001300, -2000919, -2000920, -2000181, -2000946, -2000120, -2000126, -2000127, -2000170, -2000171]);
const ids = new Set();
for (const l of lineas.slice(1)) { const c = l.split(';'); if (!SISTEMA.has(Number(c[iCat]))) ids.add(Number(c[iId])); }
console.log(`ids a leer: ${ids.size}`);
// tipo (símbolo) de cada instancia: lo entrega la auditoría de reviter (elementManifest.type.symbolId)
const simboloDe = new Map();
if (auditoria && fs.existsSync(auditoria)) for (const e of JSON.parse(fs.readFileSync(auditoria, 'utf8')).elementManifest.elements) if (ids.has(e.elementId) && e.type && e.type.symbolId) simboloDe.set(e.elementId, e.type.symbolId);
const idsLeer = new Set([...ids, ...simboloDe.values()]);
const res = leerParametros(abrirRvt(rvt), idsLeer, { log: console.log });
const obj = {};
for (const id of ids) {
    const i = res.get(id), t = res.get(simboloDe.get(id));
    if (i || t) obj[id] = { i: i ? Object.fromEntries(i) : {}, t: t ? Object.fromEntries(t) : {} };
}
fs.writeFileSync(salida, JSON.stringify(obj));
const cuentaIds = new Map();
for (const o of Object.values(obj)) for (const k of new Set([...Object.keys(o.i), ...Object.keys(o.t)])) cuentaIds.set(k, (cuentaIds.get(k) || 0) + 1);
console.log(`escrito ${salida}: ${Object.keys(obj).length} instancias; ids de parámetro distintos: ${cuentaIds.size}`);
console.log([...cuentaIds].sort((a, b) => b[1] - a[1]).slice(0, 15).map(([k, n]) => `${k}×${n}`).join(' '));
