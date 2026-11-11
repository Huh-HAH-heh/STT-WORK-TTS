from __future__ import annotations

import argparse
from pathlib import Path

import soundfile as sf
from xvibe_pocket_tts import RussianTTS


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--text", required=True)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--voices", nargs="+", default=[
        "male_deep", "female_silky", "male_authoritative"
    ])
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    tts = RussianTTS(device="cpu", auto_accent=True)
    for voice in args.voices:
        audio = tts.generate(args.text, voice=voice)
        output = args.output_dir / f"xVibe_{voice}.wav"
        sf.write(str(output), audio.detach().cpu().numpy(), tts.sample_rate)
        print(f"Создано: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
