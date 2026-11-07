@echo off
REM Builds BiosFlash Studio into .\dist
REM   build.bat          -> Windows x64, framework-dependent single EXE (needs the .NET 10 Desktop Runtime)
REM   build.bat portable -> Windows x64, self-contained single EXE (runs on any Windows 10/11 x64 PC)
REM   build.bat installer -> Windows setup .exe (Inno Setup 6) in dist\installers
REM   build.bat linux    -> Linux x64 + arm64 self-contained builds (copy the folder/tar.gz to the Linux PC,
REM                         then: sudo sh install.sh).  .deb/.rpm packages: run packaging/linux/build-packages.sh on Linux.
setlocal
cd /d "%~dp0"
set PUB=-c Release -p:PublishSingleFile=true -p:DebugType=none -p:IncludeNativeLibrariesForSelfExtract=true

if /I "%1"=="installer" (
  dotnet publish src\BiosFlashStudio.csproj %PUB% -r win-x64 --self-contained true -p:EnableCompressionInSingleFile=true -o dist\portable || goto :fail
  set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
  if not exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
  call "%%ISCC%%" /Q packaging\windows\BiosFlashStudio.iss || goto :fail
  goto :done
)

if /I "%1"=="portable" (
  dotnet publish src\BiosFlashStudio.csproj %PUB% -r win-x64 --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist\portable || goto :fail
  goto :done
)

REM Git Bash's GNU tar sets proper Unix permissions in the tarball (Windows tar cannot)
set "GITBASH=%ProgramFiles%\Git\bin\bash.exe"

if /I "%1"=="linux" (
  for %%A in (x64 arm64) do (
    if exist dist\linux-%%A rmdir /s /q dist\linux-%%A
    dotnet publish src\BiosFlashStudio.csproj %PUB% -r linux-%%A --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist\linux-%%A || goto :fail
    copy /y packaging\linux\install.sh dist\linux-%%A\ >nul
    copy /y packaging\linux\uninstall.sh dist\linux-%%A\ >nul
    copy /y packaging\linux\biosflash-studio.desktop dist\linux-%%A\ >nul
    copy /y packaging\linux\biosflash-studio.svg dist\linux-%%A\ >nul
    copy /y packaging\linux\60-biosflash-ch341a.rules dist\linux-%%A\ >nul
    copy /y packaging\linux\README-LINUX.md dist\linux-%%A\ >nul
    if exist "%GITBASH%" ( "%GITBASH%" packaging/linux/tarball.sh %%A 2.1.0 ) else ( tar -C dist -czf dist\biosflash-studio-2.1.0-linux-%%A.tar.gz linux-%%A )
  )
  goto :done
)

dotnet publish src\BiosFlashStudio.csproj %PUB% -r win-x64 --self-contained false -o dist || goto :fail

:done
echo.
echo Done. Output in the dist folder.
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1
