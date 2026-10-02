# 📓 ExportX

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-00F0FF?style=flat-square&logo=windows)](https://github.com/ItAxolotl/ExportX)
[![Framework](https://img.shields.io/badge/.NET-10.0%20WPF-FFE600?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/badge/License-MIT-4ADE80?style=flat-square)](LICENSE)
[![Release](https://img.shields.io/badge/Version-v1.5.5%20Portable-C084FC?style=flat-square)](https://github.com/ItAxolotl/ExportX/releases)

A fast, multithreaded Windows desktop music and video downloader built with **C# .NET 10 / WPF**, featuring a distinctive **Neo-Brutalism** notebook theme.

---

## Features

### 1. High-Quality Audio & Video Processing
- **Audio Formats**: `MP3`, `M4A (AAC)`, `FLAC (Lossless)`, `WAV (Lossless)`, `OPUS`.
- **Bitrate Control**: `128 kbps`, `192 kbps`, `256 kbps`, `320 kbps`.
- **Ultra HD Video**: Downloads highest-resolution video streams (4K, 1440p, 1080p60) and merges them losslessly via FFmpeg.
- **Per-Item Configuration**: Switch format or target output for individual queue items with a double-click.

### 2. Spotify & Playlist Integration
- **Spotify Library Browser**: View all user playlists and Liked Songs directly in the application.
- **Batch Metadata API**: High-speed resolution of track titles, artists, albums, durations, and 640x640 album covers via batch endpoints.
- **Live Search**: Instant in-memory search and filtering across hundreds of playlists as you type.
- **Flexible Import**: Support for Spotify URLs, YouTube playlists, M3U files, and CSV/TXT exports.
- **Smart Query Sanitizer**: Automatically strips beatmaker tags and production noise to ensure original track matching.

### 3. Google / YouTube Session Auth
- Built-in session capture via default browser to download age-restricted, private, or bot-blocked content safely.
- Session cookies remain stored strictly locally on your machine.

### 4. Multithreaded Engine
- Parallel downloads supporting up to **16 concurrent workers**.
- Individual progress tracking with real-time speed, percentage, and ETA metrics.

### 5. Automatic ID3 Tagging & Artwork
- Automatically embeds high-resolution cover artwork and fills ID3 tags (Artist, Title, Album) into downloaded audio files.

### 6. 100% Portable Mode
- Completely self-contained package. Settings, cookies, downloads, and binaries (`yt-dlp`, `ffmpeg`) reside in a single portable directory with zero external runtime dependencies.

---

## Downloads

Download the latest standalone package from [GitHub Releases](https://github.com/ItAxolotl/ExportX/releases):

| Package | Download Link | Description |
|---|---|---|
| **ExportX Portable (.ZIP)** | [⬇️ Download ExportX-Portable.zip](https://github.com/ItAxolotl/ExportX/releases/latest) | Fully self-contained portable package with bundled `yt-dlp` and `FFmpeg` (No .NET installation required). |
| **All Releases** | [📦 Releases Archive](https://github.com/ItAxolotl/ExportX/releases) | Full list of releases and changelogs. |

---

## Building from Source

### Prerequisites
- Windows 10 / 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build Steps (PowerShell)

```powershell
# Build standalone portable release package:
.\build_portable.ps1

# Run in development mode:
dotnet run
```

---

## License

This project is licensed under the [MIT License](LICENSE).
