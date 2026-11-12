using System.Text.Json;
using STT.WorkTTS;

namespace STT.WorkTTS.Processing;

public sealed class NeuralCommandProcessor : ICommandProcessor
{
    private readonly LlamaCppIntentInterpreter _interpreter;
    private readonly WindowsActionExecutor _executor;
    private readonly LocalCommandProcessor _localFallback;

    public NeuralCommandProcessor(
        LlamaCppIntentInterpreter interpreter,
        WindowsActionExecutor executor,
        LocalCommandProcessor localFallback)
    {
        _interpreter = interpreter;
        _executor = executor;
        _localFallback = localFallback;
    }

    public async Task<CommandResult> ProcessAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new CommandResult("Не удалось распознать речь. Повтори, пожалуйста.");

        // Capability questions are a deterministic help script, not an ambiguous OS command.
        if (LocalCommandProcessor.IsCapabilityRequest(text))
            return _localFallback.Process(text);

        IntentCommand intent;
        try
        {
            using (PerformanceLog.Measure("llm.qwen.intent_request"))
                intent = await _interpreter.InterpretAsync(text, cancellationToken);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            TaskCanceledException or
            InvalidDataException or
            JsonException)
        {
            return new CommandResult(
                "Не удалось обратиться к локальной языковой модели. Проверь, запущен ли llama-server.",
                ex.Message);
        }

        if (intent.Action == "unknown")
        {
            using (PerformanceLog.Measure("intent.local_fallback"))
                return _localFallback.Process(text);
        }

        using (PerformanceLog.Measure("action.execute", $"action={intent.Action}"))
            return await _executor.ExecuteAsync(intent, cancellationToken);
    }
}
