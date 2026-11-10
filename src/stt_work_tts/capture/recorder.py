from __future__ import annotations

import logging
import threading
from datetime import datetime
from pathlib import Path

import numpy as np
import sounddevice as sd
import soundfile as sf

from ..config import AudioSettings
from ..domain import AudioClip

logger = logging.getLogger(__name__)


class MicrophoneRecorder:
    """Записывает mono float32 прямо в RAM и сохраняет WAV только по настройке."""

    def __init__(self, settings: AudioSettings) -> None:
        self._settings = settings
        self._lock = threading.Lock()
        self._chunks: list[np.ndarray] = []
        self._frames = 0
        self._max_frames = settings.sample_rate * settings.max_recording_seconds
        self._stream: sd.InputStream | None = None
        self._active = False
        self.limit_reached = False

    @property
    def is_recording(self) -> bool:
        with self._lock:
            return self._active

    def start(self) -> None:
        with self._lock:
            if self._stream is not None:
                raise RuntimeError("Запись уже запущена.")
            self._chunks.clear()
            self._frames = 0
            self._active = True
            self.limit_reached = False

        try:
            stream = sd.InputStream(
                samplerate=self._settings.sample_rate,
                channels=self._settings.channels,
                dtype="float32",
                callback=self._on_audio,
            )
            stream.start()
            self._stream = stream
        except Exception:
            with self._lock:
                self._active = False
            raise

    def _on_audio(
        self,
        indata: np.ndarray,
        frames: int,
        time_info: object,
        status: sd.CallbackFlags,
    ) -> None:
        del time_info
        if status:
            logger.warning("Состояние аудиопотока: %s", status)

        with self._lock:
            if not self._active:
                raise sd.CallbackStop

            remaining = self._max_frames - self._frames
            accepted = min(frames, remaining)
            if accepted > 0:
                self._chunks.append(indata[:accepted, 0].copy())
                self._frames += accepted

            if self._frames >= self._max_frames:
                self.limit_reached = True
                self._active = False
                raise sd.CallbackStop

    def stop(self) -> AudioClip:
        stream = self._stream
        if stream is None:
            raise RuntimeError("Сначала запустите запись.")

        self._stream = None
        try:
            stream.stop()
        finally:
            stream.close()

        with self._lock:
            self._active = False
            if not self._chunks:
                raise ValueError("Микрофон не передал аудиоданные.")
            samples = np.concatenate(self._chunks).astype(np.float32, copy=False)
            self._chunks.clear()
            self._frames = 0

        saved_path: Path | None = None
        if self._settings.save_recordings:
            self._settings.recording_dir.mkdir(parents=True, exist_ok=True)
            filename = datetime.now().strftime("recording_%Y%m%d_%H%M%S.wav")
            saved_path = self._settings.recording_dir / filename
            sf.write(saved_path, samples, self._settings.sample_rate)

        return AudioClip.create(
            samples=samples,
            sample_rate=self._settings.sample_rate,
            saved_path=saved_path,
        )
