using System.Globalization;
using System.Text.RegularExpressions;

namespace STT.WorkTTS.Processing;

public static class CommandScriptParser
{
    private static readonly Regex ExplicitCmdPattern = new(
        @"(?:выполни(?:ть)?|запусти(?:ть)?|введи|исполн(?:и|ить)|выполняй)\s+(?:команду\s+)?(?:в\s+)?(?:cmd|командн\w*\s+строк\w*|консол\w*)\s*[:：,-]?\s*(?<command>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProcessListPattern = new(
        @"(?:(?:покаж\w*|обнови\w*|вывед\w*|список|перечисл\w*)\s+(?:(?:все|активн\w*|запущенн\w*)\s+)*(?:список\s+)?процесс\w*|(?:активн\w*|запущенн\w*)\s+процесс\w*|tasklist)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProcessCloseByIndexPattern = new(
        @"(?:закрой|закрыть|заверши|завершить|останови|остановить|убей|убить)\s+(?:процесс|приложение|программу)\s+(?:(?:под\s+)?(?:номер(?:ом)?|индекс(?:ом)?)\s*)?(?<index>\d{1,2}|[а-яё]+(?:\s+[а-яё]+)?)\s*[.!?]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TimerPattern = new(
        @"(?:(?:поставь|включи|запусти|создай|установи)\s+)?(?:(?:еще|ещё)\s+один\s+)?таймер\s+(?:на|через)\s+(?<number>\d+(?:[.,]\d+)?|[а-яё]+(?:\s+[а-яё]+)?)\s+(?<unit>секунд\w*|сек\w*|с|минут\w*|мин\w*|час\w*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HelpPattern = new(
        @"(?:перечисл\w*|перечислени\w*|покаж\w*|назов\w*|дай|расскаж\w*)\s+(?:(?:мне|пожалуйста|все|всё|свои|доступные|доступных)\s+)*(?:(?:список)\s+)?(?:команд\w*|функц\w*|возможност\w*|действи\w*)|что\s+ты\s+(?:умеешь|можешь)|список\s+(?:всех\s+)?(?:команд\w*|функц\w*|возможност\w*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled); 
    private static readonly Regex SpokenUrlPattern = new(
        @"(?:(?:открой|открыть|запусти|перейди\s+на|зайди\s+на)\s+)?(?<host>[a-z0-9-]+|ютуб|ютюб|ютьюб|гугл|гитхаб|википедия|реддит)\s+(?:точка|dot)\s+(?<tld>com|ru|org|net|io|dev|info|me|tv)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DirectUrlPattern = new(
        @"(?:(?:открой|открыть|запусти|перейди\s+на|зайди\s+на)\s+)(?<url>(?:[a-z0-9-]+\.)+(?:com|ru|org|net|io|dev|info|me|tv)(?:/[a-z0-9/_?&=.%#-]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);


    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ноль"] = 0, ["один"] = 1, ["одна"] = 1, ["одно"] = 1, ["одну"] = 1,
        ["два"] = 2, ["две"] = 2, ["три"] = 3, ["четыре"] = 4, ["пять"] = 5,
        ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9,
        ["десять"] = 10, ["одиннадцать"] = 11, ["двенадцать"] = 12,
        ["тринадцать"] = 13, ["четырнадцать"] = 14, ["пятнадцать"] = 15,
        ["шестнадцать"] = 16, ["семнадцать"] = 17, ["восемнадцать"] = 18,
        ["девятнадцать"] = 19, ["двадцать"] = 20, ["тридцать"] = 30,
        ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60,
        ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90,
        ["сто"] = 100,
        ["первый"] = 1, ["первого"] = 1, ["первая"] = 1,
        ["второй"] = 2, ["второго"] = 2, ["вторая"] = 2,
        ["третий"] = 3, ["третьего"] = 3, ["третья"] = 3,
        ["четвертый"] = 4, ["четвертого"] = 4, ["четвертая"] = 4,
        ["пятый"] = 5, ["пятого"] = 5, ["пятая"] = 5,
        ["шестой"] = 6, ["шестого"] = 6, ["шестая"] = 6,
        ["седьмой"] = 7, ["седьмого"] = 7, ["седьмая"] = 7,
        ["восьмой"] = 8, ["восьмого"] = 8, ["восьмая"] = 8,
        ["девятый"] = 9, ["девятого"] = 9, ["девятая"] = 9,
        ["десятый"] = 10, ["десятого"] = 10, ["десятая"] = 10
    };

    public static bool TryParse(string text, out IntentPlan plan)
    {
        var cmdMatch = ExplicitCmdPattern.Match(text);
        if (cmdMatch.Success)
        {
            plan = new IntentPlan
            {
                Actions =
                [
                    new IntentCommand
                    {
                        Action = "console.run",
                        Arguments = new IntentArguments
                        {
                            CommandLine = cmdMatch.Groups["command"].Value.Trim()
                        }
                    }
                ]
            };
            return true;
        }

        var processCloseMatch = ProcessCloseByIndexPattern.Match(text.Trim());
        if (processCloseMatch.Success
            && TryParseNumber(processCloseMatch.Groups["index"].Value, out var processIndex)
            && processIndex is >= 1 and <= 30)
        {
            plan = new IntentPlan
            {
                Actions =
                [
                    new IntentCommand
                    {
                        Action = "process.close",
                        Arguments = new IntentArguments { ProcessIndex = processIndex }
                    }
                ]
            };
            return true;
        }

        var processListMatch = ProcessListPattern.Match(text);
        if (processListMatch.Success)
        {
            plan = new IntentPlan
            {
                Actions = [new IntentCommand { Action = "process.list" }]
            };
            return true;
        }

        var commands = new List<(int Position, IntentCommand Command)>();
        var recognizedTimerMatches = new List<Match>();

        foreach (Match match in TimerPattern.Matches(text))
        {
            if (!TryParseNumber(match.Groups["number"].Value, out var amount))
                continue;

            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            var multiplier = unit.StartsWith("сек", StringComparison.Ordinal) || unit == "с"
                ? 1
                : unit.StartsWith("мин", StringComparison.Ordinal) ? 60 : 3600;
            var seconds = amount * multiplier;
            if (seconds is < 1 or > 86400)
                continue;

            recognizedTimerMatches.Add(match);
            commands.Add((match.Index, new IntentCommand
            {
                Action = "timer.start",
                Arguments = new IntentArguments
                {
                    DurationSeconds = seconds,
                    Label = $"таймер на {FormatDuration(seconds)}"
                }
            }));
        }

        var helpMatch = HelpPattern.Match(text);
        if (helpMatch.Success)
        {
            commands.Add((helpMatch.Index, new IntentCommand
            {
                Action = "assistant.list_capabilities"
            }));
        }

        var recognizedUrlMatches = new List<Match>();
        foreach (Match match in SpokenUrlPattern.Matches(text))
        {
            var host = NormalizeHost(match.Groups["host"].Value);
            var tld = match.Groups["tld"].Value.ToLowerInvariant();
            var url = $"https://{host}.{tld}";
            recognizedUrlMatches.Add(match);
            commands.Add((match.Index, new IntentCommand
            {
                Action = "url.open",
                Arguments = new IntentArguments { Url = url }
            }));
        }

        foreach (Match match in DirectUrlPattern.Matches(text))
        {
            if (recognizedUrlMatches.Any(x => x.Index == match.Index))
                continue;

            var value = match.Groups["url"].Value;
            recognizedUrlMatches.Add(match);
            commands.Add((match.Index, new IntentCommand
            {
                Action = "url.open",
                Arguments = new IntentArguments { Url = $"https://{value}" }
            }));
        }

        if (commands.Count == 0)
        {
            if (IsGreetingOnly(text))
            {
                plan = new IntentPlan
                {
                    Actions = [new IntentCommand { Action = "assistant.greeting" }]
                };
                return true;
            }

            plan = new IntentPlan();
            return false;
        }

        // Remove recognized operations, then accept only harmless greeting/filler around them.
        var chars = text.ToCharArray();
        foreach (var match in recognizedTimerMatches)
        {
            for (var i = match.Index; i < match.Index + match.Length; i++)
                chars[i] = ' ';
        }
        if (helpMatch.Success)
        {
            for (var i = helpMatch.Index; i < helpMatch.Index + helpMatch.Length; i++)
                chars[i] = ' ';
        }
        foreach (var match in recognizedUrlMatches)
        {
            for (var i = match.Index; i < match.Index + match.Length; i++)
                chars[i] = ' ';
        }

        var residue = new string(chars).ToLowerInvariant().Replace('ё', 'е');
        residue = Regex.Replace(
            residue,
            @"\b(?:привет|здравствуй\w*|добрый|день|вечер|утро|как|дела|пожалуйста|и|а|еще|один|мне|все|свои|команда|команды)\b",
            " ",
            RegexOptions.CultureInvariant);
        residue = Regex.Replace(residue, @"[\s,;.!?]+", "");
        if (residue.Length > 0)
        {
            plan = new IntentPlan();
            return false;
        }

        plan = new IntentPlan
        {
            Actions = commands.OrderBy(x => x.Position).Select(x => x.Command).ToList()
        };
        return true;
    }

    private static string NormalizeHost(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "ютуб" or "ютюб" or "ютьюб" => "www.youtube",
            "гугл" => "www.google",
            "гитхаб" => "github",
            "википедия" => "ru.wikipedia",
            "реддит" => "www.reddit",
            _ => value.Trim().ToLowerInvariant()
        };

    private static bool IsGreetingOnly(string text)
    {
        var value = text.ToLowerInvariant().Replace('ё', 'е');
        value = Regex.Replace(
            value,
            @"\b(?:привет|здравствуй\w*|добрый|день|вечер|утро|как|дела|ты|там|эй|алло|пожалуйста)\b",
            " ",
            RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"[\s,;.!?]+", "");
        return value.Length == 0 && text.Trim().Length > 0;
    }

    private static bool TryParseNumber(string value, out int result)
    {
        value = value.Trim().ToLowerInvariant().Replace('ё', 'е');
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            return true;

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        result = 0;
        foreach (var word in words)
        {
            if (!NumberWords.TryGetValue(word, out var number))
            {
                result = 0;
                return false;
            }

            result += number;
        }

        return words.Length > 0;
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds % 3600 == 0) return $"{seconds / 3600} ч.";
        if (seconds % 60 == 0) return $"{seconds / 60} мин.";
        return $"{seconds} сек.";
    }
}
