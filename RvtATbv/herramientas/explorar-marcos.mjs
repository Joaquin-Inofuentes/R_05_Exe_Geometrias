// explorar-marcos.mjs — diagnóstico: lista los objetos enmarcados de una clase (tag) con sus textos y doubles.
//   node explorar-marcos.mjs <rvt> <tag> [max]
import { abrirRvt, inflarTodo } from './lector-rvt.mjs';
const [rvt, tagS, maxS] = process.argv.slice(2);
const tag = Number(tagS), max = Number(maxS || 10);
const cfb = abrirRvt(rvt);
const pat = Buffer.from([tag & 255, tag >> 8]);
let n = 0;
for (const k of [...cfb.streams.keys()].filter((s) => s.startsWith('/partitions/'))) {
    const buf = inflarTodo(cfb.leer(k));
    for (let i = buf.indexOf(pat); i >= 12 && i >= 0; i = buf.indexOf(pat, i + 1)) {
        const o = i - 12; const ln = buf.readUInt32LE(o + 8);
        if (!(ln > 8 && ln < 3000000) || o + 16 + ln > buf.length || buf.readUInt32LE(o + 12 + ln) !== ln) continue;
        const f = buf.subarray(o, o + 16 + ln);
        const textos = [];
        for (let a = 0; a < 2; a++) { const s = f.subarray(a).toString('utf16le').match(/[\x20-\x7eÀ-ÿ]{2,}/g); if (s) for (const t of s) textos.push(t); }
        const dbl = []; for (let q = 0; q + 8 <= f.length; q++) { const d = f.readDoubleLE(q); if (Number.isFinite(d) && Math.abs(d) > 0.01 && Math.abs(d) < 5000 && Math.abs(d * 1e6 - Math.round(d * 1e6)) < 1e-3) dbl.push([q, +d.toFixed(5)]); }
        console.log(k, 'id', buf.readUInt32LE(o), 'len', ln, 'textos', JSON.stringify(textos.slice(0, 6)), 'doubles', JSON.stringify(dbl.slice(0, 8)), 'ult56:', f.length - 56, 'd@', +f.readDoubleLE(f.length - 4 - 56).toFixed(5));
        if (++n >= max) process.exit(0);
    }
}
