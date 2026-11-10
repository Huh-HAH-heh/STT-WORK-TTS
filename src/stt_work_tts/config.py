from __future__ import annotations

import tomllib
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass(frozen=True, slots=True)
class AudioSettings:
    sample_rate: int = 16_000
    channels: int = 1
    max_recording_seconds: int = 180
    save_recordings: bool = False
    recording_dir: Path = Path("data/recordings")


@dataclass(frozen=True, slots=True)
class STTSettings:
    model: str = "large-v3-turbo"
    device: str = "auto"
    compute_type: str = "auto"
    language: str = "ru"
    beam_size: int = 5
    vad_filter: bool = True
    model_cache_dir: Path = Path("models/whisper")


@dataclass(frozen=True, slots=True)
class AssistantSettings:
    enabled: bool = True
    provider: str = "ollama"
    base_url: str = "http://127.0.0.1:11434"
    model: str = "qwen3:4b"
    timeout_seconds: int = 120
    system_prompt: str = (
        "Ты — локальный голосовой помощник. Отвечай по-русски, кратко и по существу. "
        "Давай полезный результат, не пересказывай запрос."
    )


@dataclass(frozen=True, slots=True)
class TTSSettings:
    model: str = "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign"
    device: str = "auto"
    mode: str = "voice_design"
    language: str = "Russian"
    voice_profile: str = "warm_female"
    voice_description: str = ""
    play_audio: bool = True
    save_speech: bool = False
    speech_dir: Path = Path("data/speech")


@dataclass(frozen=True, slots=True)
class AppSettings:
    language: str = "ru"
    log_level: str = "INFO"


@dataclass(frozen=True, slots=True)
class AppConfig:
    app: AppSettings
    audio: AudioSettings
    stt: STTSettings
    assistant: AssistantSettings
    tts: TTSSettings


def _section(data: dict[str, Any], key: str) -> dict[str, Any]:
    value = data.get(key, {})
    if not isinstance(value, dict):
        raise ValueError(f"Секция [{key}] в config.toml должна быть таблицей.")
    return value


def load_config(path: str | Path = "config.toml") -> AppConfig:
    config_path = Path(path)
    if not config_path.is_file():
        raise FileNotFoundError(
            f"Не найден {config_path}. Запустите приложение из корня проекта."
        )

    with config_path.open("rb") as stream:
        raw = tomllib.load(stream)

    app = _section(raw, "app")
    audio = _section(raw, "audio")
    stt = _section(raw, "stt")
    assistant = _section(raw, "assistant")
    tts = _section(raw, "tts")

    result = AppConfig(
        app=AppSettings(
            language=str(app.get("language", "ru")),
            log_level=str(app.get("log_level", "INFO")).upper(),
        ),
        audio=AudioSettings(
            sample_rate=int(audio.get("sample_rate", 16_000)),
            channels=int(audio.get("channels", 1)),
            max_recording_seconds=int(audio.get("max_recording_seconds", 180)),
            save_recordings=bool(audio.get("save_recordings", False)),
            recording_dir=Path(audio.get("recording_dir", "data/recordings")),
        ),
        stt=STTSettings(
            model=str(stt.get("model", "large-v3-turbo")),
            device=str(stt.get("device", "auto")),
            compute_type=str(stt.get("compute_type", "auto")),
            language=str(stt.get("language", "ru")),
            beam_size=int(stt.get("beam_size", 5)),
            vad_filter=bool(stt.get("vad_filter", True)),
            model_cache_dir=Path(stt.get("model_cache_dir", "models/whisper")),
        ),
        assistant=AssistantSettings(
            enabled=bool(assistant.get("enabled", True)),
            provider=str(assistant.get("provider", "ollama")).lower(),
            base_url=str(assistant.get("base_url", "http://127.0.0.1:11434")).rstrip("/"),
            model=str(assistant.get("model", "qwen3:4b")),
            timeout_seconds=int(assistant.get("timeout_seconds", 120)),
            system_prompt=str(assistant.get("system_prompt", "")),
        ),
        tts=TTSSettings(
            model=str(tts.get("model", "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign")),
            device=str(tts.get("device", "auto")),
            mode=str(tts.get("mode", "voice_design")).lower(),
            language=str(tts.get("language", "Russian")),
            voice_profile=str(tts.get("voice_profile", "warm_female")).lower(),
            voice_description=str(tts.get("voice_description", "")),
            play_audio=bool(tts.get("play_audio", True)),
            save_speech=bool(tts.get("save_speech", False)),
            speech_dir=Path(tts.get("speech_dir", "data/speech")),
        ),
    )

    if result.audio.sample_rate <= 0 or result.audio.channels != 1:
        raise ValueError("Для первой версии требуется положительная частота и один канал.")
    if result.audio.max_recording_seconds <= 0:
        raise ValueError("audio.max_recording_seconds должен быть больше нуля.")
    if result.tts.mode not in {"voice_design", "custom_voice"}:
        raise ValueError("tts.mode должен быть voice_design или custom_voice.")
    if result.assistant.provider not in {"ollama", "passthrough"}:
        raise ValueError("assistant.provider должен быть ollama или passthrough.")

    return result
