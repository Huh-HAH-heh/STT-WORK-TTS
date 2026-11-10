from __future__ import annotations

import logging
import queue
import threading
from concurrent.futures import Future
from dataclasses import dataclass
from typing import Protocol

from ..domain import AudioClip, PipelineResult, SpeechAudio, Transcription

logger = logging.getLogger(__name__)


class SpeechRecognizer(Protocol):
    def transcribe(self, clip: AudioClip) -> Transcription: ...


class WorkProcessor(Protocol):
    def process(self, text: str) -> tuple[str, str | None]: ...


class SpeechSynthesizer(Protocol):
    def synthesize(self, text: str) -> SpeechAudio: ...


@dataclass(slots=True)
class _Job:
    clip: AudioClip
    future: Future[PipelineResult]


class ProcessingStack:
    """Одна очередь и один worker: STT → локальные команды → TTS."""

    def __init__(
        self,
        recognizer: SpeechRecognizer,
        processor: WorkProcessor,
        synthesizer: SpeechSynthesizer,
    ) -> None:
        self._recognizer = recognizer
        self._processor = processor
        self._synthesizer = synthesizer
        self._queue: queue.Queue[_Job | None] = queue.Queue(maxsize=2)
        self._closed = False
        self._lock = threading.Lock()
        self._worker = threading.Thread(
            target=self._run,
            name="speech-stack",
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

                transcription = self._recognizer.transcribe(job.clip)
                answer, warning = self._processor.process(transcription.text)
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
