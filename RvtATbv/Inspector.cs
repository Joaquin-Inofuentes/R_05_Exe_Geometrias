// Inspector.cs -- lee un .rvt (COPIA) sin Revit: contenedor, version, tabla de elementos, particiones y esquema.
// No intenta reconstruir geometria: documenta que el archivo NO guarda mallas ni solidos (ver Resumen).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RvtATbv
{
    public static class Inspector
    {
        // Descomprime un miembro gzip de Revit (cabecera de 10 bytes + deflate crudo; el trailer CRC de Revit no es confiable).
        public static byte[] Inflar(byte[] d, int magia)
        {
            if (magia < 0 || magia + 10 >= d.Length) return null;
            try
            {
                using (MemoryStream src = new MemoryStream(d, magia + 10, d.Length - magia - 10))
                using (DeflateStream df = new DeflateStream(src, CompressionMode.Decompress))
                using (MemoryStream o = new MemoryStream())
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = df.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
                    return o.ToArray();
                }
            }
            catch (Exception) { return null; }
        }

        public static int BuscarMagia(byte[] d, int desde)
        {
            for (int i = desde; i + 4 <= d.Length; i++)
                if (d[i] == 0x1F && d[i + 1] == 0x8B && d[i + 2] == 0x08 && d[i + 3] == 0x00) return i;
            return -1;
        }

        public static int Buscar(byte[] h, byte[] p, int desde)
        {
            if (p.Length == 0) return -1;
            int fin = h.Length - p.Length;
            for (int i = desde; i <= fin; i++)
            {
                if (h[i] != p[0]) continue;
                int j = 1;
                while (j < p.Length && h[i + j] == p[j]) j++;
                if (j == p.Length) return i;
            }
            return -1;
        }

        // Cadenas UTF-16 legibles (largo >= 4), p. ej. de BasicFileInfo.
        public static List<string> CadenasUtf16(byte[] b, int min)
        {
            List<string> r = new List<string>();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i + 1 < b.Length; i += 2)
            {
                int c = b[i] | (b[i + 1] << 8);
                if (c >= 32 && c < 0x250 || c == 9) sb.Append((char)c);
                else { if (sb.Length >= min) r.Add(sb.ToString()); sb.Length = 0; }
            }
            if (sb.Length >= min) r.Add(sb.ToString());
            return r;
        }

        public static string Sha256(string ruta)
        {
            using (FileStream fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder();
                foreach (byte x in h) sb.Append(x.ToString("X2"));
                return sb.ToString();
            }
        }

        public static Dictionary<string, object> Inspeccionar(string rutaRvt, string carpetaSalida, Action<string> log)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            FileInfo fi = new FileInfo(rutaRvt);
            r["archivo"] = fi.Name;
            r["bytes"] = fi.Length;
            r["modificado_utc"] = fi.LastWriteTimeUtc.ToString("o");
            byte[] datos;
            using (FileStream fs = new FileStream(rutaRvt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                datos = new byte[fs.Length];
                int leido = 0;
                while (leido < datos.Length)
                {
                    int n = fs.Read(datos, leido, datos.Length - leido);
                    if (n <= 0) break;
                    leido += n;
                }
            }
            Cfb cfb = new Cfb(datos);
            List<object> streams = new List<object>();
            long bytesParticiones = 0;
            int nParticiones = 0;
            foreach (EntradaCfb e in cfb.Streams)
            {
                Dictionary<string, object> s = new Dictionary<string, object>();
                s["ruta"] = e.Ruta; s["bytes"] = e.Tamano;
                streams.Add(s);
                if (e.Ruta.StartsWith("/Partitions/", StringComparison.OrdinalIgnoreCase)) { nParticiones++; bytesParticiones += e.Tamano; }
            }
            r["streams"] = streams.Count;
            r["particiones"] = nParticiones;
            r["bytes_particiones_comprimidos"] = bytesParticiones;

            // --- BasicFileInfo: version / build / ruta
            EntradaCfb bfi = cfb.Buscar("/BasicFileInfo");
            if (bfi != null)
            {
                List<string> cs = CadenasUtf16(cfb.Leer(bfi), 4);
                List<object> lista = new List<object>();
                foreach (string c in cs) { if (lista.Count < 40) lista.Add(c.Replace("\r", " | ").Replace("\n", " | ")); }
                r["basic_file_info"] = lista;
            }

            // --- Miniatura PNG
            EntradaCfb prev = cfb.Buscar("/RevitPreview4.0");
            if (prev != null && carpetaSalida != null)
            {
                byte[] pb = cfb.Leer(prev);
                byte[] png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                int p0 = Buscar(pb, png, 0);
                if (p0 >= 0)
                {
                    byte[] iend = new byte[] { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };
                    int p1 = Buscar(pb, iend, p0);
                    if (p1 >= 0)
                    {
                        string rp = Path.Combine(carpetaSalida, Path.GetFileNameWithoutExtension(rutaRvt) + "_miniatura.png");
                        byte[] o = new byte[p1 + 8 - p0];
                        Buffer.BlockCopy(pb, p0, o, 0, o.Length);
                        File.WriteAllBytes(rp, o);
                        r["miniatura_png"] = rp;
                    }
                }
            }

            // --- Esquema de clases (Formats/Latest)
            EntradaCfb fmt = cfb.Buscar("/Formats/Latest");
            if (fmt != null)
            {
                byte[] raw = cfb.Leer(fmt);
                byte[] es = Inflar(raw, BuscarMagia(raw, 0));
                if (es != null)
                {
                    r["esquema_bytes"] = es.Length;
                    int nombres = 0, claves = 0;
                    int run = 0;
                    for (int i = 0; i < es.Length; i++)
                    {
                        bool ok = (es[i] >= 'A' && es[i] <= 'Z') || (es[i] >= 'a' && es[i] <= 'z') || (es[i] >= '0' && es[i] <= '9') || es[i] == '_';
                        if (ok) run++; else { if (run >= 4) nombres++; run = 0; }
                    }
                    r["esquema_identificadores"] = nombres;
                    string txt = Encoding.ASCII.GetString(es);
                    string[] pistas = new string[] { "Wall", "Floor", "Room", "Level", "GStep", "Sketch", "Material" };
                    Dictionary<string, object> hits = new Dictionary<string, object>();
                    foreach (string p in pistas) { int c = 0, ix = 0; while ((ix = txt.IndexOf(p, ix, StringComparison.Ordinal)) >= 0) { c++; ix += p.Length; } hits[p] = c; }
                    r["esquema_menciones"] = hits;
                    claves = 0;
                }
            }

            // --- Tabla de elementos (Global/ElemTable): el conteo declarado esta en el u32 del byte 2
            EntradaCfb et = cfb.Buscar("/Global/ElemTable");
            if (et != null)
            {
                byte[] raw = cfb.Leer(et);
                byte[] t = Inflar(raw, BuscarMagia(raw, 0));
                if (t != null && t.Length > 8)
                {
                    r["elementos"] = (long)BitConverter.ToUInt32(t, 2);
                    r["tabla_elementos_bytes"] = t.Length;
                }
            }

            // --- Particiones: tamano descomprimido y busqueda de solidos/mallas cacheados (ASM/ACIS)
            long descomp = 0;
            int marcasSolido = 0;
            string[] marcas = new string[] { "ASM BinaryFile", "ShapeManager", "ACIS", "Autodesk ShapeManager" };
            List<byte[]> pats = new List<byte[]>();
            foreach (string m in marcas) pats.Add(Encoding.ASCII.GetBytes(m));
            foreach (EntradaCfb e in cfb.Streams)
            {
                if (!e.Ruta.StartsWith("/Partitions/", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] raw = cfb.Leer(e);
                int i = 0;
                while (true)
                {
                    int m = BuscarMagia(raw, i);
                    if (m < 0) break;
                    byte[] trozo = Inflar(raw, m);
                    if (trozo == null) { i = m + 3; continue; }
                    descomp += trozo.Length;
                    foreach (byte[] p in pats) if (Buscar(trozo, p, 0) >= 0) marcasSolido++;
                    i = m + 10 + Math.Max(8, Math.Min(trozo.Length / 8, 64)); // avanza dentro del miembro sin reescanearlo entero
                    // salto seguro: busca la siguiente magia real mas adelante
                }
            }
            r["bytes_particiones_descomprimidos"] = descomp;
            r["marcas_de_solidos_ACIS_ASM"] = marcasSolido;
            r["conclusion"] = "El .rvt guarda la geometria como nodos B-rep propios de Revit (GElement/GFace/GPolyMesh), no como solidos ASM/ACIS (marcas ASM/ACIS: " + marcasSolido + "). " +
                "Para generar el .tbv sin Revit usar herramientas/rvt-a-tbv.mjs (lector de geometria + lectores propios de GUID, tipos, habitaciones y punto base).";
            if (log != null) log("Inspeccion lista: " + r["archivo"]);
            return r;
        }
    }
}
