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

        return Task.Run(() => SpeakOnWorkerThread(text, cancellationToken), CancellationToken.None);
    }

    private static void SpeakOnWorkerThread(string text, CancellationToken cancellationToken)
    {
        var sapiType = Type.GetTypeFromProgID("SAPI.SpVoice")
            ?? throw new InvalidOperationException(
                "Windows SAPI не найден. Установи локальный голос с поддержкой SAPI 5, например RHVoice.");

        var voice = Activator.CreateInstance(sapiType)
            ?? throw new InvalidOperationException("Не удалось создать голосовой объект Windows SAPI.");

        try
        {
            // SVSFlagsAsync keeps the caller responsive. PurgeBeforeSpeak is used by
            // the cancellation callback to stop the current utterance.
            using var cancellation = cancellationToken.Register(() =>
            {
                try
                {
                    sapiType.InvokeMember(
                        "Speak",
                        BindingFlags.InvokeMethod,
                        binder: null,
                        target: voice,
                        args: new object[] { string.Empty, 2 });
                }
                catch
                {
                    // The caller observes cancellation even if SAPI is already shutting down.
                }
            });

            sapiType.InvokeMember(
                "Speak",
                BindingFlags.InvokeMethod,
                binder: null,
                target: voice,
                args: new object[] { text, 1 });

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completed = sapiType.InvokeMember(
                    "WaitUntilDone",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: voice,
                    args: new object[] { 100 });

                if (completed is bool done && done)
                    return;
            }
        }
        finally
        {
            if (Marshal.IsComObject(voice))
                Marshal.FinalReleaseComObject(voice);
        }
    }

    public void Dispose() { }
}

public static class SpeechSynthesizerFactory
{
    public static ISpeechSynthesizer Create(TtsSettings settings, string appDirectory)
    {
        if (!settings.PlayAudio && !settings.SaveSpeech)
            return new SilentSynthesizer();

        return settings.Engine.ToLowerInvariant() switch
        {
            "silero" or "xvibe" => new PythonWorkerSynthesizer(settings, appDirectory),
            "sapi" => new SapiSynthesizer(settings),
            "piper" => new PiperCliSynthesizer(settings, appDirectory),
            _ => throw new InvalidOperationException($"Неизвестный TTS-движок: {settings.Engine}")
        };
    }
}