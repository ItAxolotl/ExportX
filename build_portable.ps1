$ErrorActionPreference = "Stop"

Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "  EXPORT.X - Tworzenie paczki PORTABLE (.ZIP)" -ForegroundColor Yellow
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host ""

$dist = ".\dist\ExportX-Portable"
$zip = ".\dist\ExportX-Portable.zip"

# Stop any running instances so files are not locked
Stop-Process -Name "ExportX" -Force -ErrorAction SilentlyContinue

if (Test-Path $dist) { 
    try { Remove-Item -Recurse -Force $dist } catch { }
}
if (Test-Path $zip) { 
    try { Remove-Item -Force $zip } catch { }
}

New-Item -ItemType Directory -Path "$dist\bin" -Force | Out-Null
New-Item -ItemType Directory -Path "$dist\data" -Force | Out-Null
New-Item -ItemType Directory -Path "$dist\Downloads" -Force | Out-Null

Write-Host "[1/4] Sprawdzanie srodowiska .NET SDK..." -ForegroundColor Green
$dotnetVersion = & dotnet --version 2>$null
if (-not $dotnetVersion) {
    Write-Error "BŁĄD: .NET SDK nie jest zainstalowany na tym komputerze! Zainstaluj .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
}
Write-Host "  -> Wykryto .NET SDK: $dotnetVersion" -ForegroundColor Gray

Write-Host "[2/4] Kompilacja ExportX.exe (Standalone Self-Contained)..." -ForegroundColor Green
& dotnet publish ExportX.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $dist

if (-not (Test-Path "$dist\ExportX.exe")) {
    Write-Error "BŁĄD: Kompilacja nie utworzyla pliku ExportX.exe! Sprawdz bledy kompilatora powyzej."
    exit 1
}

Set-Content -Path "$dist\portable.lock" -Value "ExportX Portable Mode"

Write-Host "[2/4] Kopiowanie silnikow pobierania (yt-dlp + ffmpeg)..." -ForegroundColor Green
$ytPath = "$env:APPDATA\ExportX\bin\yt-dlp.exe"
if (Test-Path $ytPath) {
    Copy-Item $ytPath "$dist\bin\yt-dlp.exe" -Force
    Write-Host "  -> Skopiowano yt-dlp.exe" -ForegroundColor Gray
}

$ffExe = (Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter "ffmpeg.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1)
if ($ffExe) {
    Copy-Item $ffExe.FullName "$dist\bin\ffmpeg.exe" -Force
    Write-Host "  -> Skopiowano ffmpeg.exe" -ForegroundColor Gray
    $ffProbe = Join-Path $ffExe.DirectoryName "ffprobe.exe"
    if (Test-Path $ffProbe) {
        Copy-Item $ffProbe "$dist\bin\ffprobe.exe" -Force
        Write-Host "  -> Skopiowano ffprobe.exe" -ForegroundColor Gray
    }
}

$readme = @"
===========================================================
  EXPORT.X - PORTABLE MUSIC & VIDEO DOWNLOADER v1.2
===========================================================

Ta wersja aplikacji dziala w pelni przenosnie (PORTABLE MODE):
- Wszystkie ustawienia i ciasteczka sesji sa zapisywane w podfolderze 'data\'
- Pobrane utwory trafiaja domyslnie do podfolderu 'Downloads\'
- Narzedzia yt-dlp i FFmpeg znajduja sie w podfolderze 'bin\'
- Program nie wymaga instalacji .NET ani konfiguracji w systemie.

Aby uruchomic program, kliknij dwukrotnie w: ExportX.exe
"@
Set-Content -Path "$dist\README_PORTABLE.txt" -Value $readme -Encoding UTF8

Write-Host "[3/4] Pakowanie do archiwum ZIP ($zip)..." -ForegroundColor Green
$absDist = (Resolve-Path $dist).Path
$absZip = [System.IO.Path]::GetFullPath($zip)

if (Test-Path $absZip) { 
    Remove-Item -Force $absZip 
}

& tar.exe -acf "$absZip" -C "$absDist" .

Write-Host ""
Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "  SUKCES! Gotowa paczka przenosna:" -ForegroundColor Yellow
Write-Host "  - Folder: $dist" -ForegroundColor White
Write-Host "  - Archiwum ZIP: $zip" -ForegroundColor White
Write-Host "=======================================================" -ForegroundColor Cyan
