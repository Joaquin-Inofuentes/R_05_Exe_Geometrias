// diag-niveles.mjs — ¿dónde están los objetos Level que usan las habitaciones? (mapea id de nivel → nombre de producción y busca el objeto de cualquier forma)
//   node --expose-gc --max-old-space-size=3200 diag-niveles.mjs <copia.rvt> <referencia_habitaciones.json>
import fs from 'node:fs';
import { abrirRvt, inflarTodo } from './lector-rvt.mjs';
const [rvt, refJson] = process.argv.slice(2);
const ref = JSON.parse(fs.readFileSync(refJson, 'utf8'));
const nomPorRoom = new Map(ref.Rooms.map((r) => [r.ElementId, r.Level]));
const cfb = abrirRvt(rvt);
const parts = [...cfb.streams.keys()].filter((s) => s.startsWith('/partitions/')).sort((a, b) => Number(a.split('/').pop()) - Number(b.split('/').pop()));

function cadena(buf) {
    if (buf.length < 8) return [];
    const out = []; let ln = buf.readUInt32LE(4); out.push([buf.readUInt32LE(0), 8, ln]);
    let o = 8 + ln, prev = ln;
    while (o + 12 <= buf.length) {
        const pl = buf.readUInt32LE(o), id = buf.readUInt32LE(o + 4); ln = buf.readUInt32LE(o + 8);
        if (pl !== prev || o + 12 + ln > buf.length) break;
        out.push([id, o + 12, ln]); o += 12 + ln; prev = ln;
    }
    return out;
}

// 1) habitaciones → id de nivel
const nivelDeRoom = new Map();      // levelId → Set(nombres de producción)
const usados = new Map();
for (const k of parts) {
    const buf = inflarTodo(cfb.leer(k));
    const pat = Buffer.from([0x1a, 0x0d]);   // 3354
    for (let i = buf.indexOf(pat); i >= 12; i = buf.indexOf(pat, i + 1)) {
        const o = i - 12, ln = buf.readUInt32LE(o + 8);
        if (!(ln > 1500 && ln < 3000000) || o + 16 + ln > buf.length || buf.readUInt32LE(o + 12 + ln) !== ln) continue;
        const id = buf.readUInt32LE(o), lv = buf.readUInt32LE(o + 60);
        if (nomPorRoom.has(id)) { if (!usados.has(lv)) usados.set(lv, new Set()); usados.get(lv).add(nomPorRoom.get(id)); }
    }
    if (global.gc) global.gc();
}
console.log('ids de nivel usados por las habitaciones:');
for (const [lv, ns] of usados) console.log(' ', lv, [...ns].join(' | '));

// 2) dónde aparece cada id (marco de cualquier clase / registro de cadena)
const buscar = new Set(usados.keys());
for (const k of parts) {
    const buf = inflarTodo(cfb.leer(k));
    for (const [id, off, ln] of cadena(buf)) if (buscar.has(id)) console.log(`  [cadena] ${k} id ${id} off ${off} len ${ln} -> primeros bytes ${buf.subarray(off, off + 24).toString('hex')}`);
    for (const id of buscar) {
        const pat = Buffer.alloc(4); pat.writeUInt32LE(id);
        for (let i = buf.indexOf(pat); i >= 0; i = buf.indexOf(pat, i + 1)) {
            if (i + 16 > buf.length) continue;
            const ln = buf.readUInt32LE(i + 8);
            if (ln > 8 && ln < 3000000 && i + 16 + ln <= buf.length && buf.readUInt32LE(i + 12 + ln) === ln)
                console.log(`  [marco] ${k} id ${id} clase ${buf.readUInt16LE(i + 12)} len ${ln} off ${i}`);
        }
    }
    if (global.gc) global.gc();
}
