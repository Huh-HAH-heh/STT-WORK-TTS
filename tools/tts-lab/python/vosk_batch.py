from __future__ import annotations

import argparse
from pathlib import Path

from vosk_tts import Model, Synth


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--text", required=True)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--voices", nargs="+", default=["0", "1", "2", "3", "4"])
    args = parser.parse_args()

    if not args.model.is_dir():
        raise FileNotFoundError(f"Не найдена модель Vosk-TTS: {args.model}")
    args.output_dir.mkdir(parents=True, exist_ok=True)
    model = Model(model_name=str(args.model))
    synth = Synth(model)
    for speaker_id in args.voices:
        output = args.output_dir / f"Vosk_{speaker_id}.wav"
        synth.synth(args.text, str(output), speaker_id=int(speaker_id))
        print(f"Создано: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
