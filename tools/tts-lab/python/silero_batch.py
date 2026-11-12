from __future__ import annotations

import argparse
import os
from pathlib import Path

import soundfile as sf
import torch


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--text", required=True)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--voices", nargs="+", default=[
        "aidar", "baya", "kseniya", "xenia", "eugene"
    ])
    args = parser.parse_args()

    args.output_dir.mkdir(parents=True, exist_ok=True)
    torch.set_num_threads(max(1, min(6, os.cpu_count() or 2)))
    hub_args = {
        "repo_or_dir": "snakers4/silero-models",
        "model": "silero_tts",
        "language": "ru",
        "speaker": "v5_6_ru",
        "trust_repo": True,
    }
    try:
        model, _ = torch.hub.load(**hub_args)
    except AssertionError as exc:
        if "v5_6_ru" not in str(exc):
            raise
        print("Refreshing stale Silero Hub cache (missing v5_6_ru)...")
        model, _ = torch.hub.load(**hub_args, force_reload=True)
    model.to(torch.device("cpu"))
    for voice in args.voices:
        audio = model.apply_tts(text=args.text, speaker=voice, sample_rate=48000)
        output = args.output_dir / f"Silero_{voice}.wav"
        sf.write(str(output), audio.detach().cpu().numpy(), 48000)
        print(f"Создано: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
