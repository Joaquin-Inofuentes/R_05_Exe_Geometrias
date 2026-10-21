using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace ConvertidorGeometrias
{
    public class MeshData
    {
        public int ElementId;
        public string Guid;
        public int MaterialId;

        /// <summary>
        /// Clave de pieza para agrupar y validar: GUID + material.
        /// Agrupar sólo por GUID fundía todas las caras del elemento en una malla y se quedaba
        /// con el material/color de la primera: una puerta madera+vidrio salía toda de madera,
        /// y el alpha del vidrio se perdía, desactivando la protección anti-decimado.
        /// </summary>
        public string PieceKey { get { return Guid + "|" + MaterialId; } }
        // Id de BuiltInCategory de Revit (formato v3); 0 si no vino en el archivo.
        public int CategoryId;
        // Color superficial simple del material de Revit (formato v2+). Alpha < 255 = vidrio/transparente.
        public byte ColR = 200, ColG = 200, ColB = 200, ColA = 255;
        public bool HasColor;

        // Patrón de superficie vectorial del material (formato v6+), sólo en muros.
        // Ángulo en radianes y separación entre líneas en METROS. El visor lo redibuja
        // por fragmento en el shader; acá sólo se transporta. Separación <= 0 = sin hatch.
        public float HatchAngle;
        public float HatchSpacing;
        public bool HasHatch { get { return HatchSpacing > 0f; } }

        // Nombre de Familia y de Tipo del ElementType de Revit (formato .bin v7+).
        // "" = el elemento no tiene tipo (habitaciones, geometría in-situ) o el .bin es v6 o
        // anterior. En el .tbv se deduplican en una tabla de nombres; "" se escribe como -1.
        // CUIDADO: cada vez que se construye un MeshData a partir de otro (fusión por pieza,
        // clonado con normales, decimado) hay que copiar estos dos campos o el nombre se pierde
        // en silencio y el visor muestra la ficha vacía sin ningún error.
        public string FamilyName = "";
        public string TypeName = "";
        // Formato .bin v8+: nivel y área (m²) / longitud (m) / volumen (m³) leídos por el addin de
        // PARÁMETROS de Revit. NaN = el elemento no los expone. Mismo CUIDADO que arriba: copiarlos
        // en cada clonado (CopiarPropiedadesDe) o el visor muestra "—" sin ningún error.
        public string Level = "";
        public float Area = float.NaN, Length = float.NaN, Volume = float.NaN;
        public void CopiarPropiedadesDe(MeshData o)
        {
            Level = o.Level; Area = o.Area; Length = o.Length; Volume = o.Volume;
        }
        public List<Vector3> Vertices = new List<Vector3>();
        public List<Vector3> Normals = new List<Vector3>();
        public List<int> Indices = new List<int>();

        // v4: Metadata de habitaciones (solo si CategoryId == -2000160 / OST_Rooms)
        public string RoomLevel = "";
        public string RoomDepartment = "";
        public string RoomName = "";
    }

    public class PipelineStats
    {
        public int FormatVersion = 1;
        public string ArchivoRvt = "";   // .bin v8+: nombre del .rvt de origen
        public string UnidadOrigen = "";
        public Vector3 PuntoBase;
        public int Rooms;          // habitaciones con centroide válido en el JSON
        public int RoomsSinCentro; // habitaciones que llegaron sin posición
        public int InputMeshes;
        public int OutputPieces;
        public long VertsIn, VertsWelded, VertsOut;
        public long TrisIn, TrisOut;
        public long DegenerateRemoved;
        public int Fallbacks;      // piezas donde el decimado se descartó por romper la malla
        public int Retries;        // piezas que necesitaron un decimado más conservador
        public int Discarded;      // piezas sin geometría útil
        public int NotDecimated;   // piezas chicas (cubos/prismas) que se dejaron intactas
        public int ProtectedCat;   // piezas intactas por categoría protegida (muros/suelos/techos)
        public int Transparent;    // materiales con alpha (vidrios)
        public int Instanced;      // piezas que reusan la malla de otra idéntica (GLB más chico)
        public int Repaired;       // piezas rotas reparadas restaurando su geometría original
        public int PoolMeshes;     // geometrías únicas en el .tbv tras deduplicar
        public long TbvBytes;      // tamaño del binario de visor
        public int HatchedMats;    // materiales con patrón de superficie (hatch) en el .tbv
    }

    public class PiezaRota
    {
        /// <summary>Clave de agrupación (GUID|MaterialId), para localizar la pieza.</summary>
        public string Clave;
        /// <summary>GUID de Revit, para mostrar en el reporte.</summary>
        public string Guid;
        public int ElementId;
        public string Problema;
        public string Detalle;
    }

    class Program
    {
        // "TBT2" en little-endian: cabecera del formato v2 (con color de material)
        const int FormatMagic = 0x32544254;

        // Conversión para archivos <= v4, que traían las coordenadas en pies de Revit.
        const double PiesAMetros = 0.3048;

        // Id de BuiltInCategory de las habitaciones (OST_Rooms).
        const int CatRooms = -2000160;

        // Tolerancia de soldadura: grilla de 1 mm
        const double WeldGrid = 1000.0;
        // Un spike existe si la caja del decimado se sale de la original más de este % de su diagonal
        const float SpikeTolerance = 0.005f;
        // Guarda anti-cubos: un cubo soldado tiene 8 vértices / 12 triángulos.
        // Solo se decima con margen de seguridad por encima de eso — el decimado se aplica
        // únicamente cuando es necesario, no a todas las piezas.
        const int MinVertsParaDecimar = 16;
        const int MinTrisParaDecimar = 20;

        // ===== CATEGORÍAS PROTEGIDAS =====
        // Piezas de estas categorías NO se decimán ni descomponen: por naturaleza ya vienen
        // optimizadas de Revit y son las que más feo se rompen. Solo se les aplica soldadura
        // y limpieza de degenerados (sin pérdida). Ids de BuiltInCategory de Revit.
        // Agregar/quitar categorías acá según necesidad.
        static readonly HashSet<int> CategoriasProtegidas = new HashSet<int>
        {
            -2000011, // OST_Walls   (Muros)
            -2000032, // OST_Floors  (Suelos)
            -2000035, // OST_Roofs   (Techos)
            -2000014, // OST_Windows (Ventanas: el decimado rompe los paños)
            CatRooms, // OST_Rooms   (Habitaciones — geometría plana, no decimar)
        };

        // El decimado destroza los paños de vidrio (mallas casi planas y finas).
        // Un vidrio queda protegido si es translúcido (alpha < 255) o si es un
        // panel casi negro sin categoría/ventana — el patrón de un paño cuyo
        // asset de apariencia no llegó al exportador.
        static bool EsVidrioProtegido(MeshData m)
        {
            if (m.HasColor && m.ColA < 255) return true;
            bool casiNegro = m.HasColor && m.ColR < 70 && m.ColG < 70 && m.ColB < 70;
            return casiNegro && (m.CategoryId == 0 || m.CategoryId == -2000014 || m.CategoryId == -2000023);
        }

        private const string APP_GUID = "Global\\RevitGeometriaWatcher_JPG_2024";
        private static string CARPETA_TEMP = System.IO.Path.Combine(System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "MIP"), "Temp");
        private static string RUTA_BASE_SALIDA = @"C:\NO ENTRAR\JPG\DATA";
        private static string RUTA_LOG = "";

        // Máximo de reintentos antes de dar un .bin por perdido definitivamente.
        const int MAX_REINTENTOS_FALLIDOS = 2;

        static void Main(string[] args)
        {
            if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
            {
                // La carpeta Temp se DERIVA del plugin folder que pasa el addin, en vez de estar
                // hardcodeada. Si en una máquina el config.json tiene otro BasePluginFolder/TempFolderPath
                // (el instalador preserva configs viejos), el addin escribía los .bin en una carpeta que
                // este watcher jamás miraba: los .tbv y los JSON de habitaciones no aparecían nunca y no
                // había ningún error visible en ningún lado.
                CARPETA_TEMP = Path.Combine(args[0], "Temp");
                _basePluginFolder = args[0];   // T07: ProgresoLotes lee tomados_*.txt de aca
                _sufijoMutex = SufijoMutexInstalacion(args[0]);   // T14: build lado a lado
                RUTA_LOG = Path.Combine(args[0], "Logs", $"Log_Geometria_{DateTime.Now:yyyy-MM-dd}.txt");
            }
            else
            {
                RUTA_LOG = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"Log_Geometria_{DateTime.Now:yyyy-MM-dd}.txt");
            }

            if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
            {
                RUTA_BASE_SALIDA = Path.Combine(args[1], "DATA");
            }

            Console.OutputEncoding = Encoding.UTF8;
            Log("=== POST-PROCESO DE GEOMETRIAS - INICIANDO ===");

            bool createdNew;
            using (Mutex mutex = new Mutex(true, APP_GUID + _sufijoMutex, out createdNew))
            {
                if (!createdNew)
                {
                    Log("Ya hay una instancia corriendo. Cerrando esta.");
                    return; // Ya hay una instancia corriendo
                }

                if (!Directory.Exists(CARPETA_TEMP)) Directory.CreateDirectory(CARPETA_TEMP);
                if (!Directory.Exists(RUTA_BASE_SALIDA)) Directory.CreateDirectory(RUTA_BASE_SALIDA);
                try { Directory.CreateDirectory(Path.GetDirectoryName(RUTA_LOG)); } catch { }

                Consola.Encabezado(
                    "POST-PROCESO DE GEOMETRIAS",
                    "Suelda y diezma (QEM) la geometria que vuelca Revit, valida y repara las piezas rotas, y publica el .tbv del visor 3D mas el JSON de habitaciones.",
                    new[]
                    {
                        new[] { "Entrada", CARPETA_TEMP,      "|"  },
                        new[] { "Salida",  RUTA_BASE_SALIDA,  "F2" },
                        new[] { "Log",     RUTA_LOG,          "L"  }
                    });
                Log("TEMP: " + CARPETA_TEMP + " | DATA: " + RUTA_BASE_SALIDA);

                // Al arrancar se reinyectan los .bin apartados que todavía tienen reintentos: un fallo
                // transitorio (archivo a medio escribir porque Revit murió, pico de memoria) dejaba el
                // proyecto sin .tbv ni JSON de habitaciones PARA SIEMPRE, sin reintento ni aviso.
                ReintentarFallidos();
                DateTime ultimaPasadaFallidos = DateTime.Now;

                while (true)
                {
                    if (Directory.Exists(CARPETA_TEMP))
                    {
                        string[] colaArchivos = Directory.GetFiles(CARPETA_TEMP, "*.bin");

                        foreach (string filePath in colaArchivos)
                        {
                            if (EstaBloqueado(filePath)) continue;

                            ProcesarArchivo(filePath);
                        }
                    }

                    // Barrido periódico de apartados: el exe es perpetuo, así que esperar al próximo
                    // arranque para reintentar puede ser esperar días.
                    if ((DateTime.Now - ultimaPasadaFallidos).TotalMinutes >= 10)
                    {
                        ReintentarFallidos();
                        ultimaPasadaFallidos = DateTime.Now;
                    }

                    Consola.Esperando(0);
                    Consola.Esperando(0);
                    Thread.Sleep(500); // Pausa de escucha
                }
            }
        }

        // T14 (build lado a lado MIP_UNBOX): si la carpeta personal que pasa el addin (args[0]) NO es %LOCALAPPDATA%\MIP, es una
        // instalacion de prueba y el mutex lleva un sufijo propio, asi este exe no pelea la instancia unica con el watcher productivo
        // (su cola Temp ya sale de args[0]). En produccion (args[0] == %LOCALAPPDATA%\MIP) el sufijo es "" y el nombre no cambia.
        // Misma formula que CarpetaPersonalMip.SufijoMutex (R_01).
        private static string _sufijoMutex = "";
        private static string SufijoMutexInstalacion(string basePluginFolder)
        {
            try
            {
                string real = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MIP")).TrimEnd('\\');
                string p = Path.GetFullPath(basePluginFolder).TrimEnd('\\');
                if (string.Equals(p, real, StringComparison.OrdinalIgnoreCase)) return "";
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(p.ToLowerInvariant()));
                    return "_" + h[0].ToString("x2") + h[1].ToString("x2") + h[2].ToString("x2") + h[3].ToString("x2");
                }
            }
            catch { return ""; }
        }

        // T07: avance del lote "[N/M] hechos H ..." (ProgresoLotes.cs, copia de R_01/tools/_compartido). Solo informativo:
        // cualquier falla => "" y el exe sigue exactamente igual.
        private static string _basePluginFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MIP");
        private static string LineaProgreso(string nombreProyecto)
        {
            try
            {
                int bins = -1;
                try { bins = Directory.GetFiles(CARPETA_TEMP, "*.bin").Length; } catch { }
                return UnboxCompartido.ProgresoLotes.LineaPara(_basePluginFolder, nombreProyecto, bins);
            }
            catch { return ""; }
        }

        private static bool EstaBloqueado(string path)
        {
            try
            {
                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) return false;
            }
            catch { return true; }
        }

        private static void ProcesarArchivo(string filePath)
        {
            int intentoIgnorado0;
            string nombreProyecto = SepararNombreEIntento(
                Path.GetFileNameWithoutExtension(filePath), out intentoIgnorado0).Replace("_Geometria", "");

            Log($"===== Procesando: {Path.GetFileName(filePath)} =====");
            Consola.Detalle(LineaProgreso(nombreProyecto));   // T07: [N/M] del lote
            Consola.FilaInicio(nombreProyecto, "leyendo dump");

            try
            {
                var swFile = Stopwatch.StartNew();
                var stats = new PipelineStats();

                Log($"Leyendo archivo: {filePath}");
                List<MeshData> meshes = LeerBinario(filePath, stats);
                Log($"Formato v{stats.FormatVersion}{(stats.FormatVersion >= 2 ? " (con color de material)" : " (legado, colores aleatorios)")}. Se leyeron {meshes.Count} mallas.");

                // Los porcentajes son las etapas del pipeline, no un conteo de piezas: no hay una
                // unidad de avance comun entre soldar, diezmar, validar y escribir.
                Consola.FilaProgreso(20, "soldando y diezmando");
                var weldedOriginals = new Dictionary<string, MeshData>();
                List<MeshData> optimizedMeshes = OptimizeMeshes(meshes, stats, weldedOriginals);
                Log($"Geometría reducida a {optimizedMeshes.Count} piezas separadas.");

                Consola.FilaProgreso(55, "validando piezas");
                var rotasDetectadas = ValidarPiezas(weldedOriginals, optimizedMeshes);

                if (rotasDetectadas.Count > 0)
                {
                    Log($"Se detectaron {rotasDetectadas.Count} piezas rotas. Reparando...");
                    RepararPiezas(rotasDetectadas, weldedOriginals, optimizedMeshes, stats);
                }

                var rotasFinales = rotasDetectadas.Count > 0
                    ? ValidarPiezas(weldedOriginals, optimizedMeshes)
                    : rotasDetectadas;

                stats.VertsOut = optimizedMeshes.Sum(m => (long)m.Vertices.Count);
                stats.TrisOut = optimizedMeshes.Sum(m => (long)m.Indices.Count / 3);
                stats.OutputPieces = optimizedMeshes.Count;

                // Se saca el sufijo "__intentoN" que agrega el reintento de apartados: sin esto, un
                // archivo reinyectado escribiría "SO_DU__intento1_Geometria.tbv" en vez de pisar el
                // nombre real, y el visor nunca encontraría el archivo del proyecto.
                int intentoIgnorado;
                string baseName = SepararNombreEIntento(
                    Path.GetFileNameWithoutExtension(filePath), out intentoIgnorado).Replace("_Geometria", "");

                // Las habitaciones se exportan PRIMERO: son un JSON chico e independiente, y si
                // el escritor del .tbv falla no tiene por qué llevárselas puestas.
                string roomsPath = Path.Combine(RUTA_BASE_SALIDA, baseName + "_Geometria_Habitaciones.json");
                Consola.FilaProgreso(75, "exportando ambientes");
                Log($"Exportando data de habitaciones: {roomsPath}");
                ExportRoomsJson(optimizedMeshes, roomsPath, stats);

                string tbvPath = Path.Combine(RUTA_BASE_SALIDA, baseName + "_Geometria.tbv");

                Consola.FilaProgreso(90, "escribiendo tbv");
                Log($"Exportando binario de visor (dedup + índice espacial): {tbvPath}");
                ExportToViewerBin(optimizedMeshes, tbvPath, stats);

                string reportPath = Path.Combine(RUTA_BASE_SALIDA, baseName + "_Geometria_reporte.txt");

                swFile.Stop();
                string reporte = ConstruirReporte(stats, rotasDetectadas, rotasFinales, swFile.Elapsed);
                Log(reporte);
                File.WriteAllText(reportPath, reporte);
                Log($"Reporte guardado en: {reportPath}");

                Consola.Detalle(LineaProgreso(nombreProyecto));   // T07
                Consola.FilaFin(nombreProyecto, swFile.Elapsed, true,
                    $"{stats.OutputPieces} piezas" + (rotasFinales.Count > 0 ? $", {rotasFinales.Count} rota(s)" : ""));

                if (File.Exists(filePath)) File.Delete(filePath);
            }
            catch (Exception ex)
            {
                Log(" !!! FALLA: " + ex.Message + "\n" + ex.StackTrace);
                Consola.Detalle(LineaProgreso(nombreProyecto));   // T07
                Consola.FilaFin(nombreProyecto, TimeSpan.Zero, false, ex.Message);

                // El .bin es irreproducible sin volver a exportar desde Revit: en vez de
                // borrarlo se aparta, para poder diagnosticar y reprocesar.
                ApartarFallido(filePath);
            }
            finally
            {
                LiberarMemoriaEntreModelos();
            }
        }

        // Este proceso es un watcher perpetuo y las mallas son arrays > 85 KB: viven en el LOH,
        // que el GC no compacta solo. Sin esto el proceso queda en reposo con el residuo del
        // último modelo grande (medido: ~600 MB tras un IS de 8 M de vértices).
        private static void LiberarMemoriaEntreModelos()
        {
            long antes = GC.GetTotalMemory(false);
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long despues = GC.GetTotalMemory(false);
            Log($"[MEM] Managed: {antes / 1048576} MB -> {despues / 1048576} MB | Privada: {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} MB");
        }

        private static string CarpetaFallidos => Path.Combine(CARPETA_TEMP, "_Fallidos");

        // Mueve un .bin que no se pudo procesar a "_Fallidos" junto al TEMP, para que el
        // watcher no lo reintente en bucle pero tampoco se pierda. El nombre lleva el número de
        // intento (__intentoN) para poder reinyectarlo después: sin contador, o se reintenta para
        // siempre en bucle, o —como pasaba antes— no se reintenta nunca y el proyecto queda sin
        // .tbv y sin JSON de habitaciones de forma permanente y silenciosa.
        private static void ApartarFallido(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                Directory.CreateDirectory(CarpetaFallidos);

                int intento;
                string baseName = SepararNombreEIntento(Path.GetFileNameWithoutExtension(filePath), out intento);
                intento++;

                string destino = Path.Combine(CarpetaFallidos, $"{baseName}__intento{intento}.bin");
                if (File.Exists(destino)) File.Delete(destino);
                File.Move(filePath, destino);

                if (intento > MAX_REINTENTOS_FALLIDOS)
                {
                    Log($"!!! DESCARTADO DEFINITIVAMENTE tras {intento} intentos: {destino}");
                    Log($"!!! El proyecto '{baseName}' NO tiene .tbv ni JSON de habitaciones actualizados. " +
                        "Hay que volver a exportarlo desde Revit.");
                }
                else
                {
                    Log($"Archivo apartado (intento {intento}/{MAX_REINTENTOS_FALLIDOS}), se reintentará: {destino}");
                }
            }
            catch (Exception ex)
            {
                Log("No se pudo apartar el archivo fallido: " + ex.Message);
                // Último recurso: borrarlo, o el watcher entra en bucle infinito sobre él.
                try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            }
        }

        // Devuelve el nombre base sin el sufijo "__intentoN" y saca por 'out' el N encontrado (0 si no había).
        private static string SepararNombreEIntento(string nombreSinExtension, out int intento)
        {
            intento = 0;
            const string marca = "__intento";

            int pos = nombreSinExtension.LastIndexOf(marca, StringComparison.Ordinal);
            if (pos < 0) return nombreSinExtension;

            string cola = nombreSinExtension.Substring(pos + marca.Length);
            int parsed;
            if (!int.TryParse(cola, out parsed)) return nombreSinExtension;

            intento = parsed;
            return nombreSinExtension.Substring(0, pos);
        }

        // Devuelve a la cola los .bin apartados que todavía tienen reintentos disponibles.
        // Los que agotaron los intentos se dejan quietos en _Fallidos para diagnóstico.
        private static void ReintentarFallidos()
        {
            try
            {
                if (!Directory.Exists(CarpetaFallidos)) return;

                foreach (string apartado in Directory.GetFiles(CarpetaFallidos, "*.bin"))
                {
                    int intento;
                    string baseName = SepararNombreEIntento(Path.GetFileNameWithoutExtension(apartado), out intento);

                    if (intento > MAX_REINTENTOS_FALLIDOS) continue; // ya se descartó, no insistir

                    // Vuelve a la cola conservando el contador en el nombre, para que un nuevo fallo
                    // lo incremente en vez de reiniciar el ciclo desde cero.
                    string destino = Path.Combine(CARPETA_TEMP, $"{baseName}__intento{intento}.bin");
                    if (File.Exists(destino)) continue; // ya hay uno en cola para ese proyecto

                    File.Move(apartado, destino);
                    Log($"Reinyectado a la cola (intento {intento + 1}/{MAX_REINTENTOS_FALLIDOS}): {Path.GetFileName(destino)}");
                }
            }
            catch (Exception ex)
            {
                Log("No se pudieron reintentar los archivos apartados: " + ex.Message);
            }
        }

        /// <summary>
        /// Traza al ARCHIVO unicamente. La consola no la escribe esto: la dibuja Consola, que
        /// mantiene un renglon por proyecto. Antes cada Log() ademas escupia su linea a la
        /// consola, y una corrida dejaba decenas de renglones de detalle interno por proyecto
        /// entre los que no se encontraba el estado real de ninguno.
        /// </summary>
        private static void Log(string msg)
        {
            try
            {
                if (!string.IsNullOrEmpty(RUTA_LOG))
                {
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    File.AppendAllText(RUTA_LOG, $"[{timestamp}] {msg}\n");
                }
            }
            catch { }
        }

        static string ConstruirReporte(PipelineStats s, List<PiezaRota> detectadas, List<PiezaRota> rotas, TimeSpan elapsed)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("=========== ESTADÍSTICAS ===========");
            sb.AppendLine($"Formato de entrada:     v{s.FormatVersion} ({s.UnidadOrigen})");
            sb.AppendLine($"Punto Base (m):         ({s.PuntoBase.X:F3}, {s.PuntoBase.Y:F3}, {s.PuntoBase.Z:F3})");
            sb.AppendLine($"Mallas de entrada:      {s.InputMeshes}");
            sb.AppendLine($"Habitaciones exportadas: {s.Rooms}" +
                          (s.RoomsSinCentro > 0 ? $"  (¡{s.RoomsSinCentro} sin centroide, omitidas!)" : ""));
            sb.AppendLine($"Piezas de salida:       {s.OutputPieces} (descartadas vacías: {s.Discarded})");
            sb.AppendLine($"Vértices:  {s.VertsIn:N0} -> soldados {s.VertsWelded:N0} -> finales {s.VertsOut:N0}  ({Pct(s.VertsOut, s.VertsIn)})");
            sb.AppendLine($"Triángulos: {s.TrisIn:N0} -> finales {s.TrisOut:N0}  ({Pct(s.TrisOut, s.TrisIn)})");
            sb.AppendLine($"Triángulos degenerados eliminados: {s.DegenerateRemoved:N0}");
            sb.AppendLine($"Piezas chicas sin decimar (cubos/prismas protegidos): {s.NotDecimated}");
            sb.AppendLine($"Piezas intactas por categoría protegida (muros/suelos/techos): {s.ProtectedCat}");
            sb.AppendLine($"Piezas con reintento conservador:  {s.Retries}");
            sb.AppendLine($"Piezas con fallback (sin decimar): {s.Fallbacks}");
            sb.AppendLine($"Piezas rotas detectadas y REPARADAS con geometría original: {s.Repaired}");
            sb.AppendLine($"Materiales transparentes (vidrio): {s.Transparent}");
            sb.AppendLine($"Piezas instanciadas (malla compartida): {s.Instanced}");
            sb.AppendLine($"Geometrías únicas en el .tbv (dedup): {s.PoolMeshes} de {s.OutputPieces} piezas");
            sb.AppendLine($"Materiales con patrón de superficie (hatch de muros): {s.HatchedMats}" +
                          (s.HatchedMats == 0 && s.FormatVersion >= 6
                              ? " -- ningún material de muro tenía un patrón de MODELO utilizable"
                              : ""));
            if (s.TbvBytes > 0) sb.AppendLine($"Binario de visor (.tbv): {s.TbvBytes / 1024:N0} KB");

            sb.AppendLine($"Tiempo total: {elapsed.TotalSeconds:F1} s");
            sb.AppendLine("====================================");
            sb.AppendLine();

            if (detectadas.Count > 0)
            {
                sb.AppendLine($"Piezas rotas detectadas en la primera validación ({detectadas.Count}):");
                foreach (var r in detectadas)
                {
                    sb.AppendLine($"  - ElementId {r.ElementId} | GUID {r.Guid} | {r.Problema}");
                }
                sb.AppendLine();
            }

            if (rotas.Count == 0)
            {
                sb.AppendLine("VALIDACIÓN FINAL: OK — ninguna pieza rota en la salida.");
            }
            else
            {
                sb.AppendLine($"VALIDACIÓN FINAL: ¡ATENCIÓN! {rotas.Count} pieza(s) siguen con problemas tras reparar:");
                foreach (var r in rotas)
                {
                    sb.AppendLine($"  - ElementId {r.ElementId} | GUID {r.Guid}");
                    sb.AppendLine($"    Problema: {r.Problema}");
                    sb.AppendLine($"    Detalle:  {r.Detalle}");
                }
            }

            return sb.ToString();
        }

        static string Pct(long now, long before) =>
            before == 0 ? "n/a" : $"{100.0 * now / before:F1}% del original";

        // ================== VALIDACIÓN FINAL ==================
        // Chequeo independiente del pipeline: compara cada pieza final contra su original
        // soldado. Detecta piezas perdidas, NaN, spikes (vértices fuera de caja), colapsos
        // y pérdida/ganancia de superficie. Reporta GUID, ElementId y los vectores implicados.
        static List<PiezaRota> ValidarPiezas(Dictionary<string, MeshData> originales, List<MeshData> finales)
        {
            var rotas = new List<PiezaRota>();
            var finalesPorClave = finales.ToDictionary(m => m.PieceKey);

            foreach (var kv in originales)
            {
                var orig = kv.Value;
                if (orig.Indices.Count < 3) continue; // no había geometría útil de entrada

                if (!finalesPorClave.TryGetValue(kv.Key, out var fin))
                {
                    rotas.Add(new PiezaRota
                    {
                        Clave = kv.Key,
                        Guid = orig.Guid,
                        ElementId = orig.ElementId,
                        Problema = "PIEZA PERDIDA: existía en la entrada y no está en la salida",
                        Detalle = $"original: {orig.Vertices.Count} verts, {orig.Indices.Count / 3} tris, bbox {BBoxStr(orig.Vertices)}"
                    });
                    continue;
                }

                // 1. NaN / Infinity
                var nanVerts = new List<string>();
                for (int i = 0; i < fin.Vertices.Count && nanVerts.Count < 3; i++)
                {
                    var v = fin.Vertices[i];
                    if (float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z) ||
                        float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z))
                    {
                        nanVerts.Add($"v[{i}]=({v.X}, {v.Y}, {v.Z})");
                    }
                }
                if (nanVerts.Count > 0)
                {
                    rotas.Add(new PiezaRota
                    {
                        Clave = kv.Key,
                        Guid = orig.Guid,
                        ElementId = fin.ElementId,
                        Problema = "VÉRTICES NaN/INFINITY",
                        Detalle = string.Join("; ", nanVerts)
                    });
                    continue;
                }

                GetBounds(orig.Vertices, out var oMin, out var oMax);
                GetBounds(fin.Vertices, out var fMin, out var fMax);
                float diag = (oMax - oMin).Length();
                float tol = diag * SpikeTolerance + 1e-4f;

                // 2. Spike: vértices finales fuera de la caja original
                var spikes = new List<string>();
                for (int i = 0; i < fin.Vertices.Count && spikes.Count < 3; i++)
                {
                    var v = fin.Vertices[i];
                    if (v.X < oMin.X - tol || v.Y < oMin.Y - tol || v.Z < oMin.Z - tol ||
                        v.X > oMax.X + tol || v.Y > oMax.Y + tol || v.Z > oMax.Z + tol)
                    {
                        spikes.Add($"v[{i}]=({v.X:F4}, {v.Y:F4}, {v.Z:F4})");
                    }
                }
                if (spikes.Count > 0)
                {
                    rotas.Add(new PiezaRota
                    {
                        Clave = kv.Key,
                        Guid = orig.Guid,
                        ElementId = fin.ElementId,
                        Problema = "SPIKE: vértices fuera del volumen original",
                        Detalle = $"bbox original {BBoxStr(orig.Vertices)}; vértices fuera: {string.Join("; ", spikes)}"
                    });
                    continue;
                }

                // 3. Colapso: la pieza final quedó mucho más chica que la original
                float finDiag = (fMax - fMin).Length();
                if (diag > 1e-4f && finDiag < diag * 0.5f)
                {
                    rotas.Add(new PiezaRota
                    {
                        Clave = kv.Key,
                        Guid = orig.Guid,
                        ElementId = fin.ElementId,
                        Problema = "COLAPSO: la pieza se encogió más del 50%",
                        Detalle = $"bbox original {BBoxStr(orig.Vertices)} (diag {diag:F3}) -> final {BBoxStr(fin.Vertices)} (diag {finDiag:F3})"
                    });
                    continue;
                }

                // 4. Superficie: agujeros grandes o geometría duplicada
                double aOrig = TotalArea(orig);
                double aFin = TotalArea(fin);
                if (aOrig > 1e-10 && (aFin < aOrig * 0.6 || aFin > aOrig * 1.4))
                {
                    rotas.Add(new PiezaRota
                    {
                        Clave = kv.Key,
                        Guid = orig.Guid,
                        ElementId = fin.ElementId,
                        Problema = "ÁREA ANÓMALA: superficie cambió más de ±40% (agujeros o colapso)",
                        Detalle = $"área original {aOrig:F4} -> final {aFin:F4} (ratio {aFin / aOrig:F2})"
                    });
                }
            }

            return rotas;
        }

        // ================== REPARACIÓN ==================
        // Toda pieza detectada como rota se restaura a su geometría original soldada
        // (weld + limpieza de degenerados, sin decimar). Si la pieza se perdió, se re-agrega.
        static void RepararPiezas(List<PiezaRota> rotas, Dictionary<string, MeshData> originales,
                                  List<MeshData> finales, PipelineStats stats)
        {
            foreach (var rota in rotas)
            {
                if (!originales.TryGetValue(rota.Clave, out var orig)) continue;
                if (orig.Indices.Count < 3 && orig.CategoryId != CatRooms) continue;

                var reparada = ClonarConNormales(orig);

                int idx = finales.FindIndex(m => m.PieceKey == rota.Clave);
                if (idx >= 0) finales[idx] = reparada;
                else finales.Add(reparada);

                stats.Repaired++;
                // Al log, no a la consola: partiria el renglon del proyecto en curso. El conteo
                // de reparadas sale igual en el reporte y en el detalle del renglon.
                Log($"  Reparada: ElementId {rota.ElementId} | {rota.Problema}");
            }
        }

        static MeshData ClonarConNormales(MeshData src)
        {
            var clon = new MeshData
            {
                Guid = src.Guid,
                ElementId = src.ElementId,
                MaterialId = src.MaterialId,
                CategoryId = src.CategoryId,
                ColR = src.ColR, ColG = src.ColG, ColB = src.ColB, ColA = src.ColA,
                HasColor = src.HasColor,
                HatchAngle = src.HatchAngle, HatchSpacing = src.HatchSpacing,
                FamilyName = src.FamilyName, TypeName = src.TypeName,
                RoomLevel = src.RoomLevel,
                RoomDepartment = src.RoomDepartment,
                RoomName = src.RoomName,
                Vertices = new List<Vector3>(src.Vertices),
                Indices = new List<int>(src.Indices)
            };
            clon.CopiarPropiedadesDe(src);
            ComputeNormals(clon);
            return clon;
        }

        static string BBoxStr(List<Vector3> verts)
        {
            GetBounds(verts, out var min, out var max);
            return $"min({min.X:F3}, {min.Y:F3}, {min.Z:F3}) max({max.X:F3}, {max.Y:F3}, {max.Z:F3})";
        }

        // ================== OPTIMIZACIÓN ==================

        static List<MeshData> OptimizeMeshes(List<MeshData> inputMeshes, PipelineStats stats,
                                             Dictionary<string, MeshData> weldedOriginals)
        {
            stats.InputMeshes = inputMeshes.Count;
            // Agrupado por (GUID, MaterialId): un elemento con varios materiales produce una
            // pieza por material, cada una con su color y su protección de vidrio correctas.
            var grouped = inputMeshes.GroupBy(m => m.PieceKey);
            var optimizedList = new List<MeshData>();

            foreach (var group in grouped)
            {
                var first = group.First();
                var mergedMesh = new MeshData
                {
                    Guid = first.Guid,
                    ElementId = first.ElementId,
                    MaterialId = first.MaterialId,
                    CategoryId = first.CategoryId,
                    ColR = first.ColR, ColG = first.ColG, ColB = first.ColB, ColA = first.ColA,
                    HasColor = first.HasColor,
                    // Todas las caras del grupo comparten (GUID, MaterialId), así que comparten
                    // material y por lo tanto patrón: el de la primera vale para la pieza entera.
                    HatchAngle = first.HatchAngle, HatchSpacing = first.HatchSpacing,
                    // Todas las caras del grupo son del MISMO elemento (el PieceKey empieza por
                    // el GUID), así que familia y tipo de la primera valen para la pieza entera.
                    FamilyName = first.FamilyName, TypeName = first.TypeName,
                    // Sin esto la metadata de habitación se pierde acá y el JSON sale con
                    // Level/Department/Name vacíos (el visor descarta las que no tienen Level).
                    RoomLevel = first.RoomLevel,
                    RoomDepartment = first.RoomDepartment,
                    RoomName = first.RoomName
                };
                mergedMesh.CopiarPropiedadesDe(first); // v8: nivel, área, longitud, volumen

                int vertexOffset = 0;
                foreach (var mesh in group)
                {
                    mergedMesh.Vertices.AddRange(mesh.Vertices);
                    foreach (var index in mesh.Indices)
                    {
                        mergedMesh.Indices.Add(index + vertexOffset);
                    }
                    vertexOffset += mesh.Vertices.Count;
                    // Ya se copió a mergedMesh y nadie vuelve a leer la malla de entrada; soltar las
                    // listas acá evita tener el dump entero vivo junto a su copia fusionada
                    // (~220 MB en un modelo de instalaciones de 8 M de vértices).
                    mesh.Vertices = null;
                    mesh.Indices = null;
                }

                stats.VertsIn += mergedMesh.Vertices.Count;
                stats.TrisIn += mergedMesh.Indices.Count / 3;

                // 1. Soldar vértices duplicados (crucial para que QEM no rompa la malla)
                WeldVertices(mergedMesh);

                // 2. Eliminar triángulos degenerados que la soldadura pudo crear:
                //    alimentar QEM con caras de área cero es la causa típica de mallas rotas.
                stats.DegenerateRemoved += RemoveDegenerateTriangles(mergedMesh);
                CompactVertices(mergedMesh);
                stats.VertsWelded += mergedMesh.Vertices.Count;

                weldedOriginals[group.Key] = mergedMesh;

                if (mergedMesh.Indices.Count < 3 && mergedMesh.CategoryId != CatRooms)
                {
                    stats.Discarded++;
                    continue;
                }

                int originalTriangles = mergedMesh.Indices.Count / 3;

                // Categorías protegidas (muros/suelos/techos): NO se decimán nunca.
                // Ya vienen optimizadas de Revit; solo reciben soldadura y limpieza (sin pérdida).
                bool protegidaPorCategoria = CategoriasProtegidas.Contains(mergedMesh.CategoryId)
                                             || EsVidrioProtegido(mergedMesh);

                // Guarda anti-cubos: piezas chicas (cubos, prismas, detalles) NO se decimán.
                bool decimable = !protegidaPorCategoria &&
                                 mergedMesh.Vertices.Count >= MinVertsParaDecimar &&
                                 originalTriangles > MinTrisParaDecimar;

                if (protegidaPorCategoria) stats.ProtectedCat++;

                MeshData outMesh = mergedMesh;
                if (decimable)
                {
                    int targetTriangles = ComputeTarget(originalTriangles);
                    if (targetTriangles < originalTriangles)
                    {
                        // 3. Decimar con validación: si el resultado tiene spikes o queda vacío,
                        //    reintentar más conservador y, si aun así falla, conservar la malla soldada.
                        var decimated = TryDecimate(mergedMesh, targetTriangles, stats);
                        if (decimated == null && targetTriangles < originalTriangles / 2)
                        {
                            stats.Retries++;
                            decimated = TryDecimate(mergedMesh, originalTriangles / 2, stats);
                        }

                        if (decimated != null)
                        {
                            outMesh = decimated;
                        }
                        else
                        {
                            stats.Fallbacks++;
                        }
                    }
                }
                else if (!protegidaPorCategoria)
                {
                    stats.NotDecimated++;
                }

                if (outMesh.Indices.Count < 3 && outMesh.CategoryId != CatRooms)
                {
                    stats.Discarded++;
                    continue;
                }

                // outMesh comparte lista con mergedMesh cuando no se decimó: clonar para
                // que la validación final compare contra un original intacto.
                if (ReferenceEquals(outMesh, mergedMesh))
                {
                    outMesh = ClonarConNormales(mergedMesh);
                }
                else
                {
                    ComputeNormals(outMesh);
                }
                stats.VertsOut += outMesh.Vertices.Count;
                stats.TrisOut += outMesh.Indices.Count / 3;
                optimizedList.Add(outMesh);
            }

            stats.OutputPieces = optimizedList.Count;
            return optimizedList;
        }

        // Presupuesto CONSERVADOR: preferimos triángulos de más antes que piezas rotas.
        // Muros/suelos/techos ni siquiera llegan acá (categorías protegidas); esto aplica
        // a puertas, ventanas, mobiliario, columnas, sanitarios, etc.
        static int ComputeTarget(int originalTriangles)
        {
            if (originalTriangles > 10000) return (int)(originalTriangles * 0.25);
            if (originalTriangles > 2000) return (int)(originalTriangles * 0.35);
            if (originalTriangles > 500) return (int)(originalTriangles * 0.50);
            if (originalTriangles > 100) return (int)(originalTriangles * 0.65);
            return Math.Max((int)(originalTriangles * 0.80), 16);
        }

        // Devuelve la malla decimada y saneada, o null si el resultado no es confiable.
        static MeshData TryDecimate(MeshData source, int targetTriangles, PipelineStats stats)
        {
            var mdVertices = new MeshDecimator.Math.Vector3d[source.Vertices.Count];
            for (int i = 0; i < source.Vertices.Count; i++)
            {
                mdVertices[i] = new MeshDecimator.Math.Vector3d(
                    source.Vertices[i].X,
                    source.Vertices[i].Y,
                    source.Vertices[i].Z
                );
            }

            var srcMesh = new MeshDecimator.Mesh(mdVertices, source.Indices.ToArray());

            var algorithm = new MeshDecimator.Algorithms.FastQuadricMeshSimplification();
            algorithm.PreserveBorders = true;
            algorithm.PreserveSeams = true;
            algorithm.PreserveFoldovers = true; // Previene triángulos invertidos (sombras negras)
            algorithm.EnableSmartLink = false;  // No conectar muros interiores/exteriores (spikes)

            MeshDecimator.Mesh decimatedMdMesh;
            try
            {
                decimatedMdMesh = MeshDecimator.MeshDecimation.DecimateMesh(algorithm, srcMesh, targetTriangles);
            }
            catch
            {
                return null;
            }

            var outMesh = new MeshData
            {
                Guid = source.Guid,
                ElementId = source.ElementId,
                MaterialId = source.MaterialId,
                // CategoryId se perdía acá: toda pieza decimada (puertas, ventanas, mobiliario —
                // muros y losas ni llegan, están protegidos) salía al .tbv con categoría 0. En el
                // visor eso rompe el filtro por categoría y la transparencia de puertas, que se
                // deciden justamente por catId. Los muros no dependían de esto, pero el hatch se
                // enciende por categoría, así que el bug tenía que irse antes de apoyarse ahí.
                CategoryId = source.CategoryId,
                ColR = source.ColR, ColG = source.ColG, ColB = source.ColB, ColA = source.ColA,
                HasColor = source.HasColor,
                HatchAngle = source.HatchAngle, HatchSpacing = source.HatchSpacing,
                // Mismo riesgo que tuvo CategoryId acá: sin copiarlos, toda pieza decimada
                // (puertas, ventanas, mobiliario) sale al .tbv con familia/tipo -1.
                FamilyName = source.FamilyName, TypeName = source.TypeName
            };
            outMesh.CopiarPropiedadesDe(source);

            foreach (var v in decimatedMdMesh.Vertices)
            {
                if (double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z) ||
                    double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z))
                {
                    return null; // el decimado produjo basura numérica
                }
                outMesh.Vertices.Add(new Vector3((float)v.x, (float)v.y, (float)v.z));
            }
            outMesh.Indices.AddRange(decimatedMdMesh.Indices);

            stats.DegenerateRemoved += RemoveDegenerateTriangles(outMesh);
            CompactVertices(outMesh);

            if (outMesh.Indices.Count < 3) return null;

            // Validación anti-spike: la malla decimada no puede salirse de la caja original.
            GetBounds(source.Vertices, out var srcMin, out var srcMax);
            GetBounds(outMesh.Vertices, out var outMin, out var outMax);
            float tolerance = (srcMax - srcMin).Length() * SpikeTolerance + 1e-4f;
            if (outMin.X < srcMin.X - tolerance || outMin.Y < srcMin.Y - tolerance || outMin.Z < srcMin.Z - tolerance ||
                outMax.X > srcMax.X + tolerance || outMax.Y > srcMax.Y + tolerance || outMax.Z > srcMax.Z + tolerance)
            {
                return null; // se generó un spike fuera del volumen original
            }

            // Validación de área (estricta): si la superficie cambió más de ±30%,
            // el decimado colapsó la malla, abrió agujeros o plegó triángulos.
            double srcArea = TotalArea(source);
            double outArea = TotalArea(outMesh);
            if (srcArea > 1e-10 && (outArea < srcArea * 0.7 || outArea > srcArea * 1.3))
            {
                return null;
            }

            return outMesh;
        }

        static double TotalArea(MeshData mesh)
        {
            double area = 0;
            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                var a = mesh.Vertices[mesh.Indices[i]];
                var b = mesh.Vertices[mesh.Indices[i + 1]];
                var c = mesh.Vertices[mesh.Indices[i + 2]];
                area += Vector3.Cross(b - a, c - a).Length() * 0.5;
            }
            return area;
        }

        static void GetBounds(List<Vector3> verts, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            foreach (var v in verts)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        static void WeldVertices(MeshData mesh)
        {
            // Una pieza sin triángulos (habitaciones: 1 vértice = centroide, 0 caras) no tiene
            // nada que soldar. Sin este corte, el bucle de abajo — que recorre los ÍNDICES —
            // no itera nunca y deja mesh.Vertices vacío, destruyendo el centroide.
            if (mesh.Indices.Count == 0) return;

            var uniqueVertices = new List<Vector3>();
            // Clave por grilla de 1 mm con enteros (sin strings: más rápido y sin errores de formato)
            var vertexMap = new Dictionary<(long, long, long), int>(mesh.Vertices.Count);
            var newIndices = new List<int>(mesh.Indices.Count);

            foreach (var index in mesh.Indices)
            {
                if (index < 0 || index >= mesh.Vertices.Count) continue; // índice corrupto en el export
                var v = mesh.Vertices[index];
                var key = ((long)Math.Round(v.X * WeldGrid),
                           (long)Math.Round(v.Y * WeldGrid),
                           (long)Math.Round(v.Z * WeldGrid));

                if (!vertexMap.TryGetValue(key, out int newIndex))
                {
                    newIndex = uniqueVertices.Count;
                    uniqueVertices.Add(v);
                    vertexMap[key] = newIndex;
                }
                newIndices.Add(newIndex);
            }

            // Índices corruptos pueden dejar triángulos incompletos: recortar a múltiplo de 3
            int usable = newIndices.Count - (newIndices.Count % 3);
            if (usable != newIndices.Count) newIndices.RemoveRange(usable, newIndices.Count - usable);

            mesh.Vertices = uniqueVertices;
            mesh.Indices = newIndices;
        }

        // Elimina triángulos con índices repetidos o área nula. Devuelve cuántos se quitaron.
        static long RemoveDegenerateTriangles(MeshData mesh)
        {
            var newIndices = new List<int>(mesh.Indices.Count);
            long removed = 0;

            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
                if (a == b || b == c || a == c) { removed++; continue; }

                var cross = Vector3.Cross(mesh.Vertices[b] - mesh.Vertices[a],
                                          mesh.Vertices[c] - mesh.Vertices[a]);
                if (cross.LengthSquared() < 1e-14f) { removed++; continue; }

                newIndices.Add(a);
                newIndices.Add(b);
                newIndices.Add(c);
            }

            mesh.Indices = newIndices;
            return removed;
        }

        // Quita vértices que ningún triángulo referencia y reindexa.
        static void CompactVertices(MeshData mesh)
        {
            // Mismo motivo que en WeldVertices: sin índices, compactar borraría los vértices
            // que no referencia ningún triángulo — es decir, todos.
            if (mesh.Indices.Count == 0) return;

            var remap = new int[mesh.Vertices.Count];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;

            var newVertices = new List<Vector3>(mesh.Vertices.Count);
            for (int i = 0; i < mesh.Indices.Count; i++)
            {
                int old = mesh.Indices[i];
                if (remap[old] < 0)
                {
                    remap[old] = newVertices.Count;
                    newVertices.Add(mesh.Vertices[old]);
                }
                mesh.Indices[i] = remap[old];
            }

            mesh.Vertices = newVertices;
        }

        // Normales suavizadas ponderadas por área (el cross sin normalizar pesa por área:
        // caras grandes dominan y el sombreado queda más estable en muros).
        static void ComputeNormals(MeshData mesh)
        {
            mesh.Normals = new List<Vector3>(new Vector3[mesh.Vertices.Count]);

            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                int i0 = mesh.Indices[i];
                int i1 = mesh.Indices[i + 1];
                int i2 = mesh.Indices[i + 2];

                Vector3 faceNormal = Vector3.Cross(mesh.Vertices[i1] - mesh.Vertices[i0],
                                                   mesh.Vertices[i2] - mesh.Vertices[i0]);
                mesh.Normals[i0] += faceNormal;
                mesh.Normals[i1] += faceNormal;
                mesh.Normals[i2] += faceNormal;
            }

            for (int i = 0; i < mesh.Normals.Count; i++)
            {
                float len = mesh.Normals[i].Length();
                mesh.Normals[i] = len > 1e-10f ? mesh.Normals[i] / len : Vector3.UnitY;
            }
        }

        // ================== LECTURA ==================
        // v1 (legado): [elementId][guid][materialId][verts][tris]...
        // v2 ("TBT2"): cabecera [magic][version]; cada bloque agrega color RGBA del material.
        // v3: además agrega el id de BuiltInCategory (int32) después del MaterialId.
        // v4: cabecera con el Punto Base del Proyecto + metadata de habitaciones.
        // v5: las coordenadas vienen en METROS (v4 y anteriores venían en pies).
        // v6: cada bloque agrega el patrón de superficie (2 floats: ángulo rad, separación m)
        //     justo después del RGBA. Sólo los muros lo traen distinto de cero.
        // v7: cada bloque agrega DOS strings length-prefixed (BinaryWriter.Write(string)) con el
        //     nombre de Familia y de Tipo, justo DESPUÉS del categoryId y ANTES del RGBA.
        //     "" cuando el elemento no tiene ElementType (habitaciones, in-situ).
        static List<MeshData> LeerBinario(string path, PipelineStats stats)
        {
            var meshes = new List<MeshData>();

            using (var fs = File.OpenRead(path))
            using (var reader = new BinaryReader(fs))
            {
                bool v2 = false, v3 = false, v4 = false, v5 = false, v6 = false, v7 = false, v8 = false;
                if (fs.Length >= 8)
                {
                    int magic = reader.ReadInt32();
                    if (magic == FormatMagic)
                    {
                        int version = reader.ReadInt32();
                        v2 = version >= 2;
                        v3 = version >= 3;
                        v4 = version >= 4;
                        v5 = version >= 5;
                        v6 = version >= 6;
                        v7 = version >= 7;
                        v8 = version >= 8;
                        stats.FormatVersion = version;
                    }
                    else
                    {
                        fs.Position = 0; // formato legado sin cabecera
                    }
                }

                // Todo el ecosistema trabaja en metros. Los formatos <= v4 escribían pies, así
                // que se convierten al leer y de acá para abajo el pipeline es siempre métrico.
                float aMetros = v5 ? 1f : (float)PiesAMetros;
                stats.UnidadOrigen = v5 ? "metros (v5)" : "pies -> convertido a metros";

                // v4: Leer Punto Base del Proyecto (3 floats, solo para referencia)
                float baseX = 0, baseY = 0, baseZ = 0;
                if (v4)
                {
                    baseX = reader.ReadSingle() * aMetros;
                    baseY = reader.ReadSingle() * aMetros;
                    baseZ = reader.ReadSingle() * aMetros;
                }
                stats.PuntoBase = new Vector3(baseX, baseY, baseZ);

                // v8: nombre del archivo .rvt de origen, justo después del Punto Base.
                if (v8) stats.ArchivoRvt = reader.ReadString() ?? "";

                while (fs.Position < fs.Length)
                {
                    var mesh = new MeshData();
                    mesh.ElementId = reader.ReadInt32();
                    mesh.Guid = reader.ReadString();
                    mesh.MaterialId = reader.ReadInt32();

                    if (v3)
                    {
                        mesh.CategoryId = reader.ReadInt32();
                    }

                    // v7: familia y tipo van entre el categoryId y el RGBA, no al final del
                    // bloque. Leerlos fuera de ese punto desalinea todo el stream.
                    if (v7)
                    {
                        mesh.FamilyName = reader.ReadString() ?? "";
                        mesh.TypeName = reader.ReadString() ?? "";
                    }

                    // v8: nivel + área/longitud/volumen (de parámetros de Revit), después del tipo.
                    if (v8)
                    {
                        mesh.Level = reader.ReadString() ?? "";
                        mesh.Area = reader.ReadSingle();
                        mesh.Length = reader.ReadSingle();
                        mesh.Volume = reader.ReadSingle();
                    }

                    if (v2)
                    {
                        mesh.ColR = reader.ReadByte();
                        mesh.ColG = reader.ReadByte();
                        mesh.ColB = reader.ReadByte();
                        mesh.ColA = reader.ReadByte();
                        mesh.HasColor = true;
                    }

                    if (v6)
                    {
                        // El ángulo NO se toca al rotar Z-up -> Y-up (ver más abajo): el patrón
                        // vive en el plano de la cara del muro, y esa rotación mantiene la
                        // horizontal horizontal y la vertical vertical dentro de un muro.
                        mesh.HatchAngle = reader.ReadSingle();
                        mesh.HatchSpacing = reader.ReadSingle();
                    }

                    int vertexCount = reader.ReadInt32();
                    mesh.Vertices.Capacity = vertexCount;
                    for (int i = 0; i < vertexCount; i++)
                    {
                        float x = reader.ReadSingle() * aMetros;
                        float y = reader.ReadSingle() * aMetros;
                        float z = reader.ReadSingle() * aMetros;

                        // Rotar de Revit (Z-Up) a estándar GLTF/Visualizadores (Y-Up)
                        mesh.Vertices.Add(new Vector3(x, z, -y));
                    }

                    int triangleCount = reader.ReadInt32();
                    mesh.Indices.Capacity = triangleCount * 3;
                    for (int i = 0; i < triangleCount; i++)
                    {
                        mesh.Indices.Add((int)reader.ReadUInt32());
                        mesh.Indices.Add((int)reader.ReadUInt32());
                        mesh.Indices.Add((int)reader.ReadUInt32());
                    }

                    // v4: Metadata de habitaciones
                    if (v4 && mesh.CategoryId == CatRooms)
                    {
                        mesh.RoomLevel = reader.ReadString();
                        mesh.RoomDepartment = reader.ReadString();
                        mesh.RoomName = reader.ReadString();
                    }

                    meshes.Add(mesh);
                }
            }

            return meshes;
        }

        static void ExportRoomsJson(List<MeshData> meshes, string outputPath, PipelineStats stats)
        {
            var rooms = new List<object>();

            foreach (var m in meshes)
            {
                if (m.CategoryId != CatRooms) continue;

                if (m.Vertices.Count == 0)
                {
                    // Antes esto era el caso NORMAL (el soldado borraba el centroide) y salía
                    // como [0,0,0] silencioso. Ahora es una anomalía real y se reporta.
                    stats.RoomsSinCentro++;
                    Log($"  AVISO: habitación ElementId {m.ElementId} " +
                        $"('{m.RoomName}') llegó sin centroide; se omite del JSON.");
                    continue;
                }

                var c = m.Vertices[0];
                rooms.Add(new
                {
                    ElementId = m.ElementId,
                    Guid = m.Guid,
                    Level = m.RoomLevel,
                    Department = m.RoomDepartment,
                    Name = m.RoomName,
                    Center = new[] { c.X, c.Y, c.Z }
                });
                stats.Rooms++;
            }

            // Envoltura con metadata: sin esto, el consumidor no puede saber en qué unidades ni
            // en qué sistema de ejes están los centros (el pipeline rota Z-Up -> Y-Up).
            var payload = new
            {
                Units = "meters",
                AxisSystem = "Y-Up (X, Z, -Y respecto de Revit)",
                Origin = "Punto Base del Proyecto",
                ProjectBasePoint = new[] { stats.PuntoBase.X, stats.PuntoBase.Y, stats.PuntoBase.Z },
                Count = rooms.Count,
                Rooms = rooms
            };

            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            string json = System.Text.Json.JsonSerializer.Serialize(payload, options);
            File.WriteAllText(outputPath, json);
        }

        // ================== EXPORTACIÓN ==================

        static void ExportToObj(List<MeshData> meshes, string outputPath)
        {
            using (StreamWriter writer = new StreamWriter(outputPath))
            {
                writer.WriteLine("# Exportado desde ConvertidorGeometrias");

                int vertexOffset = 1;
                int normalOffset = 1;

                foreach (var mesh in meshes)
                {
                    writer.WriteLine($"o {mesh.Guid}");
                    writer.WriteLine($"usemtl MAT_{mesh.MaterialId}");

                    foreach (var v in mesh.Vertices)
                    {
                        string x = v.X.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        string y = v.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        string z = v.Z.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        writer.WriteLine($"v {x} {y} {z}");
                    }

                    bool hasNormals = mesh.Normals != null && mesh.Normals.Count == mesh.Vertices.Count;
                    if (hasNormals)
                    {
                        foreach (var vn in mesh.Normals)
                        {
                            string nx = vn.X.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            string ny = vn.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            string nz = vn.Z.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            writer.WriteLine($"vn {nx} {ny} {nz}");
                        }
                    }

                    for (int i = 0; i < mesh.Indices.Count; i += 3)
                    {
                        int i0 = mesh.Indices[i] + vertexOffset;
                        int i1 = mesh.Indices[i + 1] + vertexOffset;
                        int i2 = mesh.Indices[i + 2] + vertexOffset;

                        if (hasNormals)
                        {
                            int n0 = mesh.Indices[i] + normalOffset;
                            int n1 = mesh.Indices[i + 1] + normalOffset;
                            int n2 = mesh.Indices[i + 2] + normalOffset;
                            writer.WriteLine($"f {i0}//{n0} {i1}//{n1} {i2}//{n2}");
                        }
                        else
                        {
                            writer.WriteLine($"f {i0} {i1} {i2}");
                        }
                    }

                    vertexOffset += mesh.Vertices.Count;
                    if (hasNormals) normalOffset += mesh.Normals.Count;
                }
            }
        }

        static void ExportToGlb(List<MeshData> meshes, string outputPath, PipelineStats stats)
        {
            var scene = new SceneBuilder();

            // Un material compartido por MaterialId: menos draw calls (batching en Unity)
            var materials = new Dictionary<int, MaterialBuilder>();
            var random = new Random(42);

            // Cache de geometría para instanciado de piezas repetidas
            var meshCache = new Dictionary<(int, int, int, long),
                List<(MeshData, MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>)>>();

            foreach (var meshData in meshes)
            {
                if (meshData.Vertices == null || meshData.Vertices.Count == 0) continue;

                if (!materials.TryGetValue(meshData.MaterialId, out var material))
                {
                    float r, g, b, a;
                    if (meshData.HasColor)
                    {
                        // Color superficial simple del material de Revit; alpha < 1 = vidrio
                        r = meshData.ColR / 255f;
                        g = meshData.ColG / 255f;
                        b = meshData.ColB / 255f;
                        a = meshData.ColA / 255f;
                    }
                    else
                    {
                        // Formato legado: color aleatorio determinístico por material
                        r = (float)random.NextDouble() * 0.7f + 0.3f;
                        g = (float)random.NextDouble() * 0.7f + 0.3f;
                        b = (float)random.NextDouble() * 0.7f + 0.3f;
                        a = 1.0f;
                    }

                    material = new MaterialBuilder($"MAT_{meshData.MaterialId}")
                        .WithDoubleSide(true)
                        .WithMetallicRoughnessShader()
                        .WithChannelParam(KnownChannel.BaseColor, new Vector4(r, g, b, a))
                        .WithChannelParam(KnownChannel.MetallicRoughness, new Vector4(0.0f, 0.9f, 0, 0));

                    if (a < 0.995f)
                    {
                        material.WithAlpha(AlphaMode.BLEND);
                        stats.Transparent++;
                    }

                    materials[meshData.MaterialId] = material;
                }

                // Instanciado: si esta pieza es una copia trasladada de otra ya exportada
                // (ventanas/puertas/muebles repetidos), reusar su malla con un nodo movido.
                // Menos bytes de descarga y menos RAM de mallas en el móvil.
                Matrix4x4 transform = Matrix4x4.Identity;
                MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> meshBuilder = null;

                var geoKey = (meshData.MaterialId, meshData.Vertices.Count, meshData.Indices.Count, GeometryHash(meshData));
                if (!meshCache.TryGetValue(geoKey, out var candidates))
                {
                    candidates = new List<(MeshData, MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>)>();
                    meshCache[geoKey] = candidates;
                }

                foreach (var (refMesh, refBuilder) in candidates)
                {
                    if (EsCopiaTrasladada(refMesh, meshData, out var delta))
                    {
                        meshBuilder = refBuilder;
                        transform = Matrix4x4.CreateTranslation(delta);
                        stats.Instanced++;
                        break;
                    }
                }

                if (meshBuilder == null)
                {
                    // El nombre de la malla es el GUID de la primera pieza que la usa
                    meshBuilder = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>(meshData.Guid);
                    var prim = meshBuilder.UsePrimitive(material);

                    for (int i = 0; i < meshData.Indices.Count; i += 3)
                    {
                        var i0 = meshData.Indices[i];
                        var i1 = meshData.Indices[i + 1];
                        var i2 = meshData.Indices[i + 2];

                        prim.AddTriangle(
                            new VertexPositionNormal(meshData.Vertices[i0], meshData.Normals[i0]),
                            new VertexPositionNormal(meshData.Vertices[i1], meshData.Normals[i1]),
                            new VertexPositionNormal(meshData.Vertices[i2], meshData.Normals[i2])
                        );
                    }
                    candidates.Add((meshData, meshBuilder));
                }

                // El nodo (GameObject en Unity) lleva el GUID del elemento de Revit
                scene.AddRigidMesh(meshBuilder, transform).WithName(meshData.Guid);
            }

            var model = scene.ToGltf2();

            // Extras por nodo: cada pieza lleva su ElementId, CategoryId y MaterialId de Revit
            // además del GUID (nombre del nodo) y el color/alpha (material).
            var porGuid = new Dictionary<string, MeshData>();
            foreach (var m in meshes)
            {
                if (!porGuid.ContainsKey(m.Guid)) porGuid[m.Guid] = m;
            }
            foreach (var node in model.LogicalNodes)
            {
                if (node.Name != null && porGuid.TryGetValue(node.Name, out var md))
                {
                    node.Extras = System.Text.Json.Nodes.JsonNode.Parse(
                        $"{{\"elementId\":{md.ElementId},\"categoryId\":{md.CategoryId},\"materialId\":{md.MaterialId}}}");
                }
            }

            model.SaveGLB(outputPath);
        }

        // Hash rápido de la forma: posiciones relativas al primer vértice (grilla 0,05 mm) + índices.
        static long GeometryHash(MeshData mesh)
        {
            if (mesh.Vertices == null || mesh.Vertices.Count == 0) return 0;
            const double q = 20000.0;
            var v0 = mesh.Vertices[0];
            long hash = 1469598103934665603; // FNV-1a
            unchecked
            {
                foreach (var v in mesh.Vertices)
                {
                    hash = (hash ^ (long)Math.Round((v.X - v0.X) * q)) * 1099511628211;
                    hash = (hash ^ (long)Math.Round((v.Y - v0.Y) * q)) * 1099511628211;
                    hash = (hash ^ (long)Math.Round((v.Z - v0.Z) * q)) * 1099511628211;
                }
                foreach (var i in mesh.Indices)
                {
                    hash = (hash ^ i) * 1099511628211;
                }
            }
            return hash;
        }

        // Verificación exacta (el hash solo preselecciona): misma topología y mismas
        // posiciones relativas dentro de 0,1 mm. Devuelve la traslación entre ambas.
        static bool EsCopiaTrasladada(MeshData a, MeshData b, out Vector3 delta)
        {
            delta = default;
            if (a.Vertices == null || a.Vertices.Count == 0 || b.Vertices == null || b.Vertices.Count == 0) return false;
            if (a.Vertices.Count != b.Vertices.Count || a.Indices.Count != b.Indices.Count) return false;

            for (int i = 0; i < a.Indices.Count; i++)
            {
                if (a.Indices[i] != b.Indices[i]) return false;
            }

            delta = b.Vertices[0] - a.Vertices[0];
            const float eps = 1e-4f;
            for (int i = 0; i < a.Vertices.Count; i++)
            {
                var d = b.Vertices[i] - (a.Vertices[i] + delta);
                if (Math.Abs(d.X) > eps || Math.Abs(d.Y) > eps || Math.Abs(d.Z) > eps) return false;
            }
            return true;
        }

        // ================== BINARIO DE VISOR (.tbv) ==================
        // Formato "TBTV" ultra-optimizado para el visor web:
        //   - Deduplicación: la geometría repetida (ventanas, muebles, columnas iguales) se
        //     escribe UNA vez en un pool; cada pieza es una instancia que apunta al pool con
        //     una traslación. Enorme ahorro de bytes y de RAM/GPU en el visor.
        //   - Índice espacial: cada instancia guarda su AABB GLOBAL precalculado, para que el
        //     visor pueda cull-ear por "cubo visible" leyendo solo esos 6 floats (sin tocar
        //     la geometría) y para búsquedas por posición global.
        //   - Búsqueda por ID/GUID: cada instancia trae ElementId, CategoryId, MaterialId y GUID.
        //   - Compacto: normales en int8 (snorm), índices uint16 cuando se puede.
        //
        // Layout (little-endian):
        //   Header: int32 magic('TBTV'=0x56544254), int32 version(4),
        //           float32 unitsPerMeter,
        //           int32 matCount, int32 meshCount, int32 instCount,
        //           float32 sceneMin[3], float32 sceneMax[3]
        //
        // v2 (2026-08): agrega unitsPerMeter — el visor ya no puede asumir la
        // escala (había .tbv en pies y en metros conviviendo en la misma
        // carpeta de Drive con el mismo magic/version=1, indistinguibles para
        // el cliente; ver DIAGNOSTICO_VISOR3D.md §3.3). Acá SIEMPRE se escribe
        // 1.0 porque LeerBinario ya convirtió todo a metros al leer el
        // export.bin (ver aMetros más arriba); un lector v1 (sin este campo)
        // debe asumir pies, como venía siendo.
        // v3 (2026-08): la tabla de materiales agrega el patrón de superficie vectorial
        // (float32 hatchAngle en radianes, float32 hatchSpacing en metros). Es la definición
        // del FillPattern de Revit, NO una textura: el visor reconstruye las líneas por
        // fragmento en el shader, así que el patrón se ve nítido a cualquier zoom y suma
        // 8 bytes por material (no por pieza) al archivo. spacing <= 0 = sin patrón.
        // Sólo los muros traen patrón, y el visor además lo enciende sólo en esa categoría.
        // v4 (2026-09): agrega la TABLA DE NOMBRES (familias y tipos de Revit) y dos índices por
        // instancia. Los nombres se deduplican: un modelo con 40.000 instancias suele tener unos
        // pocos cientos de nombres distintos, así que la tabla pesa kilobytes y no megabytes.
        // La tabla va DESPUÉS de los materiales y ANTES del pool de mallas.
        //   Materiales × matCount: int32 materialId, uint8 R,G,B,A,
        //                          (v3+) float32 hatchAngle, float32 hatchSpacing
        //   (v4+) Tabla de nombres: int32 nameCount,
        //                           luego nameCount × (uint16 len, byte[len] nombre UTF8)
        //         NO contiene la cadena vacía: "sin dato" se codifica como índice -1.
        //   Meshes (pool) × meshCount:
        //           int32 vertexCount, uint8 idx16(1/0), int32 triCount,
        //           float32 positions[vc*3], int8 normals[vc*3],
        //           (idx16? uint16 : uint32) indices[triCount*3]
        // v5 (2026-10): propiedades por elemento leídas de PARÁMETROS de Revit. Tras la tabla de
        // nombres va int32 fileIdx (nombre del .rvt de origen, -1 = sin dato) y cada instancia
        // agrega, después de typeIdx: int32 levelIdx (nombre del nivel en la tabla de nombres),
        // float32 area (m²), float32 length (m, largo/longitud/perímetro), float32 volume (m³).
        // NaN = el elemento no expone ese parámetro.
        //   Instancias × instCount:
        //           int32 meshIndex, int32 elementId, int32 categoryId, int32 materialIndex,
        //           (v4+) int32 familyIdx, int32 typeIdx,   // índice en la tabla de nombres, -1 = sin dato
        //           (v5+) int32 levelIdx, float32 area, float32 length, float32 volume,
        //           float32 tx,ty,tz, float32 gmin[3], float32 gmax[3],
        //           uint16 guidLen, byte[guidLen] guid(UTF8)
        static void ExportToViewerBin(List<MeshData> meshes, string path, PipelineStats stats)
        {
            const int MAGIC = 0x56544254; // "TBTV"
            const int VERSION = 5;
            // Tope del prefijo de longitud (uint16) de cada nombre de la tabla.
            const int NAME_MAX_BYTES = 65535;
            const float UNITS_PER_METER_OUT = 1.0f; // el pipeline siempre entrega metros de acá en más

            // Tabla de materiales: un color por MaterialId
            // Sólo las piezas con geometría real: si no, las habitaciones (MaterialId -1) meten
            // un material fantasma de color (0,0,0,0) que nunca referencia ninguna instancia.
            var matIndexById = new Dictionary<int, int>();
            var matReps = new List<MeshData>();
            foreach (var m in meshes)
            {
                if (m.Vertices == null || m.Vertices.Count == 0 || m.Indices.Count < 3) continue;

                int existente;
                if (!matIndexById.TryGetValue(m.MaterialId, out existente))
                {
                    matIndexById[m.MaterialId] = matReps.Count;
                    matReps.Add(m);
                }
                else if (m.HasHatch && !matReps[existente].HasHatch)
                {
                    // El mismo material de Revit puede estar en un muro Y en una losa, y sólo
                    // el muro trae patrón. Como el primero que aparece gana el lugar de
                    // representante, si ese primero era la losa el hatch se perdía entero.
                    // Gana el que SÍ tiene patrón; que la losa no se raye lo garantiza el
                    // visor, que enciende el hatch sólo en la categoría de muros.
                    matReps[existente] = m;
                }
            }

            // Pool de geometría deduplicada + instancias (misma lógica que el instanciado del GLB)
            var pool = new List<MeshData>();
            var poolBounds = new List<(Vector3 min, Vector3 max)>();
            var poolKey = new Dictionary<(int, int, int, long), List<int>>();
            var instMeshIdx = new List<int>(meshes.Count);
            var instDelta = new List<Vector3>(meshes.Count);

            // Lista paralela a instMeshIdx/instDelta. Las piezas sin geometría (habitaciones:
            // centroide puro, sin caras) NO van al .tbv, pero antes se salteaban acá y los
            // bucles de abajo seguían recorriendo `meshes` entero: los índices se desfasaban y
            // reventaba con IndexOutOfRangeException en cuanto el modelo tenía una habitación.
            var instSource = new List<MeshData>(meshes.Count);

            foreach (var m in meshes)
            {
                if (m.Vertices == null || m.Vertices.Count == 0 || m.Indices.Count < 3) continue;

                var key = (m.MaterialId, m.Vertices.Count, m.Indices.Count, GeometryHash(m));
                if (!poolKey.TryGetValue(key, out var cand))
                {
                    cand = new List<int>();
                    poolKey[key] = cand;
                }

                int found = -1;
                Vector3 delta = Vector3.Zero;
                foreach (var pi in cand)
                {
                    if (EsCopiaTrasladada(pool[pi], m, out delta)) { found = pi; break; }
                }

                if (found < 0)
                {
                    found = pool.Count;
                    pool.Add(m);
                    GetBounds(m.Vertices, out var mn, out var mx);
                    poolBounds.Add((mn, mx));
                    cand.Add(found);
                    delta = Vector3.Zero;
                }

                instMeshIdx.Add(found);
                instDelta.Add(delta);
                instSource.Add(m);
            }

            stats.PoolMeshes = pool.Count;

            // v4: tabla de nombres deduplicada (familias y tipos juntos en la misma tabla).
            // Sólo se indexan los nombres de las instancias que REALMENTE se escriben, y nunca
            // la cadena vacía: "sin dato" viaja como -1, así el visor distingue "no vino el
            // nombre" de "el nombre es una cadena vacía" sin gastar una entrada de tabla.
            var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var nameBytes = new List<byte[]>();
            Func<string, int> indiceNombre = s =>
            {
                if (string.IsNullOrEmpty(s)) return -1;
                int idx;
                if (nameIndex.TryGetValue(s, out idx)) return idx;
                byte[] b = Encoding.UTF8.GetBytes(s);
                // El prefijo de longitud es uint16: un nombre más largo no se puede representar.
                // Descartarlo (-1) es preferible a escribir una longitud truncada, que
                // desalinearía el archivo entero a partir de esa entrada.
                if (b.Length > NAME_MAX_BYTES)
                {
                    Log($"  AVISO: nombre de familia/tipo de {b.Length} bytes (> {NAME_MAX_BYTES}); se omite.");
                    nameIndex[s] = -1;
                    return -1;
                }
                idx = nameBytes.Count;
                nameBytes.Add(b);
                nameIndex[s] = idx;
                return idx;
            };

            var instFamIdx = new List<int>(instSource.Count);
            var instTypIdx = new List<int>(instSource.Count);
            var instLvlIdx = new List<int>(instSource.Count);
            foreach (var m in instSource)
            {
                instFamIdx.Add(indiceNombre(m.FamilyName));
                instTypIdx.Add(indiceNombre(m.TypeName));
                instLvlIdx.Add(indiceNombre(m.Level));
            }
            int archivoIdx = indiceNombre(stats.ArchivoRvt);

            // AABB de toda la escena (para encuadre inicial del visor)
            Vector3 sMin = new Vector3(float.MaxValue), sMax = new Vector3(float.MinValue);
            for (int i = 0; i < instSource.Count; i++)
            {
                var (mn, mx) = poolBounds[instMeshIdx[i]];
                var d = instDelta[i];
                sMin = Vector3.Min(sMin, mn + d);
                sMax = Vector3.Max(sMax, mx + d);
            }

            // Sin ninguna instancia con geometría, el AABB queda invertido (MaxValue/MinValue)
            // y el visor no puede encuadrar. Mejor una caja degenerada en el origen.
            if (instSource.Count == 0)
            {
                sMin = Vector3.Zero;
                sMax = Vector3.Zero;
            }

            var rndColors = new Random(42);
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(MAGIC);
                w.Write(VERSION);
                w.Write(UNITS_PER_METER_OUT);
                w.Write(matReps.Count);
                w.Write(pool.Count);
                w.Write(instSource.Count);
                w.Write(sMin.X); w.Write(sMin.Y); w.Write(sMin.Z);
                w.Write(sMax.X); w.Write(sMax.Y); w.Write(sMax.Z);

                // Materiales
                foreach (var mr in matReps)
                {
                    w.Write(mr.MaterialId);
                    if (mr.HasColor)
                    {
                        w.Write(mr.ColR); w.Write(mr.ColG); w.Write(mr.ColB); w.Write(mr.ColA);
                    }
                    else
                    {
                        byte r = (byte)(rndColors.Next(0, 180) + 60);
                        byte g = (byte)(rndColors.Next(0, 180) + 60);
                        byte b = (byte)(rndColors.Next(0, 180) + 60);
                        w.Write(r); w.Write(g); w.Write(b); w.Write((byte)255);
                    }
                    // v3: patrón de superficie vectorial (0,0 = sin patrón)
                    w.Write(mr.HatchAngle);
                    w.Write(mr.HatchSpacing);
                    if (mr.HasHatch) stats.HatchedMats++;
                }

                // v4: tabla de nombres (va entre los materiales y el pool de mallas)
                w.Write(nameBytes.Count);
                foreach (var nb in nameBytes)
                {
                    w.Write((ushort)nb.Length);
                    w.Write(nb);
                }
                w.Write(archivoIdx); // v5: nombre del .rvt de origen (índice en la tabla, -1 = sin dato)

                // Meshes del pool
                foreach (var pm in pool)
                {
                    int vc = pm.Vertices.Count;
                    int tc = pm.Indices.Count / 3;
                    bool idx16 = vc <= 65535;

                    w.Write(vc);
                    w.Write((byte)(idx16 ? 1 : 0));
                    w.Write(tc);

                    for (int i = 0; i < vc; i++)
                    {
                        var v = pm.Vertices[i];
                        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
                    }
                    for (int i = 0; i < vc; i++)
                    {
                        var n = i < pm.Normals.Count ? pm.Normals[i] : Vector3.UnitY;
                        w.Write(Snorm(n.X)); w.Write(Snorm(n.Y)); w.Write(Snorm(n.Z));
                    }
                    if (idx16)
                        foreach (var ix in pm.Indices) w.Write((ushort)ix);
                    else
                        foreach (var ix in pm.Indices) w.Write((uint)ix);
                }

                // Instancias
                for (int i = 0; i < instSource.Count; i++)
                {
                    var m = instSource[i];
                    var (mn, mx) = poolBounds[instMeshIdx[i]];
                    var d = instDelta[i];
                    var gmin = mn + d;
                    var gmax = mx + d;

                    w.Write(instMeshIdx[i]);
                    w.Write(m.ElementId);
                    w.Write(m.CategoryId);
                    w.Write(matIndexById[m.MaterialId]);
                    w.Write(instFamIdx[i]); // v4: índice en la tabla de nombres, -1 = sin dato
                    w.Write(instTypIdx[i]); // v4: idem
                    w.Write(instLvlIdx[i]); // v5: nivel (índice en la tabla de nombres)
                    w.Write(m.Area);        // v5: m² (NaN = sin parámetro)
                    w.Write(m.Length);      // v5: m
                    w.Write(m.Volume);      // v5: m³
                    w.Write(d.X); w.Write(d.Y); w.Write(d.Z);
                    w.Write(gmin.X); w.Write(gmin.Y); w.Write(gmin.Z);
                    w.Write(gmax.X); w.Write(gmax.Y); w.Write(gmax.Z);

                    var gb = Encoding.UTF8.GetBytes(m.Guid ?? "");
                    w.Write((ushort)gb.Length);
                    w.Write(gb);
                }
            }

            stats.TbvBytes = new FileInfo(path).Length;
        }

        static sbyte Snorm(float f)
        {
            f = Math.Max(-1f, Math.Min(1f, f));
            return (sbyte)Math.Round(f * 127f);
        }
    }

    /// <summary>
    /// Render de la consola, unificado con el resto de los post-procesadores del pipeline
    /// (Parametros, Tablas, Posiciones, Geometrias, Planos): un encabezado fijo arriba con el
    /// nombre y la descripcion del proceso, y de ahi para abajo UN RENGLON POR PROYECTO con
    /// hora de inicio, hora de fin, barra de avance, porcentaje, etapa en curso y duracion.
    ///
    /// El renglon se dibuja en el lugar mientras avanza (con \r) y se cierra con un salto de
    /// linea recien cuando el proyecto termina, asi la consola queda como un historial legible
    /// en vez de una barra de progreso que se pisa a si misma.
    ///
    /// La barra usa '#' y '-' a proposito, no bloques Unicode: estos exes corren en la consola
    /// que les toque y con la codepage por defecto los caracteres de bloque salen como '?'.
    ///
    /// TODO el dibujado va bajo un candado. No es precaucion teorica: en Planos el avance lo
    /// reportan varios hilos a la vez (el lote de planos corre en Parallel.ForEach), y sin el
    /// candado dos hilos escribiendo su \r y sus fragmentos se entrelazaban dejando renglones
    /// ilegibles del tipo
    ///   "SO_DU  08:09  [SO_DU  ##------[] ##------ 25%]  25%  generando..SO_DU  08:09..."
    /// Los otros cuatro post-procesadores son monohilo y el candado no les cuesta nada.
    /// </summary>
            public static class Consola
    {
        private const string SIN_HORA = "--:--";

        private static readonly object _candado = new object();
        private static string _nombre = "";
        private static string _etapa = "";
        private static DateTime _inicio;
        private static int _pct;
        
        private static DateTime? _ultimoFin = null;
        private static int _esperandoIndex = 0;
        private static char[] _spinner = new[] { '\\', '|', '/', '-' };
        private static bool _enEsperando = false;
        // T07: avance "[N/M] ..." del lote (ProgresoLotes); se agrega como DETALLE al final del renglon.
        private static string _detalle = "";
        private static int _ultLargoFila = 0;
        public static void Detalle(string d) { lock (_candado) { _detalle = d ?? ""; } }

        /// <summary>Hora en que arranco el proyecto en curso; la usa el log para su columna HoraInicio.</summary>
        public static DateTime InicioActual { get { return _inicio; } }

        public static void Encabezado(string titulo, string descripcion, string[][] rutas)
        {
            lock (_candado)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(new string('=', 88));
                Console.WriteLine("  MIP  -  " + titulo);
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Gray;
                foreach (string linea in Envolver(descripcion, 84)) Console.WriteLine("  " + linea);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(new string('-', 88));
                Console.ResetColor();
                foreach (string[] r in rutas)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("  [" + r[2].PadRight(2) + "] " + r[0].PadRight(8) + " ");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine(r[1]);
                }
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(new string('=', 88));
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("DELTA\tINICIO\tFIN\tPROYECTO\tAVANCE\tETAPA\tTIEMPO\tDETALLE");
                Console.ResetColor();
            }
        }

        public static void Esperando(int pendientes)
        {
            lock (_candado)
            {
                _enEsperando = true;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("\rEsperando cambios " + _spinner[_esperandoIndex % _spinner.Length] + " " + (pendientes > 0 ? $"({pendientes} pendientes)" : "") + "    ");
                Console.ResetColor();
                _esperandoIndex++;
            }
        }
        
        private static void BorrarEsperando()
        {
            if (_enEsperando)
            {
                Console.Write("\r" + new string(' ', 50) + "\r");
                _enEsperando = false;
            }
        }

        public static void FilaInicio(string nombre, string etapa)
        {
            lock (_candado)
            {
                BorrarEsperando();
                _nombre = nombre ?? "";
                _etapa = etapa ?? "";
                _inicio = DateTime.Now;
                _pct = 0;
                Pintar(false, TimeSpan.Zero, true, null);
            }
        }

        public static void FilaProgreso(int pct)
        {
            lock (_candado)
            {
                BorrarEsperando();
                _pct = pct < 0 ? 0 : (pct > 100 ? 100 : pct);
                Pintar(false, TimeSpan.Zero, true, null);
            }
        }

        public static void FilaProgreso(int pct, string etapa)
        {
            lock (_candado)
            {
                BorrarEsperando();
                if (!string.IsNullOrEmpty(etapa)) _etapa = etapa;
                _pct = pct < 0 ? 0 : (pct > 100 ? 100 : pct);
                Pintar(false, TimeSpan.Zero, true, null);
            }
        }

        public static void FilaFin(string nombre, TimeSpan t, bool ok, string resumen)
        {
            lock (_candado)
            {
                BorrarEsperando();
                if (!string.IsNullOrEmpty(nombre)) _nombre = nombre;
                if (ok) _pct = 100;
                _etapa = ok ? "listo" : "ERROR";
                DateTime fin = DateTime.Now;
                Pintar(true, t, ok, fin, resumen);
                Console.WriteLine();
                _ultimoFin = fin;
            }
        }

        private static void Pintar(bool terminado, TimeSpan t, bool ok, DateTime? fin)
        {
            Pintar(terminado, t, ok, fin, null);
        }

        private static void Pintar(bool terminado, TimeSpan t, bool ok, DateTime? fin, string cola)
        {
            string sIni = _inicio == default(DateTime) ? SIN_HORA : _inicio.ToString("HH:mm");
            string sFin = fin.HasValue ? fin.Value.ToString("HH:mm") : SIN_HORA;
            
            string sDelta = "";
            if (_ultimoFin.HasValue)
            {
                TimeSpan delta = _inicio - _ultimoFin.Value;
                sDelta = $"{(int)delta.TotalMinutes:D2}:{delta.Seconds:D2}";
            }
            else
            {
                sDelta = "00:00";
            }

            int llenos = (int)Math.Round(_pct / 100.0 * 10);
            if (llenos < 0) llenos = 0;
            if (llenos > 10) llenos = 10;
            
            string barraAvance = "";
            if (terminado && ok) {
                barraAvance = $"Finalizado {sDelta}";
                sDelta = ""; // Se limpia del principio segun ejemplo
            } else {
                barraAvance = $"[{new string('#', llenos)}{new string('-', 10 - llenos)}] {_pct}%";
            }

            // Limpiamos linea
            Console.Write("\r" + new string(' ', AnchoUtilFila()) + "\r");
            
            if (ok) Console.ForegroundColor = ConsoleColor.Gray;
            else Console.ForegroundColor = ConsoleColor.Red;
            
            string dur = terminado ? Duracion(t) : "-";
            
            string res = $"{sDelta}\t{sIni}\t{sFin}\t{_nombre}\t{barraAvance}\t{_etapa}\t{dur}";
            if (!string.IsNullOrEmpty(cola)) res += "\t" + cola;
            if (!string.IsNullOrEmpty(_detalle)) res += (string.IsNullOrEmpty(cola) ? "\t" : "  ") + _detalle;
            _ultLargoFila = res.Length + 40;
            
            Console.Write(AjustarFila(res));
            Console.ResetColor();
        }

        // La fila se repinta con '\r'. Si es mas ancha que la ventana se parte en dos renglones y cada
        // repintado queda apilado debajo del anterior: se expanden los tabuladores (paradas de 8) y se
        // recorta a ancho-1 para que ocupe SIEMPRE un solo renglon.
        private static int AnchoUtilFila()
        {
            try { int w = Console.WindowWidth; return w > 20 ? w - 1 : 119; }
            catch { return 119; }
        }

        private static string AjustarFila(string fila)
        {
            int max = AnchoUtilFila();
            var sb = new System.Text.StringBuilder(max);
            foreach (char c in fila ?? "")
            {
                if (sb.Length >= max) break;
                if (c == '\t') { int n = 8 - (sb.Length % 8); sb.Append(' ', Math.Min(n, max - sb.Length)); }
                else if (c == '\r' || c == '\n') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }
        public static string Duracion(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return t.TotalSeconds.ToString("F1") + "s";
            return (int)t.TotalMinutes + "m " + t.Seconds + "s";
        }

        private static string[] Envolver(string texto, int ancho)
        {
            var lineas = new System.Collections.Generic.List<string>();
            string actual = "";
            foreach (string palabra in (texto ?? "").Split(' '))
            {
                if (actual.Length == 0) actual = palabra;
                else if (actual.Length + 1 + palabra.Length <= ancho) actual += " " + palabra;
                else { lineas.Add(actual); actual = palabra; }
            }
            if (actual.Length > 0) lineas.Add(actual);
            return lineas.ToArray();
        }
    }


}
