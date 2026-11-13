using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using STT.WorkTTS;

namespace STT.WorkTTS.Processing;

public sealed class NeuralCommandProcessor : ICommandProcessor
{
    private readonly LlamaCppIntentInterpreter _fastInterpreter;
    private readonly LlamaCppIntentInterpreter _heavyInterpreter;
    private readonly LlamaCppTextGenerator _textGenerator;
    private readonly WindowsActionExecutor _executor;
    private readonly LocalCommandProcessor _localFallback;
    private readonly string _rootDirectory;

    public NeuralCommandProcessor(
        LlamaCppIntentInterpreter fastInterpreter,
        LlamaCppIntentInterpreter heavyInterpreter,
        LlamaCppTextGenerator textGenerator,
        WindowsActionExecutor executor,
        LocalCommandProcessor localFallback,
        string appDirectory)
    {
        _fastInterpreter = fastInterpreter;
        _heavyInterpreter = heavyInterpreter;
        _textGenerator = textGenerator;
        _executor = executor;
        _localFallback = localFallback;
        _rootDirectory = FindProjectRoot(appDirectory);
    }

    public async Task<CommandResult> ProcessAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new CommandResult("Не удалось распознать речь. Повтори, пожалуйста.");

        var confirmationResult = await _executor.HandleConfirmationAsync(text, cancellationToken);
        if (confirmationResult is not null)
            return confirmationResult;

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
                var fastModelFailed = false;
                try
                {
                    using (PerformanceLog.Measure("llm.fast_intent.plan_request"))
                        plan = await _fastInterpreter.InterpretAsync(text, cancellationToken);
                }
                catch (Exception ex) when (IsModelException(ex))
                {
                    fastModelFailed = true;
                    PerformanceLog.Event("llm.fast_intent.rejected", ex.Message);
                    plan = new IntentPlan();
                }

                if (fastModelFailed || RequiresHeavyFallback(plan))
                    plan = await InterpretWithHeavyModelAsync(text, cancellationToken);
            }
            catch (Exception ex) when (IsModelException(ex))
            {
                return new CommandResult(
                    "Не удалось безопасно разобрать запрос. Повтори команду короче и назови действие точнее.",
                    ex.Message);
            }
        }

        if (plan.Actions.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(plan.Clarification))
                return new CommandResult(plan.Clarification);

            return _localFallback.Process(text);
        }

        var answers = new List<(int Position, string Text)>();
        var warnings = new List<string>();
        var generationTasks = new List<(int Position, Task<CommandResult> Task)>();

        for (var index = 0; index < plan.Actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = plan.Actions[index];

            if (action.Action == "assistant.answer")
            {
                generationTasks.Add((index, AnswerWithQwenAsync(action, text, cancellationToken)));
                continue;
            }

            if (action.Action == "assistant.generate_text")
            {
                generationTasks.Add((index, GenerateTextWithQwenAsync(action, text, cancellationToken)));
                continue;
            }

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
                answers.Add((index, result.Answer));
            if (!string.IsNullOrWhiteSpace(result.Warning))
                warnings.Add(result.Warning);
        }

        if (generationTasks.Count > 0)
        {
            var generatedResults = await Task.WhenAll(generationTasks.Select(x => x.Task));
            for (var i = 0; i < generatedResults.Length; i++)
            {
                var result = generatedResults[i];
                var position = generationTasks[i].Position;
                if (!string.IsNullOrWhiteSpace(result.Answer))
                    answers.Add((position, result.Answer));
                if (!string.IsNullOrWhiteSpace(result.Warning))
                    warnings.Add(result.Warning);
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.Clarification))
            warnings.Add(plan.Clarification);

        var answer = answers.Count == 0
            ? "План обработан, но действий для выполнения не найдено."
            : string.Join(" ", answers.OrderBy(x => x.Position).Select(x => x.Text));

        return new CommandResult(answer, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    private async Task<IntentPlan> InterpretWithHeavyModelAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (!_textGenerator.Enabled)
            throw new InvalidOperationException("Большая модель отключена; не удалось безопасно обработать сложную команду.");

        await _textGenerator.EnsureServerReadyAsync(cancellationToken);
        using (PerformanceLog.Measure("llm.qwen.fallback_plan"))
        {
            var plan = await _heavyInterpreter.InterpretAsync(text, cancellationToken);
            PerformanceLog.Event("llm.qwen.fallback_validated", $"actions={plan.Actions.Count}");
            return plan;
        }
    }

    private async Task<CommandResult> AnswerWithQwenAsync(
        IntentCommand action,
        string originalText,
        CancellationToken cancellationToken)
    {
        try
        {
            using (PerformanceLog.Measure("llm.qwen.answer"))
            {
                var prompt = string.IsNullOrWhiteSpace(action.Arguments.TextPrompt)
                    ? originalText
                    : action.Arguments.TextPrompt;
                var answer = await _textGenerator.AnswerAsync(prompt, cancellationToken);
                return new CommandResult(answer);
            }
        }
        catch (Exception ex) when (IsModelException(ex))
        {
            return new CommandResult("Не удалось получить ответ от Qwen 4B.", ex.Message);
        }
    }

    private async Task<CommandResult> GenerateTextWithQwenAsync(
        IntentCommand action,
        string originalText,
        CancellationToken cancellationToken)
    {
        try
        {
            string generated;
            using (PerformanceLog.Measure("llm.qwen.document_generation"))
            {
                var prompt = string.IsNullOrWhiteSpace(action.Arguments.TextPrompt)
                    ? originalText
                    : action.Arguments.TextPrompt;
                generated = await _textGenerator.GenerateDocumentAsync(prompt, cancellationToken);
            }

            var directory = Path.Combine(_rootDirectory, "data", "generated");
            Directory.CreateDirectory(directory);
            var filePath = Path.Combine(directory, $"text-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            await File.WriteAllTextAsync(filePath, generated, new UTF8Encoding(false), cancellationToken);

            if (_textGenerator.OpenGeneratedText)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    UseShellExecute = true
                };
                startInfo.ArgumentList.Add(filePath);
                Process.Start(startInfo);
                return new CommandResult("Текст подготовлен. Открываю его в Блокноте.", filePath);
            }

            return new CommandResult("Текст подготовлен и сохранён.", filePath);
        }
        catch (Exception ex) when (IsModelException(ex) || ex is UnauthorizedAccessException)
        {
            return new CommandResult("Не удалось подготовить текст с помощью Qwen 4B.", ex.Message);
        }
    }

    private static bool RequiresHeavyFallback(IntentPlan plan) =>
        plan.Actions.Count == 0
        || plan.Actions.Any(action => action.Action is "unknown" or "clarify");

    private static bool IsModelException(Exception exception) =>
        exception is HttpRequestException
            or TaskCanceledException
            or InvalidDataException
            or JsonException
            or InvalidOperationException
            or IOException
            or Win32Exception;

    private static string FindProjectRoot(string appDirectory)
    {
        for (var current = new DirectoryInfo(appDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Start.cmd"))
                || Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
        }

        return Path.GetFullPath(appDirectory);
    }
}
