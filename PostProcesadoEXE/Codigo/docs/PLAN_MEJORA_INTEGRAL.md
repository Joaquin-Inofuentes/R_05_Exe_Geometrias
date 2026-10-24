# Plan de Mejora Integral — Pipeline Revit → WebGL para Móviles de Gama Baja

**Objetivo:** que el modelo completo del edificio cargue y se mueva **ultra fluido (30+ FPS estables) en un móvil de gama baja**, ya sea servido desde el hosting anterior, como `index.html` simple, o embebido en Unity WebGL. El visor debe recibir **la ruta del archivo de modelo (.tbv) y cargarlo solo** (con caché), manteniendo el ejemplo incrustado actual solo como modo de prueba.

**Alcance:** punta a punta — Macro de Revit (IN) → `export.bin` → `ConvertidorGeometrias.exe` → formato `.tbv` → visor web (OUT) → despliegue (el hosting anterior / Unity WebGL).

---

## 1. Diagnóstico del Estado Actual (medido, no estimado)

Datos del modelo de ejemplo (`export_reporte.txt` + archivos reales):

| Métrica | Valor actual | Problema |
| :--- | :--- | :--- |
| `index.html` | **3.066 KB** | El 98% es el `.tbv` en Base64 (+33% de overhead vs binario) |
| `export.tbv` | 2.270 KB | Sin cuantización, sin compresión, GUIDs de 45 chars por instancia |
| Piezas | 2.635 | — |
| Geometrías únicas en pool | **2.536 de 2.635** | Dedup casi nulo (solo 99 instanciadas) |
| Triángulos finales | 153.732 | Razonable, pero sin LODs |
| Draw calls potenciales | **~2.500** | Un `InstancedMesh` por par (malla, material), mayoría con count=1 |
| Renders por frame | **2 (siempre)** | Escena completa se dibuja 2 veces: principal + minimapa, incluso quieto |
| Planos de clipping | 6 activos siempre | Costo de fragment shader permanente aunque no haya corte |

### Bugs críticos detectados (el visor actual está roto)

1. **[index.html:341](index.html:341)** — `new THREE.Mesh(boxGeo, boxMat)`: la variable se llama `sectionBoxMaterial`, `boxMat` no existe → `ReferenceError` en `initThree()`, **el visor no arranca**.
2. **[index.html:823](index.html:823)** — el panel de propiedades usa `inst.elId` pero la variable local se llama `instance` → `ReferenceError` al hacer clic en una pieza.
3. **Mojibake / corrupción por regex**: los scripts `clean_text.py` y `repair.py` aplicaron regex codiciosas (`s.*?lo` → `sólo`) que destruyeron código (`consólor`, `sólor`, comentarios ilegibles en líneas 420, 544, 729, 738). El HTML nunca debe editarse con regex de texto.

### Cuellos de botella por etapa

**IN — Macro Revit ([ExportarGeometria.cs](codigo/Revit/ExportarGeometria.cs)):**
- `GetInstanceGeometry()` (línea 249) **aplana toda familia a coordenadas de mundo** → dos ventanas idénticas rotadas 90° producen dos mallas distintas. Esto mata la instanciación aguas abajo (por eso solo 99/2.635 dedup).
- `ViewDetailLevel.Fine` + `face.Triangulate()` sin parámetro de LOD → teselación máxima para todo, incluso sanitarios y muebles curvos que se ven a 20 m.
- No exporta niveles (plantas) ni nombres de categoría → el visor no puede ofrecer "corte por piso" ni info legible.

**Convertidor ([Program.cs](codigo/ConvertidorGeometrias/Program.cs)):**
- Dedup por `EsCopiaTrasladada` (línea 1072): solo detecta **traslación pura**. Sin rotación ni espejo.
- Pipeline secuencial (sin `Parallel.ForEach`); hoy 2,5 s, pero escala mal con modelos grandes.
- Un solo nivel de detalle: no genera LODs pese a que el QEM ya está integrado y validado.
- No hay fusión de mallas únicas → cada muro/suelo es un draw call en el visor.

**Formato `.tbv` v1:**
- Posiciones `float32` (12 B/vértice) — cuantizables a 6 B sin pérdida visible.
- Índices `uint32` cuando la malla supera 65k vértices — evitable con chunking.
- GUID completo (45 bytes + 2 de largo) por instancia: los 37 primeros caracteres del `UniqueId` de Revit son **el mismo GUID de documento repetido 2.635 veces** (~110 KB desperdiciados).
- Sin tabla de offsets → imposible streaming o carga progresiva.
- Sin compresión (el hosting anterior puede servir Brotli, pero el Base64 embebido lo anula).

**OUT — Visor ([index.html](index.html)):**
- `MeshBasicMaterial` **ignora las normales** que el `.tbv` paga en bytes y parseo (246 KB muertos), y el modelo se ve plano, sin profundidad.
- `antialias: true` + `pixelRatio` hasta 2 + `DoubleSide` en todo + 6 clip planes: la combinación más cara posible para una GPU Mali/Adreno de gama baja.
- Bucle `animate()` renderiza siempre, aunque nada cambie → batería y throttling térmico (a los 10 min el teléfono baja frecuencias y todo se vuelve lento).
- `updateInstances()` recorre las 2.635 instancias y reescribe matrices/colores **en cada evento `input`** de los sliders (decenas de veces por segundo al arrastrar).
- Decodificación Base64 con bucle `charCodeAt` sobre 3 M de chars **en el hilo principal**.
- Raycasting de Three.js contra ~2.500 `InstancedMesh` sin filtro previo → taps lentos.
- Dependencias por CDN (Three.js r128, Google Fonts) → falla offline, incompatible con CSP estricta (Artifacts/PWA MIP).
- `backdrop-filter: blur(16px)` en paneles → costo de composición altísimo en móviles baratos.
- Errores con `alert()` y sin códigos → indepurable en campo.

---

## 2. Objetivos Medibles (presupuestos de rendimiento)

Estos números son el criterio de aceptación de todo el plan. El reporte del convertidor debe **fallar en rojo** si se exceden.

| Presupuesto | Objetivo | Hoy |
| :--- | :--- | :--- |
| Descarga total (modelo, comprimido) | **≤ 700 KB** | ~2.300 KB (3.066 KB embebido) |
| Tiempo hasta primer render (gama baja, 4G) | **≤ 3 s** | 8–15 s (si no crashea) |
| Draw calls en vista completa | **≤ 60** | ~2.500 |
| FPS moviendo cámara (Moto G / Redmi gama baja) | **≥ 30 estables** | < 15 estimado |
| FPS en reposo | render on-demand (≈0 GPU) | 2 renders/frame continuos |
| RAM pico del tab | **≤ 250 MB** | sin medir (copias múltiples del buffer) |
| Triángulos en pantalla (con LOD) | ≤ 80k | 153k siempre |

---

## 3. FASE 0 — Saneamiento (bloqueante, hacer primero)

> Sin esto, cualquier optimización se mide sobre un visor roto.

- [ ] **F0.1** Corregir `boxMat` → `sectionBoxMaterial` ([index.html:341](index.html:341)).
- [ ] **F0.2** Corregir `inst.` → `instance.` en el panel de propiedades ([index.html:823](index.html:823)).
- [ ] **F0.3** Limpiar todo el mojibake restante y **eliminar `clean_text.py` y `repair.py`** del flujo. Regla: el HTML se edita solo con herramientas que respeten UTF-8, nunca con regex de reemplazo masivo.
- [ ] **F0.4** Separar plantilla de datos: crear `visor/index.template.html` (solo código, ~30 KB, versionable y diffeable) y que **el EXE genere las salidas** (`index.html` embebido para pruebas y visor "liviano" que hace fetch). Se acaba el editar un HTML de 3 MB a mano.
- [ ] **F0.5** Test real en `test.js`: en vez de "esperar 2 s y cerrar", debe (a) fallar con exit code ≠ 0 ante cualquier `pageerror`, (b) verificar que el canvas pintó píxeles no-fondo (readPixels/screenshot), (c) simular un clic y validar que aparece el panel. Correrlo tras cada export.

**Esfuerzo:** medio día. **Impacto:** el visor vuelve a funcionar y queda protegido por CI.

---

## 4. FASE 1 — IN: Macro / Exportador de Revit (la mejora de mayor palanca)

### F1.1 Instanciación real de familias (cambio más importante de todo el plan)
Hoy: `GetInstanceGeometry()` aplana a mundo. Cambiar a:

```csharp
// Para GeometryInstance:
GeometryElement symGeo = instancia.GetSymbolGeometry(); // geometría LOCAL del símbolo
Transform tf = instancia.Transform;                     // rotación + traslación + espejo
```

- Exportar la **geometría del símbolo una sola vez** (clave: `SymbolId` + `MaterialId`) y por cada instancia solo su `Transform` (12 floats o T + quaternion).
- Nuevo `export.bin` **v4**: bloque de símbolos + bloque de instancias con transform completo. Retrocompatible: el lector del EXE ya autodetecta versión.
- **Efecto esperado:** ventanas, puertas, muebles, sanitarios, columnas tipo — todo pasa a ser 1 malla + N transforms. El pool de 2.536 geometrías únicas debería caer a **200–500**, y el `.tbv` a una fracción (la geometría es el 90% del peso).

### F1.2 Teselación acorde al uso
- `face.Triangulate(levelOfDetail)` acepta un parámetro 0..1: usar **0.3–0.5 para categorías de detalle** (muebles, sanitarios, puertas/ventanas) y dejar fino solo lo grande y plano (que además casi no genera triángulos).
- Mantener `ViewDetailLevel.Fine` solo si una categoría lo necesita; probar `Medium` global y comparar el reporte (los muros/suelos planos no cambian, los curvos bajan mucho).

### F1.3 Metadatos que el visor necesita
- Exportar tabla de **niveles** (nombre + elevación) y el `LevelId` por elemento → habilita "corte por planta" real en el visor y minimapa por piso.
- Exportar tabla `CategoryId → nombre` una sola vez → el panel de propiedades muestra "Muro" en vez de `-2000011`.
- (Opcional) `Nombre de tipo` por símbolo para el panel.

### F1.4 Robustez
- Mantener el `try/catch` por elemento, pero **contar y reportar** los elementos saltados (hoy se tragan en silencio) → línea en el reporte: `Elementos con geometría fallida: N (ids...)`.
- Escribir con `BufferedStream` (64 KB) para acelerar el export en modelos grandes.

**Esfuerzo:** 2–3 días (lo más delicado es el transform con espejos: determinante negativo → invertir winding de triángulos en el convertidor). **Impacto:** −60/80% de peso de geometría, dedup real, base para instancing en GPU.

---

## 5. FASE 2 — Convertidor (.EXE)

### F2.1 Dedup y agrupación
- Con `bin` v4 el dedup por símbolo viene gratis. Para geometría no-familia (muros, suelos), mantener el dedup actual por hash + traslación.
- **Fusión de mallas únicas ("chunking")**: las piezas que quedan con count=1 (muros, suelos, techos: 1.785 hoy) se fusionan por **(material, celda espacial)** en mega-mallas de ≤ 65.535 vértices (índices `uint16` garantizados). Cada chunk guarda su AABB y una tabla `rango de triángulos → elementId` para picking y para poder ocultar piezas individuales.
- Ordenar chunks e instancias **por material** (menos cambios de estado en GPU).

### F2.2 LODs (el QEM ya está, solo hay que usarlo 2 veces más)
- Generar por malla del pool: **LOD0** (actual), **LOD1** (~40% de tris), **LOD2** (~12%, solo siluueta). Respetar las mismas validaciones anti-rotura existentes (spikes, área, fallback).
- Las categorías protegidas (muros/suelos/techos) no generan LODs — ya son baratas por plano y el chunking las resuelve.
- Guardar los 3 niveles en el `.tbv` con offsets; el visor elige por distancia/tamaño en pantalla.

### F2.3 Paralelización y presupuestos
- `Parallel.ForEach` sobre el pipeline por-pieza (weld → degenerados → decimado → normales); es embarazosamente paralelo. El reporte y la escritura quedan secuenciales.
- El reporte agrega la sección **PRESUPUESTOS** (tabla de la sección 2) y devuelve **exit code ≠ 0** si se excede alguno → integrable en CI.

**Esfuerzo:** 3–4 días. **Impacto:** draw calls de ~2.500 → decenas; convertidor listo para modelos 10× más grandes.

---

## 6. FASE 3 — Formato `.tbv` v2 ("TBV2")

Cambios de layout (mantener lector v1 en el visor durante la transición):

| Campo | v1 | v2 | Ahorro |
| :--- | :--- | :--- | :--- |
| Cabecera | 36 B fijos | + **tabla de secciones** (offset+size de: materiales, pool LOD0/1/2, chunks, instancias, strings, metadatos) | habilita streaming y parseo perezoso |
| Posiciones | `float32 ×3` (12 B) | **`uint16 ×3` cuantizadas** + min/scale por malla (estilo `KHR_mesh_quantization`) | −50% |
| Normales | `int8 ×3` | **octaédricas `int8 ×2`** — o bit "sin normales" para chunks planos (flat shading por derivadas en shader) | −33% a −100% |
| Índices | `uint16/uint32` | siempre `uint16` (chunking lo garantiza) | hasta −50% en mallas grandes |
| Transform instancia | solo traslación | `T float3` + **quaternion smallest-three (4 B)** + flag espejo/escala | habilita instancias rotadas |
| GUID | 47 B por instancia | **prefijo de documento 1 vez** + sufijo de 4 B por instancia (el `UniqueId` es `docGuid-xxxxxxxx`) | ~110 KB en este modelo |
| Integridad | nada | `uint32` CRC + tamaño total en cabecera | detecta descargas truncadas |

### Compresión y entrega
- Generar también **`export.tbv.br`** (Brotli nivel 10) en el EXE. La cuantización mejora muchísimo la compresibilidad (los `uint16` deltificados comprimen 3–4×).
- Cliente: `fetch` + `DecompressionStream('gzip'/'br' según soporte)`; fallback a `.tbv` sin comprimir. En el hosting anterior, servir con `Content-Encoding` correcto y `Cache-Control: public, max-age=31536000, immutable` (el nombre del archivo lleva hash: `export.a1b2c3.tbv.br`).

**Estimación de peso para este modelo** (post F1.1 + F3): geometría del pool reducida por instancing real + cuantización + Brotli ≈ **400–700 KB** de descarga (vs 3.066 KB actuales embebidos). 

**Esfuerzo:** 2–3 días (escritor C# + lector JS + tests de ida y vuelta byte a byte).

---

## 7. FASE 4 — OUT: Visor Web

### F7.1 Carga de modelos por ruta (el flujo que pediste)
```
index.html?model=(URL anterior)
```
1. El visor lee `?model=` (o recibe la URL por `postMessage` desde Unity/MIP).
2. Busca en **IndexedDB** por clave `url + versión` → si está, carga local (0 red, arranque < 1 s).
3. Si no, `fetch` con streaming + barra de progreso real (`content-length`, **nunca** `.blob()` completo para medir), descomprime, guarda en IndexedDB y parsea.
4. Sin `?model=` y con `<script id="embedded-tbv">` presente → **modo demo embebido** (se conserva para probar, generado con `ConvertidorGeometrias.exe --embed`).
5. Todo error termina en un **catálogo de códigos estilo `MIP-xxx`** (sin WebGL2, descarga truncada CRC, IndexedDB bloqueado, magic inválido) con mensaje claro en español, código y contacto IT — nada de `alert()`.

### F7.2 Parseo sin congelar el hilo principal
- Parsear el `.tbv` en un **Web Worker**; devolver los `TypedArray` como **transferables** (zero-copy). Fallback al hilo principal si el Worker falla (regla MIP).
- Construir la escena **time-sliced**: presupuesto de ~8 ms por frame vía `requestAnimationFrame`, pintando primero muros/suelos (el usuario ve el edificio crecer, no una pantalla congelada).
- Liberar las copias CPU de los buffers tras subirlos a GPU (`BufferAttribute.onUpload(() => attr.array = null)`); para picking alcanza con los AABB de instancia.

### F7.3 Render: de ~2.500 draw calls a ≤ 60
- **Chunks fusionados** del `.tbv` v2 → 1 draw call por (material × celda) con culling por AABB de chunk.
- `InstancedMesh` **solo** para mallas con count > 1 (ahora reales gracias a F1.1), con matrices completas (rotación incluida).
- Un **único material compartido** por clase (opaco / vidrio) con color por instancia/vértice (ya se hace) → menos compilación de shaders, menos cambios de estado.
- Sombreado: reemplazar `MeshBasicMaterial` plano por un **shader unlit + luz hemisférica barata** (2 dot products) usando las normales que ya pagamos — el modelo gana volumen sin costo real. `DoubleSide` solo en el pase de capping.

### F7.4 Render on-demand (la mejora de batería/térmica más grande)
- Dirty-flag: renderizar **solo** cuando cambia cámara (evento `change` de OrbitControls + mientras el damping converge), sliders, selección o joystick activo. Quieto = 0 trabajo de GPU.
- **Minimapa**: renderizar a un `WebGLRenderTarget` **solo cuando cambia el corte o termina el movimiento de cámara**, y mostrarlo como textura. Nunca más 2 renders de escena por frame.
- **Clipping**: montar los 6 planos en los materiales **solo cuando algún slider sale de sus límites**; con el corte inactivo el fragment shader queda limpio.
- Sliders: acumular el valor y recalcular `updateInstances()` **una vez por frame** (rAF-debounce), no por evento `input`. Al arrastrar, actualizar solo las constantes de los planos (barato) y diferir el culling grueso a `pointerup`.
- **Resolución adaptativa**: arrancar con `pixelRatio = min(dpr, 1.5)`, medir FPS los primeros 3 s y bajar a 1.0 (y desactivar antialias) si no sostiene 30 FPS. En gama baja, nitidez < fluidez.

### F7.5 Interacción
- **Picking en CPU por AABB**: intersecar el rayo contra los `gmin/gmax` de instancias/chunks (ya están en el formato), ordenar por distancia y refinar solo contra los 3–5 candidatos → taps instantáneos sin BVH.
- LOD por distancia: histéresis simple con la boundingSphere de cada grupo (los 3 niveles ya vienen del EXE).

### F7.6 UI / entrega
- **Cero CDN**: Three.js (subir a r160+ con `three.module.min.js`) y fuentes **locales** (o `font-family: system-ui`) → funciona offline, en `file://`, tras CSP y dentro del template MIP.
- Quitar `backdrop-filter: blur` en móvil (media query) → fondo `rgba` sólido.
- Loader con **ETA real** basada en `content-length` + velocidad medida (mismo patrón que `load-estimator` de MIP).

**Esfuerzo:** 5–7 días. **Impacto:** es donde se materializa todo lo anterior.

---

## 8. FASE 5 — Despliegue: el hosting anterior + Unity WebGL

### el hosting anterior (estático)
```
/               index.html   (~25 KB, sin modelo)
/vendor/        three.module.min.js (local, pinned)
/modelos/       edificioA.f3a9.tbv.br  (immutable, hash en nombre)
rutas.json     headers: Content-Encoding br para *.tbv.br, immutable cache
```
- `index.html` con `max-age` corto; modelos con hash + `immutable` → segundo arranque casi instantáneo incluso sin IndexedDB.
- (Opcional) Service Worker cache-first para modo offline total, reutilizando los patrones de `sw.js`/`mapa.json` de MIP.

### Unity WebGL (dos vías, elegir según el caso)
1. **Visor HTML embebido en el template** (rápido de integrar): iframe/página del visor dentro del front MIP; Unity le pasa la ruta del modelo por `postMessage`. Respetar reglas MIP: carga lazy (no competir con la descompresión del build — esperar `window.isGameReady`), errores por catálogo, nada de bloquear el hilo.
2. **Loader nativo C# de `.tbv`** (mejor integración): `UnityWebRequest` → parseo con `Mesh.SetVertexBufferData/SetIndexBufferData` (API sin copias, acepta los `uint16` cuantizados directo con `VertexAttributeFormat.UNorm16`), `Graphics.RenderMeshInstanced` para repetidas, shader unlit vertex-color móvil. El mismo archivo sirve para ambos mundos — esa es la ventaja de tener formato propio.

---

## 9. FASE 6 — QA, CI y verificación en dispositivo real

- [ ] `test.js` (Puppeteer) ampliado: assert de píxeles pintados, clic → panel, **presupuesto de tiempo de parseo** (falla si > X ms con CPU throttling 4×), captura `desktop_screenshot.png`/`mobile_screenshot.png` como ya existe.
- [ ] Test de round-trip del formato: escribir `.tbv` v2 con datos sintéticos → leer en Node con el mismo parser del visor → comparar byte a byte.
- [ ] Matriz mínima de dispositivos: 1 Android gama baja real (Mali/Adreno de entrada, 2–3 GB RAM) + Chrome DevTools con CPU 4× + red Fast 3G. Medir: primer render, FPS orbitando, RAM (`performance.memory`), temperatura subjetiva a 10 min.
- [ ] El reporte del EXE es el contrato: si `PRESUPUESTOS` falla → el export no se publica.
- [ ] Actualizar `DOCUMENTACION_FORMATOS.md` (v2), `DOCUMENTACION_PIPELINE.md` (LODs/chunks) y `DOCUMENTACION_HTML.md` (carga por ruta) en el mismo PR que cada cambio.

---

## 10. Orden de Ejecución Recomendado

| # | Fase | Días | Dependencias | Resultado visible |
| :-: | :--- | :---: | :--- | :--- |
| 1 | **F0** Saneamiento + template + test real | 0,5–1 | — | El visor vuelve a funcionar; CI protege |
| 2 | **F4 quick-wins** (render on-demand, minimapa cacheado, rAF-debounce de sliders, pixelRatio adaptativo, clipping condicional) | 1–2 | F0 | **La mayor ganancia de FPS inmediata sin tocar formatos** |
| 3 | **F1** Macro con símbolos + transforms (bin v4) | 2–3 | — | Dedup real; archivos mucho más chicos |
| 4 | **F2** Chunking + LODs + paralelo | 3–4 | F1 | ≤ 60 draw calls |
| 5 | **F3** TBV2 cuantizado + Brotli | 2–3 | F2 | Descarga ≤ 700 KB |
| 6 | **F4 resto** (worker, streaming, IndexedDB, `?model=`, picking AABB, catálogo de errores) | 3–4 | F3 | Flujo final "le paso la ruta y carga" |
| 7 | **F5** el hosting anterior + integración Unity | 1–2 | F4 | Producción |
| 8 | **F6** QA en dispositivo real + docs | continuo | todas | Presupuestos verificados |

**Total estimado: ~3 semanas** de trabajo neto. Los pasos 1–2 (día y medio) ya deberían llevar el visor actual de "roto/<15 FPS" a "funcional y fluido con el modelo de prueba embebido de hoy", que es exactamente lo que necesitás para seguir probando mientras se hace el resto.

## 11. Riesgos y Mitigaciones

| Riesgo | Mitigación |
| :--- | :--- |
| Espejos en transforms de Revit (determinante < 0) invierten caras | Detectar en el convertidor y voltear winding; test con familia espejada |
| Cuantización visible en modelos muy extensos (>1 km) | min/scale **por malla**, no por escena; error máx = diagonal_malla/65535 |
| Instancias con geometría modificada por anfitrión (ventana recortada por muro) | Comparar hash de símbolo vs instancia; si difiere, exportar como malla única (fallback actual) |
| IndexedDB bloqueado (modo incógnito/Safari) | Caída silenciosa a fetch directo; código de aviso, no error fatal |
| Three.js r160 cambia APIs vs r128 | Migrar en F0 con el test de píxeles ya activo |
| Worker no disponible (file://, CSP) | Fallback a parseo en hilo principal time-sliced (patrón MIP) |
