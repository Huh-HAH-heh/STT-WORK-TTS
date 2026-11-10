from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


@dataclass(slots=True)
class AudioClip:
    """Запись PCM16 mono, хранящаяся в оперативной памяти."""

    pcm16: bytes
    sample_rate: int
    captured_at: datetime
    saved_path: Path | None = None

    @property
    def duration_seconds(self) -> float:
        return len(self.pcm16) / (self.sample_rate * 2)

    @classmethod
    def create(
        cls,
        pcm16: bytes,
        sample_rate: int,
        saved_path: Path | None = None,
    ) -> AudioClip:
        if not pcm16:
            raise ValueError("Запись пустая.")
        if len(pcm16) % 2:
            raise ValueError("Повреждён PCM16 буфер: нечётное число байтов.")
        return cls(
            pcm16=pcm16,
            sample_rate=sample_rate,
            captured_at=datetime.now(timezone.utc),
            saved_path=saved_path,
        )


@dataclass(frozen=True, slots=True)
class Transcription:
    text: str
    language: str = "ru"
    language_probability: float = 1.0


@dataclass(slots=True)
class SpeechAudio:
    pcm16: bytes
    sample_rate: int


@dataclass(slots=True)
class PipelineResult:
    transcription: Transcription
    answer: str
    speech: SpeechAudio
    processing_warning: str | None = None
