@echo off
setlocal
cd /d "%~dp0"

echo =======================================================
echo   EXPORT.X - Budowanie aplikacji do pliku .EXE
echo =======================================================
echo.

where dotnet >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [BLAD] Nie znaleziono polecenia 'dotnet'!
    echo Aby skompilowac aplikacje ze zrodel, musisz zainstalowac .NET 10 SDK:
    echo https://dotnet.microsoft.com/download/dotnet/10.0
    echo.
    echo Jesli chcesz tylko URUCHOMIC program, pobierz gotowa paczke z:
    echo https://github.com/ItAxolotl/ExportX/releases
    echo.
    pause
    exit /b 1
)

echo [1/3] Kompilacja wersji Standalone (Self-Contained)...
dotnet publish ExportX.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\ExportX-Standalone

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [BLAD] Nie udalo sie zbudowac wersji Standalone!
    echo Upewnij sie, ze posiadasz zainstalowany .NET 10 SDK.
    echo.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/3] Kompilacja wersji Lekkiej (Framework-Dependent)...
dotnet publish ExportX.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\dist\ExportX-Lightweight

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [BLAD] Nie udalo sie zbudowac wersji Lekka!
    echo.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [3/3] Tworzenie paczki Portable (ZIP ze wszystkimi zaleznosciami)...
powershell -ExecutionPolicy Bypass -File .\build_portable.ps1

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [BLAD] Wystapil problem podczas tworzenia paczki Portable!
    echo.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo =======================================================
echo   SUKCES! Gotowe pliki:
echo   - .\dist\ExportX-Portable.zip (Paczka przenosna ZIP)
echo   - .\dist\ExportX-Standalone\ExportX.exe (Samodzielny .exe)
echo   - .\dist\ExportX-Lightweight\ExportX.exe (Lekki .exe)
echo =======================================================
echo.
pause

