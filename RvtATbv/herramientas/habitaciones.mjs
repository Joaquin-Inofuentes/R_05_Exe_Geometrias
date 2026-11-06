// habitaciones.mjs — habitaciones (Room) y niveles leídos directamente del .rvt (Revit 2019–2023).
// Formato de salida = el de <proyecto>_Geometria_Habitaciones.json que escribe el exportador de producción.
//
// Ingeniería inversa (verificada contra las 95 habitaciones de RI_ARQ en R2):
//   · objeto enmarcado de clase RoomElem (tag 3354 en Revit 2021 build 20200220): [u32 id][disc][largo][u16 clase] … [eco del largo]
//   · parámetros de texto = [i32 id de parámetro][u32 n][UTF-16]: 0xFFF0A2CC = ROOM_NAME, 0xFFF0A2C5 = ROOM_DEPARTMENT
//   · id del nivel = u32 en el offset 60 del objeto; posición = doubles (pies) en los offsets 95 (x) y 103 (y); z = elevación del nivel
//   · objeto Level (tag 2199): nombre = [u32 n][UTF-16] en el offset 95; elevación (pies) = double 60 bytes antes del final
import { escanear } from './escaneo.mjs';

const FT = 0.3048;
const P_NOMBRE = 0xfff0a2cc, P_DEPTO = 0xfff0a2c5;

/** Nombre del nivel = [u32 n][UTF-16] cerca del comienzo del objeto: en el offset 95 (Revit 2021 de RI_ARQ) o 97 (marcos de 717 bytes); se busca en 88..125. */
export function nombreNivel(f) {
    if (f.length < 130) return null;
    for (let q = 88; q <= 125; q++) {
        const n = f.readUInt32LE(q);
        if (n < 1 || n > 60 || q + 4 + 2 * n > f.length) continue;
        const t = f.toString('utf16le', q + 4, q + 4 + 2 * n);
        if (/^[ -~ -ÿ]+$/.test(t) && /[A-Za-z0-9]/.test(t)) return t;
    }
    return null;
}

function parametroTexto(f, pid) {
    const pat = Buffer.alloc(4); pat.writeUInt32LE(pid >>> 0);
    for (let i = f.indexOf(pat); i >= 0; i = f.indexOf(pat, i + 1)) {
        if (i + 8 > f.length) break;
        const n = f.readUInt32LE(i + 4);
        if (n > 0 && n < 300 && i + 8 + 2 * n <= f.length) return f.toString('utf16le', i + 8, i + 8 + 2 * n);
    }
    return null;
}

/** Habitaciones ya recogidas por escaneo.mjs (datos.rooms / datos.niveles). base = [x,y,z] del Punto Base del Proyecto en METROS, ejes de Revit. */
export function habitacionesDe(datos, idt, { base = [0, 0, 0] } = {}) {
    const niveles = datos.niveles;
    const out = [];
    for (const [id, f] of [...datos.rooms].sort((a, b) => a[0] - b[0])) {
        const nivel = niveles.get(f.readUInt32LE(60));
        const x = f.readDoubleLE(95), y = f.readDoubleLE(103), zrel = f.readDoubleLE(111);
        if (![x, y, zrel].every((v) => Number.isFinite(v) && Math.abs(v) < 1e5)) continue;
        if (idt && idt.filas && idt.filas.size > 1000 && !idt.filas.has(id)) continue;      // objeto que ya no está en la tabla de elementos = habitación borrada
        if (x === 0 && y === 0) continue;      // habitación sin ubicar (Area = 0): el exportador de producción la omite
        const zFt = (nivel ? nivel.elevFt : 0) + zrel;
        out.push({
            ElementId: id,
            Guid: idt ? idt.guidDe(id) : '',
            Level: nivel ? nivel.nombre : '',
            Department: parametroTexto(f, P_DEPTO) || '',
            Name: parametroTexto(f, P_NOMBRE) || '',
            Center: [x * FT - base[0], zFt * FT - base[2], -(y * FT - base[1])]
        });
    }
    return { rooms: out, niveles: [...niveles.values()] };
}

/** Recorre el archivo por su cuenta (pruebas sueltas); la herramienta usa habitacionesDe con el recorrido único. */
export function extraerHabitaciones(cfb, idt, { base = [0, 0, 0], claseRoom = 3354, claseNivel = 2199, log = () => {} } = {}) {
    return habitacionesDe(escanear(cfb, { idsValidos: new Set(), claseRoom, claseNivel, log }), idt, { base });
}

export function jsonHabitaciones(rooms, baseTbv) {
    return JSON.stringify({
        Units: 'meters',
        AxisSystem: 'Y-Up (X, Z, -Y respecto de Revit)',
        Origin: 'Punto Base del Proyecto',
        ProjectBasePoint: baseTbv,
        Count: rooms.length,
        Rooms: rooms
    }, null, 2);
}

/** Punto Base del Proyecto en METROS, ejes de Revit, de los BasePoint recogidos por escaneo.mjs (byte 115 = 0; x,y,z en pies = doubles 256/264/272). La última copia manda.
 *  Verificado contra el _Habitaciones.json de producción en 12 archivos. */
export function puntoBaseDe(datos) {
    return datos.basePoints.length ? datos.basePoints[datos.basePoints.length - 1] : [0, 0, 0];
}

export function puntoBaseProyecto(cfb, { claseBasePoint = 467 } = {}) {
    return puntoBaseDe(escanear(cfb, { idsValidos: new Set(), claseBase: claseBasePoint }));
}
