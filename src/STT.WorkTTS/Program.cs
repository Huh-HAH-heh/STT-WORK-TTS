using System.Text.Json;
using System.Diagnostics;
using System.Text;
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
        PerformanceLog.Initialize(appDirectory);
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

            if (args.Contains("--list-voices", StringComparer.OrdinalIgnoreCase))
            {
                PrintAvailableVoices();
                return 0;
            }

            ApplyTtsCommandLineOptions(args, settings.Tts);
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
            using var recognizer = LoadVoskRecognizer(settings.Stt, appDirectory);
            var localProcessor = new LocalCommandProcessor(settings.Work, appDirectory);
            using var timers = CreateTimerService();
            using var interpreter = new LlamaCppIntentInterpreter(settings.Intent);
            var executor = new WindowsActionExecutor(timers);
            ICommandProcessor processor = settings.Intent.Enabled
                ? new NeuralCommandProcessor(interpreter, executor, localProcessor)
                : localProcessor;
            using var synthesizer = SpeechSynthesizerFactory.Create(settings.Tts, appDirectory);
            if (synthesizer is PythonWorkerSynthesizer pythonTts)
            {
                Console.WriteLine($"Загружаю TTS: {settings.Tts.Engine} / {settings.Tts.Voice}...");
                using (PerformanceLog.Measure("startup.tts.warmup", $"engine={settings.Tts.Engine};voice={settings.Tts.Voice}"))
                    await pythonTts.WarmUpAsync();
                Console.WriteLine("Голосовой движок готов.");
            }

            var pipeline = new SpeechPipeline(recognizer, processor);

            Console.WriteLine();
            Console.WriteLine("STT Work TTS — локальный голосовой помощник на C#");
            Console.WriteLine("Enter — начать запись; Enter ещё раз — остановить.");
            Console.WriteLine("q — выход; скажи «помощь» для списка команд.");
            Console.WriteLine("Для сравнения голосов запусти tools\\tts-lab\\TtsLab.cmd.");
            Console.WriteLine($"Распознавание: Vosk, {settings.Stt.SampleRate} Гц.");
            Console.WriteLine(sttDemo
                ? "Синтез отключён: режим проверки микрофона и STT."
                : $"Синтез: {settings.Tts.Engine} / {settings.Tts.Voice}.");
            Console.WriteLine(settings.Intent.Enabled
                ? $"Понимание команд: локальная нейросеть ({settings.Intent.ModelAlias}) — {settings.Intent.Endpoint}."
                : "Понимание команд: локальные правила.");
            if (sttDemo)
                Console.WriteLine("Режим --stt-demo: ответ выводится в консоль, без озвучивания.");
            Console.WriteLine("STT и LLM работают локально; облачные API не используются.");
            Console.WriteLine($"Журнал производительности: {PerformanceLog.LogPath}");
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
                    using (PerformanceLog.Measure("microphone.start"))
                        recorder.Start();
                    Console.WriteLine("● Запись идёт. Говорите... (Enter — остановить)");
                    Console.ReadLine();
                    using (PerformanceLog.Measure("microphone.stop_finalize"))
                        clip = recorder.Stop();
                    PerformanceLog.Event("microphone.recorded", $"audio_seconds={clip.DurationSeconds:F1};samples={clip.Pcm16.Length}");
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
                    using var requestTiming = PerformanceLog.Measure("request.total", $"audio_seconds={clip.DurationSeconds:F1}");
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

                    using (PerformanceLog.Measure("tts.total", $"engine={settings.Tts.Engine};voice={settings.Tts.Voice}"))
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

    private static VoskRecognizer LoadVoskRecognizer(SttSettings settings, string appDirectory)
    {
        using (PerformanceLog.Measure("startup.vosk.model_load"))
            return new VoskRecognizer(settings, appDirectory);
    }

    private static void ApplyTtsCommandLineOptions(string[] args, TtsSettings settings)
    {
        string? engine = null;
        string? voice = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--tts" or "--tts-engine")
            {
                if (i + 1 >= args.Length)
                    throw new InvalidDataException($"После {args[i]} нужно указать движок.");
                engine = args[++i].ToLowerInvariant();
            }
            else if (args[i] is "--voice" or "--tts-voice")
            {
                if (i + 1 >= args.Length)
                    throw new InvalidDataException($"После {args[i]} нужно указать голос.");
                voice = args[++i].ToLowerInvariant();
            }
        }

        if (engine is not null)
        {
            settings.Engine = engine switch
            {
                "silero" => "Silero",
                "xvibe" or "x-vibe" => "XVibe",
                "piper" => "Piper",
                "sapi" => "Sapi",
                _ => throw new InvalidDataException(
                    $"Неизвестный TTS-движок '{engine}'. Доступны: silero, xvibe.")
            };

            if (voice is null)
                settings.Voice = settings.Engine.Equals("XVibe", StringComparison.OrdinalIgnoreCase)
                    ? "female_silky"
                    : "xenia";
        }

        if (voice is not null)
            settings.Voice = voice;
    }

    private static void PrintAvailableVoices()
    {
        Console.WriteLine("Доступные TTS-движки и голоса:");
        Console.WriteLine();
        Console.WriteLine("Silero (по умолчанию):");
        Console.WriteLine("  xenia, kseniya, eugene, aidar, baya");
        Console.WriteLine("  пример: Start.cmd --tts silero --voice xenia");
        Console.WriteLine();
        Console.WriteLine("xVibePocketTTS (запасной движок):");
        Console.WriteLine("  female_silky, male_deep, male_authoritative");
        Console.WriteLine("  пример: Start.cmd --tts xvibe --voice female_silky");
        Console.WriteLine();
        Console.WriteLine("Piper и SAPI оставлены как дополнительные legacy-варианты.");
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

/// <summary>
/// Compact JSONL timing logger. It never stores transcribed phrases or generated answers.
/// </summary>
internal static class PerformanceLog
{
    private static readonly object Sync = new();
    private static string? _filePath;
    private static int _warningShown;

    public static string? LogPath => _filePath;

    public static void Initialize(string appDirectory)
    {
        lock (Sync)
        {
            if (_filePath is not null) return;
            var root = appDirectory;
            for (var current = new DirectoryInfo(appDirectory); current is not null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "Start.cmd"))
                    || Directory.Exists(Path.Combine(current.FullName, ".git")))
                {
                    root = current.FullName;
                    break;
                }
            }
            var directory = Path.Combine(root, "data", "logs");
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, $"performance-{DateTime.Now:yyyyMMdd}.jsonl");
        }
        Event("performance_logging.started", $"file={_filePath}");
    }

    public static IDisposable Measure(string stage, string? details = null) => new Measurement(stage, details);

    public static void Record(string stage, double durationMs, string? details = null) =>
        Write(stage, Math.Round(durationMs, 2), details);

    public static void Event(string stage, string? details = null) => Write(stage, null, details);

    private static void Write(string stage, double? durationMs, string? details)
    {
        var line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            stage,
            duration_ms = durationMs,
            details
        });
        lock (Sync)
        {
            if (_filePath is not null)
            {
                try { File.AppendAllText(_filePath, line + Environment.NewLine, new UTF8Encoding(false)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (Interlocked.Exchange(ref _warningShown, 1) == 0)
                        Console.Error.WriteLine($"[PERF] Не удалось записать журнал: {ex.Message}");
                }
            }
        }

        var suffix = string.IsNullOrWhiteSpace(details) ? "" : $" | {details}";
        Console.WriteLine(durationMs is null
            ? $"[PERF] {stage}{suffix}"
            : $"[PERF] {stage}: {durationMs.Value:F1} мс{suffix}");
    }

    private sealed class Measurement : IDisposable
    {
        private readonly string _stage;
        private readonly string? _details;
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private int _disposed;

        public Measurement(string stage, string? details)
        {
            _stage = stage;
            _details = details;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer.Stop();
            Write(_stage, Math.Round(_timer.Elapsed.TotalMilliseconds, 2), _details);
        }
    }
}
