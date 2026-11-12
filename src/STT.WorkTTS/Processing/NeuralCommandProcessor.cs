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

        IntentPlan plan;
        if (CommandScriptParser.TryParse(text, out var fastPlan))
        {
            using (PerformanceLog.Measure("intent.fast_script", $"actions={fastPlan.Actions.Count}"))
                plan = fastPlan;
        }
        else
        {
            try
            {
                using (PerformanceLog.Measure("llm.qwen.plan_request"))
                    plan = await _interpreter.InterpretAsync(text, cancellationToken);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or
                TaskCanceledException or
                InvalidDataException or
                JsonException)
            {
                return new CommandResult(
                    "Не удалось получить план действий от локальной модели. Проверь llama-server.",
                    ex.Message);
            }
        }

        if (plan.Actions.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(plan.Clarification))
                return new CommandResult(plan.Clarification);

            return _localFallback.Process(text);
        }

        var answers = new List<string>();
        var warnings = new List<string>();

        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CommandResult result;
            if (action.Action == "unknown")
            {
                using (PerformanceLog.Measure("intent.local_fallback"))
                    result = _localFallback.Process(text);
            }
            else
            {
                using (PerformanceLog.Measure("action.execute", $"action={action.Action}"))
                    result = await _executor.ExecuteAsync(action, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(result.Answer))
                answers.Add(result.Answer);
            if (!string.IsNullOrWhiteSpace(result.Warning))
                warnings.Add(result.Warning);
        }

        if (!string.IsNullOrWhiteSpace(plan.Clarification))
            warnings.Add(plan.Clarification);

        var answer = answers.Count == 0
            ? "План обработан, но действий для выполнения не найдено."
            : string.Join(" ", answers);

        return new CommandResult(answer, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }
}
