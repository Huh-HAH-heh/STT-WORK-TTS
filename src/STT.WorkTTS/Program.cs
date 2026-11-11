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

        if (args.Contains("--tts-demo", StringComparer.OrdinalIgnoreCase))
            return await RunTtsDemo(settings, appDirectory);

        try
        {
            using var recorder = new MicrophoneRecorder(settings.Audio, appDirectory);
            using var recognizer = new VoskRecognizer(settings.Stt, appDirectory);
            var processor = new LocalCommandProcessor(settings.Work, appDirectory);
            using var synthesizer = SpeechSynthesizerFactory.Create(settings.Tts, appDirectory);
            var pipeline = new SpeechPipeline(recognizer, processor);

            Console.WriteLine();
            Console.WriteLine("STT Work TTS — локальный голосовой помощник на C#");
            Console.WriteLine("Enter — начать запись; Enter ещё раз — остановить.");
            Console.WriteLine("q — выход; скажи «помощь» для списка команд.");
            Console.WriteLine("Для сравнения голосов запусти Start.cmd --tts-demo.");
            Console.WriteLine($"Распознавание: Vosk, {settings.Stt.SampleRate} Гц.");
            Console.WriteLine($"Синтез: {settings.Tts.Engine}.");
            Console.WriteLine("Сеть, облачные API и GPU не используются.");
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
                    var result = pipeline.Process(clip);

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

    private static async Task<int> RunTtsDemo(AppSettings settings, string appDirectory)
    {
        const string sample = "Привет! Сегодня хорошая погода. Я запомнил твою заметку и могу прочитать её вслух. Проверим длинную фразу с числами: семнадцать плюс двадцать пять равно сорок два.";

        try
        {
            Console.WriteLine("Один и тот же текст будет озвучен двумя движками.");
            Console.WriteLine();
            using (var piper = new PiperCliSynthesizer(settings.Tts, appDirectory))
            {
                Console.WriteLine("Piper — встроенный голос Denis:");
                await piper.SpeakAsync(sample);
            }

            Console.WriteLine();
            Console.WriteLine("Windows SAPI — системный голос (например, RHVoice):");
            using (var sapi = new SapiSynthesizer(settings.Tts))
                await sapi.SpeakAsync(sample);

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось выполнить сравнение: {ex.Message}");
            return 1;
        }
    }

    private static string DisplayText(string text) =>
        string.IsNullOrWhiteSpace(text) ? "[речь не распознана]" : text;
}