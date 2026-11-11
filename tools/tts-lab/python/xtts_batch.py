from __future__ import annotations

import argparse
import re
from pathlib import Path

from TTS.api import TTS


def safe_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9]+", "_", value).strip("_")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--text", required=True)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--speaker-wav", type=Path)
    parser.add_argument("--voices", nargs="+", default=["Ana Florence", "Andrew Chipper"])
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    tts = TTS("tts_models/multilingual/multi-dataset/xtts_v2", gpu=False)
    available = getattr(tts, "speakers", None) or []
    for speaker in args.voices:
        if available and speaker not in available:
            print(f"Голос отсутствует в текущем пакете XTTS: {speaker}")
            continue
        output = args.output_dir / f"XTTS_{safe_name(speaker)}.wav"
        tts.tts_to_file(
            text=args.text,
            speaker=speaker,
            language="ru",
            file_path=str(output),
        )
        print(f"Создано: {output}")

    if args.speaker_wav:
        if not args.speaker_wav.is_file():
            raise FileNotFoundError(f"Не найден WAV-образец: {args.speaker_wav}")
        output = args.output_dir / "XTTS_Cloned.wav"
        tts.tts_to_file(
            text=args.text,
            speaker_wav=str(args.speaker_wav),
            language="ru",
            file_path=str(output),
        )
        print(f"Создано: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
