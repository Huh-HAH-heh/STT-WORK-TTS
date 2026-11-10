from __future__ import annotations

import logging
import threading
from datetime import datetime
from math import gcd
from pathlib import Path

import numpy as np
import sounddevice as sd
import soundfile as sf
from scipy.signal import resample_poly

from ..config import AudioSettings
from ..domain import AudioClip

logger = logging.getLogger(__name__)


class MicrophoneRecorder:
    """Записывает mono float32 в RAM; WAV создаётся только по настройке."""

    def __init__(self, settings: AudioSettings) -> None:
        self._settings = settings
        self._lock = threading.Lock()
        self._chunks: list[np.ndarray] = []
        self._frames = 0
        self._capture_rate = settings.sample_rate
        self._max_frames = settings.sample_rate * settings.max_recording_seconds
        self._stream: sd.InputStream | None = None
        self._active = False
        self.limit_reached = False

    @property
    def is_recording(self) -> bool:
        with self._lock:
            return self._active

    def _resolve_capture_rate(self) -> int:
        requested = self._settings.sample_rate
        try:
            sd.check_input_settings(
                samplerate=requested,
                channels=self._settings.channels,
                dtype="float32",
            )
            return requested
        except (sd.PortAudioError, ValueError):
            device = sd.query_devices(kind="input")
            fallback = int(round(float(device["default_samplerate"])))
            sd.check_input_settings(
                samplerate=fallback,
                channels=self._settings.channels,
                dtype="float32",
            )
            logger.warning(
                "Микрофон не поддерживает %s Гц; запись пойдёт на %s Гц "
                "с последующим приведением к %s Гц.",
                requested,
                fallback,
                requested,
            )
            return fallback

    def start(self) -> None:
        with self._lock:
            if self._stream is not None:
                raise RuntimeError("Запись уже запущена.")

        capture_rate = self._resolve_capture_rate()
        with self._lock:
            self._chunks.clear()
            self._frames = 0
            self._capture_rate = capture_rate
            self._max_frames = capture_rate * self._settings.max_recording_seconds
            self._active = True
            self.limit_reached = False

        stream = sd.InputStream(
            samplerate=capture_rate,
            channels=self._settings.channels,
            dtype="float32",
            callback=self._on_audio,
        )
        try:
            stream.start()
            self._stream = stream
        except Exception:
            stream.close()
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
            capture_rate = self._capture_rate

        target_rate = self._settings.sample_rate
        if capture_rate != target_rate:
            divisor = gcd(capture_rate, target_rate)
            samples = resample_poly(
                samples,
                up=target_rate // divisor,
                down=capture_rate // divisor,
            ).astype(np.float32, copy=False)

        saved_path: Path | None = None
        if self._settings.save_recordings:
            self._settings.recording_dir.mkdir(parents=True, exist_ok=True)
            filename = datetime.now().strftime("recording_%Y%m%d_%H%M%S.wav")
            saved_path = self._settings.recording_dir / filename
            sf.write(saved_path, samples, target_rate)

        return AudioClip.create(
            samples=samples,
            sample_rate=target_rate,
            saved_path=saved_path,
        )
