# Building & Packaging TTSApp

## Portable self-contained build
```powershell
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```
Produces `.\publish\` — a folder with `TTSApp.exe` and everything it needs (no .NET install required on the target PC). Copy the whole folder anywhere and double-click `TTSApp.exe`.

The folder includes `Models\` (Kokoro voices) and `python\` (GPU sidecar). GPU engines add `python\.venv`, `python\runtime`, and `python\cache` on first use — those stay inside the app folder (portable).

## Installer (.exe)
1. Run `publish.ps1` (above) first. It prints the exact `iscc` command with the current version.
2. Install [Inno Setup](https://jrsoftware.org/isinfo.php).
3. Compile (replace `{version}` with the version from `publish.ps1`):
   ```powershell
   iscc build\installer.iss /DAppVersion={version}
   ```
   Output: `build\Output\TTSApp-Setup-{version}.exe`.

## Notes
- GPU engines (XTTS / Chatterbox / Fish) need a base Python 3.10+ on the machine, or the app downloads a private one automatically on first use.
- ffmpeg on PATH is required for M4B, MP3 chapter marks, and background-music mixing.
