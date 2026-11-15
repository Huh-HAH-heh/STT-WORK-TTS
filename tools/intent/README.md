# RuBERT-tiny2 command router

Быстрый маршрутизатор заменяет прежнюю Qwen3-0.6B GGUF + llama-server. На runtime не нужен Python, llama.cpp, Ollama или сетевой HTTP endpoint: C# использует ONNX Runtime CPU.

## Обучение

Запускать на основном ПК, не на ноутбуке 4 ГБ:

```powershell
.\tools\intent\Train-RuBertIntent.ps1
```

Исходный энкодер: `cointegrated/rubert-tiny2`. Скрипт создаёт классификационную голову по классам из JSONL, сохраняет словарь/метки, экспортирует ONNX и применяет динамическую INT8-квантизацию.

Данные находятся в `dataset/commands.jsonl`. Формат строки:

```json
{"text":"Открой блокнот","label":"app.open"}
```

У каждого класса нужно не менее пяти примеров. Добавляй разговорные формулировки и реальные ошибки Vosk. После изменения набора запускай обучение повторно.

После обучения должны существовать: `assets/intent/rubert-tiny2-intent-int8.onnx`, `vocab.txt` и `labels.json`.

Приложение классифицирует намерение, извлекает аргументы обычным кодом C# и передаёт план через `IntentPlanValidator`. Модель не формирует команды оболочки и не имеет прямого доступа к Windows API.
