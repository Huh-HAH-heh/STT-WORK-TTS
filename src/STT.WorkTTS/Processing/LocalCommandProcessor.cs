using System.Globalization;
using System.Text.RegularExpressions;
using STT.WorkTTS.Configuration;

namespace STT.WorkTTS.Processing;

public sealed class LocalCommandProcessor : ICommandProcessor
{
    private readonly List<string> _notes = [];
    private readonly bool _saveNotes;
    private readonly string _notesPath;

    public LocalCommandProcessor(WorkSettings settings, string appDirectory)
    {
        _saveNotes = settings.SaveNotes;
        _notesPath = AppPaths.Resolve(appDirectory, settings.NotesFile);

        if (_saveNotes && File.Exists(_notesPath))
        {
            _notes.AddRange(
                File.ReadAllLines(_notesPath)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0));
        }
    }

    public Task<CommandResult> ProcessAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Process(text));
    }

    public CommandResult Process(string text)
    {
        var command = text.Trim();
        var normalized = command.ToLowerInvariant().Replace('ё', 'е');

        if (normalized.Length == 0)
            return new CommandResult("Не удалось распознать команду. Повтори, пожалуйста.");

        if (IsCapabilityRequest(command))
            return GetCapabilities();

        if (ContainsAny(normalized, "который час", "сколько времени", "текущее время"))
            return new CommandResult($"Сейчас {DateTime.Now:HH:mm}.");

        if (ContainsAny(normalized, "какое сегодня число", "какая сегодня дата", "сегодняшняя дата"))
        {
            var now = DateTime.Now;
            var months = new[]
            {
                "января", "февраля", "марта", "апреля", "мая", "июня",
                "июля", "августа", "сентября", "октября", "ноября", "декабря"
            };
            return new CommandResult($"Сегодня {now.Day} {months[now.Month - 1]} {now.Year} года.");
        }

        var noteMatch = Regex.Match(
            command,
            @"^(?:запомни|сохрани заметку|добавь заметку)\s+(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (noteMatch.Success)
        {
            var note = noteMatch.Groups[1].Value.Trim();
            _notes.Add(note);
            return new CommandResult("Хорошо, заметка добавлена.", SaveNotes());
        }

        if (ContainsAny(normalized, "покажи заметки", "прочитай заметки", "список заметок"))
        {
            if (_notes.Count == 0)
                return new CommandResult("Пока заметок нет.");

            var list = string.Join(". ", _notes.Select((note, index) => $"{index + 1}: {note}"));
            return new CommandResult($"У тебя {_notes.Count} заметок. {list}.");
        }

        if (ContainsAny(normalized, "очисти заметки", "удали заметки"))
        {
            _notes.Clear();
            return new CommandResult("Список заметок очищен.", SaveNotes());
        }

        var calculation = TryCalculate(normalized);
        if (calculation is not null)
            return new CommandResult(calculation);

        return new CommandResult(
            $"Я расслышала: {command}. Пока для этой команды нет отдельного сценария. " +
            "Скажи «перечисли мои возможности», чтобы услышать список доступных действий.");
    }

    public static bool IsCapabilityRequest(string text)
    {
        var value = text.Trim().ToLowerInvariant().Replace('ё', 'е');
        if (ContainsAny(value,
            "помощь", "что ты умеешь", "что ты можешь", "список команд",
            "какие у тебя возможности", "перечисли возможности", "перечисли функции",
            "перечисли мне команды", "перечисли команды", "назови команды",
            "какие команды доступны", "все команды", "расскажи что умеешь",
            "все что ты умеешь", "все твои функции"))
            return true;

        var listRequest = ContainsAny(value, "список", "перечис", "все");
        var abilityRequest = ContainsAny(value, "доступ", "функц", "возмож", "умеешь", "можешь");
        return listRequest && abilityRequest
            || value.Contains("интерес", StringComparison.Ordinal)
                && ContainsAny(value, "все", "доступ", "возмож", "можешь", "умеешь");
    }

    public static CommandResult GetCapabilities() =>
        new(
            "Я могу сообщать текущее время и дату, запускать и отменять таймеры, " +
            "открывать Блокнот, Калькулятор, Проводник, браузер и настройки Windows, " +
            "открывать сайты по адресу, показывать рабочий стол, управлять громкостью, " +
            "включать и выключать звук, получать нумерованный список активных процессов и выбирать процесс по номеру, " +
            "закрывать одно приложение штатным способом с подтверждением, выполнять ограниченный набор команд CMD после подтверждения " +
            "(например, tasklist, ipconfig и taskkill по PID), блокировать компьютер с подтверждением, " +
            "выполнять простые вычисления, сохранять, читать и очищать заметки. " +
            "Например: «открой калькулятор», «покажи активные процессы», «закрой процесс номер три», «закрой Discord», " +
            "«выполни в CMD tasklist /fo csv» или «поставь таймер на пять минут».");

    private string? SaveNotes()
    {
        if (!_saveNotes)
            return null;

        try
        {
            var directory = Path.GetDirectoryName(_notesPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (_notes.Count == 0)
            {
                if (File.Exists(_notesPath))
                    File.Delete(_notesPath);
            }
            else
            {
                File.WriteAllLines(_notesPath, _notes);
            }

            return null;
        }
        catch (IOException ex)
        {
            return $"Не удалось сохранить заметки: {ex.Message}";
        }
    }

    private static string? TryCalculate(string text)
    {
        var value = Regex.Replace(
            text,
            @"^(?:посчитай|вычисли|сколько будет)\s+",
            "",
            RegexOptions.CultureInvariant).Trim();

        var match = Regex.Match(
            value,
            @"^(-?\d+(?:[.,]\d+)?)\s*(плюс|\+|минус|(?<!\w)-|умножить на|умножить|\*|х|разделить на|делить на|/)\s*(-?\d+(?:[.,]\d+)?)$",
            RegexOptions.CultureInvariant);

        if (!match.Success)
            return null;

        if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
            || !double.TryParse(match.Groups[3].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
            return null;

        var operation = match.Groups[2].Value;
        var result = operation switch
        {
            "плюс" or "+" => left + right,
            "минус" or "-" => left - right,
            "умножить на" or "умножить" or "*" or "х" => left * right,
            "разделить на" or "делить на" or "/" => right == 0 ? double.NaN : left / right,
            _ => double.NaN
        };

        if (double.IsNaN(result) || double.IsInfinity(result))
            return "Не получилось выполнить вычисление. Проверь выражение и деление на ноль.";
        if (Math.Abs(result) > 1e12)
            return "Слишком большое число для простого калькулятора.";

        var rendered = Math.Abs(result - Math.Round(result)) < 0.0000001
            ? Math.Round(result).ToString(CultureInfo.InvariantCulture)
            : result.ToString("0.####", CultureInfo.InvariantCulture);

        return $"Получилось {rendered}.";
    }

    private static bool ContainsAny(string text, params string[] phrases) =>
        phrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal));
}
