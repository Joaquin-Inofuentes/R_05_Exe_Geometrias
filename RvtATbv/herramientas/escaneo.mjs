// escaneo.mjs — UN solo recorrido de las particiones (se inflan una vez) que recoge todo lo que leen tipos.mjs y habitaciones.mjs:
//   · objetos de los elementos pedidos (ids relacionados + desplazamiento dentro del marco)           → tipos.mjs / resolverTipos
//   · nombre de todos los objetos de TIPO (última copia)                                              → tipos.mjs
//   · habitaciones (RoomElem), niveles (Level) y Punto Base del Proyecto (BasePoint)                  → habitaciones.mjs
// Marco = [u32 id][u32 disc][u32 largo][u16 clase] … [u32 eco del largo]. Las particiones se recorren por número ascendente: la última copia de un objeto es la vigente.
import { inflarTodo } from './lector-rvt.mjs';
import { cadena, nombreDeMarco, resolverTipos } from './tipos.mjs';
import { nombreNivel } from './habitaciones.mjs';

const FT = 0.3048;
const BIT_TIPO = 1, BIT_ROOM = 2, BIT_NIVEL = 4, BIT_BASE = 8;

/** opciones: { quiero:Set|Array ids de elementos, idsValidos:Set, claseGElement, clasesTipo:Set, clasesGenericas:Set, idsMateriales:Set, claseRoom, claseNivel, claseBase, log } */
export function escanear(cfb, { quiero = [], idsValidos, claseGElement = 1911, clasesTipo = new Set(), clasesGenericas = null, idsMateriales = null, claseRoom = 3354, claseNivel = 2199, claseBase = 467, log = () => {} } = {}) {
    const particiones = [...cfb.streams.keys()].filter((k) => k.startsWith('/partitions/')).sort((a, b) => Number(a.split('/').pop()) - Number(b.split('/').pop()));
    const q = quiero instanceof Set ? quiero : new Set(quiero);
    const mapa = new Uint8Array(1 << 23);                           // bitmap de ids pedidos (< 2^26): descarta casi todos los desplazamientos sin consultar el Set
    for (const id of q) if (id >= 0 && id < 0x4000000) mapa[id >> 3] |= 1 << (id & 7);
    const clase = new Uint8Array(65536);
    for (const c of clasesTipo) clase[c] |= BIT_TIPO;
    clase[claseRoom] |= BIT_ROOM; clase[claseNivel] |= BIT_NIVEL; clase[claseBase] |= BIT_BASE;

    const hayValidos = idsValidos && idsValidos.size > 1000;
    const cand = new Map(), clases = new Map(), ranuras = new Map();
    const nombres = new Map(), claseTipoDe = new Map(), matsDeTipo = new Map();
    const niveles = new Map(), rooms = new Map(), basePoints = [];

    const agregar = (id, b, ini, fin, marco = -1, cl = 0) => {
        let s = cand.get(id); if (!s) { s = new Set(); cand.set(id, s); }
        let rs = null;
        if (marco >= 0) { rs = ranuras.get(id); if (!rs) { rs = []; ranuras.set(id, rs); } }
        for (let o = ini, tope = Math.min(fin, b.length); o + 4 <= tope; o++) {
            const v = b.readUInt32LE(o);
            if (v > 100 && v !== id && idsValidos.has(v)) { s.add(v); if (rs && o - marco < 400) rs.push([v, o - marco, cl]); }
        }
    };

    for (const k of particiones) {
        const buf = inflarTodo(cfb.leer(k));
        const n = buf.length;
        const hechos = new Set();
        for (const [id, off, ln] of cadena(buf)) if (q.has(id) && !hechos.has(id)) { hechos.add(id); agregar(id, buf, off, off + ln); }
        for (let o = 0; o + 16 <= n; o++) {
            const marcaClase = clase[buf[o + 12] | (buf[o + 13] << 8)];
            const v = buf.readUInt32LE(o);
            const pedido = v < 0x4000000 && (mapa[v >> 3] & (1 << (v & 7))) !== 0;
            if (!marcaClase && !pedido) continue;
            const ln = buf.readUInt32LE(o + 8);
            if (!(ln > 8 && ln < 3000000) || o + 16 + ln > n || buf.readUInt32LE(o + 12 + ln) !== ln) continue;
            const cl = buf.readUInt16LE(o + 12);
            if (pedido && ln > 16 && cl !== claseGElement && q.has(v)) {
                if (!clases.has(v)) clases.set(v, cl);
                agregar(v, buf, o + 16, o + 12 + ln, o, cl);
            }
            if (!marcaClase) continue;
            if (hayValidos && !idsValidos.has(v)) continue;      // coincidencia casual de la clase dentro de otros datos: un objeto real tiene un id de la tabla de elementos
            if ((marcaClase & BIT_TIPO) && ln < 20000 && v >= 1000) {      // los objetos de tipo miden unos cientos de bytes; largos de 65536/655360 son números casuales
                const nom = nombreDeMarco(buf.subarray(o, o + 16 + ln), !!clasesGenericas && clasesGenericas.has(cl));
                if (nom) {
                    nombres.set(v, nom); claseTipoDe.set(v, cl);
                    if (idsMateriales) { const ms = []; for (let p = o + 16; p + 4 <= o + 12 + ln; p++) { const w = buf.readUInt32LE(p); if (idsMateriales.has(w) && !ms.includes(w)) ms.push(w); } matsDeTipo.set(v, ms); }
                }
            }
            if ((marcaClase & BIT_NIVEL) && ln > 16) {
                const f = buf.subarray(o, o + 16 + ln), nombre = nombreNivel(f);
                if (nombre !== null) niveles.set(v, { id: v, nombre, elevFt: f.readDoubleLE(f.length - 4 - 56) });
            }
            if ((marcaClase & BIT_ROOM) && ln + 16 >= 1500) rooms.set(v, Buffer.from(buf.subarray(o, o + 16 + ln)));
            if ((marcaClase & BIT_BASE) && ln >= 280 && ln <= 460 && v >= 100 && buf[o + 115] === 0) {
                const f = buf.subarray(o, o + 16 + ln);
                const val = [f.readDoubleLE(256), f.readDoubleLE(264), f.readDoubleLE(272)];
                if (val.every(Number.isFinite)) basePoints.push(val.map((x) => x * FT));
            }
        }
        if (global.gc) global.gc();
    }
    log(`escaneo: ${particiones.length} particiones · ${cand.size} elementos con objeto · ${nombres.size} tipos con nombre · ${niveles.size} niveles · ${rooms.size} habitaciones`);
    return { cand, clases, ranuras, nombres, claseTipoDe, matsDeTipo, niveles, rooms, basePoints };
}

/** Compatibilidad (pruebas sueltas): recorrido + resolución de tipos en una llamada. */
export function tiposSistema(cfb, idsValidos, necesitan, opciones = {}) {
    return resolverTipos(escanear(cfb, { ...opciones, quiero: necesitan, idsValidos }), { log: opciones.log });
}
