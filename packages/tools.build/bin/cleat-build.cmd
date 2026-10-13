@echo off
rem Runs a build function of the project in the current directory (build.clj):
rem cleat-build FUNCTION [:key value]...   Uses cleat on PATH, or %CLEAT%.
setlocal
set here=%~dp0..
if "%CLEAT%"=="" set CLEAT=cleat
"%CLEAT%" -I "%here%\src" -I "%here%\..\tools.namespace\src" -I . -m clojure.tools.build.main %*
