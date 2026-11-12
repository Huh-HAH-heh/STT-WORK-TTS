from __future__ import annotations

import contextlib
import json
import sys
import traceback
from pathlib import Path

PROTOCOL_OUT = sys.stdout


def emit(payload: dict) -> None:
    PROTOCOL_OUT.write(json.dumps(payload, ensure_ascii=False) + "\n")
    PROTOCOL_OUT.flush()


def main() -> int:
    sys.stdout = sys.stderr
    try:
        import soundfile as sf
        from xvibe_pocket_tts import RussianTTS

        tts = RussianTTS(device="cpu", auto_accent=True)
    except Exception as exc:
        sys.stdout = PROTOCOL_OUT
        emit({"ready": False, "error": f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}"})
        return 1

    sys.stdout = PROTOCOL_OUT
    emit({"ready": True, "engine": "xvibe"})

    for line in sys.stdin:
        try:
            request = json.loads(line)
            if request.get("shutdown"):
                emit({"ok": True, "shutdown": True})
                break

            text = str(request.get("text", "")).strip()
            output_path = Path(str(request.get("output_path", "")))
            voice = str(request.get("voice", "female_silky")).lower()
            if not text:
                raise ValueError("Text cannot be empty.")
            if voice not in {"male_deep", "female_silky", "male_authoritative"}:
                raise ValueError(f"Unsupported xVibe voice: {voice}")
            if not str(output_path):
                raise ValueError("Output path is required.")

            with contextlib.redirect_stdout(sys.stderr):
                audio = tts.generate(text, voice=voice)
                sf.write(str(output_path), audio.detach().cpu().numpy(), tts.sample_rate)

            emit({"ok": True, "output_path": str(output_path)})
        except Exception as exc:
            emit({"ok": False, "error": f"{type(exc).__name__}: {exc}"})

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
