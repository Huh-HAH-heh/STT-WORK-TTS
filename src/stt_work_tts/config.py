from __future__ import annotations

import tomllib
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass(frozen=True, slots=True)
class AudioSettings:
    sample_rate: int = 16_000
    max_recording_seconds: int = 90
    save_recordings: bool = False
    recording_dir: Path = Path("data/recordings")


@dataclass(frozen=True, slots=True)
class STTSettings:
    model_dir: Path = Path("assets/models/vosk-model-small-ru-0.22")
    sample_rate: int = 16_000


@dataclass(frozen=True, slots=True)
class WorkSettings:
    notes_file: Path = Path("data/notes.txt")
    save_notes: bool = False


@dataclass(frozen=True, slots=True)
class TTSSettings:
    voice: str = "female"
    cpu_threads: int = 1
    female_model: Path = Path("assets/voices/ru_RU-irina-medium.onnx")
    male_model: Path = Path("assets/voices/ru_RU-denis-medium.onnx")
    play_audio: bool = True
    save_speech: bool = False
    speech_dir: Path = Path("data/speech")


@dataclass(frozen=True, slots=True)
class AppSettings:
    log_level: str = "INFO"


@dataclass(frozen=True, slots=True)
class AppConfig:
    app: AppSettings
    audio: AudioSettings
    stt: STTSettings
    work: WorkSettings
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
            f"Не найден {config_path}. Запустите STT Work TTS из папки с config.toml."
        )

    with config_path.open("rb") as stream:
        raw = tomllib.load(stream)

    app = _section(raw, "app")
    audio = _section(raw, "audio")
    stt = _section(raw, "stt")
    work = _section(raw, "work")
    tts = _section(raw, "tts")

    result = AppConfig(
        app=AppSettings(log_level=str(app.get("log_level", "INFO")).upper()),
        audio=AudioSettings(
            sample_rate=int(audio.get("sample_rate", 16_000)),
            max_recording_seconds=int(audio.get("max_recording_seconds", 90)),
            save_recordings=bool(audio.get("save_recordings", False)),
            recording_dir=Path(audio.get("recording_dir", "data/recordings")),
        ),
        stt=STTSettings(
            model_dir=Path(stt.get("model_dir", "assets/models/vosk-model-small-ru-0.22")),
            sample_rate=int(stt.get("sample_rate", 16_000)),
        ),
        work=WorkSettings(
            notes_file=Path(work.get("notes_file", "data/notes.txt")),
            save_notes=bool(work.get("save_notes", False)),
        ),
        tts=TTSSettings(
            voice=str(tts.get("voice", "female")).lower(),
            cpu_threads=max(1, min(4, int(tts.get("cpu_threads", 1)))),
            female_model=Path(tts.get(
                "female_model", "assets/voices/ru_RU-irina-medium.onnx"
            )),
            male_model=Path(tts.get(
                "male_model", "assets/voices/ru_RU-denis-medium.onnx"
            )),
            play_audio=bool(tts.get("play_audio", True)),
            save_speech=bool(tts.get("save_speech", False)),
            speech_dir=Path(tts.get("speech_dir", "data/speech")),
        ),
    )

    if result.audio.sample_rate != 16_000 or result.stt.sample_rate != 16_000:
        raise ValueError("Для встроенной лёгкой модели Vosk требуется 16 кГц.")
    if result.audio.max_recording_seconds <= 0:
        raise ValueError("audio.max_recording_seconds должен быть больше нуля.")
    if result.tts.voice not in {"female", "male"}:
        raise ValueError("tts.voice должен быть female или male.")

    return result
