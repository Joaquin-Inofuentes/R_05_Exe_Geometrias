<!-- analizador:inicio -->
# _Revit_EXE_Geometrias — el mapa está en `0_MAPA_IA~/`

Antes de explorar el código: `cat 0_MAPA_IA~/IA_00_BRIEF.md`.
Con un bug: `grep -i 'palabra del síntoma' 0_MAPA_IA~/IA_70_SINTOMAS.*`.
Antes de afirmar que algo no existe: `grep -F 'ruta/del/archivo' 0_MAPA_IA~/IA_90_HUECOS*.jsonl`.

De qué partes está hecho (de `0_MAPA_IA~/IA_03_SUBPROYECTOS.jsonl`):

- `temp_decimator` · csharp-sln · raíz `PostProcesadoEXE/Codigo/references/temp_decimator` · **el principal**
- `webejemplo` · node · raíz `WebEjemplo`
- `codigo` · csharp-sln · raíz `PostProcesadoEXE/Codigo`
- `convertidorgeometrias` · csharp-csproj · raíz `PostProcesadoEXE/Codigo/ConvertidorGeometrias`
- `meshdecimatorlib` · csharp-csproj · raíz `PostProcesadoEXE/Codigo/ConvertidorGeometrias/MeshDecimatorLib`
- `temp_simplifier` · node · raíz `PostProcesadoEXE/Codigo/references/temp_simplifier`
- `scripts` · scripts · raíz `PostProcesadoEXE/Codigo/references/temp_simplifier/.circleci/scripts` · `sc:tooling` (no es código del producto)

El campo `sub` de los demás OUTs apunta al `id` de una de esas partes.

El bloque de arranque, los greps y la lista de tools están en el `AGENTS.md` de la
raíz del parque. Una sola copia: dos copias se separan.
<!-- analizador:fin -->
