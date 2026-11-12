using System.Globalization;
using System.Text.RegularExpressions;

namespace STT.WorkTTS.Processing;

public static class CommandScriptParser
{
    private static readonly Regex TimerPattern = new(
        @"(?:(?:поставь|включи|запусти|создай|установи)\s+)?(?:(?:еще|ещё)\s+один\s+)?таймер\s+(?:на|через)\s+(?<number>\d+(?:[.,]\d+)?|[а-яё]+(?:\s+[а-яё]+)?)\s+(?<unit>секунд\w*|сек\w*|с|минут\w*|мин\w*|час\w*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HelpPattern = new(
        @"(?:перечисл\w*|перечислени\w*|покаж\w*|назов\w*|дай|расскаж\w*)\s+(?:(?:мне|пожалуйста|все|всё|свои|доступные|доступных)\s+)*(?:(?:список)\s+)?(?:команд\w*|функц\w*|возможност\w*|действи\w*)|что\s+ты\s+(?:умеешь|можешь)|список\s+(?:всех\s+)?(?:команд\w*|функц\w*|возможност\w*)",
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
        ["сто"] = 100
    };

    public static bool TryParse(string text, out IntentPlan plan)
    {
        var commands = new List<(int Position, IntentCommand Command)>();
        var remainder = text;

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
        foreach (var match in TimerPattern.Matches(text).Cast<Match>().Concat(helpMatch.Success ? [helpMatch] : []))
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
