# audiobooks/core.py
from __future__ import annotations

import io
import wave
import logging
import platform
from pathlib import Path
from typing import Iterable, List, Optional, Tuple, Dict

# ---------- Optional deps ----------
try:
    import torch  # type: ignore
except Exception:
    torch = None  # type: ignore

try:
    import numpy as np  # type: ignore
except Exception as e:
    raise RuntimeError(
        "NumPy is required. Please run setup to install numpy==1.26.4."
    ) from e

log = logging.getLogger("audiobooks.core")
if not log.handlers:
    h = logging.StreamHandler()
    h.setFormatter(logging.Formatter("[audiobooks] %(message)s"))
    log.addHandler(h)
    log.setLevel(logging.INFO)

# ============================================================
# GPU detection (CUDA / DirectML / MPS)
# ============================================================

def _try_import_directml() -> bool:
    """Try import torch_directml at runtime, return True if usable."""
    if torch is None:
        return False
    try:
        import torch_directml  # type: ignore
        # set default device for torch ops that honor default device
        if hasattr(torch, "set_default_device"):
            torch.set_default_device("dml")  # type: ignore
        return True
    except Exception:
        return False


def _has_mps() -> bool:
    if torch is None:
        return False
    try:
        return bool(hasattr(torch.backends, "mps") and torch.backends.mps.is_available())  # type: ignore
    except Exception:
        return False


def _detect_gpu_backend() -> Optional[str]:
    """
    Return 'cuda', 'directml', 'mps', or None.
    Attempts dynamic import of torch-directml on Windows if not already loaded.
    """
    if torch is None:
        return None

    # CUDA (NVIDIA)
    try:
        if torch.cuda.is_available():
            return "cuda"
    except Exception:
        pass

    # DirectML (Windows built-in GPU via torch-directml)
    if platform.system().lower().startswith("win"):
        if _try_import_directml():
            return "directml"

    # Apple MPS
    if _has_mps():
        return "mps"

    return None


def gpu_available() -> bool:
    """True if any GPU backend (CUDA/DirectML/MPS) is available right now."""
    return _detect_gpu_backend() is not None


def select_device(prefer_gpu: bool) -> Tuple[str, Optional[str]]:
    """
    Returns (pipeline_device, backend_used).
      - pipeline_device: 'cuda' or 'cpu' (Kokoro's KPipeline supports only CUDA acceleration)
      - backend_used: 'cuda' | 'directml' | 'mps' | None (for logging)
    For DirectML/MPS we keep pipeline on CPU (Kokoro is CUDA-only) but set Torch defaults so
    other ops can benefit.
    """
    backend = _detect_gpu_backend() if prefer_gpu else None

    if backend == "cuda":
        log.info("Using CUDA for Kokoro.")
        return "cuda", "cuda"

    if backend == "directml":
        # Kokoro will still run on CPU, but Torch defaults are set to DML above.
        log.info("DirectML detected (Windows GPU). Kokoro will run on CPU (CUDA-only model).")
        return "cpu", "directml"

    if backend == "mps":
        log.info("Apple MPS detected. Kokoro will run on CPU (CUDA-only model).")
        return "cpu", "mps"

    # No GPU backend found or GPU not requested
    return "cpu", None


# ===========================
# Audio conversion utilities
# ===========================

def _to_numpy(x):
    """
    Convert audio to a float32 numpy array in [-1, 1]:
      - torch.Tensor -> detach().float().cpu().numpy()
      - list/tuple -> np.asarray(..., float32)
      - ndarray -> cast to float32 if needed, clamp to [-1,1]
      - bytes/bytearray -> unchanged (caller handles)
    """
    if torch is not None and hasattr(torch, "Tensor") and isinstance(x, torch.Tensor):
        x = x.detach().float().cpu().numpy()
    if isinstance(x, (list, tuple)):
        x = np.asarray(x, dtype=np.float32)
    if hasattr(x, "dtype"):
        arr = x
        if arr.dtype != np.float32:
            arr = arr.astype(np.float32, copy=False)
        return np.clip(arr, -1.0, 1.0)
    return x


def _numpy_to_wav_bytes(arr: "np.ndarray", samplerate: int = 22050) -> bytes:
    """Convert float32 numpy array in [-1,1] to 16-bit PCM WAV bytes."""
    arr = _to_numpy(arr)
    if not hasattr(arr, "dtype"):
        return b""

    pcm16 = np.clip(arr, -1.0, 1.0)
    pcm16 = (pcm16 * 32767.0).astype(np.int16, copy=False)

    buf = io.BytesIO()
    with wave.open(buf, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)  # 16-bit
        wf.setframerate(samplerate)
        wf.writeframes(pcm16.tobytes())
    return buf.getvalue()


def _read_wav_to_float32(data: bytes) -> "np.ndarray":
    """Decode a WAV byte blob to mono float32 [-1, 1]."""
    import wave as _wave
    import io as _io

    with _wave.open(_io.BytesIO(data), "rb") as wf:
        nch = wf.getnchannels()
        sampwidth = wf.getsampwidth()
        frames = wf.readframes(wf.getnframes())

    if sampwidth == 2:  # 16-bit PCM
        arr = np.frombuffer(frames, dtype=np.int16).astype(np.float32, copy=False) / 32767.0
    elif sampwidth == 1:
        arr = (np.frombuffer(frames, dtype=np.uint8).astype(np.float32) - 128.0) / 128.0
    elif sampwidth == 4:
        arr = (np.frombuffer(frames, dtype=np.int32).astype(np.float32)) / 2147483647.0
    else:
        arr = np.zeros(0, dtype=np.float32)

    if nch > 1:
        arr = arr.reshape(-1, nch).mean(axis=1).astype(np.float32, copy=False)

    return np.clip(arr, -1.0, 1.0)


def _collect_kokoro_samples(result_iter: Iterable, samplerate: int = 22050) -> "np.ndarray":
    """
    Collect Kokoro chunk outputs as ONE float32 array.
    Handles:
      - item.wav (bytes of a full WAV) → decode & append samples
      - item.audio / item.samples (tensor/ndarray/list) → convert & append
    """
    parts: List["np.ndarray"] = []
    for item in result_iter:
        try:
            if hasattr(item, "wav") and isinstance(item.wav, (bytes, bytearray)):
                parts.append(_read_wav_to_float32(item.wav))
                continue
            if hasattr(item, "audio"):
                arr = _to_numpy(getattr(item, "audio"))
                if hasattr(arr, "dtype"):
                    parts.append(arr.astype(np.float32, copy=False))
                continue
            if hasattr(item, "samples"):
                arr = _to_numpy(getattr(item, "samples"))
                if hasattr(arr, "dtype"):
                    parts.append(arr.astype(np.float32, copy=False))
                continue
        except Exception:
            continue

    if not parts:
        return np.zeros(0, dtype=np.float32)

    return np.concatenate(parts).astype(np.float32, copy=False)


# ===========================
# Kokoro: pipeline + voices
# ===========================

_kokoro_pipelines: Dict[Tuple[str, str], "object"] = {}

def _get_kokoro_pipeline_for_voice(voice_id: str, pipeline_device: str):
    """
    Return a cached Kokoro pipeline for the voice's language and target device.
    pipeline_device is 'cuda' or 'cpu' (Kokoro only accelerates on CUDA).
    """
    from kokoro.pipeline import KPipeline

    if not voice_id:
        raise ValueError("voice_id is required for Kokoro.")
    lang_code = voice_id[0].lower()  # 'a' or 'b'
    if lang_code not in ("a", "b"):
        lang_code = "a"

    key = (lang_code, pipeline_device)
    pipe = _kokoro_pipelines.get(key)
    if pipe is None:
        pipe = KPipeline(lang_code=lang_code, repo_id="hexgrad/Kokoro-82M", device=pipeline_device)
        _kokoro_pipelines[key] = pipe
    return pipe


def ensure_kokoro_voice(voice_id: str, prefer_gpu: bool = False):
    """
    Ensure the Kokoro voice is fetched and loaded via KPipeline.
    - Auto-correct legacy IDs (like 'af_alice' -> 'bf_alice').
    - Select device according to toggle: CUDA -> DirectML -> MPS -> CPU.
      (Kokoro only uses CUDA; for other backends we set Torch defaults and pass 'cpu' to KPipeline.)
    """
    VOICE_FIXUPS = {
        "af_alice": "bf_alice",
    }
    voice_id = VOICE_FIXUPS.get(voice_id, voice_id)

    pipeline_device, backend = select_device(prefer_gpu)
    # Informative once (no scary fallback spam)
    if backend in ("directml", "mps"):
        log.info(f"GPU backend '{backend}' detected; Kokoro runs on CPU (CUDA-only acceleration).")

    pipe = _get_kokoro_pipeline_for_voice(voice_id, pipeline_device)
    pipe.load_voice(voice_id)  # triggers download/cache if needed
    return pipe, voice_id


def kokoro_synthesize_to_wav(
    text: str,
    voice: str,
    wav_out: Path,
    use_gpu: bool = False,
    speed: float = 1.0,
    samplerate: int = 22050,
) -> float:
    """
    Synthesize TTS with Kokoro and write a single WAV file (full length).
    Returns approximate audio seconds.
    """
    if not text.strip():
        raise ValueError("No text to synthesize.")

    pipe, voice = ensure_kokoro_voice(voice, prefer_gpu=use_gpu)
    result_iter = pipe(text, voice=voice, speed=float(speed))

    samples = _collect_kokoro_samples(result_iter, samplerate=samplerate)
    if samples.size == 0:
        raise RuntimeError("Kokoro produced no audio.")

    wav_bytes = _numpy_to_wav_bytes(samples, samplerate=samplerate)

    wav_out = Path(wav_out)
    wav_out.parent.mkdir(parents=True, exist_ok=True)
    with open(wav_out, "wb") as f:
        f.write(wav_bytes)

    return float(samples.shape[0]) / float(samplerate)


# ===========================
# EPUB parsing helpers
# ===========================

def _extract_chapters_text(epub_path: Path) -> List[Tuple[str, str]]:
    """
    Return list of (chapter_title, plain_text) from an EPUB.
    Uses robust HTML parsing (no reliance on ITEM_DOCUMENT).
    """
    from ebooklib import epub
    from bs4 import BeautifulSoup

    book = epub.read_epub(str(epub_path))
    chapters: List[Tuple[str, str]] = []
    idx = 0

    def _first_heading(soup):
        for tag in ("h1", "h2", "h3", "title"):
            el = soup.find(tag)
            if el and el.get_text(strip=True):
                return el.get_text(strip=True)
        return None

    for item in book.get_items():
        if getattr(item, "media_type", "").lower() == "application/xhtml+xml":
            idx += 1
            try:
                soup = BeautifulSoup(item.get_content(), "html.parser")
                text = soup.get_text(separator=" ", strip=True)
                chapter_title = _first_heading(soup) or f"Chapter {idx}"
            except Exception:
                text = ""
                chapter_title = f"Chapter {idx}"

            if text.strip():
                chapters.append((chapter_title, text))
    if not chapters:
        chapters.append((f"Chapter 1", ""))

    return chapters


def list_chapter_titles(epub_path: Path) -> List[str]:
    """Return just the ordered list of chapter titles for GUI display."""
    return [t for (t, _txt) in _extract_chapters_text(Path(epub_path))]


def _get_book_title(epub_path: Path) -> str:
    """Read EPUB title metadata; fallback to filename stem."""
    try:
        from ebooklib import epub
        book = epub.read_epub(str(epub_path))
        titles = book.get_metadata('DC', 'title') or []
        if titles and titles[0] and titles[0][0].strip():
            return titles[0][0].strip()
    except Exception:
        pass
    return Path(epub_path).stem


def _safe_filename(s: str, maxlen: int = 120) -> str:
    """Make a string safe as a filename on Windows/macOS/Linux."""
    bad = '<>:"/\\|?*'
    out = "".join(('_' if ch in bad else ch) for ch in s).strip()
    out = " ".join(out.split())  # collapse whitespace
    if len(out) > maxlen:
        out = out[:maxlen].rstrip()
    return out or "Untitled"


# ===========================
# Converters
# ===========================

def convert_epub_to_m4b(
    epub_path: Path,
    m4b_out: Path,
    engine: str = "kokoro",
    voice: str = "af_bella",
    speed: float = 1.0,
    use_gpu: bool = False,
    selected_chapter_indices: Optional[List[int]] = None,
    samplerate: int = 22050,
    output_format: str = "m4b",   # "m4b" or "wav"
) -> Path:
    """
    Convert an EPUB to a single audio file.
    - Concatenates selected chapters' text and synthesizes to WAV.
    - If output_format == 'm4b' and ffmpeg is present, transcodes to .m4b (AAC).
      Otherwise returns the WAV.
    """
    import shutil, subprocess

    epub_path = Path(epub_path)
    m4b_out = Path(m4b_out)
    tmp_dir = m4b_out.parent / "_tmp_audio"
    tmp_dir.mkdir(parents=True, exist_ok=True)

    chapters = _extract_chapters_text(epub_path)
    if selected_chapter_indices:
        idx_set = set(selected_chapter_indices)
        chapters = [c for i, c in enumerate(chapters) if i in idx_set]

    full_text = "\n\n".join(text for (_title, text) in chapters if text.strip())
    if not full_text.strip():
        raise RuntimeError("No text content found in selected chapters.")

    wav_path = tmp_dir / "book.wav"

    if engine == "kokoro":
        kokoro_synthesize_to_wav(
            full_text, voice, wav_path,
            use_gpu=use_gpu, speed=speed, samplerate=samplerate
        )
    elif engine == "piper":
        raise NotImplementedError("Piper conversion not implemented in this build.")
    else:
        raise ValueError(f"Unknown engine: {engine}")

    fmt = (output_format or "m4b").strip().lower()
    if fmt == "wav":
        out_path = m4b_out
        if out_path.suffix.lower() != ".wav":
            out_path = out_path.with_suffix(".wav")
        if str(wav_path.resolve()) != str(out_path.resolve()):
            shutil.copyfile(str(wav_path), str(out_path))
        return out_path

    ff = shutil.which("ffmpeg")
    if ff:
        cmd = [
            ff, "-y",
            "-i", str(wav_path),
            "-c:a", "aac",
            "-b:a", "64k",
            "-movflags", "+faststart",
            str(m4b_out),
        ]
        subprocess.run(cmd, check=True)
        return m4b_out
    else:
        return wav_path


def convert_epub_to_tracks(
    epub_path: Path,
    engine: str = "kokoro",
    voice: str = "af_bella",
    speed: float = 1.0,
    use_gpu: bool = False,
    selected_chapter_indices: Optional[List[int]] = None,
    samplerate: int = 22050,
    output_format: str = "m4b",   # "m4b" or "wav"
) -> Path:
    """
    Convert an EPUB into per-chapter audio files.
    Creates a folder next to the EPUB, named after the book title.
    Each chapter saved as:  'Chapter N - Title.ext' (or 'Chapter N.ext').
    Returns the output folder path.
    """
    import shutil, subprocess

    epub_path = Path(epub_path)
    out_root = epub_path.parent
    book_title = _safe_filename(_get_book_title(epub_path))
    out_dir = out_root / book_title
    out_dir.mkdir(parents=True, exist_ok=True)

    chapters = _extract_chapters_text(epub_path)
    if selected_chapter_indices:
        idx_set = set(selected_chapter_indices)
        chapters = [c for i, c in enumerate(chapters) if i in idx_set]

    if not chapters:
        raise RuntimeError("No chapters found.")

    fmt = (output_format or "m4b").lower()
    if fmt not in ("m4b", "wav"):
        raise ValueError("output_format must be 'm4b' or 'wav'.")

    # temp dir for per-chapter WAV intermediates
    tmp_dir = out_dir / "_tmp_audio"
    tmp_dir.mkdir(parents=True, exist_ok=True)

    ffmpeg = shutil.which("ffmpeg") if fmt == "m4b" else None
    if fmt == "m4b" and not ffmpeg:
        # no ffmpeg → fall back to WAV files
        fmt = "wav"

    for idx, (title, text) in enumerate(chapters, start=1):
        if not text.strip():
            continue

        # "Chapter N - Title" or "Chapter N"
        if title and not title.startswith("Chapter "):
            base_name = f"Chapter {idx} - {title}"
        else:
            base_name = title or f"Chapter {idx}"
        base_name = _safe_filename(base_name)

        wav_path = tmp_dir / f"{base_name}.wav"

        # synthesize chapter to WAV via engine
        if engine == "kokoro":
            kokoro_synthesize_to_wav(
                text, voice, wav_path,
                use_gpu=use_gpu, speed=speed, samplerate=samplerate
            )
        elif engine == "piper":
            raise NotImplementedError("Piper conversion not implemented in this build.")
        else:
            raise ValueError(f"Unknown engine: {engine}")

        if fmt == "wav":
            final_wav = out_dir / f"{base_name}.wav"
            shutil.copyfile(str(wav_path), str(final_wav))
        else:
            # Transcode to m4b (AAC) per chapter
            cmd = [
                ffmpeg, "-y",
                "-i", str(wav_path),
                "-c:a", "aac",
                "-b:a", "64k",
                "-movflags", "+faststart",
                str(out_dir / f"{base_name}.m4b"),
            ]
            subprocess.run(cmd, check=True)

    # Uncomment to clean temp dir automatically:
    # shutil.rmtree(tmp_dir, ignore_errors=True)

    return out_dir
