using STT.WorkTTS.Domain;
using STT.WorkTTS.Speech;

namespace STT.WorkTTS.Processing;

public sealed record PipelineResult(string Transcription, string Answer, string? Warning);

public sealed class SpeechPipeline
{
    private readonly VoskRecognizer _recognizer;
    private readonly ICommandProcessor _processor;

    public SpeechPipeline(VoskRecognizer recognizer, ICommandProcessor processor)
    {
        _recognizer = recognizer;
        _processor = processor;
    }

    public async Task<PipelineResult> ProcessAsync(
        AudioClip clip,
        CancellationToken cancellationToken = default)
    {
        var transcription = _recognizer.Transcribe(clip);
        var commandResult = await _processor.ProcessAsync(transcription, cancellationToken);
        return new PipelineResult(transcription, commandResult.Answer, commandResult.Warning);
    }
}
