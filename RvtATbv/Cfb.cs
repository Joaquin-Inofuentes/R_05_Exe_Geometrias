// Cfb.cs -- lector minimo de Compound File Binary (OLE2) para analizar un .rvt. SOLO LECTURA.
// Se abre con FileShare.ReadWrite y nunca con permiso de escritura: el original no se puede modificar desde aca.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RvtATbv
{
    public class EntradaCfb
    {
        public int Id;
        public string Nombre;
        public int Tipo;            // 1 = storage, 2 = stream, 5 = raiz
        public uint Izq, Der, Hijo;
        public uint Inicio;
        public long Tamano;
        public string Ruta;
    }

    public class Cfb
    {
        private readonly byte[] d;
        private readonly int ssz, mssz;
        private readonly long corte;
        private readonly List<uint> fat = new List<uint>();
        private readonly List<uint> minifat = new List<uint>();
        private byte[] mini;
        public readonly List<EntradaCfb> Entradas = new List<EntradaCfb>();
        public readonly List<EntradaCfb> Streams = new List<EntradaCfb>();

        public Cfb(byte[] datos)
        {
            d = datos;
            if (d.Length < 512 || d[0] != 0xD0 || d[1] != 0xCF || d[2] != 0x11 || d[3] != 0xE0)
                throw new InvalidDataException("No es un contenedor OLE2 (.rvt valido).");
            ssz = 1 << BitConverter.ToUInt16(d, 30);
            mssz = 1 << BitConverter.ToUInt16(d, 32);
            int nFat = (int)BitConverter.ToUInt32(d, 44);
            uint dirStart = BitConverter.ToUInt32(d, 48);
            corte = BitConverter.ToUInt32(d, 56);
            uint miniStart = BitConverter.ToUInt32(d, 60);
            uint nMini = BitConverter.ToUInt32(d, 64);
            uint difatStart = BitConverter.ToUInt32(d, 68);
            uint nDifat = BitConverter.ToUInt32(d, 72);

            List<uint> difat = new List<uint>();
            for (int i = 0; i < 109; i++) difat.Add(BitConverter.ToUInt32(d, 76 + 4 * i));
            uint s = difatStart;
            for (uint k = 0; k < nDifat && s < 0xFFFFFFFA; k++)
            {
                int off = (int)((s + 1L) * ssz);
                int n = ssz / 4;
                for (int i = 0; i < n - 1; i++) difat.Add(BitConverter.ToUInt32(d, off + 4 * i));
                s = BitConverter.ToUInt32(d, off + 4 * (n - 1));
            }
            for (int i = 0; i < nFat && i < difat.Count; i++)
            {
                if (difat[i] >= 0xFFFFFFFA) continue;
                int off = (int)((difat[i] + 1L) * ssz);
                for (int j = 0; j < ssz / 4; j++) fat.Add(BitConverter.ToUInt32(d, off + 4 * j));
            }
            if (nMini > 0)
            {
                foreach (uint sec in Cadena(miniStart))
                {
                    int off = (int)((sec + 1L) * ssz);
                    for (int j = 0; j < ssz / 4; j++) minifat.Add(BitConverter.ToUInt32(d, off + 4 * j));
                }
            }
            byte[] dir = LeerCadena(dirStart);
            for (int i = 0; i + 128 <= dir.Length; i += 128)
            {
                int nl = BitConverter.ToUInt16(dir, i + 64);
                EntradaCfb e = new EntradaCfb();
                e.Id = i / 128;
                e.Nombre = nl >= 2 ? Encoding.Unicode.GetString(dir, i, nl - 2) : "";
                e.Tipo = dir[i + 66];
                e.Izq = BitConverter.ToUInt32(dir, i + 68);
                e.Der = BitConverter.ToUInt32(dir, i + 72);
                e.Hijo = BitConverter.ToUInt32(dir, i + 76);
                e.Inicio = BitConverter.ToUInt32(dir, i + 116);
                e.Tamano = (long)BitConverter.ToUInt64(dir, i + 120);
                Entradas.Add(e);
            }
            if (Entradas.Count == 0) throw new InvalidDataException("Directorio OLE vacio.");
            mini = Entradas[0].Tamano > 0 ? Recortar(LeerCadena(Entradas[0].Inicio), Entradas[0].Tamano) : new byte[0];
            Recorrer(Entradas[0].Hijo, "");
        }

        private static byte[] Recortar(byte[] b, long n)
        {
            if (b.Length <= n) return b;
            byte[] r = new byte[n];
            Buffer.BlockCopy(b, 0, r, 0, (int)n);
            return r;
        }

        private IEnumerable<uint> Cadena(uint s)
        {
            int guarda = 0;
            while (s < 0xFFFFFFFA && s < fat.Count && guarda++ < 50000000)
            {
                yield return s;
                s = fat[(int)s];
            }
        }

        private byte[] LeerCadena(uint s)
        {
            MemoryStream ms = new MemoryStream();
            foreach (uint x in Cadena(s))
            {
                long off = (x + 1L) * ssz;
                if (off + ssz > d.Length) break;
                ms.Write(d, (int)off, ssz);
            }
            return ms.ToArray();
        }

        private void Recorrer(uint i, string baseRuta)
        {
            if (i == 0xFFFFFFFF || i >= Entradas.Count) return;
            EntradaCfb e = Entradas[(int)i];
            Recorrer(e.Izq, baseRuta);
            e.Ruta = baseRuta + "/" + e.Nombre;
            if (e.Tipo == 2) Streams.Add(e);
            if (e.Tipo == 1) Recorrer(e.Hijo, e.Ruta);
            Recorrer(e.Der, baseRuta);
        }

        public EntradaCfb Buscar(string ruta)
        {
            for (int i = 0; i < Streams.Count; i++)
                if (string.Equals(Streams[i].Ruta, ruta, StringComparison.OrdinalIgnoreCase)) return Streams[i];
            return null;
        }

        public byte[] Leer(EntradaCfb e)
        {
            if (e.Tamano < corte)
            {
                MemoryStream ms = new MemoryStream();
                uint s = e.Inicio;
                int guarda = 0;
                while (s < 0xFFFFFFFA && s < minifat.Count && ms.Length < e.Tamano && guarda++ < 10000000)
                {
                    long off = (long)s * mssz;
                    if (off + mssz > mini.Length) break;
                    ms.Write(mini, (int)off, mssz);
                    s = minifat[(int)s];
                }
                return Recortar(ms.ToArray(), e.Tamano);
            }
            return Recortar(LeerCadena(e.Inicio), e.Tamano);
        }
    }
}
