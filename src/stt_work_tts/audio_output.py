from __future__ import annotations

from datetime import datetime
from pathlib import Path

import sounddevice as sd
import soundfile as sf

from .config import TTSSettings
from .domain import SpeechAudio


def play_speech(speech: SpeechAudio) -> None:
    """Воспроизводит результат из RAM, не создавая WAV на диске."""
    sd.play(speech.samples, samplerate=speech.sample_rate)
    sd.wait()


def save_speech(speech: SpeechAudio, settings: TTSSettings) -> Path:
    settings.speech_dir.mkdir(parents=True, exist_ok=True)
    filename = datetime.now().strftime("answer_%Y%m%d_%H%M%S.wav")
    path = settings.speech_dir / filename
    sf.write(path, speech.samples, speech.sample_rate)
    return path
