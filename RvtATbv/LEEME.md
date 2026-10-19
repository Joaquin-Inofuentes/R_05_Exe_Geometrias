# RvtATbv — .rvt → .tbv (+ habitaciones) SIN Revit

Dada la ruta absoluta de un `.rvt` de Revit 2021 (2019–2023), genera `<nombre>_Geometria.tbv` (v4, el mismo formato que lee el visor) y
`<nombre>_Geometria_Habitaciones.json` (el mismo formato que escribe el exportador de producción), leyendo solo el archivo.
**Nunca modifica el original**: lo copia a `PruebasInternas\copias_rvt\` y trabaja sobre la copia (se comprueban sha256 y fecha antes y después).

```
rvt-a-tbv.bat "C:\ARQ\X02_SO\03_ARQ\02_RVT\SO_DU.rvt"
rvt-a-tbv.bat "C:\ARQ\...\HO_ARQ.rvt" --salida D:\mis_tbv
```
Requisitos: Node.js ≥ 22.13 y la librería **reviter** (lectura de la geometría) en `PruebasInternas\repos_terceros\reviter` (`npm ci --ignore-scripts`).
Salida por defecto: `PruebasInternas\salida\<nombre>\sin_revit\` (`.tbv`, `_Habitaciones.json`, `_Resumen.json`).
RAM: ~3 GB para 100 MB; para archivos de más de 200 MB usar `--max-old-space-size=6000` (el `.bat` ya usa 6000).

## Resultados medidos contra lo que produjo el Revit de producción (R2) — 8 archivos, del más liviano al más pesado

| archivo (MB) | tiempo | elementos de producción presentes | categoría | GUID | familia+tipo | caja ≤1 cm | silueta planta / iso | triángulos (nuestro / prod) | habitaciones (nombre, nivel, centro, GUID) |
|---|---|---|---|---|---|---|---|---|---|
| SO_DU (7) | 17 s | 100 % | 100 % | **100 %** | **100 %** | 90 % | 0,999 / 0,980 | 78 % | — (sin habitaciones) |
| JU_DU (30) | 50 s | 100 % | 100 % | **100 %** | **100 %** | 81 % | 0,999 / 0,977 | 78 % | — |
| SO_MO (32) | 71 s | 97 % | 100 % | **100 %** | 97 % | 91 % | 0,999 / 0,993 | 86 % | — |
| RI_ARQ (107) | 193 s | 97 % | 100 % | **100 %** | 94,5 % | 83 % | 1,000 / 0,914 | 42 % | **95/95 en todo** |
| SO_ARQ (114) | 218 s | 99,5 % | 100 % | **100 %** | 95,5 % | 89 % | 1,000 / 0,997 | 51 % | **147/147 en todo** |
| JU_ARQ (108) | 193 s | 98,8 % | 100 % | **100 %** | 91 % | 86 % | 0,998 / 0,73 | 44 % | **132/132 en todo** |
| HO_ARQ (135) | 257 s | 97,8 % | 100 % | **100 %** | 95 % | 87 % | 0,80 / 0,90 | 39 % | 247/247 (+31 más nuevas que la referencia) |
| AF_ARQ (250) | 736 s | 97,8 % | 100 % | **100 %** | 92,5 % | 73 % | 0,990 / 0,959 | 38 % | 158/159 en todo (+5 extra) |

Notas: la referencia de R2 puede ser de una versión anterior del modelo (HO_ARQ y AF_ARQ muestran diferencias de modelo, no de lectura). Punto base del proyecto idéntico al de producción en los 12 archivos con habitaciones.
Los números detallados de cada corrida quedan en `PruebasInternas\salida\varios_ultimo.json`.

## Qué se decodificó (ingeniería inversa del formato, solo con los propios `.rvt`)
- Contenedor OLE2 + miembros gzip (deflate crudo tras 10 bytes). `Formats/Latest` = esquema de clases (tag de clase = índice en el esquema del archivo).
- **Páginas con suma de control**: los flujos grandes se guardan en páginas de 65 249 bytes = 64 896 de datos + 353 de suma de control. Con las colas dentro, ~9 % de los
  trozos gzip no se inflan y la ElemTable es ilegible desde la fila 12 958; quitándolas se leen todos los trozos y la ElemTable completa (83 521 filas = lo que declara).
- **GUID (UniqueId)** = GUID del episodio de creación + `-` + id en hexadecimal de 8 dígitos. `Global/ElemTable`: filas de 28 bytes desde el offset 6
  `[id][id original][episodio de creación][últ. mod.][últ. mod. usuario][0][dueño|-1]`; `Global/History`: cantidad de episodios = lo que cabe en el flujo
  (`floor((largo−base)/17)`), GUID del episodio en `base + 17·(cuenta−1−episodio)`.
- **Tipo de elemento de sistema** (suelos, muros, techos, barandillas…): el objeto del elemento guarda el id de su tipo en un desplazamiento fijo según su clase
  (se aprende por votación, ±24 bytes) y de una clase de tipo conocida; el nombre del tipo está en el objeto de tipo (`ff ff ff ff 26 01 [u32 n][UTF-16]`, o el parámetro
  `0xFFF09C16` en barandillas, o genérico en tramo/descansillo de escalera). **Manda la última copia** (partición de número mayor).
- **Habitaciones** (clase `RoomElem`): nombre y departamento = parámetros de texto `0xFFF0A2CC` / `0xFFF0A2C5`; posición = doubles (pies) en los offsets 95 y 103;
  nivel = id en el offset 60 → objeto `Level` (nombre = `[u32 n][UTF-16]` entre los offsets 88 y 125, elevación 60 bytes antes del final).
- **Punto base del proyecto**: objeto `BasePoint` (largo 280–460, byte 115 = 0), x,y,z en pies en los offsets 256/264/272; la última copia manda.
- **Elementos vivos**: se descartan los ids que no están en la ElemTable (borrados) y las categorías que producción nunca entrega (−2000921, −2003500, −2009630, −2009636, −2008139).
- **Familia** de elementos de sistema por clase de objeto, en español (como el Revit de producción); montantes: familia = tipo; paneles de muro cortina: «Panel del sistema»; techos: «Techo compuesto».
- Un único recorrido de particiones (`escaneo.mjs`) recoge tipos, niveles, habitaciones y punto base (antes eran 4 recorridos: 2× más lento en archivos grandes).

## Límites conocidos (medidos, no supuestos)
1. **Geometría paramétrica de familias cargables** (puertas, ventanas, sanitarios, mobiliario, barandillas): Revit la regenera con los parámetros de cada instancia; reviter solo entrega
   cajas envolventes → entre 38 % y 86 % de los triángulos de producción (la forma en planta/volumen coincide: siluetas 0,9–1,0). Para igualarlo habría que leer los documentos de familia embebidos
   (`Global/ContentDocuments`) y reimplementar el motor paramétrico.
2. **Elementos anidados con dueño** (travesaños/pasamanos de barandillas, tanques de inodoros, paños de ventana, tramos de escalera prefabricada): 2–3 % de los elementos de producción no salen.
3. **Tipo de algunos suelos** (JU_ARQ: ~95 con tipo «Losa 10/11») cuyo objeto de tipo no existe como objeto enmarcado; **familia** de instancias anidadas (sanitarios): salen sin nombre de familia.
4. **Color** del material dominante de cada elemento: 52–86 % (materiales por cara; reviter no da todas las asignaciones).
5. Elementos que el Revit de producción no exporta (vista 3D filtrada, fases): salen de más (35–90 por archivo grande).
6. Solo Revit 2019–2023 (ids de 32 bits). Revit 2024+ no está soportado.
7. Idioma: la familia de los elementos de sistema está en español; es una tabla en `rvt-a-tbv.mjs`.

## Pruebas
`node herramientas\probar.mjs` (SO_DU, ~1 min) · `node herramientas\probar.mjs --completo` (+ RI_ARQ, ~5 min): 29 comprobaciones, falla con código ≠ 0 si un umbral no se cumple.
`node herramientas\probar-varios.mjs [SO_DU JU_DU SO_MO JU_ARQ RI_ARQ SO_ARQ HO_ARQ AF_ARQ]` mide los 8 archivos de C:\ARQ (siempre sobre copias) contra los .tbv de `PruebasInternas\referencia_r2`.
Herramientas: `comparar-tbv.mjs`, `diag-famtipo.mjs`, `diag-conjunto.mjs`, `diag-desfase.mjs`, `diag-colores.mjs`/`diag-color2.mjs`, `diag-triangulos.mjs`, `probar-tipos.mjs`, `probar-habitaciones.mjs`, `RvtATbv.exe` (inspección del contenedor).

## Aviso sobre reviter
La lectura de la geometría la hace [reviter](https://github.com/ahzs645/reviter), que **no declara licencia**: está bajado solo en `PruebasInternas\repos_terceros` para uso local;
no se copió su código ni se redistribuye. Para entregar esto como producto hay que pedirle una licencia al autor o reimplementar la lectura.
