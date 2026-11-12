from __future__ import annotations

import contextlib
import json
import os
import sys
import traceback
from pathlib import Path

PROTOCOL_OUT = sys.stdout


def emit(payload: dict) -> None:
    PROTOCOL_OUT.write(json.dumps(payload, ensure_ascii=False) + "\n")
    PROTOCOL_OUT.flush()


def main() -> int:
    # Report progress before importing PyTorch; imports and model downloads can take time.
    emit({"status": "Запускаю Silero worker и загружаю зависимости PyTorch..."})
    sys.stdout = sys.stderr
    try:
        import soundfile as sf
        import torch

        torch.set_num_threads(max(1, min(6, os.cpu_count() or 2)))
        sys.stdout = PROTOCOL_OUT
        emit({"status": "Загружаю Silero v5_6_ru. При первом запуске скачиваются веса модели..."})
        sys.stdout = sys.stderr
        hub_args = {
            "repo_or_dir": "snakers4/silero-models",
            "model": "silero_tts",
            "language": "ru",
            "speaker": "v5_6_ru",
            "trust_repo": True,
        }
        try:
            model, _ = torch.hub.load(**hub_args)
        except AssertionError:
            # Cached torch.hub repositories can contain an older models.yml.
            # Refresh the checkout once; if the model still cannot load, surface the real error.
            sys.stdout = PROTOCOL_OUT
            emit({"status": "Обнаружен устаревший кэш. Обновляю Silero Hub и повторяю загрузку..."})
            sys.stdout = sys.stderr
            model, _ = torch.hub.load(**hub_args, force_reload=True)
        model.to(torch.device("cpu"))
    except Exception as exc:
        sys.stdout = PROTOCOL_OUT
        emit({"ready": False, "error": f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}"})
        return 1

    sys.stdout = PROTOCOL_OUT
    emit({"ready": True, "engine": "silero"})

    for line in sys.stdin:
        try:
            request = json.loads(line)
            if request.get("shutdown"):
                emit({"ok": True, "shutdown": True})
                break

            text = str(request.get("text", "")).strip()
            output_value = request.get("output_path")
            voice = str(request.get("voice", "xenia")).lower()
            if not text:
                raise ValueError("Text cannot be empty.")
            if voice not in {"aidar", "baya", "kseniya", "xenia", "eugene"}:
                raise ValueError(f"Unsupported Silero voice: {voice}")
            if not output_value:
                raise ValueError("Output path is required.")
            output_path = Path(str(output_value))

            with contextlib.redirect_stdout(sys.stderr):
                audio = model.apply_tts(text=text, speaker=voice, sample_rate=48000)
                sf.write(str(output_path), audio.detach().cpu().numpy(), 48000)

            emit({"ok": True, "output_path": str(output_path)})
        except Exception as exc:
            emit({"ok": False, "error": f"{type(exc).__name__}: {exc}"})

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
