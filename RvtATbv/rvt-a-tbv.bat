@echo off
rem rvt-a-tbv.bat -- .rvt -> _Geometria.tbv + _Geometria_Habitaciones.json SIN Revit. Solo lee el original (trabaja sobre una copia).
rem Uso:  rvt-a-tbv.bat "C:\ruta\absoluta\archivo.rvt" [--salida carpeta] [--trabajo carpeta] [--reviter carpeta] [--base x,y,z]
rem Requiere Node.js 22.13 o mas nuevo y la libreria reviter instalada (ver LEEME.md).
setlocal
set "AQUI=%~dp0"
if "%~1"=="" (
  echo Uso: rvt-a-tbv.bat "C:\ruta\absoluta\archivo.rvt" [opciones]
  exit /b 2
)
node --expose-gc --max-old-space-size=6000 --experimental-strip-types "%AQUI%herramientas\rvt-a-tbv.mjs" %*
exit /b %errorlevel%
