using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using STT.WorkTTS.Configuration;

namespace STT.WorkTTS.Processing;

/// <summary>
/// CPU-only intent classifier. The ONNX model selects an action; C# extracts and validates its slots.
/// It never generates shell commands or executable code.
/// </summary>
public sealed class RuBertIntentInterpreter : IDisposable
{
    private const string LogitsOutput = "logits";
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly string[] _labels;
    private readonly int _sequenceLength;
    private readonly float _confidenceThreshold;
    private readonly long _padTokenId;
    private readonly IntentExampleMatcher _exampleMatcher;

    public RuBertIntentInterpreter(IntentSettings settings, string appDirectory)
    {
        var modelPath = AppPaths.Resolve(appDirectory, settings.ModelPath);
        var vocabularyPath = AppPaths.Resolve(appDirectory, settings.VocabularyPath);
        var labelsPath = AppPaths.Resolve(appDirectory, settings.LabelsPath);

        foreach (var path in new[] { modelPath, vocabularyPath, labelsPath })
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Не найден файл RuBERT Intent: {path}. Сначала обучи модель на основном ПК и перенеси assets/intent.",
                    path);
        }

        if (settings.MaxSequenceLength is < 16 or > 128)
            throw new InvalidDataException("Intent.MaxSequenceLength должен быть от 16 до 128.");
        if (settings.IntraOpThreads is < 1 or > 4)
            throw new InvalidDataException("Intent.IntraOpThreads должен быть от 1 до 4.");
        if (settings.ConfidenceThreshold is < 0.1f or > 0.99f)
            throw new InvalidDataException("Intent.ConfidenceThreshold должен быть от 0.1 до 0.99.");

        _sequenceLength = settings.MaxSequenceLength;
        _confidenceThreshold = settings.ConfidenceThreshold;
        _labels = JsonSerializer.Deserialize<string[]>(File.ReadAllText(labelsPath))
            ?? throw new InvalidDataException("Не удалось прочитать labels.json.");
        if (_labels.Length == 0 || _labels.Distinct(StringComparer.Ordinal).Count() != _labels.Length)
            throw new InvalidDataException("labels.json пуст или содержит повторяющиеся метки.");

        _tokenizer = BertTokenizer.Create(
            vocabularyPath,
            new BertOptions
            {
                LowerCaseBeforeTokenization = false,
                RemoveNonSpacingMarks = false
            });

        _padTokenId = 0;

        var options = new SessionOptions
        {
            IntraOpNumThreads = settings.IntraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = true,
            EnableMemoryPattern = true
        };
        _session = new InferenceSession(modelPath, options);

        var requiredInputs = new[] { "input_ids", "attention_mask", "token_type_ids" };
        foreach (var name in requiredInputs)
        {
            if (!_session.InputMetadata.ContainsKey(name))
                throw new InvalidDataException($"ONNX-модель не содержит обязательный вход {name}.");
        }
        if (!_session.OutputMetadata.ContainsKey(LogitsOutput))
            throw new InvalidDataException("ONNX-модель не содержит выход logits.");

        if (_session.OutputMetadata[LogitsOutput].Dimensions.LastOrDefault() != _labels.Length)
            throw new InvalidDataException(
                $"Число выходов модели не совпадает с labels.json: {_labels.Length}.");

        var datasetPath = AppPaths.Resolve(appDirectory, "tools/intent/dataset/commands.jsonl");
        if (!File.Exists(datasetPath))
            datasetPath = AppPaths.Resolve(appDirectory, "intent/commands.jsonl");

        _exampleMatcher = new IntentExampleMatcher(datasetPath);
    }

    public Task<IntentPlan> InterpretAsync(
        string transcription,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(transcription))
            return Task.FromResult(new IntentPlan
            {
                Clarification = "Не удалось разобрать фразу. Повтори команду."
            });

        // Exact/high-confidence catalog phrases bypass ONNX entirely.
        // This is the cheapest path for frequent voice commands.
        if (_exampleMatcher.TryResolveFast(transcription, out var catalogLabel, out var catalogScore))
        {
            var catalogPlan = BuildPlan(transcription, catalogLabel);
            if (catalogPlan.Actions.Count > 0)
            {
                try
                {
                    IntentPlanValidator.Validate(transcription, catalogPlan);
                    PerformanceLog.Event(
                        "intent.catalog.fast_match",
                        $"label={catalogLabel};similarity={catalogScore:F2}");
                    return Task.FromResult(catalogPlan);
                }
                catch (InvalidDataException ex)
                {
                    PerformanceLog.Event("intent.catalog.fast_match_rejected", ex.Message);
                }
            }
        }

        using (PerformanceLog.Measure("intent.rubert.inference"))
        {
            var tokenIds = _tokenizer.EncodeToIds(
                transcription,
                _sequenceLength - 2,
                addSpecialTokens: false,
                out _,
                out _);
            var fullIds = _tokenizer.BuildInputsWithSpecialTokens(tokenIds);
            var inputIds = Enumerable.Repeat(_padTokenId, _sequenceLength).ToArray();
            var attentionMask = new long[_sequenceLength];
            var tokenTypeIds = new long[_sequenceLength];

            var count = Math.Min(fullIds.Count, _sequenceLength);
            for (var i = 0; i < count; i++)
            {
                inputIds[i] = fullIds[i];
                attentionMask[i] = 1;
            }

            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids",
                    new DenseTensor<long>(inputIds, new[] { 1, _sequenceLength })),
                NamedOnnxValue.CreateFromTensor("attention_mask",
                    new DenseTensor<long>(attentionMask, new[] { 1, _sequenceLength })),
                NamedOnnxValue.CreateFromTensor("token_type_ids",
                    new DenseTensor<long>(tokenTypeIds, new[] { 1, _sequenceLength }))
            };

            using var outputs = _session.Run(inputs);
            var logits = outputs.First(x => x.Name == LogitsOutput).AsTensor<float>().ToArray();
            if (logits.Length != _labels.Length)
                throw new InvalidDataException("Неверная размерность logits у RuBERT.");

            var bestIndex = 0;
            var bestLogit = logits[0];
            for (var i = 1; i < logits.Length; i++)
            {
                if (logits[i] > bestLogit)
                {
                    bestLogit = logits[i];
                    bestIndex = i;
                }
            }

            var confidence = SoftmaxProbability(logits, bestIndex);
            var predictedLabel = _labels[bestIndex];
            PerformanceLog.Event(
                "intent.rubert.prediction",
                $"label={predictedLabel};confidence={confidence:F3}");

            IntentPlan plan;
            if (confidence < _confidenceThreshold)
            {
                if (_exampleMatcher.TryResolve(transcription, out var matchedLabel, out var similarity))
                {
                    plan = BuildPlan(transcription, matchedLabel);
                    if (plan.Actions.Count > 0)
                    {
                        try
                        {
                            IntentPlanValidator.Validate(transcription, plan);
                            PerformanceLog.Event(
                                "intent.semantic_example.fallback",
                                $"label={matchedLabel};similarity={similarity:F2}");
                        }
                        catch (InvalidDataException)
                        {
                            plan = Clarify(
                                "Я распознала объект, но не уверена в действии. Скажи отдельно, что с ним сделать.");
                        }
                    }
                }
                else
                {
                    PerformanceLog.Event(
                        "intent.semantic_example.no_match",
                        $"classifier={predictedLabel};confidence={confidence:F3}");
                    plan = Clarify(_exampleMatcher.GetSuggestions(transcription));
                }
            }
            else
            {
                plan = BuildPlan(transcription, predictedLabel);
                if (plan.Actions.Count == 0
                    && _exampleMatcher.TryResolve(transcription, out var alternateLabel, out var alternateScore)
                    && alternateLabel != predictedLabel)
                {
                    var alternatePlan = BuildPlan(transcription, alternateLabel);
                    if (alternatePlan.Actions.Count > 0)
                    {
                        try
                        {
                            IntentPlanValidator.Validate(transcription, alternatePlan);
                            plan = alternatePlan;
                            PerformanceLog.Event(
                                "intent.semantic_example.recovered_empty_plan",
                                $"from={predictedLabel};to={alternateLabel};similarity={alternateScore:F2}");
                        }
                        catch (InvalidDataException)
                        {
                            // Keep the original clarification; an ungrounded alternative must not execute.
                        }
                    }
                }

                if (plan.Actions.Count > 0)
                {
                    try
                    {
                        IntentPlanValidator.Validate(transcription, plan);
                    }
                    catch (InvalidDataException)
                    {
                        if (_exampleMatcher.TryResolve(transcription, out var semanticLabel, out var semanticScore)
                            && semanticLabel != predictedLabel)
                        {
                            var semanticPlan = BuildPlan(transcription, semanticLabel);
                            try
                            {
                                if (semanticPlan.Actions.Count > 0)
                                    IntentPlanValidator.Validate(transcription, semanticPlan);

                                plan = semanticPlan;
                                PerformanceLog.Event(
                                    "intent.semantic_example.corrected_prediction",
                                    $"from={predictedLabel};to={semanticLabel};similarity={semanticScore:F2}");
                            }
                            catch (InvalidDataException)
                            {
                                plan = Clarify(
                                    _exampleMatcher.GetSuggestions(transcription));
                            }
                        }
                        else
                        {
                            plan = Clarify(
                                _exampleMatcher.GetSuggestions(transcription));
                        }
                    }
                }
            }

            if (plan.Actions.Count > 0)
                IntentPlanValidator.Validate(transcription, plan);

            return Task.FromResult(plan);
        }
    }

    private static float SoftmaxProbability(float[] logits, int index)
    {
        var max = logits.Max();
        double sum = 0;
        foreach (var value in logits)
            sum += Math.Exp(value - max);
        return (float)(Math.Exp(logits[index] - max) / Math.Max(sum, double.Epsilon));
    }

    private static IntentPlan BuildPlan(string text, string label)
    {
        var normalized = text.Trim().ToLowerInvariant().Replace('ё', 'е');
        var args = new IntentArguments();

        switch (label)
        {
            case "system.get_time":
            case "system.get_date":
            case "system.get_datetime":
            case "system.get_status":
            case "system.get_battery":
            case "network.get_status":
            case "window.get_active":
            case "timer.cancel":
            case "window.show_desktop":
            case "audio.mute":
            case "audio.unmute":
            case "display.gamma_night":
            case "display.gamma_restore":
            case "system.lock":
            case "process.list":
            case "assistant.list_capabilities":
            case "assistant.greeting":
                break;

            case "timer.start":
            {
                if (!TryExtractDuration(normalized, out var seconds))
                    return Clarify("На какое время поставить таймер?");
                args = new IntentArguments
                {
                    DurationSeconds = seconds,
                    Label = $"таймер на {seconds} сек."
                };
                break;
            }

            case "app.open":
            {
                var appId = ResolveApp(normalized);
                if (appId is null)
                    return Clarify("Какое приложение открыть?");
                args = new IntentArguments { AppId = appId };
                break;
            }

            case "url.open":
            {
                var url = ResolveUrl(text);
                if (url is null)
                    return Clarify("Какой именно сайт открыть? Назови адрес или домен.");
                args = new IntentArguments { Url = url };
                break;
            }

            case "audio.set_volume":
            {
                if (!TryExtractNumber(normalized, out var volume) || volume is < 0 or > 100)
                    return Clarify("До какого процента установить громкость?");
                args = new IntentArguments { VolumePercent = volume };
                break;
            }

            case "audio.adjust_volume":
            {
                var delta = ResolveVolumeDelta(normalized);
                if (delta == 0)
                    return Clarify("Сделать громче или тише?");
                args = new IntentArguments { VolumeDelta = delta };
                break;
            }

            case "process.close":
            {
                if (TryExtractProcessIndex(normalized, out var index))
                    args = new IntentArguments { ProcessIndex = index };
                else if (TryResolveProcess(normalized, out var processName))
                    args = new IntentArguments { ProcessName = processName };
                else
                    return Clarify("Какое приложение закрыть? Назови его или сначала попроси показать список процессов.");
                break;
            }

            case "assistant.answer":
                args = new IntentArguments { TextPrompt = text };
                break;

            case "assistant.generate_text":
                args = new IntentArguments { TextPrompt = text };
                break;

            default:
                return Clarify("Пока не знаю такую команду. Скажи «помощь», чтобы услышать список доступных действий.");
        }

        return new IntentPlan
        {
            Actions = [new IntentCommand { Action = label, Arguments = args }]
        };
    }

    private static IntentPlan Clarify(string message) => new() { Clarification = message };

    private static string? ResolveApp(string text)
    {
        if (text.Contains("блокнот")) return "notepad";
        if (text.Contains("калькулятор")) return "calculator";
        if (text.Contains("проводник") || text.Contains("папку")) return "explorer";
        if (text.Contains("браузер")) return "browser";
        if (text.Contains("настройк")) return "settings";
        return null;
    }

    private static string? ResolveUrl(string text)
    {
        var match = Regex.Match(
            text,
            @"(?:https?://)?(?:www\.)?[a-z0-9-]+(?:\.[a-z0-9-]+)+(?:/[a-z0-9/_?&=.%#-]*)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        var value = match.Value;
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && string.IsNullOrEmpty(uri.UserInfo)
                ? uri.ToString()
                : null;
    }

    private static int ResolveVolumeDelta(string text)
    {
        var positive = text.Contains("громче") || text.Contains("прибав")
            || text.Contains("увелич") || text.Contains("добав");
        var negative = text.Contains("тише") || text.Contains("убав")
            || text.Contains("уменьш") || text.Contains("сниз");
        if (positive == negative) return 0;

        var amount = TryExtractNumber(text, out var value) ? Math.Clamp(value, 1, 100) : 10;
        return positive ? amount : -amount;
    }

    private static bool TryExtractDuration(string text, out int seconds)
    {
        seconds = 0;
        var match = Regex.Match(
            text,
            @"(?:таймер\s+(?:на|через)\s+)?(?<value>\d{1,5})\s*(?<unit>секунд\w*|сек\w*|с\b|минут\w*|мин\w*|час\w*)",
            RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["value"].Value, out var amount))
            return false;

        var unit = match.Groups["unit"].Value;
        var multiplier = unit.StartsWith("сек", StringComparison.Ordinal) || unit == "с"
            ? 1 : unit.StartsWith("мин", StringComparison.Ordinal) ? 60 : 3600;
        var total = (long)amount * multiplier;
        if (total is < 1 or > 86400) return false;
        seconds = (int)total;
        return true;
    }

    private static bool TryExtractNumber(string text, out int number)
    {
        number = 0;
        var match = Regex.Match(text, @"\b\d{1,3}\b");
        if (match.Success && int.TryParse(match.Value, out number))
            return true;

        var words = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ноль"] = 0, ["один"] = 1, ["одна"] = 1, ["два"] = 2, ["две"] = 2,
            ["три"] = 3, ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6,
            ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10,
            ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40,
            ["пятьдесят"] = 50, ["шестьдесят"] = 60, ["семьдесят"] = 70,
            ["восемьдесят"] = 80, ["девяносто"] = 90, ["сто"] = 100
        };
        var tokens = Regex.Matches(text, @"\b[а-яё]+\b").Select(x => x.Value).ToArray();
        var found = tokens.Where(words.ContainsKey).Select(x => words[x]).ToArray();
        if (found.Length == 0) return false;
        number = found.Sum();
        return number <= 100;
    }

    private static bool TryExtractProcessIndex(string text, out int index)
    {
        index = 0;
        var match = Regex.Match(
            text,
            @"(?:процесс|приложение)\s+(?:(?:под\s+)?(?:номер|индекс)(?:ом)?\s*)?(?<value>\d{1,2})",
            RegexOptions.CultureInvariant);
        return match.Success
            && int.TryParse(match.Groups["value"].Value, out index)
            && index is >= 1 and <= 30;
    }

    private static bool TryResolveProcess(string text, out string processName)
    {
        var aliases = new (string[] Phrases, string Name)[]
        {
            (["discord", "дискорд"], "Discord"),
            (["steam", "стим"], "steam"),
            (["chrome", "хром", "гугл хром"], "chrome"),
            (["msedge", "edge", "эдж", "майкрософт эдж"], "msedge"),
            (["firefox", "фаерфокс", "файрфокс"], "firefox"),
            (["explorer", "проводник"], "explorer"),
            (["notepad", "блокнот"], "notepad"),
            (["code", "вс код", "визуал студио код"], "Code"),
            (["telegram", "телеграм"], "Telegram"),
            (["obs", "обс"], "obs64")
        };

        foreach (var alias in aliases)
        {
            if (alias.Phrases.Any(text.Contains))
            {
                processName = alias.Name;
                return true;
            }
        }

        processName = string.Empty;
        return false;
    }

    public void Dispose() => _session.Dispose();
}
