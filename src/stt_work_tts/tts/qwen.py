from __future__ import annotations

import logging

import numpy as np

from ..config import TTSSettings
from ..domain import SpeechAudio

logger = logging.getLogger(__name__)

VOICE_PROFILES = {
    "warm_female": (
        "Говори голосом взрослой женщины. Тембр тёплый, мягкий, бархатистый, "
        "средней высоты, чистый и естественный. Русская дикция отчётливая, "
        "без заметного акцента. Подача спокойная и уверенная, с лёгкой "
        "доброжелательной улыбкой в голосе. Естественный разговорный темп, "
        "плавная интонация и уместные паузы. Голос должен звучать живо, "
        "интеллигентно и приятно, без приторности, писклявости и рекламной наигранности."
    ),
    "warm_male": (
        "Говори голосом взрослого мужчины. Тембр тёплый, насыщенный, "
        "мягкий, с приятной бархатистой серединой и спокойным низким оттенком, "
        "но без искусственного баса. Русская дикция отчётливая и естественная. "
        "Подача доброжелательная, уверенная и располагающая к общению. "
        "Умеренный разговорный темп, плавная интонация и уместные паузы. "
        "Голос должен звучать интеллигентно и живо, без дикторской холодности."
    ),
}

CUSTOM_VOICE_SPEAKERS = {
    "warm_female": "Serena",
    "warm_male": "Uncle_Fu",
}


class Qwen3Synthesizer:
    """Локальный Qwen3-TTS; модель загружается один раз при первом ответе."""

    def __init__(self, settings: TTSSettings) -> None:
        self._settings = settings
        self._model = None

    def _load_model(self) -> None:
        if self._model is not None:
            return

        try:
            import torch
            from qwen_tts import Qwen3TTSModel
        except ImportError as exc:
            raise RuntimeError(
                "Не установлен Qwen3-TTS. Выполните: pip install -e ".[tts]""
            ) from exc

        configured_device = self._settings.device.lower()
        if configured_device == "auto":
            device_map = "cuda:0" if torch.cuda.is_available() else "cpu"
        elif configured_device == "cuda":
            device_map = "cuda:0"
        else:
            device_map = configured_device

        if str(device_map).startswith("cuda"):
            if not torch.cuda.is_available():
                raise RuntimeError(
                    "В config.toml выбран CUDA, но PyTorch не видит CUDA. "
                    "Установите CUDA-сборку PyTorch или задайте tts.device = "cpu"."
                )
            dtype = (
                torch.bfloat16
                if torch.cuda.is_bf16_supported()
                else torch.float16
            )
        else:
            dtype = torch.float32

        logger.info(
            "Загрузка TTS-модели %s на %s. Первый запуск скачает веса модели.",
            self._settings.model,
            device_map,
        )
        self._model = Qwen3TTSModel.from_pretrained(
            self._settings.model,
            device_map=device_map,
            dtype=dtype,
        )
        logger.info("TTS-модель готова.")

    def synthesize(self, text: str) -> SpeechAudio:
        if not text.strip():
            raise ValueError("Нельзя озвучить пустой текст.")

        self._load_model()
        assert self._model is not None

        if self._settings.mode == "voice_design":
            description = (
                self._settings.voice_description.strip()
                or VOICE_PROFILES.get(
                    self._settings.voice_profile,
                    VOICE_PROFILES["warm_female"],
                )
            )
            waveforms, sample_rate = self._model.generate_voice_design(
                text=text,
                language=self._settings.language,
                instruct=description,
            )
        else:
            speaker = CUSTOM_VOICE_SPEAKERS.get(
                self._settings.voice_profile,
                "Serena",
            )
            waveforms, sample_rate = self._model.generate_custom_voice(
                text=text,
                language=self._settings.language,
                speaker=speaker,
            )

        samples = np.asarray(waveforms[0], dtype=np.float32).reshape(-1)
        if samples.size == 0:
            raise RuntimeError("Qwen3-TTS вернул пустую аудиозапись.")
        return SpeechAudio(samples=samples, sample_rate=int(sample_rate))
