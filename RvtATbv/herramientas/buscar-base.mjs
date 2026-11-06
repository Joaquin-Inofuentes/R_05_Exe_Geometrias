// buscar-base.mjs — diagnóstico: busca doubles consecutivos (x,y,z en pies) que coincidan con un punto dado, en todas las particiones.
//   node buscar-base.mjs <rvt> <x_m> <y_m> <z_m>      (metros, ejes de Revit)
import { abrirRvt, inflarTodo } from './lector-rvt.mjs';
const [rvt, xm, ym, zm] = process.argv.slice(2);
const FT = 3.280839895, t = [Number(xm) * FT, Number(ym) * FT, Number(zm) * FT], tol = 0.005;
const cfb = abrirRvt(rvt);
for (const k of [...cfb.streams.keys()].filter((s) => s.startsWith('/partitions/'))) {
    const buf = inflarTodo(cfb.leer(k));
    for (let o = 0; o + 24 <= buf.length; o++) {
        const a = buf.readDoubleLE(o);
        if (Math.abs(a - t[0]) > tol) continue;
        const b = buf.readDoubleLE(o + 8), c = buf.readDoubleLE(o + 16);
        if (Math.abs(b - t[1]) < tol && Math.abs(c - t[2]) < tol) console.log('x,y,z consecutivos en', k, 'offset', o, [a, b, c]);
        else if (Math.abs(b - t[1]) < tol) console.log('x,y en', k, 'offset', o, [a, b, c]);
    }
}
console.log('fin');
