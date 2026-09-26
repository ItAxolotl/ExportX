@echo off
echo =======================================================
echo   EXPORT.X - Budowanie aplikacji do pliku .EXE
echo =======================================================
echo.

echo [1/3] Kompilacja wersji Standalone (Self-Contained)...
dotnet publish ExportX.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\ExportX-Standalone

if %ERRORLEVEL% NEQ 0 (
    echo [BLAD] Nie udalo sie zbudowac wersji Standalone!
    exit /b %ERRORLEVEL%
)

echo.
echo [2/3] Kompilacja wersji Lekkiej (Framework-Dependent)...
dotnet publish ExportX.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\dist\ExportX-Lightweight

if %ERRORLEVEL% NEQ 0 (
    echo [BLAD] Nie udalo sie zbudowac wersji Lekka!
    exit /b %ERRORLEVEL%
)

echo.
echo [3/3] Tworzenie paczki Portable (ZIP ze wszystkimi zaleznosciami)...
powershell -ExecutionPolicy Bypass -File .\build_portable.ps1

echo.
echo =======================================================
echo   SUKCES! Gotowe pliki:
echo   - .\dist\ExportX-Portable.zip (Paczka przenosna ZIP)
echo   - .\dist\ExportX-Standalone\ExportX.exe (Samodzielny .exe)
echo   - .\dist\ExportX-Lightweight\ExportX.exe (Lekki .exe)
echo =======================================================
echo.
