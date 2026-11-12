# Third-party notices

## Vosk
- Package: Vosk .NET 0.3.38.
- License: Apache License 2.0.
- Source: https://github.com/alphacep/vosk-api
- Russian model: vosk-model-small-ru-0.22. Keep the upstream model notices when redistributing.

## NAudio
- Package: NAudio 2.2.1.
- License: MIT.
- Source: https://github.com/naudio/NAudio

## Piper
- Native Windows CLI from the legacy Piper Windows release.
- Runtime: GPL-3.0-or-later.
- Source: https://github.com/OHF-Voice/piper1-gpl and https://github.com/rhasspy/piper
- A distributed portable archive must comply with the Piper runtime license and include the required license/source information.
- The Russian ru_RU-denis-medium ONNX model and its JSON configuration are separate assets. Review the model card and voice dataset terms before redistribution.

## Windows SAPI / RHVoice
- SAPI is a Windows system speech API. Voices are installed separately by the user and are not bundled in this application.
- RHVoice voice packages have their own licenses and distribution conditions: https://github.com/RHVoice/RHVoice

## Selected local TTS engines
- Main voice: Silero TTS v5.6, Russian speakers (default: xenia). The project uses the upstream PyTorch Hub model; verify upstream code, model and speaker resource terms before redistribution.
- Backup voice: xVibePocketTTS (default: female_silky). The upstream project code is MIT; Pocket TTS base weights and other voice/data resources have separate terms. See the upstream NOTICE and model cards.
- The C# application communicates with long-lived local Python adapter processes. Python environments and downloaded model weights are installed separately under tools/tts-lab and are not committed to Git or included in the portable archive.

## Other experimental TTS Lab engines
- The optional lab under tools/tts-lab does not bundle third-party models or runtimes in Git or in the portable application archive.
- Silero code and model terms must be checked for the selected model family and voice before redistribution.
- Vosk-TTS code: Apache License 2.0. The downloaded Russian model is a separate asset with its own terms.
- XTTS-v2 weights: Coqui Public Model License (non-commercial use only, including generated audio). The lab asks for confirmation before running XTTS.
- Piper voice ONNX files have separate model cards; the runtime is GPL-3.0-or-later.
- Sources and setup instructions: tools/tts-lab/README.md.

## Qwen intent model
- Model: Qwen3-4B-Instruct-2507, GGUF Q4_K_M.
- Original model licence: Apache License 2.0.
- Source: https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507
- GGUF quantization: https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF
- Downloaded by tools/intent/Install-QwenIntentModel.ps1 and verified with SHA-256. The model file is not committed or bundled in the portable ZIP.
