// RvtATbv.exe -- dado el camino absoluto de un .rvt: hace una COPIA SEGURA (el original solo se lee), la inspecciona sin Revit
// y documenta que contiene. Tambien resume/valida un .tbv.
//
//   RvtATbv.exe <ruta.rvt>                 copia segura + inspeccion (JSON + miniatura)
//   RvtATbv.exe inspeccionar <ruta.rvt>    igual que arriba
//   RvtATbv.exe tbv <archivo.tbv>          resumen y validacion estructural de un .tbv
//   RvtATbv.exe ayuda
//
// Opciones: --trabajo <dir> (carpeta de pruebas internas), --salida <dir>
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RvtATbv
{
    public static class Programa
    {
        public static int Main(string[] args)
        {
            try
            {
                string trabajo = null, salida = null;
                List<string> pos = new List<string>();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--trabajo" && i + 1 < args.Length) trabajo = args[++i];
                    else if (args[i] == "--salida" && i + 1 < args.Length) salida = args[++i];
                    else pos.Add(args[i]);
                }
                if (pos.Count == 0 || pos[0] == "ayuda" || pos[0] == "-h" || pos[0] == "--help") { Ayuda(); return pos.Count == 0 ? 2 : 0; }

                string cmd = pos[0].ToLowerInvariant();
                if (cmd == "tbv")
                {
                    if (pos.Count < 2) { Ayuda(); return 2; }
                    Console.WriteLine(Util.Json(TbvInfo.Resumir(pos[1])));
                    return 0;
                }
                string rvt = cmd == "inspeccionar" ? (pos.Count > 1 ? pos[1] : null) : pos[0];
                if (rvt == null || !File.Exists(rvt)) { Console.Error.WriteLine("No existe el archivo: " + rvt); return 2; }
                if (!rvt.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("Se esperaba un .rvt: " + rvt); return 2; }
                return Inspeccionar(Path.GetFullPath(rvt), trabajo, salida);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static string TrabajoPorDefecto()
        {
            string exe = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string padre = Path.GetDirectoryName(exe);
            string cand = padre == null ? null : Path.Combine(padre, "PruebasInternas");
            if (cand != null && Directory.Exists(cand)) return cand;
            return Path.Combine(exe, "trabajo");
        }

        private static int Inspeccionar(string rvt, string trabajo, string salida)
        {
            if (trabajo == null) trabajo = TrabajoPorDefecto();
            string nombre = Util.NombreSeguro(Path.GetFileNameWithoutExtension(rvt));
            if (salida == null) salida = Path.Combine(trabajo, "salida", nombre);
            string copias = Path.Combine(trabajo, "copias_rvt");
            if (Util.EsDentroDe(salida, @"C:\ARQ") || Util.EsDentroDe(trabajo, @"C:\ARQ"))
            { Console.Error.WriteLine("La carpeta de trabajo/salida no puede estar dentro de C:\\ARQ."); return 2; }
            Directory.CreateDirectory(copias);
            Directory.CreateDirectory(salida);
            Util.LogRuta = Path.Combine(salida, "RvtATbv.log");

            string shaOrig = Inspector.Sha256(rvt);
            DateTime mOrig = File.GetLastWriteTimeUtc(rvt);
            string copia = Path.Combine(copias, nombre + ".rvt");
            Util.Log("Original (solo lectura): " + rvt);
            Util.Log("Copia segura: " + copia);
            File.Copy(rvt, copia, true);
            File.SetAttributes(copia, FileAttributes.Normal);
            if (Inspector.Sha256(copia) != shaOrig) { Util.Log("La copia no coincide con el original."); return 1; }

            Dictionary<string, object> r = Inspector.Inspeccionar(copia, salida, new Action<string>(Util.Log));
            r["original"] = rvt;
            r["sha256"] = shaOrig;
            r["copia_segura"] = copia;
            string js = Path.Combine(salida, nombre + "_Inspeccion.json");
            File.WriteAllText(js, Util.Json(r), new UTF8Encoding(false));
            Util.Log("Informe: " + js);

            bool intacto = Inspector.Sha256(rvt) == shaOrig && File.GetLastWriteTimeUtc(rvt) == mOrig;
            Util.Log(intacto ? "Original intacto (sha256 y fecha iguales)." : "ALERTA: el original cambio durante la corrida.");

            Console.WriteLine();
            Console.WriteLine("Elementos: " + (r.ContainsKey("elementos") ? r["elementos"] : "?") + "  |  particiones: " + r["particiones"] + "  |  marcas ASM/ACIS: " + r["marcas_de_solidos_ACIS_ASM"]);
            Console.WriteLine(r["conclusion"]);
            return intacto ? 0 : 1;
        }

        private static void Ayuda()
        {
            Console.WriteLine("RvtATbv - lectura segura de un .rvt (sin Revit) y resumen de .tbv");
            Console.WriteLine();
            Console.WriteLine("  RvtATbv.exe <ruta.rvt>               copia segura + inspeccion");
            Console.WriteLine("  RvtATbv.exe inspeccionar <ruta.rvt>  igual");
            Console.WriteLine("  RvtATbv.exe tbv <archivo.tbv>        resumen y validacion de un .tbv");
            Console.WriteLine("  Opciones: --trabajo <dir>  --salida <dir>");
            Console.WriteLine();
            Console.WriteLine("El original NUNCA se escribe: se copia a <trabajo>\\copias_rvt y se trabaja sobre la copia.");
        }
    }
}
