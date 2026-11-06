#!/usr/bin/env node
// comparar-tbv.mjs — ¿estos dos .tbv son «el mismo» modelo? Compara estructura y dibuja ambos.
//
//   node comparar-tbv.mjs <A.tbv> <B.tbv> [--salida <carpeta>] [--nombre <prefijo>] [--sin-render]
//
// Usa el MISMO parser que el visor (Visor3D/core/TbvParser.js). Sin WebGL: un rasterizador ortográfico propio
// (z-buffer + sombreado plano) dibuja planta, frente, lateral e isométrica de A y B, más una imagen de diferencias
// (rojo = sólo A, azul = sólo B). Imprime un informe y escribe <prefijo>_comparacion.json y <prefijo>_<vista>.png.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { parseTbv } from 'file:///C:/.TBT/Proyectos/W_21_Visor3D/Visor3D/core/TbvParser.js';

console.warn = () => {};

const args = process.argv.slice(2);
const pos = args.filter((a, i) => !a.startsWith('--') && !(i > 0 && ['--salida', '--nombre'].includes(args[i - 1])));
const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : d; };
if (pos.length < 2) { console.log('Uso: node comparar-tbv.mjs <A.tbv> <B.tbv> [--salida carpeta] [--nombre prefijo] [--sin-render]'); process.exit(2); }
const [pa, pb] = pos;
const salida = opt('--salida', path.dirname(path.resolve(pb)));
const prefijo = opt('--nombre', path.basename(pb, '.tbv') + '_vs_' + path.basename(pa, '.tbv'));
fs.mkdirSync(salida, { recursive: true });

function cargar(p) {
    const b = fs.readFileSync(p);
    const d = parseTbv(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength));
    d.__ruta = p; d.__bytes = b.length;
    return d;
}
const A = cargar(pa), B = cargar(pb);

// ───────── estructura ─────────
function porElemento(d) {
    const m = new Map();
    for (const i of d.instances) {
        let e = m.get(i.elemId);
        if (!e) { e = { elemId: i.elemId, guid: i.guid, cat: i.catId, fam: d.names[i.famIdx] ?? '', tipo: d.names[i.typIdx] ?? '', inst: 0, tris: 0, min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity] }; m.set(i.elemId, e); }
        e.inst++;
        e.tris += d.meshes[i.meshIdx].tc;
        for (let k = 0; k < 3; k++) { e.min[k] = Math.min(e.min[k], i.gmin[k]); e.max[k] = Math.max(e.max[k], i.gmax[k]); }
    }
    return m;
}
const EA = porElemento(A), EB = porElemento(B);
const comunes = [...EA.keys()].filter((k) => EB.has(k));
const soloA = [...EA.keys()].filter((k) => !EB.has(k));
const soloB = [...EB.keys()].filter((k) => !EA.has(k));
let bboxOk1cm = 0, bboxOk10cm = 0, mismaCat = 0, mismoGuid = 0, mismoTipo = 0, difTris = 0, sumTrisA = 0;
const peoresBbox = [];
for (const k of comunes) {
    const a = EA.get(k), b = EB.get(k);
    let dmax = 0;
    for (let i = 0; i < 3; i++) dmax = Math.max(dmax, Math.abs(a.min[i] - b.min[i]), Math.abs(a.max[i] - b.max[i]));
    if (dmax <= 0.01) bboxOk1cm++;
    if (dmax <= 0.10) bboxOk10cm++;
    else peoresBbox.push({ elemId: k, dmax: +dmax.toFixed(3), fam: a.fam, tipo: a.tipo });
    if (a.cat === b.cat) mismaCat++;
    if (a.guid === b.guid) mismoGuid++;
    if (a.fam === b.fam && a.tipo === b.tipo) mismoTipo++;
    difTris += Math.abs(a.tris - b.tris); sumTrisA += a.tris;
}
peoresBbox.sort((x, y) => y.dmax - x.dmax);
const tris = (d) => d.meshes.reduce((n, m) => n + m.tc, 0);
const bboxD = (d) => ({ min: d.sceneMin.map((x) => +x.toFixed(3)), max: d.sceneMax.map((x) => +x.toFixed(3)) });
const histCat = (m) => { const h = {}; for (const e of m.values()) h[e.cat] = (h[e.cat] || 0) + 1; return h; };
const pct = (n, t) => t ? +(100 * n / t).toFixed(1) : null;

const informe = {
    A: { archivo: path.basename(pa), bytes: A.__bytes, version: A.version, mallas: A.meshes.length, instancias: A.instances.length, elementos: EA.size, triangulos: tris(A), materiales: A.materials.length, bbox: bboxD(A) },
    B: { archivo: path.basename(pb), bytes: B.__bytes, version: B.version, mallas: B.meshes.length, instancias: B.instances.length, elementos: EB.size, triangulos: tris(B), materiales: B.materials.length, bbox: bboxD(B) },
    elementos: {
        comunes: comunes.length, soloEnA: soloA.length, soloEnB: soloB.length,
        pct_de_A_presentes_en_B: pct(comunes.length, EA.size),
        misma_categoria: pct(mismaCat, comunes.length), mismo_guid: pct(mismoGuid, comunes.length), misma_familia_y_tipo: pct(mismoTipo, comunes.length),
        bbox_dentro_de_1cm: pct(bboxOk1cm, comunes.length), bbox_dentro_de_10cm: pct(bboxOk10cm, comunes.length),
        diferencia_de_triangulos_en_comunes: pct(difTris, sumTrisA)
    },
    categorias_A: histCat(EA), categorias_B: histCat(EB),
    peores_bbox: peoresBbox.slice(0, 8),
    ejemplos_solo_en_A: soloA.slice(0, 8).map((k) => ({ elemId: k, fam: EA.get(k).fam, tipo: EA.get(k).tipo, cat: EA.get(k).cat })),
    ejemplos_solo_en_B: soloB.slice(0, 8).map((k) => ({ elemId: k, fam: EB.get(k).fam, tipo: EB.get(k).tipo, cat: EB.get(k).cat }))
};

// ───────── render ─────────
function crc32(buf) {
    let c, crc = 0xFFFFFFFF;
    for (let n = 0; n < buf.length; n++) {
        c = (crc ^ buf[n]) & 0xFF;
        for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1;
        crc = (crc >>> 8) ^ c;
    }
    return (crc ^ 0xFFFFFFFF) >>> 0;
}
function png(w, h, rgb) {
    const raw = Buffer.alloc((w * 3 + 1) * h);
    for (let y = 0; y < h; y++) { raw[y * (w * 3 + 1)] = 0; rgb.copy(raw, y * (w * 3 + 1) + 1, y * w * 3, (y + 1) * w * 3); }
    const chunk = (t, d) => {
        const len = Buffer.alloc(4); len.writeUInt32BE(d.length);
        const td = Buffer.concat([Buffer.from(t, 'ascii'), d]);
        const c = Buffer.alloc(4); c.writeUInt32BE(crc32(td));
        return Buffer.concat([len, td, c]);
    };
    const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2;
    return Buffer.concat([Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw, { level: 6 })), chunk('IEND', Buffer.alloc(0))]);
}

const norm = (v) => { const l = Math.hypot(v[0], v[1], v[2]) || 1; return [v[0] / l, v[1] / l, v[2] / l]; };
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

const mn = [0, 1, 2].map((i) => Math.min(A.sceneMin[i], B.sceneMin[i]));
const mx = [0, 1, 2].map((i) => Math.max(A.sceneMax[i], B.sceneMax[i]));
const centro = [0, 1, 2].map((i) => (mn[i] + mx[i]) / 2);
const radio = Math.hypot(mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2]) / 2 || 1;

const VISTAS = {
    planta: { fwd: [0, -1, 0], up: [0, 0, -1] },
    frente: { fwd: [0, 0, -1], up: [0, 1, 0] },
    lateral: { fwd: [-1, 0, 0], up: [0, 1, 0] },
    iso: { fwd: norm([-1, -0.85, -1]), up: [0, 1, 0] }
};

function dibujar(d, vista, W, H) {
    const f = norm(vista.fwd);
    const r = norm(cross(f, vista.up));
    const u = cross(r, f);
    const esc = 0.92 * Math.min(W, H) / (2 * radio);
    const col = new Uint8Array(W * H * 3).fill(255);
    const zb = new Float32Array(W * H).fill(-Infinity);
    const mask = new Uint8Array(W * H);
    for (const inst of d.instances) {
        const m = d.meshes[inst.meshIdx];
        const mat = d.materials[inst.matIdx] || { r: 180, g: 180, b: 180 };
        const P = m.positions, I = m.indices;
        const sx = new Float32Array(m.vc), sy = new Float32Array(m.vc), sz = new Float32Array(m.vc);
        for (let v = 0; v < m.vc; v++) {
            const p = [P[v * 3] + inst.dx - centro[0], P[v * 3 + 1] + inst.dy - centro[1], P[v * 3 + 2] + inst.dz - centro[2]];
            sx[v] = W / 2 + dot(p, r) * esc; sy[v] = H / 2 - dot(p, u) * esc; sz[v] = -dot(p, f);
        }
        for (let t = 0; t < I.length; t += 3) {
            const a = I[t], b = I[t + 1], c = I[t + 2];
            const x0 = sx[a], y0 = sy[a], x1 = sx[b], y1 = sy[b], x2 = sx[c], y2 = sy[c];
            const den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
            if (Math.abs(den) < 1e-9) continue;
            // normal del triángulo en mundo → sombreado
            const e1 = [P[b * 3] - P[a * 3], P[b * 3 + 1] - P[a * 3 + 1], P[b * 3 + 2] - P[a * 3 + 2]];
            const e2 = [P[c * 3] - P[a * 3], P[c * 3 + 1] - P[a * 3 + 1], P[c * 3 + 2] - P[a * 3 + 2]];
            const n = norm(cross(e1, e2));
            const sh = 0.38 + 0.62 * Math.abs(dot(n, f));
            const R = Math.min(255, mat.r * sh), G = Math.min(255, mat.g * sh), Bc = Math.min(255, mat.b * sh);
            const minX = Math.max(0, Math.floor(Math.min(x0, x1, x2))), maxX = Math.min(W - 1, Math.ceil(Math.max(x0, x1, x2)));
            const minY = Math.max(0, Math.floor(Math.min(y0, y1, y2))), maxY = Math.min(H - 1, Math.ceil(Math.max(y0, y1, y2)));
            for (let y = minY; y <= maxY; y++) {
                for (let x = minX; x <= maxX; x++) {
                    const px = x + 0.5, py = y + 0.5;
                    const l1 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / den;
                    const l2 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / den;
                    const l3 = 1 - l1 - l2;
                    if (l1 < -1e-6 || l2 < -1e-6 || l3 < -1e-6) continue;
                    const z = l1 * sz[a] + l2 * sz[b] + l3 * sz[c];
                    const idx = y * W + x;
                    if (z > zb[idx]) { zb[idx] = z; col[idx * 3] = R; col[idx * 3 + 1] = G; col[idx * 3 + 2] = Bc; mask[idx] = 1; }
                }
            }
        }
    }
    return { col, mask };
}

const metricasVistas = {};
if (!args.includes('--sin-render')) {
    const W = 760, H = 520;
    for (const [nombre, v] of Object.entries(VISTAS)) {
        const ra = dibujar(A, v, W, H), rb = dibujar(B, v, W, H);
        let inter = 0, uni = 0, dif = 0, soloA = 0, soloB = 0;
        const dd = new Uint8Array(W * H * 3);
        for (let i = 0; i < W * H; i++) {
            const a = ra.mask[i], b = rb.mask[i];
            if (a && b) { inter++; uni++; const d3 = Math.abs(ra.col[i * 3] - rb.col[i * 3]) + Math.abs(ra.col[i * 3 + 1] - rb.col[i * 3 + 1]) + Math.abs(ra.col[i * 3 + 2] - rb.col[i * 3 + 2]); dif += d3 / 765; dd[i * 3] = dd[i * 3 + 1] = dd[i * 3 + 2] = 215; }
            else if (a) { uni++; soloA++; dd[i * 3] = 220; dd[i * 3 + 1] = 40; dd[i * 3 + 2] = 40; }
            else if (b) { uni++; soloB++; dd[i * 3] = 40; dd[i * 3 + 1] = 70; dd[i * 3 + 2] = 220; }
            else { dd[i * 3] = dd[i * 3 + 1] = dd[i * 3 + 2] = 255; }
        }
        metricasVistas[nombre] = { iou_silueta: uni ? +(inter / uni).toFixed(4) : 1, pixeles_solo_A: soloA, pixeles_solo_B: soloB, diferencia_color_media: inter ? +(dif / inter).toFixed(4) : 0 };
        // A | B | diferencia, con una franja separadora blanca
        const SEP = 6, TW = W * 3 + SEP * 2;
        const out = Buffer.alloc(TW * H * 3, 255);
        for (let y = 0; y < H; y++) {
            for (let x = 0; x < W; x++) {
                const o = (y * TW + x) * 3, i = (y * W + x) * 3;
                out[o] = ra.col[i]; out[o + 1] = ra.col[i + 1]; out[o + 2] = ra.col[i + 2];
                const o2 = (y * TW + W + SEP + x) * 3; out[o2] = rb.col[i]; out[o2 + 1] = rb.col[i + 1]; out[o2 + 2] = rb.col[i + 2];
                const o3 = (y * TW + (W + SEP) * 2 + x) * 3; out[o3] = dd[i]; out[o3 + 1] = dd[i + 1]; out[o3 + 2] = dd[i + 2];
            }
        }
        fs.writeFileSync(path.join(salida, `${prefijo}_${nombre}.png`), png(TW, H, out));
    }
    informe.render = metricasVistas;
}

fs.writeFileSync(path.join(salida, prefijo + '_comparacion.json'), JSON.stringify(informe, null, 2));

// ───────── texto ─────────
const e = informe.elementos;
console.log(`A = ${informe.A.archivo}  (v${A.version}, ${informe.A.elementos} elementos, ${informe.A.instancias} instancias, ${informe.A.triangulos} triángulos, ${(A.__bytes / 1024).toFixed(0)} KB)`);
console.log(`B = ${informe.B.archivo}  (v${B.version}, ${informe.B.elementos} elementos, ${informe.B.instancias} instancias, ${informe.B.triangulos} triángulos, ${(B.__bytes / 1024).toFixed(0)} KB)`);
console.log(`Elementos de A presentes en B: ${e.comunes}/${informe.A.elementos} (${e.pct_de_A_presentes_en_B} %) · sólo en A: ${e.soloEnA} · sólo en B: ${e.soloEnB}`);
console.log(`En los comunes: misma categoría ${e.misma_categoria} % · mismo GUID ${e.mismo_guid} % · misma familia/tipo ${e.misma_familia_y_tipo} % · caja dentro de 1 cm ${e.bbox_dentro_de_1cm} % (10 cm: ${e.bbox_dentro_de_10cm} %) · triángulos distintos ${e.diferencia_de_triangulos_en_comunes} %`);
console.log(`Caja de la escena A ${JSON.stringify(informe.A.bbox)}  B ${JSON.stringify(informe.B.bbox)}`);
for (const [n, m] of Object.entries(metricasVistas)) console.log(`Vista ${n.padEnd(8)} silueta IoU ${m.iou_silueta} · sólo A ${m.pixeles_solo_A} px · sólo B ${m.pixeles_solo_B} px · diferencia de color ${m.diferencia_color_media}`);
console.log('Informe:', path.join(salida, prefijo + '_comparacion.json'));
