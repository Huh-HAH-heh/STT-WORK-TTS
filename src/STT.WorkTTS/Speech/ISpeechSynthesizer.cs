namespace STT.WorkTTS.Speech;

public interface ISpeechSynthesizer : IDisposable
{
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);
}
