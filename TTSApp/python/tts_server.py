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

# Coqui XTTS shows an interactive license [y/n] prompt on first load. We run headless,
# so auto-accept the non-commercial CPML to avoid hanging forever waiting for stdin.
os.environ.setdefault("COQUI_TOS_AGREED", "1")

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
        kwargs = dict(
            text=req.text,
            language=req.language or "en",
            speed=req.speed,
            temperature=req.temperature,
            repetition_penalty=req.repetition_penalty,
        )
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
        kw = dict(
            exaggeration=req.exaggeration,
            cfg_weight=req.cfg_weight,
            temperature=req.temperature,
        )
        if req.speaker_wav and os.path.isfile(req.speaker_wav):
            kw["audio_prompt_path"] = req.speaker_wav
        try:
            wav = self.model.generate(req.text, **kw)
        except TypeError:
            # Older chatterbox signature without tuning kwargs.
            wav = self.model.generate(
                req.text,
                **(
                    {"audio_prompt_path": req.speaker_wav}
                    if req.speaker_wav and os.path.isfile(req.speaker_wav)
                    else {}
                ),
            )
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
        ref = (
            req.speaker_wav
            if (req.speaker_wav and os.path.isfile(req.speaker_wav))
            else None
        )
        if ref is None:
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


def build_engine(model_id: str):
    if model_id == "chatterbox":
        return ChatterboxEngine()
    if model_id == "fish-opus":
        return FishEngine()
    if model_id == "vibevoice":
        return VibeVoiceEngine()
    return XttsEngine()  # default


# ---------------- HTTP ----------------


class SynthRequest(BaseModel):
    text: str
    speaker: str = ""
    speaker_wav: str = ""  # reference audio path for cloning (overrides speaker)
    speed: float = 1.0
    language: str = "en"
    denoise: bool = False  # run a de-reverb/denoise pass on the output (DeepFilterNet)
    # Voice tuning (each engine uses the relevant ones).
    temperature: float = 0.7
    repetition_penalty: float = 2.0
    exaggeration: float = 0.5
    cfg_weight: float = 0.5
    cfg_scale: float = 1.3


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
    if req.denoise:
        samples = _dereverb(samples, _engine.sr)
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
