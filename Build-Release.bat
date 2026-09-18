@echo off
rem PKG Sender release builder - run from D:\OpenCode\pkg-sender
rem Self-contained single-file: runs on any PC, no .NET runtime needed.
cd /d "%~dp0"
set DOTNET=D:\OpenCode\.dotnet\dotnet.exe
if not exist "%DOTNET%" set DOTNET=dotnet
"%DOTNET%" publish library\PkgSender.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o dist --nologo -v q
if errorlevel 1 (
  echo Publish FAILED
  pause
  exit /b 1
)
copy /y payload\pkg-receiver.elf dist\pkg-receiver.elf >nul
echo.
echo Published: dist\PkgSender.exe
set ISCC=iscc
where iscc >nul 2>nul
if errorlevel 1 set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" (
  echo Inno Setup (iscc) not found - install Inno Setup 6 to build the installer.
  echo Then run: iscc installer.iss
  pause
  exit /b 0
)
"%ISCC%" installer.iss
echo.
echo Built installer in current folder.
pause
