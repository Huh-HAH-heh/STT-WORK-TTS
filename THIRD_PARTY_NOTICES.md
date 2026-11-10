# Third-party notices

## Vosk

- Runtime: Vosk API, Apache License 2.0.
- Russian model: vosk-model-small-ru-0.22.
- Source: https://alphacephei.com/vosk/models
- Preserve upstream provenance and license notices.

## Piper

- Runtime: Piper TTS 1.8.0, GPL-3.0-or-later.
- Source: https://github.com/OHF-Voice/piper1-gpl
- A distributed portable archive must comply with the Piper runtime license and include applicable license/source information. Review the project's own licensing before redistributing a combined application.

## Included Russian voice

- ru_RU-denis-medium.
- Its upstream model card identifies the OHF-Voice voice-datasets training data as CC0.
- Voice models and base checkpoints have their own terms; review the upstream model card and provenance before commercial redistribution.

## Optional female voice

The code supports a user-supplied Russian female Piper ONNX model through tts.female_model. The commonly listed ru_RU-irina-medium model is deliberately NOT bundled by the portable builder because its official model card lists the RHVoice dataset license as "Unknown". Obtain a model with clear redistribution rights before adding it to a public archive.

## Python dependencies

The portable archive bundles Python runtime dependencies, including sounddevice and ONNX Runtime. Retain their original license notices in the distribution and review all dependency licenses before redistribution.
