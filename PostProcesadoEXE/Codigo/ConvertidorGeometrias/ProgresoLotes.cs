// COPIA: fuente en R_01/tools/_compartido/ProgresoLotes.cs
// ---------------------------------------------------------------------------------------------
// ProgresoLotes (T07 de _PLAN_UNBOX, PLAN_FABLE 3a). Calcula el avance "N de M" de la exportacion por lotes
// leyendo los archivos que ya existen en la carpeta personal del addin (BasePluginFolder = args[0] de los exes):
//
//   tomados_<GUID>.txt   lote de cada Revit: lineas  ESTADO;ruta;minutos
//                        ESTADO = PENDIENTE | PROCESANDO (Intento n) | RESCATE FINAL | COMPLETADO | FALLIDO | SIN_CAMBIOS
//   tomados_<GUID>.lock  candado abierto en exclusiva mientras el Revit dueño vive (sin candado ocupado = lote huerfano)
//   pedidos.txt          cola: una ruta por linea (todavia no reclamada por ningun Revit)
//   delta\plan_delta_<PERFIL>.csv   plan del script 5_exportar_delta_2sesiones.ps1 (columna Ruta), del perfil activo
//
// Reglas (todas verificadas en tools\tests\PruebaProgresoLotes.ps1):
//   Total     = union (sin distinguir mayusculas) de las rutas de los tres origenes.
//   Hechos    = COMPLETADO + SIN_CAMBIOS.            EnCurso = PROCESANDO* / RESCATE* de lotes VIVOS.
//   Fallidos  = FALLIDO*.                            Pendientes = el resto (incluye PROCESANDO de un lote huerfano).
//   Faltan    = Total - Hechos - Fallidos           (lo que todavia hay que hacer, incluido lo en curso).
//   Tolerancia: archivo bloqueado -> 3 reintentos x 100 ms y, si sigue bloqueado, el ultimo contenido leido (memoria);
//               carpeta/archivo inexistente o vacio -> estado vacio. NINGUN metodo publico lanza excepciones.
//   Memoria: una ruta vista terminada (COMPLETADO/SIN_CAMBIOS/FALLIDO) que despues desaparece del disco (el addin borra
//            el lote al terminar) y que sigue en el plan de la corrida (plan mas viejo que la observacion) conserva su
//            estado terminal. Es por proceso: no hay nada persistido.
//   Orden de las rutas (para N): las del plan (orden del plan), luego tomados_* (por fecha de creacion), luego pedidos.txt.
//
// Sin dependencias; compila en net472/net48/net8 con C# 7.3. Namespace propio para poder copiarlo tal cual a cualquier exe.
// ---------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace UnboxCompartido
{
    /// <summary>Conteo por sesion (Revit) = primeros 8 caracteres del GUID del lote.</summary>
    public sealed class SesionProgreso
    {
        public int EnCurso;
        public int Hechos;
    }

    public sealed class EstadoLotes
    {
        public int Total;
        public int Hechos;        // Completados + SinCambios
        public int Completados;
        public int SinCambios;
        public int Fallidos;
        public int Pendientes;
        public string[] EnCurso = new string[0];   // rutas
        /// <summary>guid8 -> conteos. Incluye lotes huerfanos (con enCurso = 0).</summary>
        public Dictionary<string, SesionProgreso> PorSesion = new Dictionary<string, SesionProgreso>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Todas las rutas en el orden usado para N (1-based).</summary>
        public List<string> Rutas = new List<string>();
        public int LotesVivos;
        public int LotesHuerfanos;
        public bool HayLote;       // existe al menos un tomados_*.txt legible
        public bool UsoPlan;       // el plan del perfil activo aporto rutas
        public bool Parcial;       // algun archivo no se pudo leer (ni habia copia en memoria)
        public string Perfil;      // DATA | PLANOS | null

        public int Faltan { get { int f = Total - Hechos - Fallidos; return f < 0 ? 0 : f; } }
    }

    public sealed class ProgresoJson
    {
        public string Actualizado = "";
        public string Perfil = "";
        public int Total, Hechos, Faltan, Fallidos, TomadosPorOtrasSesiones;
        public List<string> EnCursoArchivos = new List<string>();
        public Dictionary<string, SesionProgreso> Sesiones = new Dictionary<string, SesionProgreso>(StringComparer.OrdinalIgnoreCase);

        public string Resumen()
        {
            return "progreso.json: " + Hechos + "/" + Total + " hechos · en curso " + EnCursoArchivos.Count
                + " · faltan " + Faltan + (Fallidos > 0 ? " · fallidos " + Fallidos : "")
                + (string.IsNullOrEmpty(Actualizado) ? "" : " (" + Actualizado + ")");
        }
    }

    public static class ProgresoLotes
    {
        // ---- parametros ------------------------------------------------------------------------------------------
        public const int ReintentosLectura = 3;
        public const int EsperaReintentoMs = 100;
        /// <summary>Un plan_delta mas viejo que esto se ignora (puede ser de una noche anterior, sin relacion con el lote actual).</summary>
        public static double MaxEdadPlanHoras = 14;
        private const double VentanaMemoriaHoras = 48;

        private enum Clase { Pendiente, EnCurso, Completado, SinCambios, Fallido }

        private sealed class Memo { public Clase C; public DateTime Visto; public string Sesion; }

        private static readonly object _candado = new object();
        private static string _memoBase = "";
        private static readonly Dictionary<string, Memo> _memo = new Dictionary<string, Memo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string[]> _cacheArchivos = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // ---- API publica -----------------------------------------------------------------------------------------

        /// <summary>Estado de todos los lotes. Nunca lanza: ante cualquier error devuelve lo que alcanzo a leer.</summary>
        public static EstadoLotes Leer(string basePluginFolder)
        {
            var e = new EstadoLotes();
            try
            {
                lock (_candado) { LeerInterno(basePluginFolder, e); }
            }
            catch (Exception) { e.Parcial = true; }
            return e;
        }

        /// <summary>N (1-based) de la ruta o del nombre de proyecto en el orden de aparicion; "?" si no matchea.</summary>
        public static string IndiceDe(EstadoLotes estado, string rutaONombreProyecto)
        {
            try
            {
                if (estado == null || estado.Rutas.Count == 0 || string.IsNullOrWhiteSpace(rutaONombreProyecto)) return "?";
                string q = rutaONombreProyecto.Trim().Trim('"');
                // 1) ruta completa
                for (int i = 0; i < estado.Rutas.Count; i++)
                    if (string.Equals(estado.Rutas[i], q, StringComparison.OrdinalIgnoreCase)) return (i + 1).ToString(CultureInfo.InvariantCulture);
                // 2) nombre de archivo / proyecto (sin sufijos de los .bin: _DUMP_TOTAL, _Geometria, ...)
                string nombre = NombreBase(q);
                if (nombre.Length == 0) return "?";
                for (int i = 0; i < estado.Rutas.Count; i++)
                    if (string.Equals(NombreBase(estado.Rutas[i]), nombre, StringComparison.OrdinalIgnoreCase)) return (i + 1).ToString(CultureInfo.InvariantCulture);
                // 3) comparacion laxa (solo letras y digitos): el nombre del .bin puede venir saneado
                string laxo = Laxo(nombre);
                if (laxo.Length == 0) return "?";
                for (int i = 0; i < estado.Rutas.Count; i++)
                    if (Laxo(NombreBase(estado.Rutas[i])) == laxo) return (i + 1).ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception) { }
            return "?";
        }

        /// <summary>"[N/M] hechos H · en curso E (sesiones A1b2,C3d4) · faltan F" (+ " · fallidos X" si hay).</summary>
        public static string Linea(string n, int m, EstadoLotes estado)
        {
            try
            {
                if (estado == null) estado = new EstadoLotes();
                var sb = new StringBuilder();
                sb.Append('[').Append(string.IsNullOrEmpty(n) ? "?" : n).Append('/').Append(m).Append("] hechos ").Append(estado.Hechos);
                sb.Append(" · en curso ").Append(estado.EnCurso.Length);
                var ses = new List<string>();
                foreach (var kv in estado.PorSesion)
                    if (kv.Value.EnCurso > 0) ses.Add(Corto(kv.Key));
                ses.Sort(StringComparer.OrdinalIgnoreCase);
                if (ses.Count > 0) sb.Append(" (sesiones ").Append(string.Join(",", ses.ToArray())).Append(')');
                sb.Append(" · faltan ").Append(estado.Faltan);
                if (estado.Fallidos > 0) sb.Append(" · fallidos ").Append(estado.Fallidos);
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        public static string Linea(int n, int m, EstadoLotes estado) { return Linea(n.ToString(CultureInfo.InvariantCulture), m, estado); }

        /// <summary>
        /// Atajo para los exes: Leer + IndiceDe + Linea. Devuelve "" si algo falla (el exe sigue normal).
        /// Sin ningun lote/pedido/plan: "[?/binsEnCola] (sin lote)" (binsEnCola &lt; 0 = desconocido -> "?"; se muestra al menos 1: el .bin en curso).
        /// </summary>
        public static string LineaPara(string basePluginFolder, string rutaONombreProyecto, int binsEnCola = -1)
        {
            try
            {
                EstadoLotes e = Leer(basePluginFolder);
                if (e.Total == 0)
                    return "[?/" + (binsEnCola >= 0 ? Math.Max(1, binsEnCola).ToString(CultureInfo.InvariantCulture) : "?") + "] (sin lote)";
                return Linea(IndiceDe(e, rutaONombreProyecto), e.Total, e);
            }
            catch (Exception) { return ""; }
        }

        /// <summary>Lee UNBOX\_Metadata\progreso.json (lo escribe solo el addin). null si no existe o no se entiende.</summary>
        public static ProgresoJson LeerProgresoJson(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                string[] l;
                lock (_candado) { l = LeerLineas(path); }
                if (l == null || l.Length == 0) return null;
                var raiz = MiniJson.Parse(string.Join("\n", l)) as Dictionary<string, object>;
                if (raiz == null) return null;
                var p = new ProgresoJson();
                p.Actualizado = MiniJson.Texto(raiz, "actualizado");
                p.Perfil = MiniJson.Texto(raiz, "perfil");
                p.Total = MiniJson.Entero(raiz, "total");
                p.Hechos = MiniJson.Entero(raiz, "hechos");
                p.Faltan = MiniJson.Entero(raiz, "faltan");
                p.Fallidos = MiniJson.Entero(raiz, "fallidos");
                p.TomadosPorOtrasSesiones = MiniJson.Entero(raiz, "tomados_por_otras_sesiones");
                var ec = MiniJson.Valor(raiz, "en_curso") as List<object>;
                if (ec != null)
                    foreach (object o in ec)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d != null) p.EnCursoArchivos.Add(MiniJson.Texto(d, "archivo"));
                    }
                var ss = MiniJson.Valor(raiz, "sesiones") as Dictionary<string, object>;
                if (ss != null)
                    foreach (var kv in ss)
                    {
                        var d = kv.Value as Dictionary<string, object>;
                        if (d != null) p.Sesiones[kv.Key] = new SesionProgreso { Hechos = MiniJson.Entero(d, "hechos"), EnCurso = MiniJson.Entero(d, "en_curso") };
                    }
                return p;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Ruta de progreso.json segun Paths.UnboxFolder de config.json en BasePluginFolder; null si no esta configurada.</summary>
        public static string RutaProgresoJson(string basePluginFolder)
        {
            try
            {
                string[] l;
                lock (_candado) { l = LeerLineas(Path.Combine(basePluginFolder, "config.json")); }
                if (l == null) return null;
                var raiz = MiniJson.Parse(string.Join("\n", l)) as Dictionary<string, object>;
                var paths = raiz == null ? null : MiniJson.Valor(raiz, "Paths") as Dictionary<string, object>;
                string unbox = paths == null ? "" : MiniJson.Texto(paths, "UnboxFolder");
                if (string.IsNullOrWhiteSpace(unbox)) return null;
                return Path.Combine(unbox, "_Metadata", "progreso.json");
            }
            catch (Exception) { return null; }
        }

        // ---- lectura ---------------------------------------------------------------------------------------------

        private sealed class Cand { public string Ruta; public Clase C; public string Sesion; public int Rango; }

        private static void LeerInterno(string basePluginFolder, EstadoLotes e)
        {
            if (string.IsNullOrWhiteSpace(basePluginFolder) || !Directory.Exists(basePluginFolder)) return;
            if (!string.Equals(_memoBase, basePluginFolder, StringComparison.OrdinalIgnoreCase))
            {
                _memoBase = basePluginFolder; _memo.Clear(); _cacheArchivos.Clear();
            }
            DateTime ahora = DateTime.Now;
            foreach (string k in new List<string>(_memo.Keys))
                if ((ahora - _memo[k].Visto).TotalHours > VentanaMemoriaHoras) _memo.Remove(k);

            var orden = new List<string>();
            var vistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ganadoras = new Dictionary<string, Cand>(StringComparer.OrdinalIgnoreCase);
            var enPlan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) plan del perfil activo (define el orden estable de N)
            DateTime mtimePlan = DateTime.MinValue;
            e.Perfil = PerfilActivo(basePluginFolder);
            try
            {
                string plan = Path.Combine(Path.Combine(basePluginFolder, "delta"), e.Perfil == null ? "plan_delta.csv" : "plan_delta_" + e.Perfil + ".csv");
                if (File.Exists(plan))
                {
                    mtimePlan = File.GetLastWriteTime(plan);
                    if ((ahora - mtimePlan).TotalHours <= MaxEdadPlanHoras)
                    {
                        string[] lp = LeerLineas(plan);
                        if (lp == null) e.Parcial = true;
                        else
                            foreach (string r in RutasDelPlan(lp))
                                if (vistas.Add(r)) { orden.Add(r); enPlan.Add(r); }
                        if (enPlan.Count > 0) e.UsoPlan = true;
                    }
                }
            }
            catch (Exception) { e.Parcial = true; }

            // 2) lotes tomados_*.txt
            var lotes = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(basePluginFolder, "tomados_*.txt"))
                    if (f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) lotes.Add(f);
            }
            catch (Exception) { e.Parcial = true; }
            lotes.Sort(delegate (string a, string b)
            {
                int c = SafeCreation(a).CompareTo(SafeCreation(b));
                return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            foreach (string f in lotes)
            {
                string nombre = Path.GetFileNameWithoutExtension(f);
                string guid = nombre.Length > 8 ? nombre.Substring(8) : nombre;
                string g8 = guid.Length > 8 ? guid.Substring(0, 8) : guid;
                bool vivo = LoteVivo(basePluginFolder, guid);
                string[] lineas = LeerLineas(f, true);
                if (lineas == null) { e.Parcial = true; continue; }
                e.HayLote = true;
                if (vivo) e.LotesVivos++; else e.LotesHuerfanos++;
                SesionProgreso sp;
                if (!e.PorSesion.TryGetValue(g8, out sp)) { sp = new SesionProgreso(); e.PorSesion[g8] = sp; }
                foreach (string raw in lineas)
                {
                    string estado, ruta;
                    if (!ParsearLinea(raw, out estado, out ruta)) continue;
                    Clase c = Clasificar(estado, vivo);
                    if (c == Clase.Completado || c == Clase.SinCambios) sp.Hechos++;
                    else if (c == Clase.EnCurso) sp.EnCurso++;
                    if (vistas.Add(ruta)) orden.Add(ruta);
                    int rango = Rango(c);
                    Cand previo;
                    if (!ganadoras.TryGetValue(ruta, out previo) || rango > previo.Rango)
                        ganadoras[ruta] = new Cand { Ruta = ruta, C = c, Sesion = g8, Rango = rango };
                }
            }

            // 3) pedidos.txt (rutas todavia sin reclamar: pendientes)
            var soloPedido = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string ped = Path.Combine(basePluginFolder, "pedidos.txt");
                if (File.Exists(ped))
                {
                    string[] lp = LeerLineas(ped, true);
                    if (lp == null) e.Parcial = true;
                    else
                        foreach (string raw in lp)
                        {
                            string r = LimpiarRuta(raw);
                            if (r.Length == 0) continue;
                            if (vistas.Add(r)) orden.Add(r);
                            soloPedido.Add(r);
                        }
                }
            }
            catch (Exception) { e.Parcial = true; }

            // 4) consolidar
            var enCurso = new List<string>();
            foreach (string r in orden)
            {
                Cand g;
                Clase c = Clase.Pendiente;
                if (ganadoras.TryGetValue(r, out g)) c = g.C;
                else if (enPlan.Contains(r) && !soloPedido.Contains(r))
                {
                    Memo m;
                    if (_memo.TryGetValue(r, out m) && m.Visto > mtimePlan && (m.C == Clase.Completado || m.C == Clase.SinCambios || m.C == Clase.Fallido)) c = m.C;
                }
                switch (c)
                {
                    case Clase.Completado: e.Completados++; e.Hechos++; break;
                    case Clase.SinCambios: e.SinCambios++; e.Hechos++; break;
                    case Clase.Fallido: e.Fallidos++; break;
                    case Clase.EnCurso: enCurso.Add(r); break;
                    default: e.Pendientes++; break;
                }
            }
            e.Rutas = orden;
            e.Total = orden.Count;
            e.EnCurso = enCurso.ToArray();

            // 5) memoria: lo observado en disco manda; si cambia de clase se actualiza la hora
            foreach (var kv in ganadoras)
            {
                Memo m;
                if (!_memo.TryGetValue(kv.Key, out m) || m.C != kv.Value.C) _memo[kv.Key] = new Memo { C = kv.Value.C, Visto = ahora, Sesion = kv.Value.Sesion };
            }
        }

        private static int Rango(Clase c)
        {
            switch (c)
            {
                case Clase.Completado: case Clase.SinCambios: return 4;
                case Clase.EnCurso: return 3;
                case Clase.Fallido: return 2;
                default: return 1;
            }
        }

        private static Clase Clasificar(string estado, bool vivo)
        {
            string s = (estado ?? "").Trim().ToUpperInvariant();
            if (s.StartsWith("COMPLETADO", StringComparison.Ordinal)) return Clase.Completado;
            if (s.StartsWith("SIN_CAMBIOS", StringComparison.Ordinal)) return Clase.SinCambios;
            if (s.StartsWith("FALLIDO", StringComparison.Ordinal)) return Clase.Fallido;
            if (s.StartsWith("PROCESANDO", StringComparison.Ordinal) || s.StartsWith("RESCATE", StringComparison.Ordinal))
                return vivo ? Clase.EnCurso : Clase.Pendiente;   // lote huerfano: nadie lo esta procesando
            return Clase.Pendiente;
        }

        private static bool ParsearLinea(string raw, out string estado, out string ruta)
        {
            estado = null; ruta = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string l = raw.TrimStart('﻿');
            string[] p = l.Split(new[] { ';' }, 3);
            if (p.Length < 2) return false;
            string r = LimpiarRuta(p[1]);
            if (r.Length == 0) return false;
            estado = p[0]; ruta = r;
            return true;
        }

        private static string LimpiarRuta(string s)
        {
            if (s == null) return "";
            return s.Trim().TrimStart('﻿').Trim().Trim('"').Trim();
        }

        private static DateTime SafeCreation(string f)
        {
            try { return File.GetCreationTimeUtc(f); } catch (Exception) { return DateTime.MaxValue; }
        }

        /// <summary>True si el Revit dueño del lote sigue vivo: el candado existe y esta tomado en exclusiva. Solo lectura, se suelta al instante.</summary>
        private static bool LoteVivo(string basePluginFolder, string guid)
        {
            try
            {
                string lk = Path.Combine(basePluginFolder, "tomados_" + guid + ".lock");
                if (!File.Exists(lk)) return false;
                try
                {
                    using (new FileStream(lk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
                    return false;           // se pudo abrir: nadie lo tiene tomado
                }
                catch (IOException) { return true; }
                catch (Exception) { return false; }
            }
            catch (Exception) { return false; }
        }

        private static string PerfilActivo(string basePluginFolder)
        {
            try
            {
                string f = Path.Combine(basePluginFolder, "perfil_activo.txt");
                if (File.Exists(f))
                {
                    string[] l = LeerLineas(f);
                    if (l != null && l.Length > 0)
                    {
                        string p = NormalizarPerfil(l[0]);
                        if (p != null) return p;
                    }
                }
            }
            catch (Exception) { }
            try { return NormalizarPerfil(Environment.GetEnvironmentVariable("MIP_PERFIL")); } catch (Exception) { return null; }
        }

        private static string NormalizarPerfil(string v)
        {
            if (v == null) return null;
            string s = v.Trim().TrimStart('﻿').Trim().ToUpperInvariant();
            return s == "DATA" || s == "PLANOS" ? s : null;
        }

        /// <summary>
        /// Lee un archivo de texto sin pelearse con quien escribe: 3 reintentos x 100 ms; si no, la ultima copia leida (o null).
        /// estricto = archivo de lineas que otro proceso reescribe (tomados_*, pedidos): un contenido vacio cuando antes habia
        /// lineas se trata como escritura a medias (reintenta), y una ultima linea sin salto de linea se descarta (truncada).
        /// </summary>
        private static string[] LeerLineas(string path, bool estricto = false)
        {
            for (int i = 0; i < ReintentosLectura; i++)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    string texto;
                    // Lectura "sin rotura": el escritor (File.WriteAllLines) trunca y reescribe el archivo mientras lo
                    // leemos. Si el archivo cambia de largo entre el inicio y el final de la lectura, el texto puede
                    // mezclar la cabeza del contenido viejo con la cola del nuevo (lineas partidas que cuentan como
                    // una ruta mas: Total 4 en vez de 3). Se detecta comparando el largo antes/despues y se reintenta.
                    bool cambioDuranteLectura = false;
                    byte[] bytes;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var ms = new MemoryStream())
                    {
                        long largoAntes = fs.Length;
                        fs.CopyTo(ms);
                        long largoDespues = fs.Length;
                        cambioDuranteLectura = largoAntes != largoDespues || ms.Length != largoAntes;
                        bytes = ms.ToArray();
                    }
                    if (cambioDuranteLectura)
                    {
                        if (i < ReintentosLectura - 1) Thread.Sleep(EsperaReintentoMs);
                        continue;
                    }
                    using (var sr = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), true))
                        texto = sr.ReadToEnd();
                    if (estricto)
                    {
                        string[] anterior;
                        if (texto.Length == 0 && _cacheArchivos.TryGetValue(path, out anterior) && anterior.Length > 0)
                        {
                            if (i < ReintentosLectura - 1) Thread.Sleep(EsperaReintentoMs);
                            continue;   // el escritor trunco el archivo y todavia no lo relleno
                        }
                        int nl = texto.LastIndexOf((char)10);
                        if (texto.Length > 0 && nl != texto.Length - 1) texto = nl < 0 ? "" : texto.Substring(0, nl + 1);
                    }
                    var lista = new List<string>();
                    using (var rd = new StringReader(texto))
                    {
                        string linea;
                        while ((linea = rd.ReadLine()) != null) lista.Add(linea);
                    }
                    string[] l = lista.ToArray();
                    _cacheArchivos[path] = l;
                    return l;
                }
                catch (IOException) { if (i < ReintentosLectura - 1) Thread.Sleep(EsperaReintentoMs); }
                catch (UnauthorizedAccessException) { if (i < ReintentosLectura - 1) Thread.Sleep(EsperaReintentoMs); }
                catch (Exception) { break; }
            }
            string[] previo;
            return _cacheArchivos.TryGetValue(path, out previo) ? previo : null;
        }

        // ---- plan_delta CSV --------------------------------------------------------------------------------------

        private static IEnumerable<string> RutasDelPlan(string[] lineas)
        {
            var res = new List<string>();
            if (lineas.Length == 0) return res;
            char sep = ',';
            string cab = lineas[0].TrimStart('﻿');
            if (cab.IndexOf(',') < 0 && cab.IndexOf(';') >= 0) sep = ';';
            List<string> cols = CsvCampos(cab, sep);
            int idx = -1;
            for (int i = 0; i < cols.Count; i++)
                if (string.Equals(cols[i].Trim(), "Ruta", StringComparison.OrdinalIgnoreCase)) idx = i;
            if (idx < 0) idx = cols.Count - 1;
            for (int i = 1; i < lineas.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lineas[i])) continue;
                List<string> f = CsvCampos(lineas[i], sep);
                if (idx >= f.Count) continue;
                string r = LimpiarRuta(f[idx]);
                if (r.Length > 0) res.Add(r);
            }
            return res;
        }

        private static List<string> CsvCampos(string linea, char sep)
        {
            var campos = new List<string>();
            var sb = new StringBuilder();
            bool comillas = false;
            for (int i = 0; i < linea.Length; i++)
            {
                char c = linea[i];
                if (comillas)
                {
                    if (c == '"')
                    {
                        if (i + 1 < linea.Length && linea[i + 1] == '"') { sb.Append('"'); i++; }
                        else comillas = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') comillas = true;
                else if (c == sep) { campos.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(c);
            }
            campos.Add(sb.ToString());
            return campos;
        }

        // ---- nombres ---------------------------------------------------------------------------------------------

        private static readonly string[] Sufijos = { "_DUMP_TOTAL", "_Geometria", "_Habitaciones", "_Parametros", "_Posiciones", "_Tablas", "_IndicesDeParametros" };

        private static string NombreBase(string rutaONombre)
        {
            string s = (rutaONombre ?? "").Trim().Trim('"');
            int bar = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
            if (bar >= 0) s = s.Substring(bar + 1);
            if (s.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            int it = s.IndexOf("__intento", StringComparison.OrdinalIgnoreCase);   // reintento de apartados del exe de geometria
            if (it > 0) s = s.Substring(0, it);
            bool cambio = true;
            while (cambio)
            {
                cambio = false;
                foreach (string suf in Sufijos)
                    if (s.Length > suf.Length && s.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(0, s.Length - suf.Length); cambio = true; }
            }
            return s;
        }

        private static string Laxo(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static string Corto(string guid8) { return guid8.Length > 4 ? guid8.Substring(0, 4) : guid8; }

        // ---- JSON minimo (sin dependencias) ----------------------------------------------------------------------

        private static class MiniJson
        {
            public static object Parse(string s)
            {
                int i = 0;
                s = (s ?? "").TrimStart('﻿');
                object v = Valor(s, ref i);
                return v;
            }

            public static object Valor(Dictionary<string, object> d, string clave)
            {
                foreach (var kv in d) if (string.Equals(kv.Key, clave, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                return null;
            }
            public static string Texto(Dictionary<string, object> d, string clave)
            {
                object v = Valor(d, clave);
                return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            public static int Entero(Dictionary<string, object> d, string clave)
            {
                object v = Valor(d, clave);
                if (v is double) return (int)(double)v;
                int n;
                return v != null && int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
            }

            private static void Espacios(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

            private static object Valor(string s, ref int i)
            {
                Espacios(s, ref i);
                if (i >= s.Length) throw new FormatException("json truncado");
                char c = s[i];
                if (c == '{')
                {
                    var d = new Dictionary<string, object>();
                    i++; Espacios(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    while (true)
                    {
                        Espacios(s, ref i);
                        string k = Cadena(s, ref i);
                        Espacios(s, ref i);
                        if (i >= s.Length || s[i] != ':') throw new FormatException("falta ':'");
                        i++;
                        d[k] = Valor(s, ref i);
                        Espacios(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        if (i < s.Length && s[i] == '}') { i++; return d; }
                        throw new FormatException("objeto mal cerrado");
                    }
                }
                if (c == '[')
                {
                    var l = new List<object>();
                    i++; Espacios(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    while (true)
                    {
                        l.Add(Valor(s, ref i));
                        Espacios(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        if (i < s.Length && s[i] == ']') { i++; return l; }
                        throw new FormatException("arreglo mal cerrado");
                    }
                }
                if (c == '"') return Cadena(s, ref i);
                int ini = i;
                while (i < s.Length && ",}] \t\r\n".IndexOf(s[i]) < 0) i++;
                string tok = s.Substring(ini, i - ini);
                if (tok == "true") return true;
                if (tok == "false") return false;
                if (tok == "null") return null;
                double dv;
                if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) return dv;
                throw new FormatException("token invalido: " + tok);
            }

            private static string Cadena(string s, ref int i)
            {
                if (i >= s.Length || s[i] != '"') throw new FormatException("se esperaba una cadena");
                i++;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("\\u truncado");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4; break;
                        default: sb.Append(e); break;
                    }
                }
                throw new FormatException("cadena sin cerrar");
            }
        }
    }
}
