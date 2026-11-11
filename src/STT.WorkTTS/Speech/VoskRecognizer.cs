using System.Text.Json;
using STT.WorkTTS.Configuration;
using STT.WorkTTS.Domain;
using Vosk;
using NativeVoskRecognizer = Vosk.VoskRecognizer;

namespace STT.WorkTTS.Speech;

public sealed class VoskRecognizer : IDisposable
{
    private readonly Model _model;
    private readonly float _sampleRate;

    public VoskRecognizer(SttSettings settings, string appDirectory)
    {
        var modelPath = AppPaths.Resolve(appDirectory, settings.ModelPath);
        if (!Directory.Exists(modelPath))
            throw new DirectoryNotFoundException($"Не найдена модель распознавания Vosk: {modelPath}");

        Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(modelPath);
        _sampleRate = settings.SampleRate;
    }

    public string Transcribe(AudioClip clip)
    {
        if (clip.Pcm16.Length == 0)
            return string.Empty;

        using var recognizer = new NativeVoskRecognizer(_model, _sampleRate);
        recognizer.SetWords(false);
        recognizer.AcceptWaveform(clip.Pcm16, clip.Pcm16.Length);

        using var document = JsonDocument.Parse(recognizer.FinalResult());
        return document.RootElement.TryGetProperty("text", out var value)
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    public void Dispose() => _model.Dispose();
}