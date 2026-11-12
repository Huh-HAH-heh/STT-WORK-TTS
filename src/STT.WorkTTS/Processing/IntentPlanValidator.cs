namespace STT.WorkTTS.Processing;

public static class IntentPlanValidator
{
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "system.get_time", "system.get_date", "system.get_datetime",
        "timer.start", "timer.cancel",
        "app.open", "url.open", "window.show_desktop",
        "audio.set_volume", "audio.adjust_volume", "audio.mute", "audio.unmute",
        "system.lock",
        "assistant.list_capabilities", "assistant.greeting",
        "assistant.answer", "assistant.generate_text",
        "unknown", "clarify"
    };

    public static void Validate(string utterance, IntentPlan plan)
    {
        if (plan.Actions is null || plan.Actions.Count > 8)
            throw new InvalidDataException("План содержит недопустимое число действий.");

        if (plan.Clarification?.Length > 400)
            throw new InvalidDataException("Слишком длинный текст уточнения.");

        var normalized = utterance.ToLowerInvariant().Replace('ё', 'е');
        var unsupportedShellRequest = System.Text.RegularExpressions.Regex.IsMatch(
            normalized,
            @"\b(?:powershell|power\s*shell|пауэр\s*шелл|повер\s*шелл|повар\s*шелл|cmd|командн\w*\s+строк\w*|скрипт\w*|ярлык\w*)\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        foreach (var command in plan.Actions)
        {
            if (command is null || command.Arguments is null)
                throw new InvalidDataException("План содержит пустое действие или аргументы.");

            if (!Actions.Contains(command.Action))
                throw new InvalidDataException($"Неизвестное действие: {command.Action}");

            ValidateArguments(command);
            ValidateGrounding(normalized, command);
        }

        if (unsupportedShellRequest
            && plan.Actions.Count > 0
            && !plan.Actions.Any(x => x.Action is "unknown" or "clarify"))
        {
            throw new InvalidDataException(
                "В запросе есть PowerShell/скрипт/ярлык, но модель не отразила неподдерживаемую часть команды.");
        }
    }

    private static void ValidateArguments(IntentCommand command)
    {
        var args = command.Arguments;

        bool Has(string name) => name switch
        {
            "duration_seconds" => args.DurationSeconds is not null,
            "label" => args.Label is not null,
            "timer_id" => args.TimerId is not null,
            "app_id" => args.AppId is not null,
            "url" => args.Url is not null,
            "volume_percent" => args.VolumePercent is not null,
            "volume_delta" => args.VolumeDelta is not null,
            "text_prompt" => args.TextPrompt is not null,
            _ => false
        };

        bool HasUnexpected(params string[] allowed) =>
            new[] { "duration_seconds", "label", "timer_id", "app_id", "url",
                    "volume_percent", "volume_delta", "text_prompt" }
                .Any(name => Has(name) && !allowed.Contains(name, StringComparer.Ordinal));

        bool IsEmptyExcept(params string[] allowed) => !HasUnexpected(allowed);

        switch (command.Action)
        {
            case "system.get_time":
            case "system.get_date":
            case "system.get_datetime":
            case "window.show_desktop":
            case "audio.mute":
            case "audio.unmute":
            case "system.lock":
            case "assistant.list_capabilities":
            case "assistant.greeting":
            case "unknown":
                Require(IsEmptyExcept(), $"{command.Action} не принимает аргументы.");
                break;

            case "timer.start":
                Require(IsEmptyExcept("duration_seconds", "label"), "У timer.start лишние аргументы.");
                Require(args.DurationSeconds is >= 1 and <= 86400, "У timer.start некорректная длительность.");
                Require(args.Label is null || args.Label.Length <= 80, "Слишком длинная подпись таймера.");
                break;

            case "timer.cancel":
                Require(IsEmptyExcept("timer_id"), "У timer.cancel лишние аргументы.");
                Require(args.TimerId is null || args.TimerId.Length <= 40, "Некорректный идентификатор таймера.");
                break;

            case "app.open":
                Require(IsEmptyExcept("app_id"), "У app.open лишние аргументы.");
                Require(args.AppId is "notepad" or "calculator" or "explorer" or "browser" or "settings",
                    "app.open содержит неизвестное приложение.");
                break;

            case "url.open":
                Require(IsEmptyExcept("url"), "У url.open лишние аргументы.");
                Require(args.Url is not null && args.Url.Length <= 2048
                    && Uri.TryCreate(args.Url, UriKind.Absolute, out var uri)
                    && uri.Host.Length > 0
                    && uri.Scheme is "http" or "https"
                    && string.IsNullOrEmpty(uri.UserInfo),
                    "url.open содержит недопустимый URL.");
                break;

            case "audio.set_volume":
                Require(IsEmptyExcept("volume_percent"), "У audio.set_volume лишние аргументы.");
                Require(args.VolumePercent is >= 0 and <= 100, "Громкость должна быть от 0 до 100.");
                break;

            case "audio.adjust_volume":
                Require(IsEmptyExcept("volume_delta"), "У audio.adjust_volume лишние аргументы.");
                Require(args.VolumeDelta is >= -100 and <= 100 && args.VolumeDelta != 0,
                    "Некорректное изменение громкости.");
                break;

            case "assistant.answer":
            case "assistant.generate_text":
                Require(IsEmptyExcept("text_prompt"), $"{command.Action} содержит лишние аргументы.");
                Require(!string.IsNullOrWhiteSpace(args.TextPrompt) && args.TextPrompt.Length <= 4000,
                    $"{command.Action} должен содержать текст запроса.");
                break;

            case "clarify":
                Require(IsEmptyExcept(), "У clarify не должно быть аргументов.");
                Require(!string.IsNullOrWhiteSpace(command.Clarification),
                    "Для clarify отсутствует текст уточнения.");
                break;
        }

        static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidDataException(message);
        }
    }

    private static void ValidateGrounding(string text, IntentCommand command)
    {
        bool HasAny(params string[] terms) => terms.Any(text.Contains);

        var grounded = command.Action switch
        {
            "system.get_time" => HasAny("врем", "который час", "сколько времени"),
            "system.get_date" => HasAny("дат", "число", "какое сегодня"),
            "system.get_datetime" => HasAny("дат", "врем", "который час"),
            "timer.start" => text.Contains("таймер"),
            "timer.cancel" => text.Contains("таймер") && HasAny("отмен", "останов", "сброс"),
            "app.open" => HasAny("открой", "открыть", "запусти", "запустить", "включи")
                && AppIsMentioned(text, command.Arguments.AppId!),
            "url.open" => HasAny("открой", "открыть", "перейди", "зайди", "сайт", "ссылк", "точка", ".com", ".ru", "www."),
            "window.show_desktop" => HasAny("рабочий стол", "сверни окна", "свернуть окна", "покажи стол"),
            "audio.set_volume" or "audio.adjust_volume" or "audio.mute" or "audio.unmute" =>
                HasAny("громк", "тише", "громче", "звук", "беззвуч", "звук"),
            "system.lock" => HasAny("заблок", "блокиров"),
            "assistant.list_capabilities" => HasAny("команд", "функци", "возможност", "что ты умеешь", "что ты можешь"),
            "assistant.greeting" => HasAny("привет", "здравствуй", "добрый"),
            "assistant.answer" => HasAny("объясни", "расскажи", "почему", "зачем", "что такое", "кто такой", "как работает", "ответь"),
            "assistant.generate_text" => HasAny("напиши", "составь", "подготовь", "создай", "сгенерируй", "сделай текст"),
            "unknown" or "clarify" => true,
            _ => false
        };

        if (!grounded)
            throw new InvalidDataException(
                $"Действие {command.Action} не подтверждается словами исходного запроса.");
    }

    private static bool AppIsMentioned(string text, string appId) => appId switch
    {
        "notepad" => text.Contains("блокнот"),
        "calculator" => text.Contains("калькулятор"),
        "explorer" => text.Contains("проводник") || text.Contains("папк"),
        "browser" => text.Contains("браузер"),
        "settings" => text.Contains("настройк"),
        _ => false
    };
}
