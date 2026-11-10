# Runtime assets

The portable archive builder downloads the models into this folder before packaging.

- models/vosk-model-small-ru-0.22 — compact Russian offline speech recognition.
- voices/ru_RU-irina-medium.onnx — female Russian Piper voice.
- voices/ru_RU-denis-medium.onnx — male Russian Piper voice.

The models are intentionally not committed to Git. The portable builder pins their upstream URLs and places them into the final archive. The target computer receives a ZIP with the models already included and does not download them on first launch.
