@echo off
rem Runs the project in the current directory, as `clj -M:alias` does:
rem   cleat-run -M:alias[:alias...] [args...]      Uses cleat on PATH, or %CLEAT%.
setlocal
set here=%~dp0..
if "%CLEAT%"=="" set CLEAT=cleat
"%CLEAT%" -I "%here%\src" -I "%here%\..\tools.namespace\src" -m clojure.tools.build.main %*
