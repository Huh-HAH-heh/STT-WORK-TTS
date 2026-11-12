using STT.WorkTTS.Domain;
using STT.WorkTTS;
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
        string transcription;
        using (PerformanceLog.Measure("stt.vosk.transcribe", $"audio_seconds={clip.DurationSeconds:F1}"))
            transcription = _recognizer.Transcribe(clip);

        CommandResult commandResult;
        using (PerformanceLog.Measure("assistant.intent_and_action"))
            commandResult = await _processor.ProcessAsync(transcription, cancellationToken);

        return new PipelineResult(transcription, commandResult.Answer, commandResult.Warning);
    }
}
