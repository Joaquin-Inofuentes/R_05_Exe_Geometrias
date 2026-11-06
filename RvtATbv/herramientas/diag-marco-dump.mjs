import fs from 'node:fs';
import { abrirRvt, inflarTodo } from './lector-rvt.mjs';
const [,, rvt, ...ids] = process.argv;
const want = new Set(ids.map(Number));
const cfb = abrirRvt(rvt);
const parts = [...cfb.streams.keys()].filter(k=>k.startsWith('/partitions/')).sort((a,b)=>Number(a.split('/').pop())-Number(b.split('/').pop()));
const out = new Map();
for (const k of parts){ const buf=inflarTodo(cfb.leer(k)); for(let o=0;o+16<=buf.length;o++){ const v=buf.readUInt32LE(o); if(!want.has(v))continue; const ln=buf.readUInt32LE(o+8); if(buf.readUInt16LE(o+12)===1911)continue; if(!(ln>16&&ln<3e6)||o+16+ln>buf.length||buf.readUInt32LE(o+12+ln)!==ln)continue; out.set(v,Buffer.from(buf.subarray(o,o+16+ln))); } if(global.gc)global.gc(); }
for (const [id,b] of out){ console.log('== id',id,'len',b.length,'clase',b.readUInt16LE(12)); let s=''; for(let o=0;o+8<=b.length;o++){const d=b.readDoubleLE(o); if(Number.isFinite(d)&&Math.abs(d)>1e-3&&Math.abs(d)<1e4) s+=` d@${o}=${+d.toFixed(4)}(${+(d*304.8).toFixed(0)}mm)`;} console.log(s); let f=''; for(let o=0;o+4<=b.length;o+=1){const x=b.readFloatLE(o); if(Number.isFinite(x)&&Math.abs(x)>1e-2&&Math.abs(x)<1e3&&Math.abs(x*304.8-Math.round(x*304.8))<0.5&&Math.round(x*304.8)>=50) f+=` f@${o}=${+x.toFixed(4)}`;} console.log('floats:',f); console.log(b.toString('hex').match(/.{1,64}/g).join('\n')); }
