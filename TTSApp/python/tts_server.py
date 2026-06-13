"""
Local TTS sidecar for TTSApp (GPU).

Launched by the WPF app:  python tts_server.py --port 8765 --model <id>

Supported --model ids:
  xtts-v2      Coqui XTTS v2  (built-in speakers + voice cloning)
  chatterbox   Resemble Chatterbox (single default voice + voice cloning)

Endpoints:
  GET  /health      -> {"status","device","model"}
  GET  /speakers    -> {"speakers": [...]}
  POST /synthesize  -> WAV bytes   body: {text, speaker, speaker_wav, speed, language}

Only one model is loaded at a time; the app restarts this process to switch models.
"""

import argparse
import io
import os
import wave

import numpy as np
import torch
from fastapi import FastAPI, Response
from pydantic import BaseModel

app = FastAPI()
_engine = None  # set at startup


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
        kwargs = dict(text=req.text, language=req.language or "en", speed=req.speed)
        if req.speaker_wav and os.path.isfile(req.speaker_wav):
            kwargs["speaker_wav"] = req.speaker_wav
        else:
            spk = self.speakers()
            kwargs["speaker"] = req.speaker or (spk[0] if spk else None)
        return np.array(self.tts.tts(**kwargs))


class ChatterboxEngine:
    name = "chatterbox"

    def __init__(self):
        from chatterbox.tts import ChatterboxTTS

        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.model = ChatterboxTTS.from_pretrained(device=self.device)
        self.sr = self.model.sr

    def speakers(self) -> list[str]:
        # Chatterbox has no named speakers; it uses its default voice or a cloned reference.
        return ["Default"]

    def synth(self, req) -> np.ndarray:
        if req.speaker_wav and os.path.isfile(req.speaker_wav):
            wav = self.model.generate(req.text, audio_prompt_path=req.speaker_wav)
        else:
            wav = self.model.generate(req.text)
        # Chatterbox returns a torch tensor
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
        ref = (
            req.speaker_wav
            if (req.speaker_wav and os.path.isfile(req.speaker_wav))
            else None
        )
        wav = self.engine.tts(req.text, reference_audio=ref)  # API-dependent
        if hasattr(wav, "detach"):
            wav = wav.detach().cpu().numpy()
        return np.asarray(wav).squeeze()


def build_engine(model_id: str):
    if model_id == "chatterbox":
        return ChatterboxEngine()
    if model_id == "fish-opus":
        return FishEngine()
    return XttsEngine()  # default


# ---------------- HTTP ----------------


class SynthRequest(BaseModel):
    text: str
    speaker: str = ""
    speaker_wav: str = ""  # reference audio path for cloning (overrides speaker)
    speed: float = 1.0
    language: str = "en"


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


@app.get("/health")
def health():
    return {
        "status": "ok" if _engine is not None else "loading",
        "device": getattr(_engine, "device", "cpu"),
        "model": getattr(_engine, "name", "?"),
    }


@app.get("/speakers")
def speakers():
    return {"speakers": _engine.speakers()}


@app.post("/synthesize")
def synthesize(req: SynthRequest):
    samples = _engine.synth(req)
    data = to_wav_bytes(samples, _engine.sr)
    return Response(content=data, media_type="audio/wav")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--model", type=str, default="xtts-v2")
    args = parser.parse_args()

    _engine = build_engine(args.model)

    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=args.port, log_level="warning")
