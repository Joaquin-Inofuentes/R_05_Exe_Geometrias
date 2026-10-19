// marcos-clase.mjs — diagnóstico: lista los objetos enmarcados de una clase (tag) con sus doubles finitos.
//   node marcos-clase.mjs <rvt> <tag> [max]
import { abrirRvt, inflarTodo } from './lector-rvt.mjs';
const [rvt, tagS, maxS] = process.argv.slice(2);
const tag = Number(tagS), max = Number(maxS || 20);
const cfb = abrirRvt(rvt);
const pat = Buffer.from([tag & 255, tag >> 8]);
let n = 0;
for (const k of [...cfb.streams.keys()].filter((s) => s.startsWith('/partitions/'))) {
    const buf = inflarTodo(cfb.leer(k));
    for (let i = buf.indexOf(pat); i >= 12; i = buf.indexOf(pat, i + 1)) {
        const o = i - 12, ln = buf.readUInt32LE(o + 8);
        if (!(ln > 16 && ln < 3000000) || o + 16 + ln > buf.length || buf.readUInt32LE(o + 12 + ln) !== ln) continue;
        const f = buf.subarray(o, o + 16 + ln), d = [];
        for (let q = 0; q + 8 <= f.length && d.length < 16; q++) { const v = f.readDoubleLE(q); if (Number.isFinite(v) && Math.abs(v) > 1e-9 && Math.abs(v) < 1e5 && Math.abs(v * 1e5 - Math.round(v * 1e5)) < 1e-2) d.push(`${q}:${+v.toFixed(5)}`); }
        console.log(k, 'id', buf.readUInt32LE(o), 'len', ln, d.join(' '));
        if (++n >= max) process.exit(0);
    }
    if (global.gc) global.gc();
}
