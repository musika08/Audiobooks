# audiobooks/core.py
from __future__ import annotations

import io
import wave
import logging
from pathlib import Path
from typing import Iterable, List, Optional, Tuple, Dict

import shutil
import subprocess
import sys
import platform
import tempfile
import zipfile
import urllib.request
import re
import hashlib
import warnings

warnings.filterwarnings("ignore", category=UserWarning, message="dropout option adds dropout")
warnings.filterwarnings("ignore", category=FutureWarning, message="torch.nn.utils.weight_norm")

# ---------- Optional deps ----------
try:
    import torch  # type: ignore
except Exception:
    torch = None  # type: ignore

try:
    import numpy as np  # type: ignore
except Exception as e:
    raise RuntimeError("NumPy is required. Please run setup to install numpy.") from e

# Optional sentence splitter
_BLINGFIRE = None
try:
    import blingfire  # type: ignore
    _BLINGFIRE = blingfire
except Exception:
    _BLINGFIRE = None

# GPU backend flags
_HAS_DIRECTML = False
_HAS_MPS = False
if torch is not None:
    try:
        import torch_directml  # type: ignore
        _HAS_DIRECTML = True
    except Exception:
        _HAS_DIRECTML = False
    try:
        _HAS_MPS = bool(hasattr(torch.backends, "mps") and torch.backends.mps.is_available())  # type: ignore
    except Exception:
        _HAS_MPS = False

log = logging.getLogger("audiobooks.core")
if not log.handlers:
    h = logging.StreamHandler()
    h.setFormatter(logging.Formatter("[audiobooks] %(message)s"))
    log.addHandler(h)
    log.setLevel(logging.INFO)

__version__ = "1.0.0"

# ============================================================
# Availability helpers
# ============================================================

def kokoro_available() -> bool:
    try:
        import kokoro  # noqa: F401
        from kokoro.pipeline import KPipeline  # noqa: F401
        return True
    except Exception:
        return False

def _detect_gpu_backend() -> Optional[str]:
    if torch is None:
        return None
    try:
        if torch.cuda.is_available():
            return "cuda"
        if _HAS_DIRECTML:
            return "directml"
        if _HAS_MPS:
            return "mps"
    except Exception:
        pass
    return None

def gpu_available() -> bool:
    return _detect_gpu_backend() is not None

def select_device(prefer_gpu: bool) -> Tuple[str, Optional[str]]:
    """
    Returns (pipeline_device, backend_used).
      - pipeline_device: 'cuda' or 'cpu' (Kokoro accelerates only on CUDA)
      - backend_used: 'cuda' | 'directml' | 'mps' | None
    """
    backend = _detect_gpu_backend() if prefer_gpu else None

    if backend == "cuda":
        log.info("Using CUDA for Kokoro.")
        return "cuda", "cuda"

    if backend == "directml":
        try:
            if hasattr(torch, "set_default_device"):
                torch.set_default_device("dml")  # type: ignore
            log.info("DirectML detected. Kokoro will run on CPU (CUDA-only acceleration).")
        except Exception:
            pass
        return "cpu", "directml"

    if backend == "mps":
        try:
            if hasattr(torch, "set_default_device"):
                torch.set_default_device("mps")  # type: ignore
            log.info("Apple MPS detected. Kokoro will run on CPU (CUDA-only acceleration).")
        except Exception:
            pass
        return "cpu", "mps"

    if prefer_gpu:
        log.info("GPU requested but none available. Fallback to CPU.")
    return "cpu", None

# ===========================
# Audio utilities
# ===========================

def _to_numpy(x):
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
    arr = _to_numpy(arr)
    if not hasattr(arr, "dtype"):
        return b""
    pcm16 = np.clip(arr, -1.0, 1.0)
    pcm16 = (pcm16 * 32767.0).astype(np.int16, copy=False)
    buf = io.BytesIO()
    with wave.open(buf, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(samplerate)
        wf.writeframes(pcm16.tobytes())
    return buf.getvalue()

def _read_wav_to_float32(data: bytes) -> "np.ndarray":
    import wave as _wave, io as _io
    with _wave.open(_io.BytesIO(data), "rb") as wf:
        nch = wf.getnchannels()
        sampwidth = wf.getsampwidth()
        frames = wf.readframes(wf.getnframes())
    if sampwidth == 2:
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

# ===========================
# Kokoro: pipeline + voices
# ===========================

_kokoro_pipelines: Dict[Tuple[str, str], "object"] = {}

def _get_kokoro_pipeline_for_voice(voice_id: str, pipeline_device: str):
    from kokoro.pipeline import KPipeline
    if not voice_id:
        raise ValueError("voice_id is required for Kokoro.")
    lang_code = voice_id[0].lower()
    if lang_code not in ("a", "b"):
        lang_code = "a"
    key = (lang_code, pipeline_device)
    pipe = _kokoro_pipelines.get(key)
    if pipe is None:
        pipe = KPipeline(lang_code=lang_code, repo_id="hexgrad/Kokoro-82M", device=pipeline_device)
        _kokoro_pipelines[key] = pipe
    return pipe

def ensure_kokoro_voice(voice_id: str, prefer_gpu: bool = False):
    VOICE_FIXUPS = {"af_alice": "bf_alice"}
    voice_id = VOICE_FIXUPS.get(voice_id, voice_id)
    pipeline_device, backend = select_device(prefer_gpu)
    if backend in ("directml", "mps"):
        log.info(f"GPU backend '{backend}' detected; Kokoro will run on CPU (CUDA only).")
    pipe = _get_kokoro_pipeline_for_voice(voice_id, pipeline_device)
    pipe.load_voice(voice_id)
    return pipe, voice_id

def kokoro_synthesize_to_array(
    text: str,
    voice: str,
    use_gpu: bool = False,
    speed: float = 1.0,
    samplerate: int = 22050,
) -> "np.ndarray":
    if not text.strip():
        return np.zeros(0, dtype=np.float32)
    pipe, voice = ensure_kokoro_voice(voice, prefer_gpu=use_gpu)
    result_iter = pipe(text, voice=voice, speed=float(speed))
    samples = _collect_kokoro_samples(result_iter, samplerate=samplerate)
    if samples.size == 0:
        raise RuntimeError("Kokoro produced no audio.")
    return samples

# >>> Keep this wrapper for Preview and any other callers
def kokoro_synthesize_to_wav(
    text: str,
    voice: str,
    wav_out: Path,
    use_gpu: bool = False,
    speed: float = 1.0,
    samplerate: int = 22050,
) -> float:
    """
    Convenience wrapper used by the GUI Preview:
    synthesize `text` with Kokoro and write a mono WAV file.
    Returns: duration in seconds.
    """
    samples = kokoro_synthesize_to_array(
        text, voice, use_gpu=use_gpu, speed=speed, samplerate=samplerate
    )
    wav_out = Path(wav_out)
    wav_out.parent.mkdir(parents=True, exist_ok=True)
    wav_bytes = _numpy_to_wav_bytes(samples, samplerate=samplerate)
    with open(wav_out, "wb") as f:
        f.write(wav_bytes)
    return float(samples.shape[0]) / float(samplerate)

def _collect_kokoro_samples(result_iter: Iterable, samplerate: int = 22050) -> "np.ndarray":
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
# Persistent FFmpeg (download once, reuse)
# ===========================

def ensure_ffmpeg() -> str:
    """
    Ensure ffmpeg is available and return its executable path.
    Downloads ONCE into audiobooks/ffmpeg-bin/ and reuses thereafter.
    """
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg:
        return ffmpeg

    root_dir = Path(__file__).resolve().parent
    bin_dir = root_dir / "ffmpeg-bin"
    exe_name = "ffmpeg.exe" if platform.system().lower() == "windows" else "ffmpeg"
    local_ffmpeg = bin_dir / exe_name
    if local_ffmpeg.exists():
        return str(local_ffmpeg)

    bin_dir.mkdir(parents=True, exist_ok=True)
    sysname = platform.system().lower()

    if sysname == "windows":
        url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
        tmp_zip = Path(tempfile.gettempdir()) / "ffmpeg-release-essentials.zip"
        log.info("Downloading FFmpeg (Windows portable zip)... This is a one-time setup.")
        urllib.request.urlretrieve(url, tmp_zip)
        with zipfile.ZipFile(tmp_zip, "r") as zf:
            zf.extractall(bin_dir)
        exe = None
        for p in bin_dir.rglob("ffmpeg.exe"):
            exe = p
            break
        if not exe:
            raise FileNotFoundError("ffmpeg.exe not found in downloaded archive.")
        exe.rename(local_ffmpeg)
        return str(local_ffmpeg)

    elif sysname == "linux":
        log.info("FFmpeg not found. Please install via your package manager, e.g.: sudo apt-get install ffmpeg")
        return "ffmpeg"

    elif sysname == "darwin":
        log.info("FFmpeg not found. Please install via Homebrew: brew install ffmpeg")
        return "ffmpeg"

    else:
        raise RuntimeError("Unsupported OS for auto FFmpeg install. Please install FFmpeg manually.")

# ============================================================
# Paragraph/formatting helpers
# ============================================================

_SOFT_HYPHEN = "\u00AD"
_HARD_HYPHEN = "-"
_WS_RE = re.compile(r"[ \t]+")
_MID_SENTENCE_RE = re.compile(r"[^\.\!\?\:\;\)\]\"»]\s*$")  # line ends with no strong end punctuation

def _normalize_ws(s: str) -> str:
    s = s.replace("\r\n", "\n").replace("\r", "\n")
    s = s.replace(_SOFT_HYPHEN, "")
    s = "\n".join(_WS_RE.sub(" ", ln).rstrip() for ln in s.split("\n"))
    return s

def _merge_wrapped_lines(text: str) -> str:
    lines = text.split("\n")
    out: List[str] = []
    buf: List[str] = []

    def flush():
        if buf:
            out.append(" ".join(buf).strip())
            buf.clear()

    for raw in lines:
        line = raw.strip()
        if not line:
            flush()
            out.append("")
            continue

    # same paragraph (hyphen + mid-sentence continuation) merge
        if not buf:
            buf.append(line)
            continue

        prev = buf[-1]
        if prev.endswith(_HARD_HYPHEN) and not prev.endswith("--"):
            buf[-1] = prev[:-1]
            buf.append(line.lstrip())
            continue

        if _MID_SENTENCE_RE.search(prev):
            buf.append(line.lstrip())
        else:
            flush()
            buf.append(line)

    flush()

    compact: List[str] = []
    blank = False
    for ln in out:
        if ln == "":
            if not blank:
                compact.append("")
            blank = True
        else:
            compact.append(ln)
            blank = False
    return "\n\n".join(compact).strip("\n")

def fix_paragraph_structure(text: str) -> str:
    if not text:
        return ""
    return _merge_wrapped_lines(_normalize_ws(text))

# ============================================================
# EPUB / DOCX / PDF extraction
# ============================================================

def _html_to_text_with_paragraphs(html_bytes: bytes) -> str:
    from bs4 import BeautifulSoup  # type: ignore
    try:
        soup = BeautifulSoup(html_bytes, "lxml")
    except Exception:
        soup = BeautifulSoup(html_bytes, "html.parser")

    for tag in soup(["script", "style", "noscript"]):
        tag.decompose()

    blocks = []
    def push(txt: str):
        t = txt.strip()
        if t:
            blocks.append(t)

    for el in soup.find_all(["h1","h2","h3","h4","p","li","blockquote","pre","div","br"]):
        name = el.name.lower()
        if name == "br":
            blocks.append("")
            continue
        text = el.get_text(" ", strip=True)
        if not text:
            continue
        if name == "li" and not text.startswith(("•", "-", "·")):
            text = f"• {text}"
        push(text)

    if not blocks:
        whole = soup.get_text(" ", strip=True)
        return fix_paragraph_structure(whole)

    out_lines: List[str] = []
    blank = False
    for b in blocks:
        if b == "":
            if not blank:
                out_lines.append("")
                blank = True
        else:
            out_lines.append(b)
            blank = False
    text = "\n\n".join(out_lines)
    return fix_paragraph_structure(text)

def _extract_chapters_text(epub_path: Path) -> List[Tuple[str, str]]:
    """
    Returns list of (title, body) for each chapter in an EPUB with paragraphs preserved.
    """
    from ebooklib import epub  # type: ignore
    book = epub.read_epub(str(epub_path))
    chapters: List[Tuple[str, str]] = []
    idx = 0

    def _first_heading_text(html_bytes: bytes) -> Optional[str]:
        from bs4 import BeautifulSoup  # type: ignore
        try:
            soup = BeautifulSoup(html_bytes, "lxml")
        except Exception:
            soup = BeautifulSoup(html_bytes, "html.parser")
        for tag in ("h1", "h2", "h3", "title"):
            el = soup.find(tag)
            if el and el.get_text(strip=True):
                return el.get_text(strip=True)
        return None

    for item in book.get_items():
        if getattr(item, "media_type", "").lower() in {"application/xhtml+xml", "application/x-dtbncx+xml", "text/html"}:
            idx += 1
            try:
                html_bytes = item.get_content()
                title = _first_heading_text(html_bytes) or f"Chapter {idx}"
                body = _html_to_text_with_paragraphs(html_bytes)
                if body.strip():
                    chapters.append((title, body))
            except Exception:
                chapters.append((f"Chapter {idx}", ""))

    if not chapters:
        chapters.append(("Chapter 1", ""))
    return chapters

def list_chapter_titles(epub_path: Path) -> List[str]:
    return [t for (t, _txt) in _extract_chapters_text(Path(epub_path))]

def _get_book_title(epub_path: Path) -> str:
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
    bad = '<>:"/\\|?*'
    out = "".join(('_' if ch in bad else ch) for ch in s).strip()
    out = " ".join(out.split())
    if len(out) > maxlen:
        out = out[:maxlen].rstrip()
    return out or "Untitled"

# --------- DOCX / PDF loaders used by GUI ---------

def load_txt_as_chapter(path: Path) -> Tuple[str, str]:
    text = path.read_text(encoding="utf-8", errors="ignore")
    return path.stem, fix_paragraph_structure(text)

def load_docx_as_chapter(path: Path) -> Tuple[str, str]:
    import docx  # type: ignore
    d = docx.Document(str(path))
    paras = []
    for p in d.paragraphs:
        t = (p.text or "").strip()
        if t:
            paras.append(t)
        else:
            if paras and paras[-1] != "":
                paras.append("")
    text = "\n\n".join([p for p in paras if p != ""] + ([] if (paras and paras[-1] != "") else []))
    return path.stem, fix_paragraph_structure(text)

def load_pdf_as_chapter(path: Path) -> Tuple[str, str]:
    text = ""
    try:
        from pdfminer.high_level import extract_text  # type: ignore
        from pdfminer.layout import LAParams  # type: ignore
        laparams = LAParams(line_margin=0.35, word_margin=0.1, boxes_flow=None, char_margin=2.0)
        text = extract_text(str(path), laparams=laparams) or ""
    except Exception:
        try:
            from pypdf import PdfReader  # type: ignore
            rdr = PdfReader(str(path))
            text = "\n\n".join((page.extract_text() or "") for page in rdr.pages)
        except Exception:
            text = ""
    return path.stem, fix_paragraph_structure(text)

# ============================================================
# Title/Intro/Outro and sentence synthesis
# ============================================================

# Case-insensitive heading detection for body cleanup
_CHAPTER_HEADING_RE = re.compile(
    r'^\s*(chapter)\s+([0-9]+|[ivxlcdm]+)\s*[:\-–]?\s*(.*)$', re.IGNORECASE
)

def _strip_leading_heading_from_body_core(body: str, title: str) -> str:
    if not body:
        return ""
    s = body.replace("\r\n", "\n").replace("\r", "\n")
    lines = s.split("\n")
    first_idx = next((i for i, ln in enumerate(lines) if ln.strip()), None)
    if first_idx is None:
        return s.strip("\n")
    first = lines[first_idx].strip()
    if first.casefold() == (title or "").strip().casefold():
        i = first_idx + 1
        while i < len(lines) and not lines[i].strip():
            i += 1
        return "\n".join(lines[i:]).strip("\n")
    if _CHAPTER_HEADING_RE.match(first):
        i = first_idx + 1
        while i < len(lines) and not lines[i].strip():
            i += 1
        return "\n".join(lines[i:]).strip("\n")
    return s.strip("\n")

def _normalize_title_for_voice(idx: int, title: str) -> str:
    t = (title or "").strip()
    if re.match(r"^\s*chapter\b", t, flags=re.IGNORECASE):
        return t
    if t:
        return f"Chapter {idx}: {t}"
    return f"Chapter {idx}"

# Fast, dependency-light sentence splitter
_SENT_RE = re.compile(r"(?<=\S[\.!\?])\s+(?=[A-Z0-9\"“(])")

def split_into_sentences(text: str) -> List[str]:
    text = (text or "").strip()
    if not text:
        return []
    if _BLINGFIRE is not None:
        try:
            s = _BLINGFIRE.text_to_sentences(text)
            return [ln.strip() for ln in s.splitlines() if ln.strip()]
        except Exception:
            pass
    parts = _SENT_RE.split(text)
    return [p.strip() for p in parts if p.strip()]

# ------------- In-run synthesis cache (voice,speed,text_hash) -------------
_SYN_CACHE: Dict[Tuple[str, float, str], np.ndarray] = {}

def _cache_key(voice: str, speed: float, text: str) -> Tuple[str, float, str]:
    h = hashlib.sha1(text.encode("utf-8")).hexdigest()
    return (voice, float(speed), h)

def synth_text_to_array_cached(text: str, voice: str, use_gpu: bool, speed: float, sr: int) -> "np.ndarray":
    if not text.strip():
        return np.zeros(0, dtype=np.float32)
    key = _cache_key(voice, speed, text)
    arr = _SYN_CACHE.get(key)
    if arr is not None:
        return arr
    arr = kokoro_synthesize_to_array(text, voice, use_gpu=use_gpu, speed=speed, samplerate=sr)
    _SYN_CACHE[key] = arr
    return arr

def _zeros(seconds: float, sr: int) -> "np.ndarray":
    return np.zeros(int(round(max(0.0, seconds) * sr)), dtype=np.float32)

def _compose_spoken_chapter(
    idx: int,
    title: str,
    body: str,
    *,
    include_titles: bool,
    title_pause_s: float,
    sentence_pause_s: float,
    voice: str,
    use_gpu: bool,
    speed: float,
    samplerate: int,
    intro_template: Optional[str] = None,
    outro_template: Optional[str] = None,
    enable_intro: bool = False,
    enable_outro: bool = False,
) -> "np.ndarray":
    """
    Build audio for one chapter with sentence-level synthesis, optional title/intro/outro, and pauses.
    """
    import numpy as np
    pieces: List[np.ndarray] = []

    if enable_intro and intro_template:
        intro_text = intro_template.format(n=idx, title=title.strip())
        if intro_text.strip():
            pieces.append(synth_text_to_array_cached(intro_text, voice, use_gpu, speed, samplerate))
            pieces.append(_zeros(title_pause_s, samplerate))

    if include_titles:
        title_text = _normalize_title_for_voice(idx, title)
        pieces.append(synth_text_to_array_cached(title_text, voice, use_gpu, speed, samplerate))
        if title_pause_s > 0:
            pieces.append(_zeros(title_pause_s, samplerate))

    body_clean = _strip_leading_heading_from_body_core(body or "", title or "")
    if body_clean:
        sents = split_into_sentences(body_clean) or [body_clean]
        for i, sent in enumerate(sents):
            pieces.append(synth_text_to_array_cached(sent, voice, use_gpu, speed, samplerate))
            if i != len(sents) - 1 and sentence_pause_s > 0:
                pieces.append(_zeros(sentence_pause_s, samplerate))

    if enable_outro and outro_template:
        outro_text = outro_template.format(n=idx, title=title.strip())
        if outro_text.strip():
            pieces.append(_zeros(title_pause_s, samplerate))
            pieces.append(synth_text_to_array_cached(outro_text, voice, use_gpu, speed, samplerate))

    if not pieces:
        return np.zeros(0, dtype=np.float32)
    return np.concatenate(pieces).astype(np.float32, copy=False)

# ============================================================
# Encoding helpers (bitrate, chapters, mastering)
# ============================================================

def _run_ffmpeg(cmd: List[str]) -> None:
    subprocess.run(cmd, check=True)

def _write_wav(path: Path, samples: "np.ndarray", sr: int):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "wb") as f:
        f.write(_numpy_to_wav_bytes(samples, samplerate=sr))

def _maybe_master(input_wav: Path, output_wav: Path, enable_master: bool) -> Path:
    if not enable_master:
        return input_wav
    ff = ensure_ffmpeg()
    cmd = [ff, "-y", "-i", str(input_wav), "-af", "loudnorm=I=-18:TP=-2:LRA=11", str(output_wav)]
    _run_ffmpeg(cmd)
    return output_wav

def _build_ffmetadata(book_title: str, chapters: List[Tuple[str, int, int]]) -> str:
    lines = [";FFMETADATA1", f"title={book_title}"]
    for title, start_ms, end_ms in chapters:
        lines += [
            "[CHAPTER]",
            "TIMEBASE=1/1000",
            f"START={max(0, int(start_ms))}",
            f"END={max(0, int(end_ms))}",
            f"title={title}",
        ]
    return "\n".join(lines) + "\n"

def _attach_cover_and_metadata_args(cover_path: Optional[Path], meta: Dict[str, str]) -> List[str]:
    args: List[str] = []
    for k, v in (meta or {}).items():
        if v:
            args += ["-metadata", f"{k}={v}"]
    if cover_path and cover_path.exists():
        args += ["-i", str(cover_path)]
    return args

# ===========================
# Converters
# ===========================

def convert_epub_to_m4b(
    epub_path: Path,
    m4b_out: Path,
    *,
    engine: str = "kokoro",
    voice: str = "af_bella",
    speed: float = 1.0,
    use_gpu: bool = False,
    selected_chapter_indices: Optional[List[int]] = None,
    samplerate: int = 22050,
    output_format: str = "m4b",   # "m4b" or "wav"
    chapters_override: Optional[List[Tuple[str, str]]] = None,
    include_titles: bool = True,
    title_pause_seconds: float = 0.5,
    sentence_pause_seconds: float = 0.2,
    bitrate_kbps: int = 64,
    enable_mastering: bool = False,
    # Intro/Outro
    enable_intro: bool = False,
    enable_outro: bool = False,
    intro_template: str = "",
    outro_template: str = "",
    # Cover + metadata (M4B)
    cover_image: Optional[Path] = None,
    meta_title: Optional[str] = None,
    meta_artist: Optional[str] = None,
    meta_album: Optional[str] = None,
    embed_chapters: bool = True,  # Only honored for m4b + single export
) -> Path:
    """
    Single file export. Supports WAV or M4B.
    """
    import numpy as np

    epub_path = Path(epub_path)
    m4b_out = Path(m4b_out)

    tmp = m4b_out.parent / "_tmp_audio"
    tmp.mkdir(parents=True, exist_ok=True)

    try:
        chapters_src = chapters_override[:] if chapters_override is not None else _extract_chapters_text(epub_path)
        if selected_chapter_indices:
            sel = set(selected_chapter_indices)
            chapters_src = [c for i, c in enumerate(chapters_src) if i in sel]
        if not chapters_src:
            raise RuntimeError("No text content found in selected chapters.")
        if engine != "kokoro":
            raise NotImplementedError("Only Kokoro engine is implemented in this build.")

        audios: List[np.ndarray] = []
        toc: List[Tuple[str, int, int]] = []
        cur_ms = 0
        for i, (title, text) in enumerate(chapters_src, start=1):
            ch = _compose_spoken_chapter(
                i, title, text,
                include_titles=include_titles,
                title_pause_s=float(title_pause_seconds),
                sentence_pause_s=float(sentence_pause_seconds),
                voice=voice, use_gpu=use_gpu, speed=speed, samplerate=samplerate,
                intro_template=intro_template, outro_template=outro_template,
                enable_intro=enable_intro, enable_outro=enable_outro,
            )
            if ch.size == 0:
                continue
            length_ms = int(round(1000.0 * ch.shape[0] / samplerate))
            start_ms = cur_ms
            end_ms = cur_ms + length_ms
            chap_title = _normalize_title_for_voice(i, title)
            toc.append((chap_title, start_ms, end_ms))
            cur_ms = end_ms
            audios.append(ch)

        if not audios:
            raise RuntimeError("No audio generated.")
        full = np.concatenate(audios).astype(np.float32, copy=False)

        wav_raw = tmp / "book_raw.wav"
        _write_wav(wav_raw, full, samplerate)

        wav_final = wav_raw
        if enable_mastering:
            wav_master = tmp / "book_master.wav"
            wav_final = _maybe_master(wav_raw, wav_master, enable_mastering)

        fmt = (output_format or "m4b").strip().lower()
        if fmt == "wav":
            out_path = m4b_out.with_suffix(".wav")
            if str(wav_final.resolve()) != str(out_path.resolve()):
                shutil.copyfile(str(wav_final), str(out_path))
            return out_path

        ff = ensure_ffmpeg()
        meta = {
            "title": meta_title or _get_book_title(epub_path),
            "artist": meta_artist or "",
            "album": meta_album or (meta_title or _get_book_title(epub_path)),
        }

        ffmeta_file = None
        extra_inputs: List[str] = []
        map_args: List[str] = []
        if embed_chapters:
            ffmeta_file = tmp / "chapters.ffmeta"
            ffmeta_file.write_text(_build_ffmetadata(meta["title"], toc), encoding="utf-8")
            extra_inputs += ["-i", str(ffmeta_file)]
            map_args += ["-map_metadata", "1"]

        cover_args = _attach_cover_and_metadata_args(cover_image, meta)
        cmd = [ff, "-y", "-i", str(wav_final)]
        cmd += extra_inputs
        cmd += cover_args
        cmd += ["-map", "0:a"]

        if cover_image and cover_image.exists():
            cover_idx = 1 + (1 if embed_chapters else 0)
            cmd += ["-map", f"{cover_idx}:v", "-disposition:v:0", "attached_pic"]
            cmd += ["-metadata:s:v:0", "title=Cover", "-metadata:s:v:0", "comment=Cover (front)"]

        if map_args:
            cmd += map_args

        cmd += ["-c:a", "aac", "-b:a", f"{int(bitrate_kbps)}k", "-movflags", "+faststart", str(m4b_out)]
        _run_ffmpeg(cmd)
        return m4b_out

    finally:
        try:
            shutil.rmtree(tmp, ignore_errors=True)
        except Exception:
            pass

def convert_epub_to_tracks(
    epub_path: Path,
    *,
    engine: str = "kokoro",
    voice: str = "af_bella",
    speed: float = 1.0,
    use_gpu: bool = False,
    selected_chapter_indices: Optional[List[int]] = None,
    samplerate: int = 22050,
    output_format: str = "m4b",   # "m4b" or "wav"
    chapters_override: Optional[List[Tuple[str, str]]] = None,
    include_titles: bool = True,
    title_pause_seconds: float = 0.5,
    sentence_pause_seconds: float = 0.2,
    bitrate_kbps: int = 64,
    enable_mastering: bool = False,
    enable_intro: bool = False,
    enable_outro: bool = False,
    intro_template: str = "",
    outro_template: str = "",
) -> Path:
    """
    Per-chapter export (no global chapter table).
    """
    import numpy as np

    epub_path = Path(epub_path)
    out_root = epub_path.parent
    book_title = _safe_filename(_get_book_title(epub_path))
    out_dir = out_root / book_title
    out_dir.mkdir(parents=True, exist_ok=True)

    chapters = chapters_override[:] if chapters_override is not None else _extract_chapters_text(epub_path)
    if selected_chapter_indices:
        idx_set = set(selected_chapter_indices)
        chapters = [c for i, c in enumerate(chapters) if i in idx_set]
    if not chapters:
        raise RuntimeError("No chapters found.")

    fmt = (output_format or "m4b").lower()
    if fmt not in ("m4b", "wav"):
        raise ValueError("output_format must be 'm4b' or 'wav'.")

    tmp_dir = out_dir / "_tmp_audio"
    tmp_dir.mkdir(parents=True, exist_ok=True)

    try:
        ffmpeg = ensure_ffmpeg() if fmt == "m4b" else None

        for idx, (title, text) in enumerate(chapters, start=1):
            if not (title.strip() or text.strip()):
                continue

            ch_audio = _compose_spoken_chapter(
                idx, title, text,
                include_titles=include_titles,
                title_pause_s=float(title_pause_seconds),
                sentence_pause_s=float(sentence_pause_seconds),
                voice=voice, use_gpu=use_gpu, speed=speed, samplerate=samplerate,
                intro_template=intro_template, outro_template=outro_template,
                enable_intro=enable_intro, enable_outro=enable_outro,
            )
            if ch_audio.size == 0:
                continue

            if title and not title.startswith("Chapter "):
                base_name = f"Chapter {idx}: {title}"
            else:
                base_name = title or f"Chapter {idx}"
            base_name = _safe_filename(base_name.replace(":", " -"))

            wav_path = tmp_dir / f"{base_name}.wav"
            _write_wav(wav_path, ch_audio, samplerate)

            if enable_mastering:
                master_wav = tmp_dir / f"{base_name}.master.wav"
                _maybe_master(wav_path, master_wav, enable_mastering)
                wav_path = master_wav

            if fmt == "wav":
                final_wav = out_dir / f"{base_name}.wav"
                shutil.copyfile(str(wav_path), str(final_wav))
            else:
                out_file = out_dir / f"{base_name}.m4b"
                cmd = [ffmpeg, "-y", "-i", str(wav_path), "-c:a", "aac", "-b:a", f"{int(bitrate_kbps)}k", "-movflags", "+faststart", str(out_file)]
                _run_ffmpeg(cmd)

        return out_dir

    finally:
        try:
            shutil.rmtree(tmp_dir, ignore_errors=True)
        except Exception:
            pass
