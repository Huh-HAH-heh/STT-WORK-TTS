# STT Work TTS — C# edition

Компактный автономный голосовой помощник для Windows x64. Основное приложение переписано на C# / .NET 10. Python в рабочем конвейере не используется.

## Конвейер

Микрофон → Vosk STT → локальные команды → выбранный TTS → динамики

- Захват микрофона и воспроизведение сделаны через NAudio.
- Русское офлайн-распознавание выполняет Vosk .NET.
- Команды и заметки обрабатываются обычным C# без LLM.
- Piper запускается как нативный Windows CLI, не через Python.
- SAPI — альтернативный движок Windows. С установленным RHVoice можно сравнить русские локальные голоса с Piper.
- Записи, заметки и речь не сохраняются на диск без включения соответствующих параметров.

## Запуск готового архива

1. Открой вкладку Actions репозитория.
2. Скачай STT-WORK-TTS-Windows-x64 из последнего успешного запуска workflow.
3. Распакуй ZIP в отдельную папку.
4. Запусти Start.cmd.

На целевом Windows x64 ПК не нужны Python, интернет, Ollama или видеокарта.

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

## Что умеет локальный обработчик

- «Помощь» — доступные команды.
- «Который час?» — локальные системные часы.
- «Какая сегодня дата?» — локальная системная дата.
- «Посчитай 17 плюс 25» — простые действия с двумя числами.
- «Запомни купить молоко» — заметка в памяти.
- «Покажи заметки» — чтение заметок.
- «Очисти заметки» — очистка списка.

Это не свободный диалог с ИИ. Неизвестная команда не выдумывается: приложение повторяет распознанный текст.

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
