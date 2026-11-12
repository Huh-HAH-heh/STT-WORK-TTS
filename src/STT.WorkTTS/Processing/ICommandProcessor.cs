namespace STT.WorkTTS.Processing;

public interface ICommandProcessor
{
    Task<CommandResult> ProcessAsync(
        string text,
        CancellationToken cancellationToken = default);
}
