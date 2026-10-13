@echo off
rem Runs neorepl on cleat: the cleat command on PATH, or the one %CLEAT% names.
setlocal
set "here=%~dp0.."
if "%CLEAT%"=="" set "CLEAT=cleat"
"%CLEAT%" -I "%here%\..\nrepl\src" -I "%here%\..\tools.build\src" -I "%here%\..\tools.namespace\src" -I "%here%\src" -m neorepl.main %*
