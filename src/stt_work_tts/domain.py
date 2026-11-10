from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from numpy.typing import NDArray

FloatAudio = NDArray[np.float32]


@dataclass(slots=True)
class AudioClip:
    """Одноканальная запись с микрофона, находящаяся в оперативной памяти."""

    samples: FloatAudio
    sample_rate: int
    captured_at: datetime
    saved_path: Path | None = None

    @property
    def duration_seconds(self) -> float:
        return len(self.samples) / self.sample_rate

    @classmethod
    def create(
        cls,
        samples: NDArray[np.floating],
        sample_rate: int,
        saved_path: Path | None = None,
    ) -> AudioClip:
        data = np.asarray(samples, dtype=np.float32).reshape(-1)
        if data.size == 0:
            raise ValueError("Запись пустая.")
        if not np.isfinite(data).all():
            raise ValueError("В записи обнаружены некорректные значения.")
        return cls(
            samples=np.ascontiguousarray(data),
            sample_rate=sample_rate,
            captured_at=datetime.now(timezone.utc),
            saved_path=saved_path,
        )


@dataclass(frozen=True, slots=True)
class Transcription:
    text: str
    language: str
    language_probability: float


@dataclass(slots=True)
class SpeechAudio:
    samples: FloatAudio
    sample_rate: int


@dataclass(slots=True)
class PipelineResult:
    transcription: Transcription
    answer: str
    speech: SpeechAudio
    processing_warning: str | None = None
