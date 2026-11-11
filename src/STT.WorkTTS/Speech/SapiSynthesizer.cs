using System.Reflection;
using System.Runtime.InteropServices;
using STT.WorkTTS.Configuration;

namespace STT.WorkTTS.Speech;

public sealed class SapiSynthesizer : ISpeechSynthesizer
{
    private readonly bool _enabled;

    public SapiSynthesizer(TtsSettings settings)
    {
        _enabled = settings.PlayAudio;
    }

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled || string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        var sapiType = Type.GetTypeFromProgID("SAPI.SpVoice")
            ?? throw new InvalidOperationException(
                "Windows SAPI не найден. Установи локальный голос с поддержкой SAPI 5, например RHVoice.");

        var voice = Activator.CreateInstance(sapiType)
            ?? throw new InvalidOperationException("Не удалось создать голосовой объект Windows SAPI.");

        try
        {
            sapiType.InvokeMember(
                "Speak",
                BindingFlags.InvokeMethod,
                binder: null,
                target: voice,
                args: new object[] { text, 0 });
        }
        finally
        {
            if (Marshal.IsComObject(voice))
                Marshal.FinalReleaseComObject(voice);
        }

        return Task.CompletedTask;
    }

    public void Dispose() { }
}

public static class SpeechSynthesizerFactory
{
    public static ISpeechSynthesizer Create(TtsSettings settings, string appDirectory) =>
        settings.Engine.Equals("Sapi", StringComparison.OrdinalIgnoreCase)
            ? new SapiSynthesizer(settings)
            : new PiperCliSynthesizer(settings, appDirectory);
}