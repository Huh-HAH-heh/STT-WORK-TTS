from __future__ import annotations

import json
import logging
from pathlib import Path

from ..config import STTSettings
from ..domain import AudioClip, Transcription

logger = logging.getLogger(__name__)


class VoskRecognizer:
    """Распознавание русской речи на CPU из PCM16, без файлов и сетевых запросов."""

    def __init__(self, settings: STTSettings) -> None:
        self._settings = settings
        self._model = None

    def _load_model(self) -> None:
        if self._model is not None:
            return

        model_dir: Path = self._settings.model_dir
        if not model_dir.is_dir():
            raise FileNotFoundError(
                f"Не найдена STT-модель: {model_dir}. "
                "Используйте готовый portable-архив со встроенной моделью."
            )

        try:
            from vosk import Model, SetLogLevel
        except ImportError as exc:
            raise RuntimeError(
                "Не найден Vosk. Запускайте готовый portable-архив."
            ) from exc

        SetLogLevel(-1)
        logger.info("Загрузка компактной русской модели Vosk...")
        self._model = Model(str(model_dir))
        logger.info("Модель Vosk готова.")

    def transcribe(self, clip: AudioClip) -> Transcription:
        self._load_model()
        assert self._model is not None

        from vosk import KaldiRecognizer

        recognizer = KaldiRecognizer(self._model, clip.sample_rate)
        recognizer.SetWords(False)

        parts: list[str] = []
        chunk_size = 8_000
        for offset in range(0, len(clip.pcm16), chunk_size):
            chunk = clip.pcm16[offset : offset + chunk_size]
            if recognizer.AcceptWaveform(chunk):
                value = json.loads(recognizer.Result())
                text = str(value.get("text", "")).strip()
                if text:
                    parts.append(text)

        final_value = json.loads(recognizer.FinalResult())
        final_text = str(final_value.get("text", "")).strip()
        if final_text:
            parts.append(final_text)

        return Transcription(text=" ".join(parts).strip())
