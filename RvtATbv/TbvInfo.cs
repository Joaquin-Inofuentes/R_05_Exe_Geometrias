// TbvInfo.cs -- resumen y validacion estructural de un .tbv (mismo formato que Visor3D/core/TbvParser.js, v1..v4).
using System;
using System.Collections.Generic;
using System.IO;

namespace RvtATbv
{
    public static class TbvInfo
    {
        public static Dictionary<string, object> Resumir(string ruta)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            byte[] b = File.ReadAllBytes(ruta);
            r["archivo"] = Path.GetFileName(ruta);
            r["bytes"] = b.Length;
            try
            {
                int o = 0;
                int magic = BitConverter.ToInt32(b, o); o += 4;
                if (magic != 0x56544254) throw new InvalidDataException("firma TBTV incorrecta");
                int ver = BitConverter.ToInt32(b, o); o += 4;
                if (ver < 1 || ver > 4) throw new InvalidDataException("version no soportada: " + ver);
                float upm = 1f;
                if (ver >= 2) { upm = BitConverter.ToSingle(b, o); o += 4; }
                int mat = BitConverter.ToInt32(b, o); o += 4;
                int pool = BitConverter.ToInt32(b, o); o += 4;
                int inst = BitConverter.ToInt32(b, o); o += 4;
                float[] mn = new float[3], mx = new float[3];
                for (int i = 0; i < 3; i++) { mn[i] = BitConverter.ToSingle(b, o); o += 4; }
                for (int i = 0; i < 3; i++) { mx[i] = BitConverter.ToSingle(b, o); o += 4; }
                for (int i = 0; i < mat; i++) { o += 8; if (ver >= 3) o += 8; }
                int nombres = 0;
                if (ver >= 4)
                {
                    nombres = BitConverter.ToInt32(b, o); o += 4;
                    for (int i = 0; i < nombres; i++) { int len = BitConverter.ToUInt16(b, o); o += 2 + len; }
                }
                long tris = 0, verts = 0;
                for (int i = 0; i < pool; i++)
                {
                    int vc = BitConverter.ToInt32(b, o); o += 4;
                    bool i16 = b[o++] == 1;
                    int tc = BitConverter.ToInt32(b, o); o += 4;
                    o += vc * 12 + vc * 3 + tc * 3 * (i16 ? 2 : 4);
                    tris += tc; verts += vc;
                }
                int minInst = ver >= 4 ? 62 : 54;
                Dictionary<int, int> porCat = new Dictionary<int, int>();
                for (int i = 0; i < inst; i++)
                {
                    int meshIdx = BitConverter.ToInt32(b, o);
                    int cat = BitConverter.ToInt32(b, o + 8);
                    int cur; porCat.TryGetValue(cat, out cur); porCat[cat] = cur + 1;
                    int guidLen = BitConverter.ToUInt16(b, o + minInst - 2);
                    o += minInst + guidLen;
                }
                r["version"] = ver;
                r["unidades_por_metro"] = upm;
                r["materiales"] = mat;
                r["nombres"] = nombres;
                r["mallas"] = pool;
                r["instancias"] = inst;
                r["triangulos"] = tris;
                r["vertices"] = verts;
                r["bbox_min"] = new double[] { mn[0], mn[1], mn[2] };
                r["bbox_max"] = new double[] { mx[0], mx[1], mx[2] };
                Dictionary<string, object> pc = new Dictionary<string, object>();
                foreach (KeyValuePair<int, int> kv in porCat) pc[kv.Key.ToString()] = kv.Value;
                r["instancias_por_categoria"] = pc;
                r["valido"] = (o == b.Length);
                if (o != b.Length) r["error"] = "sobran o faltan bytes: leidos " + o + " de " + b.Length;
            }
            catch (Exception ex)
            {
                r["valido"] = false;
                r["error"] = ex.Message;
            }
            return r;
        }
    }
}
