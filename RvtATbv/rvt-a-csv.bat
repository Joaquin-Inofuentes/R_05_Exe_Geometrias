@echo off
rem rvt-a-csv.bat -- .rvt -> 3 CSV (crudo, profesional con medidas reales, catalogo de variantes) SIN Revit. Trabaja sobre una copia.
rem Uso:  rvt-a-csv.bat "C:\ruta\absoluta\archivo.rvt" [--salida carpeta] [--trabajo carpeta]
setlocal
set "AQUI=%~dp0"
if "%~1"=="" ( echo Uso: rvt-a-csv.bat "C:\ruta\absoluta\archivo.rvt" [opciones] & exit /b 2 )
node "%AQUI%herramientas\rvt-a-csv.mjs" %*
exit /b %errorlevel%
