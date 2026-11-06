// lector-rvt.mjs — lector propio (ingeniería inversa del formato, solo lectura) de un .rvt de Revit 2019–2023 (ids de 32 bits).
// Contenedor OLE2 + miembros gzip (deflate crudo) + Global/History (episodios→GUID) + Global/ElemTable (episodio de creación e id original).
// Validado en SO_DU.rvt: 275/275 UniqueId iguales a los del .tbv de R2.
import fs from 'node:fs';
import zlib from 'node:zlib';

// ───────── contenedor OLE2 ─────────
export class Cfb {
    constructor(buf) {
        const d = this.d = buf;
        if (d.readUInt32BE(0) !== 0xD0CF11E0 || d.readUInt32BE(4) !== 0xA1B11AE1) throw new Error('No es un contenedor OLE2 (.rvt válido)');
        this.ssz = 1 << d.readUInt16LE(30); this.mssz = 1 << d.readUInt16LE(32);
        const nFat = d.readUInt32LE(44), dirStart = d.readUInt32LE(48), miniStart = d.readUInt32LE(60), nMini = d.readUInt32LE(64);
        this.corte = d.readUInt32LE(56);
        let difatStart = d.readUInt32LE(68); const nDifat = d.readUInt32LE(72);
        const difat = []; for (let i = 0; i < 109; i++) difat.push(d.readUInt32LE(76 + 4 * i));
        for (let k = 0, s = difatStart; k < nDifat && s < 0xFFFFFFFA; k++) {
            const off = (s + 1) * this.ssz, n = this.ssz / 4;
            for (let i = 0; i < n - 1; i++) difat.push(d.readUInt32LE(off + 4 * i));
            s = d.readUInt32LE(off + 4 * (n - 1));
        }
        this.fat = [];
        for (let i = 0; i < nFat && i < difat.length; i++) {
            if (difat[i] >= 0xFFFFFFFA) continue;
            const off = (difat[i] + 1) * this.ssz;
            for (let j = 0; j < this.ssz / 4; j++) this.fat.push(d.readUInt32LE(off + 4 * j));
        }
        this.minifat = [];
        if (nMini) for (const s of this.cadena(miniStart)) { const off = (s + 1) * this.ssz; for (let j = 0; j < this.ssz / 4; j++) this.minifat.push(d.readUInt32LE(off + 4 * j)); }
        const dir = this.leerCadena(dirStart);
        this.entradas = [];
        for (let i = 0; i + 128 <= dir.length; i += 128) {
            const nl = dir.readUInt16LE(i + 64);
            this.entradas.push({ id: i / 128, nombre: nl >= 2 ? dir.toString('utf16le', i, i + nl - 2) : '', tipo: dir[i + 66], izq: dir.readUInt32LE(i + 68), der: dir.readUInt32LE(i + 72), hijo: dir.readUInt32LE(i + 76), ini: dir.readUInt32LE(i + 116), tam: Number(dir.readBigUInt64LE(i + 120)) });
        }
        const raiz = this.entradas[0];
        this.mini = raiz.tam > 0 ? this.leerCadena(raiz.ini).subarray(0, raiz.tam) : Buffer.alloc(0);
        this.streams = new Map();
        const rec = (i, base) => {
            if (i === 0xFFFFFFFF || i >= this.entradas.length) return;
            const e = this.entradas[i]; rec(e.izq, base);
            e.ruta = base + '/' + e.nombre;
            if (e.tipo === 2) this.streams.set(e.ruta.toLowerCase(), e);
            if (e.tipo === 1) rec(e.hijo, e.ruta);
            rec(e.der, base);
        };
        rec(raiz.hijo, '');
    }
    * cadena(s) { let g = 0; while (s < 0xFFFFFFFA && s < this.fat.length && g++ < 5e7) { yield s; s = this.fat[s]; } }
    leerCadena(s) {
        const trozos = [];
        for (const x of this.cadena(s)) { const off = (x + 1) * this.ssz; if (off + this.ssz > this.d.length) break; trozos.push(this.d.subarray(off, off + this.ssz)); }
        return Buffer.concat(trozos);
    }
    leer(ruta) {
        const e = this.streams.get(ruta.toLowerCase()); if (!e) return null;
        if (e.tam < this.corte) {
            const t = []; let s = e.ini, g = 0, n = 0;
            while (s < 0xFFFFFFFA && s < this.minifat.length && n < e.tam && g++ < 1e7) { t.push(this.mini.subarray(s * this.mssz, (s + 1) * this.mssz)); n += this.mssz; s = this.minifat[s]; }
            return Buffer.concat(t).subarray(0, e.tam);
        }
        const bruto = this.leerCadena(e.ini).subarray(0, e.tam);
        return PAGINADO.test(ruta.toLowerCase()) ? sinColasDePagina(bruto) : bruto;
    }
}

// Los flujos de Revit se guardan en páginas de 65 249 bytes: 64 896 de datos + 353 de suma de control. Con las colas dentro, ~9 % de los trozos gzip no se inflan
// (456 en RI_ARQ) y la ElemTable queda ilegible a partir de la fila 12 958; sin ellas la ElemTable da las 83 521 filas que declara. reviter ya lo hace (stripRevitPageChecksums).
// Solo cambia lo que la carga nativa de Revit lee por páginas: particiones, Formats/Latest y Global/{ContentDocuments,DocumentIncrementTable,ElemTable,History,Latest,PartitionTable}.
const PAGINA_DATOS = 64896, PAGINA_TOTAL = 65249;
const PAGINADO = /^\/(partitions\/[^/]+|formats\/latest|global\/(contentdocuments|documentincrementtable|elemtable|history|latest|partitiontable))$/i;
export function sinColasDePagina(b) {
    const completas = Math.floor(b.length / PAGINA_TOTAL);
    if (!completas) return b;
    const t = [];
    for (let k = 0; k < completas; k++) t.push(b.subarray(k * PAGINA_TOTAL, k * PAGINA_TOTAL + PAGINA_DATOS));
    t.push(b.subarray(completas * PAGINA_TOTAL));      // la última página parcial se conserva entera
    return Buffer.concat(t);
}

// ───────── gzip de Revit: cabecera de 10 bytes + deflate crudo (el CRC final no es confiable) ─────────
export function inflarMiembro(buf, magia) {
    try {
        const r = zlib.inflateRawSync(buf.subarray(magia + 10), { info: true });
        return { datos: r.buffer, usado: 10 + r.engine.bytesWritten };
    } catch (_) { return null; }
}
export function buscarMagia(buf, desde) {
    for (let i = desde; i + 4 <= buf.length; i++) if (buf[i] === 0x1f && buf[i + 1] === 0x8b && buf[i + 2] === 8 && buf[i + 3] === 0) return i;
    return -1;
}
// Las particiones son una cadena de trozos de ~128 KB: [registro de 32 bytes: u32 …, u32 0x0d740000, u32 4 …][gzip]. Un registro válido delimita cada trozo.
// ~9 % de los trozos de un archivo grande no se inflan solos: la mayoría continúa el trozo anterior (usa sus últimos 32 KB como diccionario);
// el resto se corta en un punto (todo lo anterior es válido). Ver estad: { ok, ventana, parcial, perdido, bytesPerdidos }.
const VENTANA = 32768;
const esRegistro = (buf, p) => p >= 32 && buf.readUInt32LE(p - 28) === 0x0d740000;
function rescatar(datos, dic) {
    const op = { finishFlush: zlib.constants.Z_SYNC_FLUSH, ...(dic ? { dictionary: dic } : {}) };
    let lo = 0, hi = datos.length, mejor = null;
    while (hi - lo > 8) {
        const mid = (lo + hi) >> 1;
        try { mejor = zlib.inflateRawSync(datos.subarray(0, mid), op); lo = mid; } catch (_) { hi = mid; }
    }
    return mejor;
}
export function inflarTodo(buf, estad = null) {
    const inicios = [];
    for (let i = 0; ;) { const m = buscarMagia(buf, i); if (m < 0) break; if (esRegistro(buf, m)) inicios.push(m); i = m + 3; }
    const partes = [];
    const E = estad || {}; E.ok = E.ok || 0; E.ventana = E.ventana || 0; E.parcial = E.parcial || 0; E.perdido = E.perdido || 0; E.bytesPerdidos = E.bytesPerdidos || 0;
    if (inicios.length === 0) {      // streams sin registros (Formats, Global…): recorrido miembro a miembro
        let i = 0;
        for (;;) {
            const m = buscarMagia(buf, i); if (m < 0) break;
            const r = inflarMiembro(buf, m);
            if (!r) { i = m + 3; continue; }
            partes.push(r.datos); i = m + r.usado;
        }
        return Buffer.concat(partes);
    }
    let previo = null;
    for (let k = 0; k < inicios.length; k++) {
        const p = inicios[k], fin = k + 1 < inicios.length ? inicios[k + 1] - 32 : buf.length;
        const datos = buf.subarray(p + 10, fin);
        let sal = null;
        try { sal = zlib.inflateRawSync(datos); E.ok++; } catch (_) {
            const dic = previo ? previo.subarray(Math.max(0, previo.length - VENTANA)) : null;
            if (dic) { try { sal = zlib.inflateRawSync(datos, { dictionary: dic }); E.ventana++; } catch (_2) { /* sigue */ } }
            if (!sal) {
                const a = rescatar(datos, null), b = dic ? rescatar(datos, dic) : null;
                sal = b && (!a || b.length > a.length) ? b : a;
                if (sal && sal.length) E.parcial++; else { sal = null; E.perdido++; }
                E.bytesPerdidos += datos.length;
            }
        }
        if (sal) { partes.push(sal); previo = sal; }
    }
    return Buffer.concat(partes);
}

// ───────── abrir y leer identidades ─────────
export function abrirRvt(ruta) {
    const buf = fs.readFileSync(ruta);
    return new Cfb(buf);
}

const FORMATOS_HISTORIAL_ANGOSTO = new Set([0x045d, 0x0468, 0x0482, 0x04a2, 0x04dc]);   // Revit 2019–2023 (ids de 32 bits)

function guidTexto(b, o) {
    const h = (n, w) => n.toString(16).padStart(w, '0');
    return [h(b.readUInt32LE(o), 8), h(b.readUInt16LE(o + 4), 4), h(b.readUInt16LE(o + 6), 4), b.subarray(o + 8, o + 10).toString('hex'), b.subarray(o + 10, o + 16).toString('hex')].join('-');
}

/** Devuelve { guidDe(id), elementos, formato } o null si la versión no es la de 32 bits. */
export function identidades(cfb) {
    const eh = cfb.leer('/Global/History'), et = cfb.leer('/Global/ElemTable');
    if (!eh || !et) return null;
    const hist = inflarTodo(eh), tabla = inflarTodo(et.subarray(8));
    const formato = hist.readUInt16LE(0);
    if (!FORMATOS_HISTORIAL_ANGOSTO.has(formato)) return null;
    const n = hist.readUInt32LE(102), baseEp = 106 + 4 * n + 4;
    const sig = Math.floor((hist.length - baseEp) / 17);      // cantidad de episodios = lo que cabe en el flujo (la cuenta de la cabecera, u32@14, a veces está desfasada: SO_MO +6)
    // filas de 28 bytes: [id][id original][episodio de creación][última mod.][última mod. de usuario][..][..]
    let off = -1;
    const ok6 = (() => { let prev = -1; for (let k = 0; k < 8; k++) { const o = 6 + 28 * k; if (o + 28 > tabla.length || tabla.readInt32LE(o + 20) !== 0 || tabla.readInt32LE(o) <= prev) return false; prev = tabla.readInt32LE(o); } return true; })();
    if (ok6 && tabla.readUInt32LE(2) > 0) off = 6;      // cabecera [u16][u32 cuenta] y filas de 28 bytes desde el 6
    for (let o = 0; o < 40 && off < 0; o++) {
        if (o + 56 > tabla.length) break;
        const a = tabla.readUInt32LE(o), b = tabla.readUInt32LE(o + 4), a2 = tabla.readUInt32LE(o + 28), b2 = tabla.readUInt32LE(o + 32);
        if (a === b && a2 === b2 && a > 0 && a < 1000 && a2 > a) off = o;
    }
    if (off < 0) return null;
    // Filas válidas: [id][id original][creación][últ. mod.][últ. mod. usuario][0][dueño|-1], con ids estrictamente crecientes.
    // En archivos grandes hay bloques intercalados de largo variable que desalinean las filas siguientes: se resincroniza buscando
    // 4 filas válidas seguidas (campo 6 = 0 e ids crecientes).
    const idEn = (o) => tabla.readInt32LE(o);
    const valida = (o, prev) => o + 28 <= tabla.length && tabla.readInt32LE(o + 20) === 0 && idEn(o) > prev && idEn(o) < 0x3fffffff;
    const cadenaValida = (o, prev) => { for (let k = 0; k < 4; k++) { if (!valida(o + 28 * k, prev)) return false; prev = idEn(o + 28 * k); } return true; };
    const filas = new Map(); let bloques = 0, bytesSaltados = 0, prevId = -1;
    for (let o = off; o + 28 <= tabla.length;) {
        if (valida(o, prevId)) {
            const id = idEn(o); prevId = id;
            filas.set(id, { id, original: tabla.readInt32LE(o + 4), creacion: tabla.readInt32LE(o + 8), modif: tabla.readInt32LE(o + 12), modifUsuario: tabla.readInt32LE(o + 16), dueno: tabla.readInt32LE(o + 24) });
            o += 28; continue;
        }
        let hallado = -1;
        for (let p = o + 1; p + 112 <= tabla.length && p < o + 60000; p++) if (cadenaValida(p, prevId)) { hallado = p; break; }
        if (hallado < 0) break;
        bloques++; bytesSaltados += hallado - o; o = hallado;
    }
    const cacheEp = new Map();
    const guidEpisodio = (e) => {
        if (cacheEp.has(e)) return cacheEp.get(e);
        const o = baseEp + 17 * (sig - 1 - e);
        const g = (e >= 0 && e < sig && o >= 0 && o + 16 <= hist.length) ? guidTexto(hist, o) : '';
        cacheEp.set(e, g); return g;
    };
    return {
        formato, elementos: filas.size, bloquesSaltados: bloques, bytesSaltados,
        filas,
        guidDe(id) {
            const f = filas.get(id); if (!f) return '';
            const g = guidEpisodio(f.creacion);
            return g ? `${g}-${(f.original >>> 0).toString(16).padStart(8, '0')}` : '';
        }
    };
}

// uso directo: node lector-rvt.mjs <rvt> [id ...]
if (process.argv[1] && process.argv[1].endsWith('lector-rvt.mjs') && process.argv[2]) {
    const cfb = abrirRvt(process.argv[2]);
    const idt = identidades(cfb);
    if (!idt) { console.log('Versión no soportada para identidades.'); process.exit(1); }
    console.log(`historial 0x${idt.formato.toString(16)} · ${idt.elementos} elementos`);
    for (const a of process.argv.slice(3)) console.log(a, idt.guidDe(Number(a)));
}
