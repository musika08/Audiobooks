# TTSApp GPU Sidecar (XTTS v2)

The WPF app auto-starts `tts_server.py` when you select a GPU engine (e.g. XTTS v2).
This folder is copied next to the built `.exe` so the app can find it.

## Setup is automatic

The app installs everything itself the first time you pick a GPU engine:
1. Creates a private virtual env at `python\.venv`
2. Installs PyTorch (CUDA 12.1) + the requirements into it
3. Launches the server from that venv

Requirement: a base **Python 3.10+** must exist on the machine (the app uses the
`py` launcher or `python` on PATH to *create* the venv — it cannot install Python
itself). Get it from https://python.org (check "Add to PATH").

First run downloads several GB (torch + model weights) and takes a few minutes;
progress shows in the app status bar. Subsequent launches are fast (a `.venv\.deps_ok`
marker skips reinstall).

### Manual setup (optional, if you prefer)

```powershell
cd python
python -m venv .venv
.\.venv\Scripts\python -m pip install torch --index-url https://download.pytorch.org/whl/cu121
.\.venv\Scripts\python -m pip install -r requirements.txt
```

## Manual test (optional)

```powershell
python tts_server.py --port 8765 --model xtts-v2
# then:
curl http://127.0.0.1:8765/health
curl http://127.0.0.1:8765/speakers
```

## Engines
- **XTTS v2** (`--model xtts-v2`) — built-in studio speakers; voice cloning via reference audio; honors speed + language.
- **Chatterbox** (`--model chatterbox`) — single default voice; voice cloning via reference audio. Ignores the speed/language controls.

Switching GPU engines in the app restarts this process (only one model in VRAM at a time).

## Voice cloning
Click the 🎤 button next to the Voice dropdown, pick a clean ~6–15s reference clip (wav/mp3/flac).
The reference path is sent to the server and overrides the selected speaker. Click again (✓) to turn cloning off.

## Notes
- GPU required for usable speed. On CPU it loads but is very slow.
- Fish/OpenAudio is not yet wired (its inference API is multi-step and version-sensitive).
