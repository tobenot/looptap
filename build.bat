@echo off
setlocal
cd /d "%~dp0"
if not exist bin mkdir bin
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc not found: %CSC%
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /utf8output /warn:4 /win32manifest:src\app.manifest /out:bin\LoopTap.exe src\*.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK: bin\LoopTap.exe
exit /b 0
