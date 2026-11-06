@echo off
setlocal EnableExtensions

REM =================================================================================================
REM copiar_resultados.bat -- encola un lote de modelos para el addin MIP y deja Revit abierto.
REM =================================================================================================
REM RUTAS. Hay TRES carpetas en juego y confundirlas fue el bug que rompio este bat:
REM
REM   INSTALL  C:\ProgramData\Autodesk\Revit\Addins\2021\MIP  (la DEFINITIVA, todos los usuarios)
REM            Binarios (DLL + exes del pipeline) + la semilla de resultados.txt. De SOLO LECTURA:
REM            solo el instalador y el build escriben ahi. Es donde vive ESTE bat, asi que se
REM            deduce con %~dp0 y no se hardcodea: si manana el instalador cambia de carpeta, el
REM            bat la sigue sin tocar una linea.
REM
REM   DATA     %LOCALAPPDATA%\MIP
REM            Estado en runtime del addin: config.json, pedidos.txt, tomados.txt, Logs, resultados.txt.
REM            Es por-usuario y escribible sin elevar (ver ConfigurationManager._runtimeDataFolder y la
REM            historia larga en tools\InstallerApp.cs).
REM
REM   OJO      Hasta 2026-09 el estado vivia AL LADO de los binarios (en INSTALL). Este bat apuntaba
REM            ahi, y cuando el estado se mudo a DATA, "if exist tomados.txt" daba falso, "if exist
REM            resultados.txt" daba falso, e imprimia "ERROR: resultados.txt no existe" y salia sin
REM            encolar NADA. El sintoma era "abre Revit pero no procesa". El estado va SIEMPRE a DATA;
REM            el instalador migra solo lo que haya quedado en INSTALL (MigrarEstadoLegacy).
set "INSTALL=%~dp0"
set "DATA=%LOCALAPPDATA%\MIP"

if not exist "%DATA%" mkdir "%DATA%" >nul 2>&1
if not exist "%DATA%" (
    echo ERROR: no se pudo crear la carpeta de datos "%DATA%".
    goto :FIN
)

REM =========================
REM 1. SEMBRAR resultados.txt EN DATA SI FALTA
REM =========================
REM resultados.txt es el maestro de rutas de vinculos: ademas de ser la fuente de la cola, el addin lo
REM lee en runtime desde DATA (GestorConfiguracion.ResultadosFile -> AsistenteArchivos.ObtenerDiccionarioVinculos)
REM para redirigir los vinculos de cada modelo. El instalador lo extrae del zip a INSTALL junto con los
REM binarios, NO a DATA, asi que en una instalacion limpia el addin lo busca en DATA y no lo encuentra:
REM el diccionario sale con 0 rutas y los vinculos no se redireccionan. Copiarlo aca deja las dos cosas
REM consistentes con una sola operacion.
REM Solo se copia SI FALTA: una vez en DATA es un archivo que el usuario edita (que modelos exportar), y
REM pisarlo en cada corrida con la semilla de fabrica le borraria los cambios.
if not exist "%DATA%\resultados.txt" (
    if exist "%INSTALL%resultados.txt" (
        copy "%INSTALL%resultados.txt" "%DATA%\resultados.txt" /Y >nul
        echo Sembrado resultados.txt en "%DATA%" desde la carpeta de instalacion.
    )
)

REM =========================
REM 2. NO PISAR UNA COLA QUE YA TIENE TRABAJO
REM =========================
REM El addin toma pedidos.txt y lo ACUMULA en tomados.txt, asi que cada corrida del bat volvia a encolar
REM los MISMOS modelos encima de los que ya estaban pendientes. Medido el 2026-08-18: tres corridas
REM dejaron tomados.txt con 36 lineas para 12 modelos reales, o sea cada modelo exportandose 3 veces.
REM
REM Se agrava con el relanzamiento automatico tras un SubmitPrint() colgado (ver
REM ImageProcessing.AbortarPorSubmitPrintColgado): ahi Revit se reabre SOLO y retoma tomados.txt, asi
REM que si ademas alguien corre el bat, la cola se vuelve a duplicar.
REM
REM Regla: si tomados.txt tiene contenido, hay un lote vivo y no se encola nada. El addin lo termina
REM solo. Igual se sigue al paso 4, porque si ese lote quedo a medias con Revit cerrado hay que
REM reabrirlo para que lo retome.
REM
REM Se mira el TAMANO del archivo y no la cantidad de lineas: un "for /f" que cuente lineas adentro de
REM un bloque IF necesita expansion retardada para poder leer el resultado, y ese es un clasico de bat
REM que falla en silencio (la variable sale vacia y el IF nunca entra). Con %%~zI el dato esta
REM disponible en el acto y no hay nada que expandir despues.
set "HAYLOTE="
if exist "%DATA%\tomados.txt" for %%I in ("%DATA%\tomados.txt") do if %%~zI GTR 0 set "HAYLOTE=1"

if defined HAYLOTE (
    echo Ya hay un lote en curso en tomados.txt: no se vuelve a encolar para no duplicarlo.
    echo Para forzar una cola nueva: borrar "%DATA%\tomados.txt" y volver a correr este bat.
    goto :REVIT
)

REM =========================
REM 3. ENCOLAR (solo si no habia lote activo)
REM =========================
REM Se copia ANTES de lanzar Revit a proposito. VigilanteArchivosRevit.Start() mira el tamano de
REM pedidos.txt al arrancar y levanta el evento si tiene contenido, ademas de montar el FileSystemWatcher.
REM Dejando el archivo listo primero, el lote entra por esa via si Revit estaba cerrado, y por el watcher
REM si ya estaba abierto. Al reves habia una ventana entre "Revit arranco" y "el watcher existe" donde el
REM cambio podia no ser visto por nadie.
if exist "%DATA%\resultados.txt" (
    copy "%DATA%\resultados.txt" "%DATA%\pedidos.txt" /Y >nul
    if errorlevel 1 (
        echo ERROR: fallo la copia de resultados.txt a pedidos.txt.
        goto :FIN
    )
    for %%I in ("%DATA%\pedidos.txt") do echo Encolado: resultados.txt -^> pedidos.txt ^(%%~zI bytes^)
) else (
    echo ERROR: no existe "%DATA%\resultados.txt" ni "%INSTALL%resultados.txt".
    echo         Sin ese archivo no hay lista de modelos que encolar.
    goto :FIN
)

REM =========================
REM 4. VERIFICAR / LANZAR REVIT
REM =========================
REM OJO con el filtro: NO usar "tasklist | find /I "Revit.exe"".
REM "Revit.exe" es SUBCADENA de "OptimizarDumpDeRevit.exe" (el watcher de geometria, que corre de forma
REM perpetua). Con el find suelto, ese proceso hacia match SIEMPRE, el bat creia que Revit ya estaba
REM abierto y NUNCA lo lanzaba -- pero igual copiaba pedidos.txt, con lo que el sintoma era "cambia
REM pedidos pero Revit no arranca". /FI "IMAGENAME eq Revit.exe" compara el nombre de imagen COMPLETO.
:REVIT
tasklist /FI "IMAGENAME eq Revit.exe" /NH | find /I "Revit.exe" >nul
IF %ERRORLEVEL% NEQ 0 (
    echo Revit no esta ejecutandose. Iniciando...
    IF EXIST "C:\Program Files\Autodesk\Revit 2021\Revit.exe" (
        start "" "C:\Program Files\Autodesk\Revit 2021\Revit.exe" /language ESP
    ) ELSE (
        echo ERROR: No se encontro Revit 2021 en "C:\Program Files\Autodesk\Revit 2021\Revit.exe"
        goto :FIN
    )
) else (
    echo Revit ya esta ejecutandose.
)

:FIN
echo Proceso terminado.
REM Se pausa para que el resultado sea legible: este bat se corre casi siempre por doble clic desde el
REM acceso directo del escritorio, y ahi la consola se cierra sola al terminar. Con /q no pausa, para
REM poder llamarlo desde otro script sin que quede colgado esperando una tecla.
if /I not "%~1"=="/q" pause
endlocal
exit /B 0
