import { abrirRvt, inflarTodo } from './lector-rvt.mjs';
const [,, rvt, ...ids] = process.argv; const want = new Set(ids.map(Number));
const cfb = abrirRvt(rvt);
const parts = [...cfb.streams.keys()].filter(k=>k.startsWith('/partitions/')).sort((a,b)=>Number(a.split('/').pop())-Number(b.split('/').pop()));
for (const k of parts){ const buf=inflarTodo(cfb.leer(k)); for(let o=0;o+16<=buf.length;o++){ const v=buf.readUInt32LE(o); if(!want.has(v))continue; const ln=buf.readUInt32LE(o+8); if(!(ln>=4&&ln<3e6)||o+16+ln>buf.length||buf.readUInt32LE(o+12+ln)!==ln)continue; console.log(k.split('/').pop(),'id',v,'ln',ln,'clase',buf.readUInt16LE(o+12)); } if(global.gc)global.gc(); }
// y quién referencia al id como u32 dentro de otros marcos (los que lo apuntan)
