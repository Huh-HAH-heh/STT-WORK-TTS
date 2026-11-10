# Third-party notices

This project packages third-party offline speech engines and model files.

## Vosk

- Runtime: Vosk API, Apache License 2.0.
- Russian model: vosk-model-small-ru-0.22.
- Source: https://alphacephei.com/vosk/models
- Preserve the license and provenance notices supplied with the upstream model archive.

## Piper

- Runtime: Piper TTS 1.8.0, GPL-3.0-or-later.
- Source: https://github.com/OHF-Voice/piper1-gpl
- A portable distribution must include the corresponding license and comply with the Piper runtime license.

## Russian Piper voices

The build script downloads original voice files and model cards from the versioned rhasspy/piper-voices v1.0.0 repository.

- ru_RU-denis-medium: model card identifies the OHF-Voice voice dataset as CC0.
- ru_RU-irina-medium: model card reports the RHVoice dataset license as "Unknown".

The female Irina model is included for local evaluation because it provides the requested female voice option. Its dataset rights are not established by the upstream model card. Do not redistribute that model or use it commercially until its rights have been independently confirmed. The build archive is intended for personal/local evaluation.

## Python dependencies

The portable archive bundles Python runtime dependencies. Their original license files should be retained by PyInstaller in the distribution folder. Review the licenses of all bundled packages before redistributing a modified application.
