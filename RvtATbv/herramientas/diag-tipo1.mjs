// diag-tipo1.mjs — por qué un elemento no resuelve su tipo: muestra su objeto, ids relacionados, clase y nombre de cada uno.
//   node --expose-gc diag-tipo1.mjs <copia.rvt> <schema.json> <id> [id...]
import fs from 'node:fs';
import { abrirRvt, identidades, inflarTodo } from './lector-rvt.mjs';
import { nombreDeMarco } from './tipos.mjs';
const [rvt, schemaP, ...ids] = process.argv.slice(2);
const tags = new Map(JSON.parse(fs.readFileSync(schemaP, 'utf8')).map((c) => [c.tag, c.name]));
const cfb = abrirRvt(rvt), idt = identidades(cfb);
const quiero = new Set(ids.map(Number));
const parts = [...cfb.streams.keys()].filter((k) => k.startsWith('/partitions/')).sort((a, b) => Number(a.split('/').pop()) - Number(b.split('/').pop()));
function marcosDe(buf, wanted, cb) {
    for (let o = 0; o + 16 <= buf.length; o++) {
        const v = buf.readUInt32LE(o); if (!wanted.has(v)) continue;
        const ln = buf.readUInt32LE(o + 8);
        if (ln > 16 && ln < 3000000 && o + 16 + ln <= buf.length && buf.readUInt32LE(o + 12 + ln) === ln) cb(v, o, ln, buf);
    }
}
const rel = new Map();
for (const k of parts) {
    const buf = inflarTodo(cfb.leer(k));
    marcosDe(buf, quiero, (v, o, ln) => {
        const cl = buf.readUInt16LE(o + 12); if (tags.get(cl) === 'GElement') return;
        const s = rel.get(v) || new Set(); rel.set(v, s);
        const r = new Set(); for (let q = o + 16; q + 4 <= o + 12 + ln; q++) { const w = buf.readUInt32LE(q); if (w > 100 && w !== v && idt.filas.has(w)) r.add(w); }
        console.log(`id ${v} [${k}] clase ${cl}=${tags.get(cl)} len ${ln} relacionados ${r.size}`);
        for (const w of r) s.add(w);
    });
    if (global.gc) global.gc();
}
const todos = new Set(); for (const s of rel.values()) for (const w of s) if (w > 1000) todos.add(w);
for (const [v, s] of rel) console.log('rel de', v, [...s].filter((w) => w > 1000).join(','));
for (const k of parts) {
    const buf = inflarTodo(cfb.leer(k));
    marcosDe(buf, todos, (v, o, ln) => {
        const cl = buf.readUInt16LE(o + 12), n = nombreDeMarco(buf.subarray(o, o + 16 + ln));
        if (n || process.env.TODOS) console.log(`   rel ${v} [${k}] clase ${cl}=${tags.get(cl)} len ${ln} nombre «${n}»`);
    });
    if (global.gc) global.gc();
}
