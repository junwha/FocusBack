@echo off
rem Builds FocusBack.exe (Windows frontend, in this folder). Needs nothing installed: the C# compiler ships with Windows, and the
rem shared core (pq_core.dll) is downloaded from this repo's GitHub releases.
rem   build.bat          download the core matching this checkout, then compile
rem   build.bat --local  use the pq_core.dll already in this folder (CI / your own build)
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if /i "%~1"=="--local" goto compile

set "CORE="
for /f %%h in ('git rev-parse --short^=12 HEAD:core 2^>nul') do set "CORE=%%h"
if "%CORE%"=="" (
  if exist pq_core.dll goto compile
  echo Could not tell which core this checkout needs ^(git not found^).
  echo Install FocusBack-Setup-x64.exe from the latest release instead, or put pq_core.dll here.
  exit /b 1
)
set "HAVE="
if exist pq_core.ver set /p HAVE=<pq_core.ver
if "%HAVE%"=="%CORE%" if exist pq_core.dll goto compile
echo Downloading pq_core.dll (core %CORE%)...
curl -fsSL -o pq_core.dll "https://github.com/junwha/FocusBack/releases/download/core/pq_core-%CORE%-win-x64.dll"
if errorlevel 1 (
  echo Download failed. The core for this commit is published by CI a few minutes after it is pushed to main.
  exit /b 1
)
>pq_core.ver echo %CORE%

:compile
if not exist pq_core.dll ( echo pq_core.dll is missing. & exit /b 1 )
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /out:FocusBack.exe FocusBack.cs Core.cs
