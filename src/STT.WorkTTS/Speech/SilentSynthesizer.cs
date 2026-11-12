namespace STT.WorkTTS.Speech;

public sealed class SilentSynthesizer : ISpeechSynthesizer
{
    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
