using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

	public static class ExportarGeometria
	{
	    private const int FORMAT_MAGIC = 0x32544254; // "TBT2" en little-endian
	    // v4: cabecera con el Punto Base del Proyecto (3 floats) antes de los bloques.
	    // v5: las coordenadas se escriben en METROS, relativas al Punto Base (v4 y anteriores
	    //     iban en pies de Revit, sin relativizar). Ver ObtenerPuntoBasePies/PIES_A_METROS.
	    // v6: cada bloque agrega el patrón de superficie (hatch) del material como DOS floats
	    //     -- ángulo en radianes y separación entre líneas en metros -- justo después del
	    //     RGBA. Sólo los muros lo calculan (ver ObtenerHatchMaterial); el resto escribe
	    //     (0,0) = sin hatch.
	    // v7: cada bloque agrega DOS strings length-prefixed (BinaryWriter.Write(string)) con
	    //     el nombre de Familia y de Tipo del ElementType, justo DESPUÉS de categoryId y
	    //     ANTES del RGBA. "" si el elemento no tiene ElementType.
	    // Layout idéntico, campo a campo, al escritor principal
	    // (Ejecutador/Features/GeometryExport/ExportadorGeometria.cs) y a LeerBinario()
	    // (PostProcesadoEXE/Codigo/ConvertidorGeometrias/Program.cs). Esta macro no exporta
	    // habitaciones (OST_Rooms no está en 'categories'), así que el bloque de metadata de
	    // habitación de v4 (Nivel/Departamento/Nombre) nunca se escribe ni hace falta acá: el
	    // lector sólo lo pide cuando CategoryId == OST_Rooms, y eso no ocurre en este export.
	    private const int FORMAT_VERSION = 7;

	    // Factor de conversión de pies (unidad interna de Revit) a metros.
	    private const double PIES_A_METROS = 0.3048;

	    // Sólo los muros llevan hatch: ver ObtenerHatchMaterial.
	    private const int CAT_MUROS = -2000011; // BuiltInCategory.OST_Walls

	    // Límites de cordura para la separación del patrón (metros); fuera de la banda: sin hatch.
	    private const double HATCH_SEP_MIN = 0.002;  // 2 mm
	    private const double HATCH_SEP_MAX = 5.0;    // 5 m

	    public static void ExportRawDump(Document doc, string filePath = @"C:\.TBT\Proyectos\_Revit_EXE_Geometrias\PostProcesadoEXE\IN\export2.bin")
	    {
	        var categories = new List<BuiltInCategory>
	        {
	            BuiltInCategory.OST_Walls,
	            BuiltInCategory.OST_Floors,
	            BuiltInCategory.OST_Roofs,
	            BuiltInCategory.OST_Doors,
	            BuiltInCategory.OST_Windows,
	            BuiltInCategory.OST_StructuralColumns,
	            BuiltInCategory.OST_StructuralFraming,
	            BuiltInCategory.OST_Furniture,
	            BuiltInCategory.OST_PlumbingFixtures
	        };
	        var catFilter = new ElementMulticategoryFilter(categories);
	        var geomOptions = new Options { DetailLevel = ViewDetailLevel.Fine };

	        var collector = new FilteredElementCollector(doc)
	            .WherePasses(catFilter)
	            .WhereElementIsNotElementType();

	        string directoryPath = Path.GetDirectoryName(filePath);
	        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
	        {
	            Directory.CreateDirectory(directoryPath);
	        }

	        var colorCache = new Dictionary<int, byte[]>();
	        // Cache del patrón por MaterialId (v6): evita resolver FillPatternElement/GetFillGrids
	        // por cara cuando miles de caras comparten el mismo material.
	        var hatchCache = new Dictionary<int, float[]>();
	        // Cache de [familia, tipo] por TypeId (v7): miles de instancias comparten el mismo tipo.
	        var tipoCache = new Dictionary<int, string[]>();

	        // v5: Punto Base del Proyecto en pies (coordenadas internas de Revit).
	        XYZ puntoBase = ObtenerPuntoBasePies(doc);

	        using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096 * 10))
	        using (var writer = new BinaryWriter(fs))
	        {
	            writer.Write(FORMAT_MAGIC);
	            writer.Write(FORMAT_VERSION);

	            // v4/v5: Punto Base del Proyecto, en METROS.
	            writer.Write((float)(puntoBase.X * PIES_A_METROS));
	            writer.Write((float)(puntoBase.Y * PIES_A_METROS));
	            writer.Write((float)(puntoBase.Z * PIES_A_METROS));

	            foreach (Element elem in collector)
	            {
	                try
	                {
	                    ExportarElemento(doc, elem, geomOptions, writer, colorCache, hatchCache, tipoCache, puntoBase);
	                }
	                catch
	                {
	                    // Un elemento con geometría corrupta no debe abortar el export completo
	                }
	            }
	        }
	    }

	    private static void ExportarElemento(Document doc, Element elem, Options geomOptions,
	                                         BinaryWriter writer, Dictionary<int, byte[]> colorCache,
	                                         Dictionary<int, float[]> hatchCache,
	                                         Dictionary<int, string[]> tipoCache, XYZ puntoBase)
	    {
	        GeometryElement geomElem = elem.get_Geometry(geomOptions);
	        if (geomElem == null) return;

	        int elementId = elem.Id.IntegerValue;
	        string guid = elem.UniqueId;
	        // Id de BuiltInCategory (negativo, p.ej. Muros = -2000011); 0 si no tiene categoría
	        int categoryId = elem.Category != null ? elem.Category.Id.IntegerValue : 0;

	        // v7: familia y tipo se resuelven UNA vez por elemento (se escriben por cara).
	        string[] famTipo = ObtenerFamiliaTipo(doc, elem, tipoCache);
	        string familia = famTipo[0];
	        string tipo = famTipo[1];

	        List<Solid> solids = ObtenerTodosLosSolidos(geomElem);

	        foreach (Solid solid in solids)
	        {
	            foreach (Face face in solid.Faces)
	            {
	                Mesh mesh = face.Triangulate();
	                if (mesh == null || mesh.NumTriangles == 0 || mesh.Vertices.Count == 0) continue;

	                int materialId = face.MaterialElementId != null ? face.MaterialElementId.IntegerValue : -1;
	                byte[] rgba = ObtenerColorMaterial(doc, elem, face.MaterialElementId, colorCache);
	                // v6: sólo muros calculan el patrón real. El resto escribe (0,0) = sin hatch.
	                float[] hatch = (categoryId == CAT_MUROS)
	                    ? ObtenerHatchMaterial(doc, elem, face.MaterialElementId, hatchCache)
	                    : SIN_HATCH;

	                writer.Write(elementId);
	                writer.Write(guid);
	                writer.Write(materialId);
	                writer.Write(categoryId); // v3: categoría (el conversor protege muros/suelos/techos)
	                writer.Write(familia);    // v7: FamilyName del ElementType ("" si no tiene)
	                writer.Write(tipo);       // v7: Name del ElementType ("" si no tiene)
	                writer.Write(rgba[0]); // R
	                writer.Write(rgba[1]); // G
	                writer.Write(rgba[2]); // B
	                writer.Write(rgba[3]); // A (vidrios < 255 según transparencia del material)
	                writer.Write(hatch[0]); // v6: ángulo del patrón (radianes)
	                writer.Write(hatch[1]); // v6: separación entre líneas (metros); <= 0 = sin hatch

	                int vertexCount = mesh.Vertices.Count;
	                writer.Write(vertexCount);
	                foreach (XYZ v in mesh.Vertices)
	                {
	                    // v5: relativo al Punto Base y convertido a metros
	                    writer.Write((float)((v.X - puntoBase.X) * PIES_A_METROS));
	                    writer.Write((float)((v.Y - puntoBase.Y) * PIES_A_METROS));
	                    writer.Write((float)((v.Z - puntoBase.Z) * PIES_A_METROS));
	                }

	                int triangleCount = mesh.NumTriangles;
	                writer.Write(triangleCount);
	                for (int i = 0; i < triangleCount; i++)
	                {
	                    MeshTriangle tri = mesh.get_Triangle(i);
	                    writer.Write(tri.get_Index(0));
	                    writer.Write(tri.get_Index(1));
	                    writer.Write(tri.get_Index(2));
	                }
	            }
	        }
	    }

	    /// <summary>Valor compartido para "este bloque no lleva patrón" (evita asignar por cara).</summary>
	    private static readonly float[] SIN_HATCH = new float[] { 0f, 0f };

	    /// <summary>Valor compartido para "este elemento no tiene ElementType" (v7).</summary>
	    private static readonly string[] SIN_TIPO = new string[] { "", "" };

	    /// <summary>
	    /// v5: Punto Base del Proyecto en coordenadas internas de Revit (pies). Mismo criterio
	    /// que el escritor principal: usa el LocationPoint del elemento (el bounding box del
	    /// Punto Base es la caja del pictograma, no su posición). Nunca lanza: sin Punto Base
	    /// o ante cualquier error, cae a XYZ.Zero.
	    /// </summary>
	    private static XYZ ObtenerPuntoBasePies(Document doc)
	    {
	        try
	        {
	            Element pBase = new FilteredElementCollector(doc)
	                .OfCategory(BuiltInCategory.OST_ProjectBasePoint)
	                .WhereElementIsNotElementType()
	                .FirstElement();

	            if (pBase == null) return XYZ.Zero;

	            LocationPoint loc = pBase.Location as LocationPoint;
	            if (loc != null && loc.Point != null) return loc.Point;

	            // Sin LocationPoint: el centro de la caja del pictograma es el punto; el Min no.
	            BoundingBoxXYZ bb = pBase.get_BoundingBox(null);
	            if (bb != null) return (bb.Min + bb.Max) * 0.5;

	            return XYZ.Zero;
	        }
	        catch
	        {
	            return XYZ.Zero;
	        }
	    }

	    /// <summary>
	    /// v7: devuelve [FamilyName, Name] del ElementType del elemento, cacheado por TypeId.
	    /// Devuelve ["",""] cuando el elemento no tiene tipo (in-situ) o la API lanza al leer
	    /// FamilyName/Name. Nunca devuelve null (rompería BinaryWriter.Write(string)).
	    /// </summary>
	    private static string[] ObtenerFamiliaTipo(Document doc, Element elem, Dictionary<int, string[]> cache)
	    {
	        try
	        {
	            ElementId typeId = elem.GetTypeId();
	            if (typeId == null || typeId == ElementId.InvalidElementId) return SIN_TIPO;

	            int key = typeId.IntegerValue;
	            string[] cacheado;
	            if (cache.TryGetValue(key, out cacheado)) return cacheado;

	            string[] resultado = SIN_TIPO;
	            ElementType et = doc.GetElement(typeId) as ElementType;
	            if (et != null)
	            {
	                string familia = "";
	                string tipo = "";
	                try { familia = et.FamilyName ?? ""; } catch { }
	                try { tipo = et.Name ?? ""; } catch { }
	                if (familia.Length != 0 || tipo.Length != 0)
	                    resultado = new string[] { familia, tipo };
	            }

	            cache[key] = resultado;
	            return resultado;
	        }
	        catch
	        {
	            return SIN_TIPO;
	        }
	    }

	    /// <summary>
	    /// v6: patrón de superficie del material como { ángulo (rad), separación (m) }, tomando
	    /// el primer FillGrid del SurfaceForegroundPatternId -- mismo criterio que el escritor
	    /// principal (sólo patrones de tipo Model, sin relleno sólido, separación dentro de
	    /// [HATCH_SEP_MIN, HATCH_SEP_MAX]). No reproduce el diagnóstico detallado que ahí se
	    /// vuelca a Bitacora: esta macro no tiene esa infraestructura de log, así que ante
	    /// cualquier caso no representable o excepción devuelve SIN_HATCH en silencio.
	    /// </summary>
	    private static float[] ObtenerHatchMaterial(Document doc, Element elem, ElementId materialElementId,
	                                                Dictionary<int, float[]> cache)
	    {
	        Material mat = null;
	        try
	        {
	            int idCara = materialElementId != null ? materialElementId.IntegerValue : -1;
	            if (idCara >= 0) mat = doc.GetElement(materialElementId) as Material;

	            // Paridad con ObtenerColorMaterial: si la cara no declara material propio, cae
	            // al material de la categoría.
	            if (mat == null && elem != null && elem.Category != null)
	            {
	                mat = elem.Category.Material;
	            }
	        }
	        catch
	        {
	            return SIN_HATCH;
	        }

	        if (mat == null) return SIN_HATCH;

	        // La caché va por el material RESUELTO, no por el id que venía de la cara: si no,
	        // todos los muros "Por categoría" (cara = -1) compartirían una sola entrada.
	        int key = mat.Id.IntegerValue;
	        float[] cached;
	        if (cache.TryGetValue(key, out cached)) return cached;

	        float[] resultado = SIN_HATCH;
	        try
	        {
	            if (mat.SurfaceForegroundPatternId != ElementId.InvalidElementId)
	            {
	                var fpe = doc.GetElement(mat.SurfaceForegroundPatternId) as FillPatternElement;
	                FillPattern fp = (fpe != null) ? fpe.GetFillPattern() : null;

	                // Se descartan: relleno sólido (ya cubierto por el color plano) y patrones de
	                // Dibujo/Drafting (escala de papel, sin tamaño real en 3D).
	                if (fp != null && !fp.IsSolidFill && fp.Target == FillPatternTarget.Model)
	                {
	                    IList<FillGrid> grids = fp.GetFillGrids();
	                    if (grids != null && grids.Count > 0)
	                    {
	                        FillGrid g = grids[0];
	                        double sep = g.Offset * PIES_A_METROS;
	                        if (sep >= HATCH_SEP_MIN && sep <= HATCH_SEP_MAX)
	                        {
	                            resultado = new float[] { (float)g.Angle, (float)sep };
	                        }
	                    }
	                }
	            }
	        }
	        catch
	        {
	            // Un material con patrón corrupto no puede tumbar el export: sin hatch.
	            resultado = SIN_HATCH;
	        }

	        cache[key] = resultado;
	        return resultado;
	    }

	    private static byte[] ObtenerColorMaterial(Document doc, Element elem, ElementId materialElementId,
	                                               Dictionary<int, byte[]> cache)
	    {
	        int key = materialElementId != null ? materialElementId.IntegerValue : -1;
	        byte[] cached;
	        if (key >= 0 && cache.TryGetValue(key, out cached)) return cached;

	        Material mat = null;
	        if (key >= 0) mat = doc.GetElement(materialElementId) as Material;

	        bool esDelMaterial = mat != null;
	        if (mat == null && elem.Category != null) mat = elem.Category.Material;

	        byte r = 190, g = 190, b = 190, a = 255; // gris neutro si no hay material
	        if (mat != null)
	        {
	            Color col = mat.Color;
	            if (col != null && col.IsValid)
	            {
	                r = col.Red;
	                g = col.Green;
	                b = col.Blue;
	            }

	            int t = mat.Transparency; // 0 (opaco) .. 100 (invisible)
	            if (t > 0)
	            {
	                int alpha = (int)Math.Round(255.0 * (100 - t) / 100.0);
	                if (alpha < 20) alpha = 20;   // piso: que el vidrio nunca desaparezca del todo
	                if (alpha > 255) alpha = 255;
	                a = (byte)alpha;
	            }
	        }

	        byte[] rgba = new byte[] { r, g, b, a };
	        if (key >= 0 && esDelMaterial) cache[key] = rgba;
	        return rgba;
	    }

	    private static List<Solid> ObtenerTodosLosSolidos(GeometryElement geomElem)
	    {
	        List<Solid> solidos = new List<Solid>();
	        if (geomElem == null) return solidos;

	        foreach (GeometryObject obj in geomElem)
	        {
	            if (obj is Solid)
	            {
	                Solid solido = obj as Solid;
	                if (solido != null && solido.Faces.Size > 0 && solido.Volume > 0)
	                {
	                    solidos.Add(solido);
	                }
	            }
	            else if (obj is GeometryInstance)
	            {
	                GeometryInstance instancia = obj as GeometryInstance;
	                GeometryElement geometriaInterna = instancia.GetInstanceGeometry();
	                solidos.AddRange(ObtenerTodosLosSolidos(geometriaInterna));
	            }
	        }
	        return solidos;
	    }
	}
