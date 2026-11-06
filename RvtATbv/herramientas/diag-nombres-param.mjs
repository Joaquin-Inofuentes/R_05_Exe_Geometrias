import { abrirRvt, inflarTodo } from './lector-rvt.mjs';
const [,, rvt, ...txt] = process.argv;
const cfb = abrirRvt(rvt);
const claves = txt.map(t => ({ t, b: Buffer.from(t, 'utf16le') }));
const ids = [0xFFF096AD, 0xFFF0B8AB, 0xFFF0B8AC];
const idb = ids.map(i => { const x = Buffer.alloc(4); x.writeUInt32LE(i); return x; });
const flujos = [...cfb.streams.keys()];
let hallados = 0;
for (const k of flujos) {
  let buf; try { buf = inflarTodo(cfb.leer(k)); } catch { continue; }
  if (!buf || buf.length < 100) continue;
  for (const c of claves) { let from = 0, n = 0; while (n < 3) { const i = buf.indexOf(c.b, from); if (i < 0) break; n++; from = i + 2; hallados++; console.log(`${k} "${c.t}" @${i}`, buf.subarray(Math.max(0, i - 40), i).toString('hex'), '|', buf.subarray(i + c.b.length, i + c.b.length + 16).toString('hex')); } }
  idb.forEach((b, j) => { let from = 0, n = 0; while (n < 2) { const i = buf.indexOf(b, from); if (i < 0) break; n++; from = i + 4; if (!k.startsWith('/partitions/')) console.log(`${k} id 0x${ids[j].toString(16)} @${i}`); } });
  if (global.gc) global.gc();
}
console.log('hallados texto:', hallados);
