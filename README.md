# STT Work TTS — C# edition

Компактный автономный голосовой помощник для Windows x64. Основное приложение переписано на C# / .NET 10. Python в рабочем конвейере не используется.

## Конвейер

Микрофон → Vosk STT → Qwen3-4B (локальная модель намерений) → C# Intent Router → Windows API → выбранный TTS → динамики

- Захват микрофона и воспроизведение сделаны через NAudio.
- Русское офлайн-распознавание выполняет Vosk .NET.
- Русские голосовые запросы разбирает Qwen3-4B через локальный llama-server; C# проверяет типизированное намерение и вызывает разрешённый обработчик.
- Windows-действия выполняются через Win32 API, Core Audio и ограниченный список приложений; модель не запускает произвольные PowerShell/CMD-команды.
- Таймеры, дата и время исполняются детерминированным кодом C#, а не вычисляются нейросетью.
- Piper запускается как нативный Windows CLI, не через Python.
- SAPI — альтернативный движок Windows. С установленным RHVoice можно сравнить русские локальные голоса с Piper.
- Записи, заметки и речь не сохраняются на диск без включения соответствующих параметров.

## Запуск готового архива

1. Открой вкладку Actions репозитория.
2. Скачай STT-WORK-TTS-Windows-x64 из последнего успешного запуска workflow.
3. Распакуй ZIP в отдельную папку.
4. Запусти Start.cmd.

На целевом Windows x64 ПК не нужны Python или облачные API. Для нейросетевой обработки команд нужен локальный llama-server и отдельно скачанная Qwen GGUF-модель; GPU опционален, CPU-режим тоже доступен.

## Сборка разработчиком

Нужен .NET 10 SDK и Windows x64. При первой сборке скрипт загрузит модель Vosk, нативный Piper CLI и один голос.

~~~powershell
git clone -b feature/csharp-rewrite --single-branch https://github.com/Huh-HAH-heh/STT-WORK-TTS.git
cd STT-WORK-TTS
.\RUN.cmd
~~~

Или вручную:

~~~powershell
powershell -ExecutionPolicy Bypass -File .\build_portable.ps1
~~~

Готовый архив появится в корне репозитория: STT-WORK-TTS-Windows-x64.zip.

## Настройка движка речи

Файл настроек: src/STT.WorkTTS/appsettings.json (в переносимом архиве — рядом с EXE).

По умолчанию используется Piper:

~~~json
"Tts": {
  "Engine": "Piper",
  "PiperExecutable": "assets/piper/piper.exe",
  "PiperModel": "assets/voices/ru_RU-denis-medium.onnx",
  "PlayAudio": true,
  "SaveSpeech": false,
  "SpeechDirectory": "data/speech"
}
~~~

Для сравнения с голосами, установленными в Windows, поменяй Engine на Sapi:

~~~json
"Tts": {
  "Engine": "Sapi"
}
~~~

SAPI использует установленные системные голоса. Дополнительные русские голоса можно установить отдельно, например RHVoice с SAPI 5. Такой голос не входит в portable ZIP, поэтому он нужен только на компьютере, где проводится эксперимент.

Для сравнения одного и того же текста запусти:

~~~cmd
Start.cmd --tts-demo
~~~

Сначала прозвучит встроенный Piper Denis, затем выбранный системный голос SAPI. Пример фразы содержит короткие предложения, длинную конструкцию и числа, чтобы можно было сравнить дикцию и естественность.

## Локальная нейросеть и Windows-команды

По умолчанию включён LLM intent router. Сначала скачай Qwen3-4B-Instruct-2507 Q4_K_M (~2.5 GB):

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\intent\Install-QwenIntentModel.ps1
~~~

Запусти llama-server (нужен отдельный Windows build llama.cpp):

~~~powershell
.\tools\intent\Start-IntentModel.ps1 -ServerPath "C:\llama.cpp\build\bin\Release\llama-server.exe"
~~~

Сервер доступен только на 127.0.0.1:8082. Его настройки находятся в Intent в appsettings.json. Модель не включается в Git или portable ZIP.

Перед использованием голосового ввода можно проверить классификацию без микрофона:

~~~powershell
dotnet run --project .\src\STT.WorkTTS\STT.WorkTTS.csproj -c Release -- --intent-demo
~~~

Действия первой версии: текущее время и дата, таймер, открытие блокнота/калькулятора/проводника/браузера/настроек, URL, показ рабочего стола, громкость, mute/unmute и блокировка компьютера. Таймеры живут, пока работает приложение.

LLM возвращает JSON по JSON Schema. C# повторно проверяет действие и аргументы, а затем вызывает конкретные WinAPI/Core Audio обработчики. Произвольные команды shell, удаление файлов, реестр, выключение и перезагрузка не выполняются.

Если на RX 7600 уже занята VRAM большой моделью (например, coding-моделью 30B), останови её перед запуском Qwen3-4B либо запусти intent-модель с параметром -GpuLayers 0 на CPU.

Подробнее: [tools/intent/README.md](tools/intent/README.md).

## Что умеет локальный обработчик

- «Помощь» — доступные команды.
- «Который час?» — локальные системные часы.
- «Какая сегодня дата?» — локальная системная дата.
- «Посчитай 17 плюс 25» — простые действия с двумя числами.
- «Запомни купить молоко» — заметка в памяти.
- «Покажи заметки» — чтение заметок.
- «Очисти заметки» — очистка списка.

Нейросеть определяет намерение, но не является свободным автономным агентом: неизвестная команда не исполняется, а для неё используется прежний локальный обработчик.

## Параметры аудио и приватность

В appsettings.json:

- Audio.SaveRecordings = false — исходная запись живёт только в RAM.
- Work.SaveNotes = false — заметки живут только в RAM.
- Tts.SaveSpeech = false — временный WAV удаляется после воспроизведения.
- Audio.MaxRecordingSeconds = 90 — ограничение буфера PCM16 mono; около 2,9 МБ при 16 кГц.

## Структура C#-проекта

~~~text
src/STT.WorkTTS/
  Audio/
    MicrophoneRecorder.cs
    AudioPlayer.cs
  Configuration/
    AppSettings.cs
  Domain/
    AudioClip.cs
  Processing/
    LocalCommandProcessor.cs
    SpeechPipeline.cs
    CommandResult.cs
  Speech/
    VoskRecognizer.cs
    ISpeechSynthesizer.cs
    PiperCliSynthesizer.cs
    SapiSynthesizer.cs
  Program.cs
  appsettings.json
  STT.WorkTTS.csproj

assets/
  models/       — Vosk Russian Small
  piper/        — native Windows Piper runtime
  voices/       — voice ONNX + JSON
~~~

## Лаборатория сравнения голосов

Для прослушивания нескольких русских TTS-моделей и их голосов используй отдельную экспериментальную площадку: [tools/tts-lab](tools/tts-lab/README.md). Она сравнивает до 19 вариантов Piper, xVibePocketTTS, Silero, Vosk-TTS и XTTS-v2 на одинаковой фразе, записывает субъективные оценки в CSV и не утяжеляет основной portable ZIP.

Установка и запуск описаны в [инструкции TTS Lab](tools/tts-lab/README.md). Основной конвейер приложения по-прежнему реализован на C#; Python задействован только как изолированный inference-адаптер для отдельных экспериментальных движков.


## Лицензии

См. THIRD_PARTY_NOTICES.md. Piper runtime распространяется под GPL-3.0-or-later; перед распространением готового ZIP необходимо сохранить соответствующие уведомления и выполнить условия лицензии.
