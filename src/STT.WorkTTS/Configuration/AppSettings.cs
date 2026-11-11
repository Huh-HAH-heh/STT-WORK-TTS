namespace STT.WorkTTS.Configuration;

public sealed class AppSettings
{
    public AudioSettings Audio { get; set; } = new();
    public SttSettings Stt { get; set; } = new();
    public WorkSettings Work { get; set; } = new();
    public TtsSettings Tts { get; set; } = new();

    public void Validate()
    {
        if (Audio.SampleRate != 16000 || Stt.SampleRate != 16000)
            throw new InvalidDataException("Для текущей компактной модели Vosk используется частота 16000 Гц.");
        if (Audio.MaxRecordingSeconds is < 1 or > 300)
            throw new InvalidDataException("Audio.MaxRecordingSeconds должен быть от 1 до 300.");
        if (!Tts.Engine.Equals("Piper", StringComparison.OrdinalIgnoreCase)
            && !Tts.Engine.Equals("Sapi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tts.Engine должен быть Piper или Sapi.");
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
    public string Engine { get; set; } = "Piper";
    public string PiperExecutable { get; set; } = "assets/piper/piper.exe";
    public string PiperModel { get; set; } = "assets/voices/ru_RU-denis-medium.onnx";
    public bool PlayAudio { get; set; } = true;
    public bool SaveSpeech { get; set; }
    public string SpeechDirectory { get; set; } = "data/speech";
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