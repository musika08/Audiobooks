Audiobooks v0.06 (stable Kokoro)

1) Double-click setup.bat (creates .venv; installs torch CPU, numpy 1.26.4, kokoro with fallback, epub deps)
2) Double-click start_audiobooks.bat
3) In the GUI: set voice to af_bella, type a line, click Test → kokoro_test.wav
4) Choose an EPUB → Convert → outputs .m4b (requires ffmpeg in PATH)

Notes:
- NumPy pinned to 1.26.4 to avoid NumPy 2.x ABI issues with Kokoro/Transformers.
- Removed spaCy/thinc (not used; they require NumPy>=2).
- KPipeline-only path (no legacy module-call fallback).
- If ffmpeg isn’t installed, install it or add it to PATH.
