from __future__ import annotations

import logging
from typing import Protocol

import httpx

from ..config import AssistantSettings

logger = logging.getLogger(__name__)


class TextProcessor(Protocol):
    def process(self, text: str) -> tuple[str, str | None]:
        """Вернуть ответ и необязательное предупреждение."""


class PassthroughProcessor:
    """Режим без LLM: передаёт распознанную фразу дальше без облачных вызовов."""

    def process(self, text: str) -> tuple[str, str | None]:
        return text, "Локальная LLM отключена: в TTS передан исходный текст."


class OllamaTextProcessor:
    """Обработка запроса локальной моделью через API Ollama."""

    def __init__(self, settings: AssistantSettings) -> None:
        self._settings = settings
        self._client = httpx.Client(timeout=settings.timeout_seconds)

    def process(self, text: str) -> tuple[str, str | None]:
        try:
            response = self._client.post(
                f"{self._settings.base_url}/api/chat",
                json={
                    "model": self._settings.model,
                    "stream": False,
                    "messages": [
                        {"role": "system", "content": self._settings.system_prompt},
                        {"role": "user", "content": text},
                    ],
                },
            )
            response.raise_for_status()
            payload = response.json()
            answer = str(payload.get("message", {}).get("content", "")).strip()
            if not answer:
                raise RuntimeError("Ollama вернул пустой ответ.")
            return answer, None
        except (httpx.HTTPError, ValueError, RuntimeError, KeyError) as exc:
            logger.warning("Локальная обработка недоступна: %s", exc)
            return text, (
                "Ollama недоступна или модель не загружена. "
                "В TTS передан исходный распознанный текст."
            )

    def close(self) -> None:
        self._client.close()


def create_text_processor(settings: AssistantSettings) -> TextProcessor:
    if not settings.enabled or settings.provider == "passthrough":
        return PassthroughProcessor()
    if settings.provider == "ollama":
        return OllamaTextProcessor(settings)
    raise ValueError(f"Неизвестный обработчик текста: {settings.provider}")
