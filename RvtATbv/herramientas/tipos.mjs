// tipos.mjs — nombre del TIPO de elementos de sistema (suelos, losas, barandillas, escaleras…) y clase de cada elemento, leyendo el .rvt.
//
// Regla (verificada: 148/148 suelos de SO_DU; RI_ARQ en la validación del README):
//   · el objeto del elemento (registro de la cadena de la partición o marco [id][disc][largo][clase]…[eco]) guarda los ids de sus objetos relacionados;
//   · el que es su TIPO es un objeto enmarcado que contiene la entrada de nombre: …ff ff ff ff 26 01 [ff ff ff ff e1 01] [u32 n][UTF-16 nombre]
//     (o, en la variante vieja, 47 96 f0 ff [u32 n][clave] … e1 01 [u32 n][nombre]); el nombre del tipo es el último texto de esa entrada.
import { inflarTodo } from './lector-rvt.mjs';

const CLASE_GELEMENT_DEFECTO = 1911;                    // geometría de un elemento: se ignora como "objeto del elemento"
const M_NOMBRE = Buffer.from([0xff, 0xff, 0xff, 0xff, 0x26, 0x01]);
const M_E101 = Buffer.from([0xff, 0xff, 0xff, 0xff, 0xe1, 0x01]);
const M_VIEJO = Buffer.from([0x47, 0x96, 0xf0, 0xff]);
const M_PARAM_TIPO = Buffer.from([0x16, 0x9c, 0xf0, 0xff, 0xff, 0xff, 0xff, 0xff]);      // parámetro 0xFFF09C16 (nombre de tipo) = [id][ffffffff][u32 n][UTF-16] (barandillas)

/** Tags de clase que son TIPOS (…Attributes, …Attr, …Type, …Symbol) a partir de la tabla de clases del esquema. */
/** Tipos cuyo nombre no lleva marca conocida y se lee con el recorrido genérico (tramo y descansillo de escalera). */
export function clasesGenericasDe(taggedClasses) {
    return new Set(taggedClasses.filter((c) => /^Stairs(Run|Landing)Type$/.test(c.name)).map((c) => c.tag));
}

export function clasesDeTipo(taggedClasses) {
    return new Set(taggedClasses.filter((c) => /(Attributes|Attr|Type|Symbol)$/.test(c.name) && !/^(ElementType|FamilySymbolCache)$/.test(c.name)).map((c) => c.tag));
}

export function cadena(buf) {   // primera sección de una partición: [id][largo][cuerpo] ([largoPrevio][id][largo][cuerpo])*
    if (buf.length < 8) return [];
    const out = []; let ln = buf.readUInt32LE(4); out.push([buf.readUInt32LE(0), 8, ln]);
    let o = 8 + ln, prev = ln;
    while (o + 12 <= buf.length) {
        const pl = buf.readUInt32LE(o), id = buf.readUInt32LE(o + 4); ln = buf.readUInt32LE(o + 8);
        if (pl !== prev || o + 12 + ln > buf.length) break;
        out.push([id, o + 12, ln]); o += 12 + ln; prev = ln;
    }
    return out;
}

function textoEn(b, p) {
    if (p + 4 > b.length) return null;
    const n = b.readUInt32LE(p);
    if (!(n > 0 && n < 200) || p + 4 + 2 * n > b.length) return null;
    const s = b.toString('utf16le', p + 4, p + 4 + 2 * n);
    return /[\u0000-\u001f]/.test(s) ? null : s;
}

export function nombreDeMarco(b, generico = false) {
    let i = b.indexOf(M_NOMBRE);
    if (i >= 0) {
        let p = i + M_NOMBRE.length;
        if (b.subarray(p, p + 6).equals(M_E101)) p += 6;
        const t = textoEn(b, p);
        if (t) return t;
    }
    i = b.indexOf(M_PARAM_TIPO);
    if (i >= 0) { const t = textoEn(b, i + M_PARAM_TIPO.length); if (t) return t; }
    i = b.indexOf(M_VIEJO);
    if (i >= 0) {
        const t1 = textoEn(b, i + 4);
        if (t1 === null) return null;
        const j = b.indexOf(Buffer.from([0xe1, 0x01]), i + 8 + 2 * t1.length);
        if (j >= 0) { const t2 = textoEn(b, j + 2); if (t2) return t2; }
        return t1;
    }
    // Sin marca conocida (tipos de tramo/descansillo de escalera…): primera cadena [u32 n][UTF-16] legible cerca del comienzo del objeto, precedida por el u32 = 1.
    if (generico && b.length > 140) {
        for (let o = 120; o < Math.min(b.length - 8, 400); o++) {
            if (b.readUInt32LE(o) !== 1) continue;
            const t = textoEn(b, o + 4);
            if (t && t.length >= 2 && /^[A-Za-z0-9ÁÉÍÓÚÜÑáéíóúüñ°ºª·\-_.,:;()\/+%' ]+$/.test(t) && /[A-Za-z]/.test(t)) return t;
        }
    }
    return null;
}

/** Resuelve el tipo de cada elemento con lo que dejó el recorrido único (escaneo.mjs → datos). Devuelve { tipos: Map id → nombre de tipo, clases, tipoIds, matsDeTipo }. */
export function resolverTipos(datos, { log = () => {} } = {}) {
    const { cand, clases, ranuras, nombres, claseTipoDe, matsDeTipo } = datos;
    // Ranura del tipo: cada clase de elemento guarda el id de su tipo en un desplazamiento fijo de su objeto. Se aprende por votación: el desplazamiento donde más
    // elementos de esa clase tienen un id que es un objeto con nombre de tipo.
    const votos = new Map();      // clase del elemento → (desplazamiento → votos)
    for (const [id, rs] of ranuras) {
        const c = clases.get(id); if (c == null) continue;
        const m = votos.get(c) || new Map(); votos.set(c, m);
        const vistos = new Set();
        for (const [v, off] of rs) if (nombres.has(v) && !vistos.has(off)) { vistos.add(off); m.set(off, (m.get(off) || 0) + 1); }
    }
    const ranuraDe = new Map();
    for (const [c, m] of votos) { if (!c) continue; const t = [...m].sort((a, b) => b[1] - a[1]); if (t.length && t[0][1] >= 3) ranuraDe.set(c, t[0][0]); }
    // clase de tipo dominante de cada clase de elemento, mirando solo los ids cercanos a la ranura (±24 bytes: el diseño del objeto se corre unos bytes)
    const dominante = new Map();
    for (const [c, best] of ranuraDe) {
        const tc = new Map();
        for (const [id, rs] of ranuras) {
            if (clases.get(id) !== c) continue;
            const vistos = new Set();
            for (const [v, off] of rs) if (nombres.has(v) && Math.abs(off - best) <= 24 && !vistos.has(v)) { vistos.add(v); const k = claseTipoDe.get(v); tc.set(k, (tc.get(k) || 0) + 1); }
        }
        const t = [...tc].sort((a, b) => b[1] - a[1]); if (t.length) dominante.set(c, t[0][0]);
    }
    log(`tipos: ranuras aprendidas ${[...ranuraDe].map(([c, o]) => c + '@' + o + '→' + dominante.get(c)).join(' ')}`);
    const tipos = new Map(), tipoIds = new Map();
    for (const [id, rs] of ranuras) {
        const c = clases.get(id), best = ranuraDe.get(c), T = dominante.get(c); if (best == null || T == null) continue;
        const todosT = new Map(), banda = new Map(), exacto = [];
        for (const [v, off] of rs) {
            if (!nombres.has(v) || claseTipoDe.get(v) !== T) continue;
            todosT.set(v, off);
            if (Math.abs(off - best) <= 24) banda.set(v, off);
            if (off === best) exacto.push(v);
        }
        const unico = (m) => { const n = new Set([...m.keys()].map((v) => nombres.get(v))); return n.size === 1 ? [...m.keys()].pop() : null; };
        if (process.env.TIPOS_DEBUG && Number(process.env.TIPOS_DEBUG) === id) console.error('tipos-debug ranura', c, best, 'T', T, 'hits', rs.filter(([w]) => nombres.has(w)).map(([w, o]) => `${w}@${o}=${nombres.get(w)}[${claseTipoDe.get(w)}]`).join(' | '));
        let v = unico(todosT);
        if (v == null) v = unico(banda);
        if (v == null && exacto.length) v = exacto[exacto.length - 1];
        if (v != null) { tipos.set(id, nombres.get(v)); tipoIds.set(id, v); }
    }
    const ambiguos = [];
    const clasesDeTipoConocidas = new Set(dominante.values());
    const pareja = new Map();      // clase del elemento → (clase del tipo → veces) en los casos sin ambigüedad
    for (const [id, s] of cand) {
        if (tipos.has(id)) continue;
        // Solo vale un candidato cuya clase de tipo sea la de esa clase de elemento (si se aprendió) o, si no, alguna clase de tipo conocida: los ids chicos que
        // aparecen por casualidad dentro del objeto (p. ej. «Banquina…», «Plataforma 1») no son el tipo.
        const Tc = dominante.get(clases.get(id));
        const hit = [...s].filter((v) => nombres.has(v) && (Tc != null ? claseTipoDe.get(v) === Tc : clasesDeTipoConocidas.has(claseTipoDe.get(v))));
        const u = new Set(hit.map((v) => nombres.get(v)));
        if (process.env.TIPOS_DEBUG && Number(process.env.TIPOS_DEBUG) === id) console.error(`tipos-debug ${id}: candidatos ${s.size}, con nombre ${hit.map((v) => v + '=' + nombres.get(v)).join(' | ')}`);
        if (u.size === 1) {
            tipos.set(id, [...u][0]); tipoIds.set(id, hit[0]);
            const ce = clases.get(id);
            if (ce != null && new Set(hit.map((v) => claseTipoDe.get(v))).size === 1) { const m = pareja.get(ce) || new Map(); pareja.set(ce, m); const ct = claseTipoDe.get(hit[0]); m.set(ct, (m.get(ct) || 0) + 1); }
        } else if (u.size > 1) ambiguos.push([id, hit]);
    }
    // Ambiguos (ids pequeños que aparecen por casualidad dentro del objeto): vale el candidato cuya clase de tipo es la habitual para la clase de ese elemento.
    for (const [id, hit] of ambiguos) {
        const m = pareja.get(clases.get(id)); if (!m) continue;
        const mejor = [...m].sort((a, b) => b[1] - a[1])[0][0];
        const h2 = hit.filter((v) => claseTipoDe.get(v) === mejor);
        if (new Set(h2.map((v) => nombres.get(v))).size === 1) { tipos.set(id, nombres.get(h2[0])); tipoIds.set(id, h2[0]); }
    }
    return { tipos, clases, tipoIds, matsDeTipo };
}
