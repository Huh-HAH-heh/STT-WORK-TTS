namespace STT.WorkTTS.Configuration;

public sealed class AppSettings
{
    public AudioSettings Audio { get; set; } = new();
    public SttSettings Stt { get; set; } = new();
    public WorkSettings Work { get; set; } = new();
    public TtsSettings Tts { get; set; } = new();
    public IntentSettings Intent { get; set; } = new();

    public void Validate()
    {
        if (Audio.SampleRate != 16000 || Stt.SampleRate != 16000)
            throw new InvalidDataException("Для текущей компактной модели Vosk используется частота 16000 Гц.");
        if (Audio.MaxRecordingSeconds is < 1 or > 300)
            throw new InvalidDataException("Audio.MaxRecordingSeconds должен быть от 1 до 300.");
        if (!new[] { "Silero", "XVibe", "Piper", "Sapi" }
            .Contains(Tts.Engine, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Tts.Engine должен быть Silero, XVibe, Piper или Sapi.");

        var allowedVoices = Tts.Engine.Equals("Silero", StringComparison.OrdinalIgnoreCase)
            ? new[] { "aidar", "baya", "kseniya", "xenia", "eugene" }
            : Tts.Engine.Equals("XVibe", StringComparison.OrdinalIgnoreCase)
                ? new[] { "male_deep", "female_silky", "male_authoritative" }
                : Array.Empty<string>();

        if (allowedVoices.Length > 0 && !allowedVoices.Contains(Tts.Voice, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Голос '{Tts.Voice}' не поддерживается движком {Tts.Engine}.");

        if (Intent.TimeoutSeconds is < 5 or > 120)
            throw new InvalidDataException("Intent.TimeoutSeconds должен быть от 5 до 120.");
        if (!Uri.TryCreate(Intent.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttp
            || !endpoint.IsLoopback)
            throw new InvalidDataException("Intent.Endpoint должен указывать на локальный HTTP-сервер (127.0.0.1 или localhost).");
        if (string.IsNullOrWhiteSpace(Intent.ModelAlias))
            throw new InvalidDataException("Intent.ModelAlias не может быть пустым.");
    }
}

public sealed class AudioSettings
{
    public int SampleRate { get; set; } = 16000;
    public int MaxRecordingSeconds { get; set; } = 90;
    public bool SaveRecordings { get; set; }
    public string RecordingDirectory { get; set; } = "data/recordings";
}

public sealed class SttSettings
{
    public string ModelPath { get; set; } = "assets/models/vosk-model-small-ru-0.22";
    public int SampleRate { get; set; } = 16000;
}

public sealed class WorkSettings
{
    public bool SaveNotes { get; set; }
    public string NotesFile { get; set; } = "data/notes.txt";
}

public sealed class TtsSettings
{
    public string Engine { get; set; } = "Silero";
    public string Voice { get; set; } = "xenia";
    public string SileroPythonExecutable { get; set; } = "tools/tts-lab/envs/silero/Scripts/python.exe";
    public string SileroWorkerScript { get; set; } = "tools/tts-runtime/silero_worker.py";
    public string XvibePythonExecutable { get; set; } = "tools/tts-lab/vendor/xVibePocketTTS/.venv/Scripts/python.exe";
    public string XvibeWorkerScript { get; set; } = "tools/tts-runtime/xvibe_worker.py";
    public string PiperExecutable { get; set; } = "assets/piper/piper.exe";
    public string PiperModel { get; set; } = "assets/voices/ru_RU-denis-medium.onnx";
    public bool PlayAudio { get; set; } = true;
    public bool SaveSpeech { get; set; }
    public string SpeechDirectory { get; set; } = "data/speech";
}

public sealed class IntentSettings
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "http://127.0.0.1:8082/v1/chat/completions";
    public string ModelAlias { get; set; } = "stt-intent";
    public int TimeoutSeconds { get; set; } = 30;
}

public static class AppPaths
{
    public static string Resolve(string root, string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        // Supports both the portable folder and dotnet run from the source tree.
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
        {
            var candidate = Path.GetFullPath(Path.Combine(current.FullName, path));
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
        }

        return Path.GetFullPath(Path.Combine(root, path));
    }
}