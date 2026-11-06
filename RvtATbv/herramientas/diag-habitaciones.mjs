// diag-habitaciones.mjs — diagnóstico: busca marcos por id (cualquier clase) y compara dos habitaciones campo a campo.
//   node diag-habitaciones.mjs <rvt> <id> [id ...]
import fs from 'node:fs';
import { abrirRvt, inflarTodo } from './lector-rvt.mjs';
const [rvt, ...ids] = process.argv.slice(2);
const want = new Set(ids.map(Number));
const cfb = abrirRvt(rvt);
const out = [];
for (const k of [...cfb.streams.keys()].filter((s) => s.startsWith('/partitions/')).sort((a, b) => Number(a.split('/').pop()) - Number(b.split('/').pop()))) {
    const buf = inflarTodo(cfb.leer(k));
    for (const id of want) {
        const pat = Buffer.alloc(4); pat.writeUInt32LE(id);
        for (let i = buf.indexOf(pat); i >= 0; i = buf.indexOf(pat, i + 1)) {
            if (i + 16 > buf.length) continue;
            const ln = buf.readUInt32LE(i + 8);
            if (ln > 16 && ln < 3000000 && i + 16 + ln <= buf.length && buf.readUInt32LE(i + 12 + ln) === ln)
                out.push({ k, id, clase: buf.readUInt16LE(i + 12), len: ln, f: Buffer.from(buf.subarray(i, i + 16 + ln)) });
        }
    }
    if (global.gc) global.gc();
}
for (const m of out) {
    const dbl = [];
    for (let q = 0; q + 8 <= m.f.length && dbl.length < 14; q++) { const d = m.f.readDoubleLE(q); if (Number.isFinite(d) && Math.abs(d) > 1e-3 && Math.abs(d) < 1e4 && Math.abs(d * 1e4 - Math.round(d * 1e4)) < 1e-6 * 1e4) dbl.push(`${q}:${+d.toFixed(4)}`); }
    console.log(m.k, 'id', m.id, 'clase', m.clase, 'len', m.len, 'u32@56..72', [56, 60, 64, 68].map((o) => m.f.readUInt32LE(o)).join(','), 'doubles', dbl.join(' '));
}
