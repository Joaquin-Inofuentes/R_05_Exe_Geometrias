// Util.cs -- JSON, registro y utilidades chicas.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace RvtATbv
{
    public static class Util
    {
        public static string LogRuta;

        public static void Log(string m)
        {
            string l = DateTime.Now.ToString("HH:mm:ss") + "  " + m;
            Console.WriteLine(l);
            try { if (LogRuta != null) File.AppendAllText(LogRuta, l + Environment.NewLine, new UTF8Encoding(false)); } catch (Exception) { }
        }

        public static string Json(object o)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return Indentar(js.Serialize(o));
        }

        public static Dictionary<string, object> LeerJson(string ruta)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<Dictionary<string, object>>(File.ReadAllText(ruta, Encoding.UTF8));
        }

        // Indentado simple (la salida de JavaScriptSerializer es compacta).
        private static string Indentar(string s)
        {
            StringBuilder sb = new StringBuilder();
            int n = 0; bool str = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (str) { sb.Append(c); if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); } else if (c == '"') str = false; continue; }
                if (c == '"') { str = true; sb.Append(c); continue; }
                if (c == '{' || c == '[')
                {
                    char cierre = c == '{' ? '}' : ']';
                    if (i + 1 < s.Length && s[i + 1] == cierre) { sb.Append(c).Append(cierre); i++; continue; }
                    sb.Append(c).Append('\n'); n++; sb.Append(' ', n * 2);
                }
                else if (c == '}' || c == ']') { sb.Append('\n'); n--; sb.Append(' ', n * 2); sb.Append(c); }
                else if (c == ',') { sb.Append(c).Append('\n'); sb.Append(' ', n * 2); }
                else if (c == ':') sb.Append(": ");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string NombreSeguro(string nombre)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in nombre)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.') sb.Append(c);
                else if (c == ' ') sb.Append('_');
                else sb.Append('_');
            }
            return sb.ToString();
        }

        // ---- memoria libre
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public static double RamLibreGB()
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(m)) return -1;
            return m.ullAvailPhys / 1073741824.0;
        }

        public static bool EsDentroDe(string ruta, string carpeta)
        {
            string a = Path.GetFullPath(ruta).TrimEnd('\\') + "\\";
            string b = Path.GetFullPath(carpeta).TrimEnd('\\') + "\\";
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
