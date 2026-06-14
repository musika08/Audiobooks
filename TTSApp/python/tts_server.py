"""
Local TTS sidecar for TTSApp (GPU).

Launched by the WPF app:  python tts_server.py --port 8765 --model <id>

Supported --model ids:
  xtts-v2      Coqui XTTS v2  (built-in speakers + voice cloning)
  chatterbox   Resemble Chatterbox (single default voice + voice cloning)
  fish-opus    OpenAudio / Fish-Speech (experimental)
  vibevoice    Microsoft VibeVoice (experimental)

Endpoints:
  GET  /health      -> {"status","device","model"}
  GET  /speakers    -> {"speakers": [...]}
  POST /synthesize  -> WAV bytes   body: {text, speaker, speaker_wav, speed, language}

Only one model is loaded at a time; the app restarts this process to switch models.
"""

import argparse
import io
import os
import re
import types
import wave

# Coqui XTTS shows an interactive license [y/n] prompt on first load. We run headless,
# so auto-accept the non-commercial CPML to avoid hanging forever waiting for stdin.
os.environ.setdefault("COQUI_TOS_AGREED", "1")

import numpy as np
import torch
from fastapi import FastAPI, Response, HTTPException
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

app = FastAPI()
_engine = None  # set at startup

# Allowed models — keep in sync with PythonSidecarEngine.KnownModels in C#.
KNOWN_MODELS = {"xtts-v2", "chatterbox", "fish-opus", "vibevoice"}

# Voice-clone references must live under this directory (set by the C# launcher).
VOICES_DIR = os.path.abspath(os.environ.get("TTSAPP_VOICES_DIR", ""))

# Reasonable safety limits.
MAX_TEXT_LENGTH = 100_000
MAX_PAUSE_MS = 4_000


# ---------------- engine implementations ----------------


class XttsEngine:
    name = "xtts-v2"

    def __init__(self):
        from TTS.api import TTS

        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.tts = TTS("tts_models/multilingual/multi-dataset/xtts_v2").to(self.device)
        self.sr = self.tts.synthesizer.output_sample_rate

    def speakers(self) -> list[str]:
        spk = getattr(self.tts, "speakers", None) or []
        return sorted(str(s) for s in spk)

    def synth(self, req) -> np.ndarray:
        kwargs = dict(
            text=req.text,
            language=req.language or "en",
            speed=req.speed,
            temperature=req.temperature,
            repetition_penalty=req.repetition_penalty,
        )
        ref = resolve_speaker_wav(req.speaker_wav)
        if ref:
            kwargs["speaker_wav"] = ref
        else:
            spk = self.speakers()
            kwargs["speaker"] = req.speaker or (spk[0] if spk else None)
        return np.array(self.tts.tts(**kwargs))


class ChatterboxEngine:
    name = "chatterbox"

    def __init__(self):
        # Chatterbox watermarks output via `perth`; if perth's backend failed to load,
        # PerthImplicitWatermarker is None and crashes. Patch in a no-op watermarker.
        try:
            import perth

            if getattr(perth, "PerthImplicitWatermarker", None) is None:

                class _NoWatermark:
                    def apply_watermark(self, wav, sample_rate=None, **kw):
                        return wav

                    def get_watermark(self, *a, **k):
                        return None

                perth.PerthImplicitWatermarker = _NoWatermark
        except Exception:
            pass

        from chatterbox.tts import ChatterboxTTS

        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.model = ChatterboxTTS.from_pretrained(device=self.device)
        self.sr = self.model.sr

    def speakers(self) -> list[str]:
        # Chatterbox has no named speakers; it uses its default voice or a cloned reference.
        return ["Default"]

    def synth(self, req) -> np.ndarray:
        kw = dict(
            exaggeration=req.exaggeration,
            cfg_weight=req.cfg_weight,
            temperature=req.temperature,
        )
        ref = resolve_speaker_wav(req.speaker_wav)
        if ref:
            kw["audio_prompt_path"] = ref
        try:
            wav = self.model.generate(req.text, **kw)
        except TypeError:
            # Older chatterbox signature without tuning kwargs.
            fallback = {}
            ref = resolve_speaker_wav(req.speaker_wav)
            if ref:
                fallback["audio_prompt_path"] = ref
            wav = self.model.generate(req.text, **fallback)
        if hasattr(wav, "detach"):
            wav = wav.detach().cpu().numpy()
        return np.asarray(wav).squeeze()


class FishEngine:
    """OpenAudio / Fish-Speech (experimental, GPU). Loaded only when selected.

    The fish-speech inference API changes between releases, so this wraps it
    defensively and raises a clear error if the installed version differs.
    """

    name = "fish-opus"

    def __init__(self):
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        try:
            # Newer OpenAudio/fish-speech expose a high-level engine.
            from fish_speech.inference_engine import TTSInferenceEngine  # type: ignore

            self.engine = TTSInferenceEngine(device=self.device)
            self.sr = getattr(self.engine, "sample_rate", 44100)
        except Exception as e:  # pragma: no cover
            raise RuntimeError(
                "Fish/OpenAudio is not wired for this installed version. "
                "Install fish-speech and adapt FishEngine in tts_server.py. "
                f"Underlying error: {e}"
            )

    def speakers(self) -> list[str]:
        return ["Default"]

    def synth(self, req) -> np.ndarray:
        ref = resolve_speaker_wav(req.speaker_wav)
        wav = self.engine.tts(req.text, reference_audio=ref)  # API-dependent
        if hasattr(wav, "detach"):
            wav = wav.detach().cpu().numpy()
        return np.asarray(wav).squeeze()


class VibeVoiceEngine:
    """Microsoft VibeVoice (GPU, experimental). Long-form, expressive cloning.

    First cut: single-speaker — needs a reference clip via the clone (mic) button.
    Uses 'sdpa' attention to avoid the flash-attn build requirement on Windows.
    """

    name = "vibevoice"

    def __init__(self):
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        model_path = "microsoft/VibeVoice-1.5B"
        try:
            from vibevoice.modular.modeling_vibevoice_inference import (
                VibeVoiceForConditionalGenerationInference,
            )
            from vibevoice.processor.vibevoice_processor import VibeVoiceProcessor

            self.processor = VibeVoiceProcessor.from_pretrained(model_path)
            self.model = VibeVoiceForConditionalGenerationInference.from_pretrained(
                model_path,
                torch_dtype=torch.bfloat16,
                device_map=self.device,
                attn_implementation="sdpa",  # avoid flash-attn on Windows
            )
            self.model.eval()
            self.sr = 24000
        except Exception as e:  # pragma: no cover
            raise RuntimeError(
                "VibeVoice failed to load. It may need a newer transformers or a different "
                "install. See https://github.com/vibevoice-community/VibeVoice . "
                f"Underlying error: {e}"
            )

    def speakers(self) -> list[str]:
        return ["Default"]

    def synth(self, req) -> np.ndarray:
        ref = resolve_speaker_wav(req.speaker_wav)
        if not ref:
            raise RuntimeError(
                "VibeVoice needs a reference voice. Click the mic button and pick a clean "
                "audio clip to clone."
            )
        script = f"Speaker 1: {req.text}"
        inputs = self.processor(
            text=[script],
            voice_samples=[[ref]],
            padding=True,
            return_tensors="pt",
        ).to(self.device)
        with torch.no_grad():
            out = self.model.generate(
                **inputs,
                tokenizer=self.processor.tokenizer,
                cfg_scale=req.cfg_scale,
            )
        audio = out.speech_outputs[0]
        if hasattr(audio, "detach"):
            audio = audio.detach().cpu().float().numpy()
        return np.asarray(audio).squeeze()


def resolve_speaker_wav(raw_path: str) -> str | None:
    """Return a validated absolute path to a voice-clone reference, or None."""
    if not raw_path:
        return None
    abs_path = os.path.abspath(raw_path)
    if not os.path.isfile(abs_path):
        return None
    if VOICES_DIR and not abs_path.startswith(VOICES_DIR + os.sep):
        raise HTTPException(
            status_code=400,
            detail=f"Clone reference must be inside the voices directory ({VOICES_DIR}).",
        )
    return abs_path


def normalize_text(t: str) -> str:
    """Tidy a spoken segment (ellipsis handled separately by splitting)."""
    if not t:
        return t
    t = t.replace("…", ".")
    t = re.sub(r"\.{2,}", ".", t)
    t = re.sub(r"\s*([,.;:!?])\1+", r"\1", t)
    t = re.sub(r"[ \t]+", " ", t)
    return t.strip()


def segment_with_pauses(text, p_comma, p_sentence, p_ellipsis, p_paragraph):
    """Split text into (spoken_segment, pause_ms_after) honoring punctuation.
    Each segment keeps its punctuation for intonation; ellipses are removed and
    become pure silence. Only breaks where the relevant pause > 0 (fewer model calls)."""
    text = (text or "").replace("\r\n", "\n").replace("\r", "\n").replace("…", "...")
    out = []
    paras = re.split(r"\n[ \t]*\n+", text)
    for pidx, para in enumerate(paras):
        para = re.sub(r"[ \t]*\n[ \t]*", " ", para).strip()
        if not para:
            continue

        buf = ""
        i, n = 0, len(para)
        while i < n:
            ch = para[i]
            # Ellipsis / dot-run → always break (remove dots), insert ellipsis pause.
            if ch == "." and i + 1 < n and para[i + 1] == ".":
                j = i
                while j < n and para[j] == ".":
                    j += 1
                if buf.strip():
                    out.append((buf.strip(), p_ellipsis))
                    buf = ""
                elif out:
                    out[-1] = (out[-1][0], out[-1][1] + p_ellipsis)
                else:
                    out.append(("", p_ellipsis))
                i = j
                continue

            buf += ch
            if ch in ".!?;:" and p_sentence > 0:
                if buf.strip():
                    out.append((buf.strip(), p_sentence))
                    buf = ""
            elif ch == "," and p_comma > 0:
                if buf.strip():
                    out.append((buf.strip(), p_comma))
                    buf = ""
            i += 1

        if buf.strip():
            out.append((buf.strip(), 0))

        if pidx < len(paras) - 1 and out and p_paragraph > 0:
            out[-1] = (out[-1][0], out[-1][1] + p_paragraph)

    return out if out else [(text.strip(), 0)]


def build_engine(model_id: str):
    if model_id not in KNOWN_MODELS:
        raise ValueError(
            f"Unknown model '{model_id}'. Supported models: {', '.join(sorted(KNOWN_MODELS))}"
        )
    if model_id == "chatterbox":
        return ChatterboxEngine()
    if model_id == "fish-opus":
        return FishEngine()
    if model_id == "vibevoice":
        return VibeVoiceEngine()
    return XttsEngine()


# ---------------- HTTP ----------------


class SynthRequest(BaseModel):
    text: str = Field(..., min_length=1, max_length=MAX_TEXT_LENGTH)
    speaker: str = ""
    speaker_wav: str = ""  # reference audio path for cloning (overrides speaker)
    speed: float = Field(1.0, ge=0.5, le=2.0)
    language: str = "en"
    denoise: bool = False  # run a de-reverb/denoise pass on the output (DeepFilterNet)
    # Voice tuning (each engine uses the relevant ones).
    temperature: float = Field(0.7, ge=0.0, le=2.0)
    repetition_penalty: float = Field(2.0, ge=1.0, le=10.0)
    exaggeration: float = Field(0.5, ge=0.0, le=2.0)
    cfg_weight: float = Field(0.5, ge=0.0, le=2.0)
    cfg_scale: float = Field(1.3, ge=1.0, le=5.0)
    # Pause durations (ms) inserted after punctuation (0 = no break there).
    pause_comma: int = Field(0, ge=0, le=MAX_PAUSE_MS)
    pause_sentence: int = Field(0, ge=0, le=MAX_PAUSE_MS)
    pause_ellipsis: int = Field(300, ge=0, le=MAX_PAUSE_MS)
    pause_paragraph: int = Field(0, ge=0, le=MAX_PAUSE_MS)


def _request_with_text(req: SynthRequest, text: str):
    """Return a shallow copy of the request with a new text value."""
    data = req.model_dump() if hasattr(req, "model_dump") else req.dict()
    data["text"] = text
    return types.SimpleNamespace(**data)


# Lazy DeepFilterNet state (loaded only when denoise is first requested).
_df = {"model": None, "state": None, "sr": 48000}


def _dereverb(samples: np.ndarray, sr: int) -> np.ndarray:
    """Reduce room reverb/noise via DeepFilterNet. Returns audio at the original sample rate."""
    try:
        import torch
        import torchaudio

        if _df["model"] is None:
            from df.enhance import init_df

            model, state, _ = init_df()
            _df["model"], _df["state"], _df["sr"] = model, state, state.sr()

        from df.enhance import enhance

        wav = torch.from_numpy(np.asarray(samples, dtype=np.float32)).unsqueeze(0)
        target = _df["sr"]
        if sr != target:
            wav = torchaudio.functional.resample(wav, sr, target)
        out = enhance(_df["model"], _df["state"], wav)
        if target != sr:
            out = torchaudio.functional.resample(out, target, sr)
        return out.squeeze().cpu().numpy()
    except Exception as e:  # pragma: no cover
        print(f"De-reverb skipped (DeepFilterNet unavailable): {e}", flush=True)
        return samples


def to_wav_bytes(samples: np.ndarray, sr: int) -> bytes:
    samples = np.asarray(samples, dtype=np.float32).squeeze()
    samples = np.clip(samples, -1.0, 1.0)
    pcm = (samples * 32767.0).astype("<i2")
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(pcm.tobytes())
    return buf.getvalue()


@app.exception_handler(RequestValidationError)
async def validation_exception_handler(_, exc: RequestValidationError):
    return JSONResponse(status_code=422, content={"detail": exc.errors()})


@app.exception_handler(Exception)
async def generic_exception_handler(_, exc: Exception):
    # Log the real error server-side, return a sanitized message to the client.
    print(f"Synthesis error: {exc}", flush=True)
    return JSONResponse(
        status_code=500,
        content={"detail": f"Synthesis failed: {type(exc).__name__}"},
    )


@app.get("/health")
def health():
    return {
        "status": "ok" if _engine is not None else "loading",
        "device": getattr(_engine, "device", "cpu"),
        "model": getattr(_engine, "name", "?"),
    }


@app.get("/speakers")
def speakers():
    if _engine is None:
        raise HTTPException(status_code=503, detail="Engine not loaded yet")
    return {"speakers": _engine.speakers()}


@app.post("/synthesize")
def synthesize(req: SynthRequest):
    if _engine is None:
        raise HTTPException(status_code=503, detail="Engine not loaded yet")

    sr = _engine.sr

    # Pace punctuation: speak each segment (with its punctuation for intonation), then
    # insert real silence after it. Ellipses become silence (no grunt/"ah").
    pieces = segment_with_pauses(
        req.text,
        req.pause_comma,
        req.pause_sentence,
        req.pause_ellipsis,
        req.pause_paragraph,
    )
    chunks = []
    for seg, pause_ms in pieces:
        seg = normalize_text(seg)
        if seg and re.search(r"[A-Za-z0-9]", seg):
            # Do not mutate the incoming request object; build a local kwargs-only stand-in.
            chunks.append(
                np.asarray(
                    _engine.synth(_request_with_text(req, seg)), dtype=np.float32
                ).squeeze()
            )
        if pause_ms > 0:
            chunks.append(
                np.zeros(
                    int(sr * min(pause_ms, MAX_PAUSE_MS) / 1000.0), dtype=np.float32
                )
            )

    samples = np.concatenate(chunks) if chunks else np.zeros(1, dtype=np.float32)

    if req.denoise:
        samples = _dereverb(samples, sr)
    data = to_wav_bytes(samples, sr)
    return Response(content=data, media_type="audio/wav")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--model", type=str, default="xtts-v2")
    args = parser.parse_args()

    _engine = build_engine(args.model)

    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=args.port, log_level="warning")
