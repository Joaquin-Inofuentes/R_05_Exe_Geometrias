// parametros.mjs — lee de un .rvt (solo lectura) los parámetros por instancia de familia, SIN Revit.
// El objeto de la instancia (marco con clase ≠ GElement) guarda cada parámetro en una ranura de 30 bytes:
//   [8 B][double (pies)][ff ff ff ff][u32 id = 0xFFF0xxxx][6 B]      id = BuiltInParameter de Revit (negativo), p.ej. -1001301
// Devuelve Map<idElemento, Map<idParametro(int32 negativo), valorEnPies>>. La última copia del marco manda.
import { inflarTodo } from './lector-rvt.mjs';

const GELEMENT_DEFECTO = 1911;

/** cfb: Cfb abierto; ids: Set de ids de elemento a leer; opciones: { claseGElement, log } */
export function leerParametros(cfb, ids, { claseGElement = GELEMENT_DEFECTO, log = () => {} } = {}) {
    const particiones = [...cfb.streams.keys()].filter((k) => k.startsWith('/partitions/')).sort((a, b) => Number(a.split('/').pop()) - Number(b.split('/').pop()));
    const mapa = new Uint8Array(1 << 23);
    for (const id of ids) if (id >= 0 && id < 0x4000000) mapa[id >> 3] |= 1 << (id & 7);
    const marcos = new Map();
    for (const k of particiones) {
        const buf = inflarTodo(cfb.leer(k)), n = buf.length;
        for (let o = 0; o + 16 <= n; o++) {
            const v = buf.readUInt32LE(o);
            if (v >= 0x4000000 || !(mapa[v >> 3] & (1 << (v & 7)))) continue;
            const ln = buf.readUInt32LE(o + 8);
            if (!(ln > 16 && ln < 3000000) || o + 16 + ln > n || buf.readUInt32LE(o + 12 + ln) !== ln) continue;
            const cl = buf.readUInt16LE(o + 12);
            if (cl === claseGElement || ln < 100) continue;
            const previo = marcos.get(v);
            if (!previo || ln >= previo.length - 200) marcos.set(v, Buffer.from(buf.subarray(o, o + 16 + ln)));
        }
        if (global.gc) global.gc();
    }
    const res = new Map();
    for (const [id, b] of marcos) {
        const ranuras = new Map();
        for (let d = 0; d + 16 <= b.length; d++) {
            if (b[d + 8] !== 0xff || b[d + 9] !== 0xff || b[d + 10] !== 0xff || b[d + 11] !== 0xff || b[d + 14] !== 0xf0 || b[d + 15] !== 0xff) continue;
            const v = b.readDoubleLE(d);
            if (!Number.isFinite(v)) continue;
            ranuras.set(b.readInt32LE(d + 12), v);
        }
        if (ranuras.size) res.set(id, ranuras);
    }
    log(`parámetros: ${res.size}/${ids.size} instancias con ranuras`);
    return res;
}
