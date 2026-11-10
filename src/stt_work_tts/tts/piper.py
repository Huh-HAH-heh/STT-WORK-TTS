from __future__ import annotations

import logging
from pathlib import Path

from ..config import TTSSettings
from ..domain import SpeechAudio

logger = logging.getLogger(__name__)


class PiperSynthesizer:
    """Локальная нейросетевая озвучка ONNX-моделью на CPU."""

    def __init__(self, settings: TTSSettings) -> None:
        self._settings = settings
        self._voice = None
        self._loaded_path: Path | None = None

    def _model_path(self) -> Path:
        return (
            self._settings.female_model
            if self._settings.voice == "female"
            else self._settings.male_model
        )

    def _load_voice(self) -> None:
        model_path = self._model_path()
        if self._voice is not None and self._loaded_path == model_path:
            return

        config_path = Path(f"{model_path}.json")
        if not model_path.is_file() or not config_path.is_file():
            raise FileNotFoundError(
                f"Не найдена голосовая модель Piper: {model_path}. "
                "В готовом архиве модели уже должны находиться в assets/voices."
            )

        try:
            from piper import PiperVoice
        except ImportError as exc:
            raise RuntimeError(
                "Не найден Piper TTS. Запустите приложение из portable-архива."
            ) from exc

        logger.info("Загрузка голоса Piper: %s", self._settings.voice)
        self._voice = PiperVoice.load(str(model_path), use_cuda=False)
        self._loaded_path = model_path
        logger.info("Голос Piper готов.")

    def synthesize(self, text: str) -> SpeechAudio:
        normalized = text.strip()
        if not normalized:
            raise ValueError("Нельзя озвучить пустой текст.")

        self._load_voice()
        assert self._voice is not None

        audio = bytearray()
        sample_rate: int | None = None
        for chunk in self._voice.synthesize(normalized):
            if sample_rate is None:
                sample_rate = int(chunk.sample_rate)
            elif sample_rate != int(chunk.sample_rate):
                raise RuntimeError("Piper вернул аудио с разными частотами.")
            audio.extend(chunk.audio_int16_bytes)

        if not audio or sample_rate is None:
            raise RuntimeError("Piper не вернул аудиоданные.")

        return SpeechAudio(pcm16=bytes(audio), sample_rate=sample_rate)
