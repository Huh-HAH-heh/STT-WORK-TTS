using System.Text.Json;
using STT.WorkTTS.Audio;
using STT.WorkTTS.Configuration;
using STT.WorkTTS.Domain;
using STT.WorkTTS.Processing;
using STT.WorkTTS.Speech;

namespace STT.WorkTTS;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var appDirectory = AppContext.BaseDirectory;
        var settingsPath = Path.Combine(appDirectory, "appsettings.json");

        if (!File.Exists(settingsPath))
        {
            Console.Error.WriteLine($"Не найден файл настроек: {settingsPath}");
            return 1;
        }

        AppSettings settings;
        try
        {
            var json = await File.ReadAllTextAsync(settingsPath);
            settings = JsonSerializer.Deserialize<AppSettings>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? throw new InvalidDataException("Файл настроек пустой.");
            settings.Validate();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка конфигурации: {ex.Message}");
            return 1;
        }

        if (args.Contains("--intent-demo", StringComparer.OrdinalIgnoreCase))
            return await RunIntentDemo(settings);

        var sttDemo = args.Contains("--stt-demo", StringComparer.OrdinalIgnoreCase);
        if (sttDemo)
        {
            // Keep the real microphone -> Vosk -> intent -> Windows action path,
            // but do not require any TTS engine or installed voice while testing STT.
            settings.Tts.PlayAudio = false;
            settings.Tts.SaveSpeech = false;
        }

        try
        {
            using var recorder = new MicrophoneRecorder(settings.Audio, appDirectory);
            using var recognizer = new VoskRecognizer(settings.Stt, appDirectory);
            var localProcessor = new LocalCommandProcessor(settings.Work, appDirectory);
            using var timers = CreateTimerService();
            using var interpreter = new LlamaCppIntentInterpreter(settings.Intent);
            var executor = new WindowsActionExecutor(timers);
            ICommandProcessor processor = settings.Intent.Enabled
                ? new NeuralCommandProcessor(interpreter, executor, localProcessor)
                : localProcessor;
            using var synthesizer = SpeechSynthesizerFactory.Create(settings.Tts, appDirectory);
            var pipeline = new SpeechPipeline(recognizer, processor);

            Console.WriteLine();
            Console.WriteLine("STT Work TTS — локальный голосовой помощник на C#");
            Console.WriteLine("Enter — начать запись; Enter ещё раз — остановить.");
            Console.WriteLine("q — выход; скажи «помощь» для списка команд.");
            Console.WriteLine("Для сравнения голосов запусти tools\\tts-lab\\TtsLab.cmd.");
            Console.WriteLine($"Распознавание: Vosk, {settings.Stt.SampleRate} Гц.");
            Console.WriteLine(sttDemo
                ? "Синтез отключён: режим проверки микрофона и STT."
                : $"Синтез: {settings.Tts.Engine}.");
            Console.WriteLine(settings.Intent.Enabled
                ? $"Понимание команд: локальная нейросеть ({settings.Intent.ModelAlias}) — {settings.Intent.Endpoint}."
                : "Понимание команд: локальные правила.");
            if (sttDemo)
                Console.WriteLine("Режим --stt-demo: ответ выводится в консоль, без озвучивания.");
            Console.WriteLine("STT и LLM работают локально; облачные API не используются.");
            Console.WriteLine();

            while (true)
            {
                Console.Write("[Enter] Записать  |  [q] Выход: ");
                var command = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (command is null || command is "q" or "й" or "quit" or "exit")
                    break;

                AudioClip clip;
                try
                {
                    recorder.Start();
                    Console.WriteLine("● Запись идёт. Говорите... (Enter — остановить)");
                    Console.ReadLine();
                    clip = recorder.Stop();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка записи: {ex.Message}");
                    continue;
                }

                if (recorder.LimitReached)
                    Console.WriteLine("Достигнут максимальный размер записи.");

                Console.WriteLine($"Записано: {clip.DurationSeconds:F1} сек.");
                if (clip.SavedPath is not null)
                    Console.WriteLine($"WAV сохранён: {clip.SavedPath}");

                try
                {
                    Console.WriteLine("Распознаю речь...");
                    var result = await pipeline.ProcessAsync(clip);

                    Console.WriteLine();
                    Console.WriteLine($"Распознано: {DisplayText(result.Transcription)}");
                    Console.WriteLine($"Ответ: {result.Answer}");

                    if (result.Warning is not null)
                        Console.WriteLine($"Примечание: {result.Warning}");

                    if (settings.Tts.Engine.Equals("Sapi", StringComparison.OrdinalIgnoreCase)
                        && settings.Tts.SaveSpeech)
                    {
                        Console.WriteLine("SAPI озвучивает напрямую через Windows; отдельный WAV не создаётся.");
                    }

                    await synthesizer.SpeakAsync(result.Answer);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка обработки: {ex.Message}");
                }

                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось запустить приложение: {ex.Message}");
            Console.Error.WriteLine("Проверь assets, appsettings.json и доступ к микрофону.");
            return 1;
        }

        Console.WriteLine("Работа завершена.");
        return 0;
    }

    private static async Task<int> RunIntentDemo(AppSettings settings)
    {
        using var timers = CreateTimerService();
        using var interpreter = new LlamaCppIntentInterpreter(settings.Intent);
        var local = new LocalCommandProcessor(settings.Work, AppContext.BaseDirectory);
        var processor = new NeuralCommandProcessor(
            interpreter,
            new WindowsActionExecutor(timers),
            local);

        Console.WriteLine("Тест нейросетевого понимания команд. Микрофон, Vosk и TTS не используются.");
        Console.WriteLine("Вводи фразы на русском; q — выход.");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            var text = Console.ReadLine();
            if (text is null || text.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                break;

            var result = await processor.ProcessAsync(text);
            Console.WriteLine($"Ответ: {result.Answer}");
            if (result.Warning is not null)
                Console.WriteLine($"Примечание: {result.Warning}");
            Console.WriteLine();
        }

        return 0;
    }

    private static TimerService CreateTimerService() =>
        new(message =>
        {
            lock (Console.Out)
            {
                Console.WriteLine();
                Console.WriteLine($"\u0007🔔 {message}");
            }

            try { Console.Beep(880, 700); }
            catch { /* Sound output may be unavailable in some sessions. */ }
        });

    private static string DisplayText(string text) =>
        string.IsNullOrWhiteSpace(text) ? "[речь не распознана]" : text;
}