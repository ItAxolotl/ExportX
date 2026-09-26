# 📓 EXPORT.X // Neo-Brutalism Music & Video Downloader

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-00F0FF?style=flat-square&logo=windows)](https://github.com)
[![Framework](https://img.shields.io/badge/.NET-10.0%20WPF-FFE600?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/badge/License-MIT-4ADE80?style=flat-square)](LICENSE)
[![Release](https://img.shields.io/badge/Version-v1.2%20Portable-C084FC?style=flat-square)](dist/ExportX-Portable.zip)

Nowoczesna, ekstremalnie wydajna i wielowątkowa aplikacja desktopowa dla systemu Windows (napisana w **C# .NET 10 / WPF**) łącząca surowy styl **Neo-Brutalism** (jaskrawe akcenty, 2.5px solidne czarne obramowania, twarde cienie) z motywem **szkolnego zeszytu w kratkę** (papier milimetrowy/kratka, czerwony margines, znaczniki sticky-notes).

---

## ⚡ Główne Możliwości i Funkcje

### 1. 🎵 Pobieranie Muzyki i Wideo w Najwyższej Jakości:
- **Formaty Audio**: `MP3`, `M4A (AAC)`, `FLAC (Lossless)`, `WAV (Lossless)`, `OPUS (High Quality)`.
- **Regulacja Bitrate**: `128 kbps`, `192 kbps`, `256 kbps`, `320 kbps (Najwyższa jakość)`.
- **Wideo MP4 Ultra HD**: Pobiera strumienie wideo najwyższej rozdzielczości (4K, 1440p, 1080p60) i bezstratnie scala je z najlepszym audio przez FFmpeg.
- **Konfiguracja per utwór**: Każdy utwór w kolejce może mieć niezależnie wybrany format. Dwuklik myszą na wierszu przełącza format cyklicznie.

### 2. 📋 Pełna Obsługa Playlist & Importu:
- **Spotify**: Wklej link do playlisty, albumu lub pojedynczego utworu Spotify (np. `https://open.spotify.com/playlist/...`) – aplikacja pobiera całą playlistę z automatyczną paginacją (nawet 500+ utworów) bez potrzeby podawania klucza API!
- **YouTube**: Pełna obsługa playlist, filmów, teledysków i YouTube Music.
- **Pliki CSV / TXT / M3U**: Wczytywanie list utworów wyeksportowanych ze Spotify (Exportify / Spotlistr) oraz plików tekstowych.
- **Wklejanie listy**: Dialog `📋 WKLEJ LISTĘ` pozwala wkleić dziesiątki utworów/linków naraz.
- **Inteligentny filtr zapytań**: Automatycznie oczyszcza nazwy wykonawców z tagów beatmakerów (np. `@atutowy`, `Nolyrics Beats`), pobierając oryginalne utwory zamiast samych podkładów muzycznych.

### 3. 🔑 Wbudowane Bezpieczne Logowanie YouTube / Google:
- Opcja **`🔑 KONTO YOUTUBE`** otwiera wbudowaną przeglądarkę Microsoft WebView2, umożliwiając bezpieczne logowanie na konto Google.
- Ciasteczka sesji są zapisywane **wyłącznie lokalnie na Twoim dysku** (`youtube_cookies.txt`).
- **Odblokowuje utwory z ograniczeniem wiekowym (+18)**, prywatne playlisty oraz zapobiega blokadom anty-botowym YouTube.

### 4. 🚀 Wielowątkowość (Do 16 wątków równolegle):
- Możliwość jednoczesnego pobierania od 1 do **16 utworów naraz**.
- Paski postępu dla każdego utworu w czasie rzeczywistym oraz główny pasek postępu całej kolejki.

### 5. 🏷️ Automatyczne Tagowanie ID3 & Okładki:
- Automatyczne osadzanie oryginalnej okładki wysokiej rozdzielczości w plikach audio.
- Uzupełnianie tagów ID3 (Wykonawca, Tytuł, Album).

### 6. 💼 Wersja Portable (100% Przenośna):
- Program potrafi działać w trybie **PORTABLE** – wszystkie dane, ciasteczka, pobrane pliki i silniki (`yt-dlp`, `ffmpeg`) znajdują się w jednym folderze.
- Idealne do uruchamiania z pendrive'a lub dowolnego folderu bez instalacji i bez śladów w systemie!

---

## 🎨 Design & Stylistyka

- **Tło zeszytowe (`NotebookGridPatternBrush`)**: Struktura kartki w kratkę.
- **Pionowy czerwony margines zeszytowy**: Element stylistyki vintage notebook.
- **Neonowa paleta markerów zakreślających**:
  - 🟨 **Neon Yellow** (`#FFE600`) – nagłówki, przyciski wyszukiwania i sesji
  - 🟩 **Neon Lime** (`#4ADE80`) – przycisk `▶ START POBIERANIA`
  - 🟦 **Neon Cyan** (`#00F0FF`) – akcje list i folderów
  - 🟪 **Neon Purple** (`#C084FC`) – aktualizacja silnika pobierania
  - 🟥 **Neon Pink** (`#FF5376`) – przycisk `⏹ ZATRZYMAJ` i usuwanie
- **Fizyczny feedback (Tactile Click)**: Przyciski z 2.5px czarnym obramowaniem i twardym cieniem wciskają się fizycznie przy kliknięciu.

---

## 📦 Pobieranie Gotowych Wersji

Gotową wersję możesz pobrać bezpośrednio z zakładki **[GitHub Releases](https://github.com/ItAxolotl/ExportX/releases)**:

| Wersja | Plik do pobrania | Opis |
|---|---|---|
| 💼 **Paczka Gotowa (.ZIP)** | [⬇️ **Pobierz ExportX.zip**](https://github.com/ItAxolotl/ExportX/releases/download/v1.2/ExportX.zip) | W pełni niezależna paczka ze wszystkimi zależnościami (`yt-dlp`, `ffmpeg`, brak potrzeby instalacji .NET) |
| 🪶 **Wszystkie wydania** | [📦 **Przejdź do Releases**](https://github.com/ItAxolotl/ExportX/releases) | Lista wszystkich wersji i plików instalacyjnych |

---

## 🛠️ Budowanie ze Źródeł

### Wymagania:
- System **Windows 10 / 11** (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Kompilacja:
Wystarczy uruchomić jeden ze skryptów w głównym folderze:

```cmd
# Pełna kompilacja wszystkich wersji (Standalone, Lightweight oraz Portable ZIP):
.\build_exe.bat

# Lub wyłącznie paczka Portable:
powershell -ExecutionPolicy Bypass -File .\build_portable.ps1
```

### Uruchomienie w trybie deweloperskim:
```cmd
dotnet run
```

---

## 📄 Licencja

Projekt wydany na licencji **[MIT](LICENSE)**.
Możesz go swobodnie używać, modyfikować i rozpowszechniać.
