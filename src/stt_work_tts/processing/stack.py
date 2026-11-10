from __future__ import annotations

import logging
import queue
import threading
from concurrent.futures import Future
from dataclasses import dataclass
from typing import Protocol

from ..domain import AudioClip, PipelineResult, SpeechAudio, Transcription
from .text_processor import TextProcessor

logger = logging.getLogger(__name__)


class SpeechRecognizer(Protocol):
    def transcribe(self, clip: AudioClip) -> Transcription: ...


class SpeechSynthesizer(Protocol):
    def synthesize(self, text: str) -> SpeechAudio: ...


@dataclass(slots=True)
class _Job:
    clip: AudioClip
    future: Future[PipelineResult]


class ProcessingStack:
    """Последовательная очередь: аудио → STT → обработка текста → TTS."""

    def __init__(
        self,
        recognizer: SpeechRecognizer,
        processor: TextProcessor,
        synthesizer: SpeechSynthesizer,
    ) -> None:
        self._recognizer = recognizer
        self._processor = processor
        self._synthesizer = synthesizer
        self._queue: queue.Queue[_Job | None] = queue.Queue()
        self._closed = False
        self._lock = threading.Lock()
        self._worker = threading.Thread(
            target=self._run,
            name="speech-processing-stack",
            daemon=True,
        )
        self._worker.start()

    def submit(self, clip: AudioClip) -> Future[PipelineResult]:
        future: Future[PipelineResult] = Future()
        with self._lock:
            if self._closed:
                raise RuntimeError("Очередь обработки уже закрыта.")
            self._queue.put(_Job(clip=clip, future=future))
        return future

    def _run(self) -> None:
        while True:
            job = self._queue.get()
            try:
                if job is None:
                    return
                if not job.future.set_running_or_notify_cancel():
                    continue

                logger.info("Этап 1/3: распознавание речи.")
                transcription = self._recognizer.transcribe(job.clip)

                warning: str | None = None
                if transcription.text:
                    logger.info("Этап 2/3: обработка распознанного текста.")
                    answer, warning = self._processor.process(transcription.text)
                else:
                    answer = "Я не разобрал речь. Попробуйте записать фразу ещё раз."

                logger.info("Этап 3/3: синтез голосового ответа.")
                speech = self._synthesizer.synthesize(answer)
                job.future.set_result(
                    PipelineResult(
                        transcription=transcription,
                        answer=answer,
                        speech=speech,
                        processing_warning=warning,
                    )
                )
            except Exception as exc:
                if job is not None and not job.future.done():
                    job.future.set_exception(exc)
                logger.exception("Ошибка в очереди обработки.")
            finally:
                self._queue.task_done()

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
            self._queue.put(None)
        self._worker.join()
