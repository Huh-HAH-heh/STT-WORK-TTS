# Local Windows Intent Engine

Локальный нейросетевой диспетчер команд. Он получает транскрипцию Vosk, классифицирует намерение через Qwen3-4B-Instruct и передаёт типизированную команду в C#.

## Модель

- Qwen3-4B-Instruct-2507
- GGUF Q4_K_M
- Размер: около 2.5 GB
- Лицензия модели: Apache 2.0
- Runtime: llama.cpp / llama-server
- Транспорт: OpenAI-compatible HTTP API на loopback
- Структурированный ответ: JSON Schema

Модель не входит в Git и portable ZIP. Она скачивается отдельно и проверяется по SHA-256.

## Установка модели

Из корня репозитория:

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\intent\Install-QwenIntentModel.ps1
~~~

Модель появится в папке models/intent. Установщик использует curl.exe, скачивает файл с поддержкой Range/resume, повторяет неудачные попытки и проверяет SHA-256 по закреплённой ревизии Hugging Face.

Если подключение оборвалось, не удаляй файл с расширением .download. Просто повтори ту же команду установки: загрузчик продолжит с сохранённого места.

## Запуск сервера

Нужен Windows build llama-server.exe с Vulkan или CPU backend. Если llama-server.exe добавлен в PATH:

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\intent\Start-IntentModel.ps1
~~~

Либо передай полный путь к серверу:

~~~powershell
.\tools\intent\Start-IntentModel.ps1 -ServerPath "C:\llama.cpp\build\bin\Release\llama-server.exe"
~~~

Сервер слушает только 127.0.0.1:8082; наружу в локальную сеть он не выставляется. Алиас модели — stt-intent.

Если на GPU уже загружена большая модель (например, Qwen3-Coder 30B), освободи VRAM перед запуском 4B-модели либо запусти intent-модель с параметром -GpuLayers 0 для CPU. Не стоит одновременно загружать обе модели в 8 GB VRAM без проверки фактического распределения памяти.

## Проверка понимания без микрофона

Когда сервер запущен:

~~~powershell
dotnet run --project .\src\STT.WorkTTS\STT.WorkTTS.csproj -c Release -- --intent-demo
~~~

Примеры фраз:

- Который час?
- Поставь таймер на пять минут.
- Открой блокнот.
- Сделай громкость на двадцать процентов ниже.
- Покажи рабочий стол.
- Открой настройки Windows.

После этого запускай приложение обычным способом. В основном конвейере Vosk превращает речь в текст, нейросеть разбирает запрос, а C# выполняет разрешённое действие.

## Разрешённые команды первой версии

- system.get_time, system.get_date, system.get_datetime
- timer.start, timer.cancel
- app.open — блокнот, калькулятор, проводник, браузер, настройки
- url.open — только HTTP/HTTPS
- window.show_desktop
- audio.set_volume, audio.adjust_volume, audio.mute, audio.unmute
- system.lock

Операции с файлами, PowerShell/CMD, реестром, выключением компьютера и другими потенциально опасными действиями не исполняются. Для них нужна отдельная система подтверждений.

Таймеры работают, пока приложение STT Work TTS запущено. Перезапуск приложения сбрасывает активные таймеры. Постоянные напоминания и Windows Task Scheduler — отдельный следующий этап.

## Архитектура безопасности

LLM возвращает JSON по ограниченной схеме. C# повторно проверяет action и параметры, затем вызывает только конкретные методы Windows. Модель не генерирует исполняемые PowerShell/CMD-команды, shell-код или произвольные пути запуска.
