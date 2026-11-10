from __future__ import annotations

import logging

from .audio_output import play_speech, save_speech
from .capture.recorder import MicrophoneRecorder
from .config import AppConfig, load_config
from .processing.stack import ProcessingStack
from .processing.text_processor import OllamaTextProcessor, create_text_processor
from .stt.whisper import FasterWhisperRecognizer
from .tts.qwen import Qwen3Synthesizer

logger = logging.getLogger("stt_work_tts")


def _configure_logging(level: str) -> None:
    numeric_level = getattr(logging, level, logging.INFO)
    logging.basicConfig(
        level=numeric_level,
        format="%(asctime)s | %(levelname)s | %(message)s",
        datefmt="%H:%M:%S",
    )


def _build_stack(config: AppConfig) -> tuple[ProcessingStack, object]:
    processor = create_text_processor(config.assistant)
    stack = ProcessingStack(
        recognizer=FasterWhisperRecognizer(config.stt),
        processor=processor,
        synthesizer=Qwen3Synthesizer(config.tts),
    )
    return stack, processor


def main() -> None:
    config = load_config()
    _configure_logging(config.app.log_level)

    recorder = MicrophoneRecorder(config.audio)
    stack, processor = _build_stack(config)

    print()
    print("STT Work TTS — локальный голосовой помощник")
    print("Enter — начать запись; Enter ещё раз — остановить и обработать.")
    print("q — выход.")
    print()
    print(
        "Запись хранится в оперативной памяти. "
        + (
            "Копия WAV будет сохраняться на диск."
            if config.audio.save_recordings
            else "Сохранение исходной записи на диск выключено."
        )
    )
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
                if recorder.is_recording:
                    try:
                        clip = recorder.stop()
                    except Exception:
                        print("\nЗапись отменена.")
                        continue
                else:
                    print("\nЗапись остановлена.")
                    continue
            except Exception as exc:
                print(f"Не удалось записать звук: {exc}")
                logger.exception("Ошибка захвата аудио.")
                continue

            if recorder.limit_reached:
                print("Достигнут максимальный лимит записи.")
            if clip.saved_path:
                print(f"Исходная запись сохранена: {clip.saved_path}")
            print(f"Записано: {clip.duration_seconds:.1f} сек. Передаю в очередь...")

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
                print(f"Голосовой ответ сохранён: {output_path}")

            if config.tts.play_audio:
                print("Воспроизведение ответа...")
                try:
                    play_speech(result.speech)
                except Exception as exc:
                    print(f"Не удалось воспроизвести звук: {exc}")

            print()
    except KeyboardInterrupt:
        print("\nЗавершение работы...")
    finally:
        stack.close()
        if isinstance(processor, OllamaTextProcessor):
            processor.close()

    print("Работа завершена.")


if __name__ == "__main__":
    main()
