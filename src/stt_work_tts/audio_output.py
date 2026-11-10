from __future__ import annotations

import wave
from datetime import datetime
from pathlib import Path

import sounddevice as sd

from .config import TTSSettings
from .domain import SpeechAudio


def play_speech(speech: SpeechAudio) -> None:
    """Воспроизводит PCM16 напрямую из памяти."""
    with sd.RawOutputStream(
        samplerate=speech.sample_rate,
        channels=1,
        dtype="int16",
    ) as stream:
        chunk_size = 16_000
        for offset in range(0, len(speech.pcm16), chunk_size):
            stream.write(speech.pcm16[offset : offset + chunk_size])


def save_speech(speech: SpeechAudio, settings: TTSSettings) -> Path:
    """Экспортирует готовую речь в WAV только при включённом флаге."""
    settings.speech_dir.mkdir(parents=True, exist_ok=True)
    filename = datetime.now().strftime("answer_%Y%m%d_%H%M%S.wav")
    path = settings.speech_dir / filename
    with wave.open(str(path), "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(speech.sample_rate)
        wav_file.writeframes(speech.pcm16)
    return path
