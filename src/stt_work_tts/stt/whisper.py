from __future__ import annotations

import logging
from pathlib import Path

from ..config import STTSettings
from ..domain import AudioClip, Transcription

logger = logging.getLogger(__name__)


class FasterWhisperRecognizer:
    """Локальное распознавание аудио-массива без создания временного файла."""

    def __init__(self, settings: STTSettings) -> None:
        self._settings = settings
        self._model = None

    def _load_model(self) -> None:
        if self._model is not None:
            return

        try:
            from faster_whisper import WhisperModel
        except ImportError as exc:
            raise RuntimeError(
                "Не установлен faster-whisper. Выполните: pip install -e ."
            ) from exc

        cache_dir: Path = self._settings.model_cache_dir
        cache_dir.mkdir(parents=True, exist_ok=True)

        logger.info(
            "Загрузка STT-модели %s (device=%s, compute_type=%s)...",
            self._settings.model,
            self._settings.device,
            self._settings.compute_type,
        )
        self._model = WhisperModel(
            self._settings.model,
            device=self._settings.device,
            compute_type=self._settings.compute_type,
            download_root=str(cache_dir),
        )
        logger.info("STT-модель готова.")

    def transcribe(self, clip: AudioClip) -> Transcription:
        self._load_model()
        assert self._model is not None

        segments, info = self._model.transcribe(
            clip.samples,
            language=self._settings.language,
            beam_size=self._settings.beam_size,
            vad_filter=self._settings.vad_filter,
            condition_on_previous_text=False,
        )

        # transcribe() возвращает генератор: чтение сегментов запускает фактическое распознавание.
        text = " ".join(segment.text.strip() for segment in segments).strip()
        return Transcription(
            text=text,
            language=info.language or self._settings.language,
            language_probability=float(info.language_probability or 0.0),
        )
