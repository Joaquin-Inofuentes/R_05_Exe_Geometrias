@echo off
rem Compila RvtATbv.exe con el compilador de .NET Framework 4 que trae Windows (sin SDK ni descargas).
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "AQUI=%~dp0"
"%CSC%" /nologo /target:exe /optimize+ /out:"%AQUI%RvtATbv.exe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll "%AQUI%Cfb.cs" "%AQUI%Inspector.cs" "%AQUI%TbvInfo.cs" "%AQUI%Util.cs" "%AQUI%Program.cs"
if errorlevel 1 (echo FALLO la compilacion & exit /b 1)
echo OK: %AQUI%RvtATbv.exe
endlocal
