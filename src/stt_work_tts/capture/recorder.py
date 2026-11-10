from __future__ import annotations

import audioop
import logging
import threading
import wave
from datetime import datetime
from pathlib import Path

import sounddevice as sd

from ..config import AudioSettings
from ..domain import AudioClip

logger = logging.getLogger(__name__)


class MicrophoneRecorder:
    """Минимальный захват PCM16 mono. На диск пишет только по явной настройке."""

    def __init__(self, settings: AudioSettings) -> None:
        self._settings = settings
        self._lock = threading.Lock()
        self._chunks: list[bytes] = []
        self._byte_count = 0
        self._max_bytes = settings.sample_rate * settings.max_recording_seconds * 2
        self._capture_rate = settings.sample_rate
        self._stream: sd.RawInputStream | None = None
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
                channels=1,
                dtype="int16",
            )
            return requested
        except (sd.PortAudioError, ValueError):
            device = sd.query_devices(kind="input")
            fallback = int(round(float(device["default_samplerate"])))
            sd.check_input_settings(
                samplerate=fallback,
                channels=1,
                dtype="int16",
            )
            logger.info(
                "Микрофон использует %s Гц; аудио будет приведено к %s Гц.",
                fallback,
                requested,
            )
            return fallback

    def start(self) -> None:
        if self._stream is not None:
            raise RuntimeError("Запись уже запущена.")

        capture_rate = self._resolve_capture_rate()
        with self._lock:
            self._chunks.clear()
            self._byte_count = 0
            self._capture_rate = capture_rate
            self._max_bytes = capture_rate * self._settings.max_recording_seconds * 2
            self._active = True
            self.limit_reached = False

        stream = sd.RawInputStream(
            samplerate=capture_rate,
            channels=1,
            dtype="int16",
            blocksize=4096,
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
        indata: object,
        frames: int,
        time_info: object,
        status: sd.CallbackFlags,
    ) -> None:
        del time_info
        if status:
            logger.warning("Состояние аудиопотока: %s", status)

        raw = bytes(indata)
        with self._lock:
            if not self._active:
                raise sd.CallbackStop

            remaining = self._max_bytes - self._byte_count
            accepted = min(len(raw), remaining)
            if accepted > 0:
                self._chunks.append(raw[:accepted])
                self._byte_count += accepted

            if self._byte_count >= self._max_bytes:
                self.limit_reached = True
                self._active = False
                raise sd.CallbackStop

    def stop(self) -> AudioClip:
        stream = self._stream
        if stream is None:
            raise RuntimeError("Сначала запустите запись.")

        self._stream = None
        try:
            if stream.active:
                stream.stop()
        finally:
            stream.close()

        with self._lock:
            self._active = False
            if not self._chunks:
                raise ValueError("Микрофон не передал аудиоданные.")
            pcm16 = b"".join(self._chunks)
            self._chunks.clear()
            self._byte_count = 0
            capture_rate = self._capture_rate

        target_rate = self._settings.sample_rate
        if capture_rate != target_rate:
            try:
                pcm16, _ = audioop.ratecv(
                    pcm16, 2, 1, capture_rate, target_rate, None
                )
            except Exception as exc:
                raise RuntimeError(f"Не удалось изменить частоту аудио: {exc}") from exc

        saved_path: Path | None = None
        if self._settings.save_recordings:
            self._settings.recording_dir.mkdir(parents=True, exist_ok=True)
            filename = datetime.now().strftime("recording_%Y%m%d_%H%M%S.wav")
            saved_path = self._settings.recording_dir / filename
            with wave.open(str(saved_path), "wb") as wav_file:
                wav_file.setnchannels(1)
                wav_file.setsampwidth(2)
                wav_file.setframerate(target_rate)
                wav_file.writeframes(pcm16)

        return AudioClip.create(
            pcm16=pcm16,
            sample_rate=target_rate,
            saved_path=saved_path,
        )
