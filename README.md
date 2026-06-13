# AI Audiobook Studio

A Windows desktop app (WPF / .NET 8) that turns **.txt / .pdf / .epub** files into audiobooks using local text-to-speech. Import a book, edit chapters, pick a voice, and export to **WAV / MP3 / M4B** with chapter marks.

## Features
- **Document import** — TXT, PDF, EPUB with automatic chapter detection
- **Chapter editor** — reorder, search/filter, per-chapter voice, add/delete, live duration estimates
- **Voices** — multiple offline Kokoro models (English + 100+ multilingual voices); optional GPU engines (XTTS v2, Chatterbox) with voice cloning
- **Pacing** — global pause scale plus per-type pauses (comma, sentence, ellipsis, paragraph) and inline `[pause 500]` tags
- **Audio** — loudness normalization (Peak / RMS / true **LUFS** BS.1770), silence trimming, export presets (ACX / Podcast / Plain), optional background music + intro/outro
- **Export** — WAV, MP3 (with ID3 chapter marks), M4B audiobook with cover art
- **Player** — built-in preview player, mini player, pronunciation dictionary, dark/light/midnight themes

## Requirements
- Windows 10/11, x64
- .NET 8 SDK (to build) — or use the self-contained build (no install needed)
- **ffmpeg** on PATH (for M4B, MP3 chapter marks, background-music mixing)
- **GPU engines only:** an Nvidia GPU + CUDA. Python 3.10+ is auto-installed if absent.

The Kokoro TTS models (~80 MB+) download automatically on first run.

## Build & run
```powershell
git clone https://github.com/musika08/Audiobooks.git
cd Audiobooks
dotnet build TTSApp/TTSApp.csproj -c Debug
# run the produced TTSApp.exe under TTSApp/bin/Debug/net8.0-windows10.0.17763.0/
```

## Package a portable build / installer
```powershell
powershell -ExecutionPolicy Bypass -File build\publish.ps1   # -> .\publish
iscc build\installer.iss                                     # -> installer .exe (needs Inno Setup)
```

## Engines
| Engine | Device | Voices | Cloning |
|--------|--------|--------|---------|
| Kokoro English v0.19 | CPU | 11 | no |
| Kokoro Multi-Lang v1.0 | CPU | 53 | no |
| Kokoro Multi-Lang v1.1 | CPU | 103 (mostly Mandarin) | no |
| XTTS v2 | GPU | built-in studio voices | yes |
| Chatterbox | GPU | default | yes |
| Fish / OpenAudio | GPU | experimental | — |

GPU engines run as a local Python sidecar (`TTSApp/python/`), started automatically by the app. See [TTSApp/python/README.md](TTSApp/python/README.md).

## Project layout
- `TTSApp/` — WPF app source
- `TTSApp/python/` — GPU TTS sidecar (FastAPI server + engines)
- `build/` — publish script + Inno Setup installer
