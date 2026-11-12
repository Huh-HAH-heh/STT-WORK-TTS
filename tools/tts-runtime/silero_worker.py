from __future__ import annotations

import contextlib
import io
import json
import os
import shutil
import sys
import tempfile
import traceback
import urllib.request
import zipfile
from pathlib import Path, PurePosixPath

PROTOCOL_OUT = sys.stdout
SILERO_MODEL_ID = "v5_6_ru"
SILERO_REPO_ARCHIVE = (
    "https://codeload.github.com/snakers4/silero-models/zip/refs/heads/master"
)
SILERO_REPO_CACHE_NAME = "snakers4_silero-models_master"


def emit(payload: dict) -> None:
    PROTOCOL_OUT.write(json.dumps(payload, ensure_ascii=False) + "\n")
    PROTOCOL_OUT.flush()


def _is_valid_silero_repo(repo_dir: Path) -> bool:
    hubconf = repo_dir / "hubconf.py"
    models_yml = repo_dir / "models.yml"
    if not hubconf.is_file() or not models_yml.is_file():
        return False

    try:
        return SILERO_MODEL_ID in models_yml.read_text(encoding="utf-8")
    except OSError:
        return False


def _extract_silero_archive(archive_bytes: bytes, destination: Path) -> None:
    expected_root = "silero-models-master"

    with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
        extracted_anything = False

        for entry in archive.infolist():
            archive_path = PurePosixPath(entry.filename)
            if not archive_path.parts or archive_path.parts[0] != expected_root:
                continue

            relative_parts = archive_path.parts[1:]
            if not relative_parts:
                continue
            if any(part in {"", ".", ".."} for part in relative_parts):
                raise ValueError("Silero archive contains an unsafe path.")

            target = destination.joinpath(*relative_parts)
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
                continue

            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, target.open("wb") as output:
                shutil.copyfileobj(source, output)
            extracted_anything = True

    if not extracted_anything or not _is_valid_silero_repo(destination):
        raise RuntimeError(
            f"Downloaded Silero repository does not contain {SILERO_MODEL_ID}."
        )


def ensure_silero_repo(torch) -> Path:
    hub_cache = Path(torch.hub.get_dir()).expanduser()
    hub_cache.mkdir(parents=True, exist_ok=True)
    repo_dir = hub_cache / SILERO_REPO_CACHE_NAME

    if _is_valid_silero_repo(repo_dir):
        return repo_dir

    emit({
        "status": (
            "Локальный кэш Silero устарел. Загружаю официальный архив "
            "напрямую, без GitHub API..."
        )
    })

    request = urllib.request.Request(
        SILERO_REPO_ARCHIVE,
        headers={"User-Agent": "STT-WORK-TTS"},
    )
    with urllib.request.urlopen(request, timeout=90) as response:
        archive_bytes = response.read()

    with tempfile.TemporaryDirectory(
        prefix=".silero-repo-",
        dir=str(hub_cache),
    ) as temporary_directory:
        temporary_root = Path(temporary_directory)
        staged_repo = temporary_root / "repo"
        staged_repo.mkdir()
        _extract_silero_archive(archive_bytes, staged_repo)

        backup_repo = hub_cache / f"{SILERO_REPO_CACHE_NAME}.previous"
        if backup_repo.exists():
            shutil.rmtree(backup_repo)

        if repo_dir.exists():
            repo_dir.rename(backup_repo)

        try:
            staged_repo.rename(repo_dir)
        except Exception:
            if backup_repo.exists() and not repo_dir.exists():
                backup_repo.rename(repo_dir)
            raise
        else:
            if backup_repo.exists():
                shutil.rmtree(backup_repo)

    return repo_dir


def main() -> int:
    # Report progress before importing PyTorch; imports and model downloads can take time.
    emit({"status": "Запускаю Silero worker и загружаю зависимости PyTorch..."})
    sys.stdout = sys.stderr
    try:
        import soundfile as sf
        import torch

        torch.set_num_threads(max(1, min(6, os.cpu_count() or 2)))
        sys.stdout = PROTOCOL_OUT
        emit({"status": "Проверяю локальный Silero Hub и модель v5_6_ru..."})
        sys.stdout = sys.stderr

        repo_dir = ensure_silero_repo(torch)
        sys.stdout = PROTOCOL_OUT
        emit({"status": "Загружаю Silero v5_6_ru и голосовые веса..."})
        sys.stdout = sys.stderr

        model, _ = torch.hub.load(
            repo_or_dir=str(repo_dir),
            source="local",
            model="silero_tts",
            language="ru",
            speaker=SILERO_MODEL_ID,
        )
        model.to(torch.device("cpu"))
    except Exception as exc:
        sys.stdout = PROTOCOL_OUT
        emit({
            "ready": False,
            "error": f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}",
        })
        return 1

    sys.stdout = PROTOCOL_OUT
    emit({"ready": True, "engine": "silero"})

    for line in sys.stdin:
        try:
            request = json.loads(line)
            if request.get("shutdown"):
                emit({"ok": True, "shutdown": True})
                break

            text = str(request.get("text", "")).strip()
            output_value = request.get("output_path")
            voice = str(request.get("voice", "xenia")).lower()
            if not text:
                raise ValueError("Text cannot be empty.")
            if voice not in {"aidar", "baya", "kseniya", "xenia", "eugene"}:
                raise ValueError(f"Unsupported Silero voice: {voice}")
            if not output_value:
                raise ValueError("Output path is required.")
            output_path = Path(str(output_value))

            with contextlib.redirect_stdout(sys.stderr):
                audio = model.apply_tts(text=text, speaker=voice, sample_rate=48000)
                sf.write(str(output_path), audio.detach().cpu().numpy(), 48000)

            emit({"ok": True, "output_path": str(output_path)})
        except Exception as exc:
            emit({"ok": False, "error": f"{type(exc).__name__}: {exc}"})

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
