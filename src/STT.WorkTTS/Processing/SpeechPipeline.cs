using STT.WorkTTS.Domain;
using STT.WorkTTS.Speech;

namespace STT.WorkTTS.Processing;

public sealed record PipelineResult(string Transcription, string Answer, string? Warning);

public sealed class SpeechPipeline
{
    private readonly VoskRecognizer _recognizer;
    private readonly LocalCommandProcessor _processor;

    public SpeechPipeline(VoskRecognizer recognizer, LocalCommandProcessor processor)
    {
        _recognizer = recognizer;
        _processor = processor;
    }

    public PipelineResult Process(AudioClip clip)
    {
        var transcription = _recognizer.Transcribe(clip);
        var commandResult = _processor.Process(transcription);
        return new PipelineResult(transcription, commandResult.Answer, commandResult.Warning);
    }
}