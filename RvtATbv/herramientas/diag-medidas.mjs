#!/usr/bin/env node
// diag-medidas.mjs — ¿dónde guarda el .rvt las medidas (Ancho/Alto/Profundidad) de una instancia de familia?
// Idea: la caja envolvente de la malla (CSV profesional) da la medida real en mm; se buscan dobles (pies) iguales a esa medida dentro del
// objeto de cada elemento y se vota por desplazamiento fijo. Solo lectura de la copia.
//   node --max-old-space-size=4000 diag-medidas.mjs <copia.rvt> <csv_profesional> "<fragmento de Familia>" [tolerancia_mm=3]
import fs from 'node:fs';
import { abrirRvt, inflarTodo } from './lector-rvt.mjs';

const [,, rvt, csv, frag, tolArg] = process.argv;
if (!rvt || !csv || !frag) { console.error('Uso: node diag-medidas.mjs <copia.rvt> <csv_profesional> "<familia>" [tol_mm]'); process.exit(2); }
const tol = Number(tolArg || 3) / 304.8;
const FT = 304.8;

const lineas = fs.readFileSync(csv, 'utf8').replace(/^﻿/, '').split(/\r?\n/).filter(Boolean);
const cab = lineas[0].split(';'); const ix = (n) => cab.indexOf(n);
const objetivo = new Map();
for (const l of lineas.slice(1)) {
    const c = l.split(';');
    if (!(c[ix('Familia')] || '').toLowerCase().includes(frag.toLowerCase())) continue;
    objetivo.set(Number(c[ix('ID_Elemento')]), { mayor: +c[ix('Caja_Mayor_Planta_mm')], menor: +c[ix('Caja_Menor_Planta_mm')], alto: +c[ix('Caja_Alto_mm')], tipo: c[ix('Tipo')] });
}
console.log(`objetivo: ${objetivo.size} elementos con familia ~ "${frag}"`);
if (!objetivo.size) process.exit(1);

const cfb = abrirRvt(rvt);
const particiones = [...cfb.streams.keys()].filter((k) => k.startsWith('/partitions/')).sort((a, b) => Number(a.split('/').pop()) - Number(b.split('/').pop()));
const mapa = new Uint8Array(1 << 23);
for (const id of objetivo.keys()) mapa[id >> 3] |= 1 << (id & 7);
const marcos = new Map();            // id → { buf, cl } (la última copia manda)
for (const k of particiones) {
    const buf = inflarTodo(cfb.leer(k)), n = buf.length;
    for (let o = 0; o + 16 <= n; o++) {
        const v = buf.readUInt32LE(o);
        if (v >= 0x4000000 || !(mapa[v >> 3] & (1 << (v & 7)))) continue;
        const ln = buf.readUInt32LE(o + 8);
        if (!(ln > 16 && ln < 3000000) || o + 16 + ln > n || buf.readUInt32LE(o + 12 + ln) !== ln) continue;
        const cl = buf.readUInt16LE(o + 12);
        const previo = marcos.get(v);
        if (cl !== 1911 && (!previo || ln >= 100)) marcos.set(v, { b: Buffer.from(buf.subarray(o, o + 16 + ln)), cl });     // marco completo [id][disc][largo][clase]…
    }
    if (global.gc) global.gc();
}
console.log(`marcos encontrados: ${marcos.size}/${objetivo.size}`);

// votos: para cada tipo de medida y desplazamiento (relativo al inicio del marco)
const votos = { mayor: new Map(), menor: new Map(), alto: new Map() };
const largos = new Map();
for (const [id, m] of marcos) {
    const t = objetivo.get(id), b = m.b; largos.set(b.length, (largos.get(b.length) || 0) + 1);
    for (let o = 0; o + 8 <= b.length; o++) {
        const d = b.readDoubleLE(o); if (!Number.isFinite(d) || d <= 0 || d > 200) continue;
        for (const [nom, mm] of [['mayor', t.mayor], ['menor', t.menor], ['alto', t.alto]]) if (mm > 0 && Math.abs(d * FT - mm) <= tol * FT) { const mp = votos[nom]; mp.set(o, (mp.get(o) || 0) + 1); }
    }
}
console.log('largos de marco (top):', [...largos].sort((a, b) => b[1] - a[1]).slice(0, 5).map(([l, c]) => `${l}B×${c}`).join(' '));
for (const nom of ['mayor', 'menor', 'alto']) {
    const top = [...votos[nom]].sort((a, b) => b[1] - a[1]).slice(0, 5);
    console.log(`${nom}: ` + top.map(([o, c]) => `off ${o} → ${c}/${marcos.size} (${Math.round(100 * c / marcos.size)} %)`).join(' · '));
}
