from __future__ import annotations

import logging
import os
import sys
from pathlib import Path

from .audio_output import play_speech, save_speech
from .capture.recorder import MicrophoneRecorder
from .config import AppConfig, load_config
from .processing.local_processor import OfflineWorkProcessor
from .processing.stack import ProcessingStack
from .stt.vosk import VoskRecognizer
from .tts.piper import PiperSynthesizer

logger = logging.getLogger("stt_work_tts")


def _application_root() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[2]


def _configure_logging(level: str) -> None:
    numeric_level = getattr(logging, level, logging.INFO)
    logging.basicConfig(
        level=numeric_level,
        format="%(asctime)s | %(levelname)s | %(message)s",
        datefmt="%H:%M:%S",
    )


def _build_stack(config: AppConfig) -> ProcessingStack:
    processor = OfflineWorkProcessor(
        save_notes=config.work.save_notes,
        notes_file=str(config.work.notes_file),
    )
    return ProcessingStack(
        recognizer=VoskRecognizer(config.stt),
        processor=processor,
        synthesizer=PiperSynthesizer(config.tts),
    )


def main() -> None:
    os.chdir(_application_root())
    config = load_config()
    _configure_logging(config.app.log_level)

    recorder = MicrophoneRecorder(config.audio)
    stack = _build_stack(config)

    print()
    print("STT Work TTS — автономный голосовой помощник")
    print("Enter — начать запись; Enter ещё раз — остановить.")
    print("q — выход; команда «помощь» — список встроенных действий.")
    print()
    print(
        "Аудио хранится только в оперативной памяти. "
        + (
            "Сохранение WAV включено."
            if config.audio.save_recordings
            else "Сохранение исходной записи на диск выключено."
        )
    )
    print(f"Голос: {'женский' if config.tts.voice == 'female' else 'мужской'}.")
    print("Интернет, облако, Ollama и GPU не требуются.")
    print()

    try:
        while True:
            try:
                command = input("[Enter] Записать  |  [q] Выход: ").strip().lower()
            except EOFError:
                break

            if command in {"q", "quit", "exit", "й"}:
                break

            try:
                recorder.start()
                print("● Запись идёт. Говорите... (Enter — остановить)")
                input()
                clip = recorder.stop()
            except KeyboardInterrupt:
                try:
                    clip = recorder.stop()
                except Exception:
                    print("\nЗапись отменена.")
                    continue
            except Exception as exc:
                print(f"Не удалось записать звук: {exc}")
                logger.exception("Ошибка захвата аудио.")
                continue

            if recorder.limit_reached:
                print("Достигнут лимит записи.")
            if clip.saved_path:
                print(f"Исходная запись сохранена: {clip.saved_path}")
            print(f"Записано {clip.duration_seconds:.1f} сек. Обрабатываю...")

            try:
                result = stack.submit(clip).result()
            except Exception as exc:
                print(f"Ошибка обработки: {exc}")
                logger.exception("Ошибка конвейера.")
                continue

            print()
            print(f"Распознано: {result.transcription.text or '[речь не распознана]'}")
            print(f"Ответ: {result.answer}")
            if result.processing_warning:
                print(f"Примечание: {result.processing_warning}")

            if config.tts.save_speech:
                output_path = save_speech(result.speech, config.tts)
                print(f"Речь сохранена: {output_path}")

            if config.tts.play_audio:
                try:
                    play_speech(result.speech)
                except Exception as exc:
                    print(f"Не удалось воспроизвести ответ: {exc}")
                    logger.exception("Ошибка вывода звука.")
            print()
    except KeyboardInterrupt:
        print("\nЗавершение работы...")
    finally:
        stack.close()

    print("Работа завершена.")


if __name__ == "__main__":
    main()
