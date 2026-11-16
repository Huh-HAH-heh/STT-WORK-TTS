using System.Text.Json;
using System.Text.RegularExpressions;

namespace STT.WorkTTS.Processing;

/// <summary>
/// Lightweight semantic backstop for low-confidence classifier results.
/// It compares normalized intent examples, not whole-command string matches.
/// </summary>
internal sealed class IntentExampleMatcher
{
    private static readonly Regex WordPattern = new(
        @"[a-z0-9]+|[а-яё]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "а", "и", "или", "в", "во", "на", "над", "под", "к", "ко", "с", "со",
        "по", "для", "из", "у", "мне", "меня", "мой", "моя", "мое", "мои",
        "пожалуйста", "сейчас", "компьютер", "компьютера", "windows",
        "ну", "же", "бы", "ли", "то", "это", "вот"
    };

    private static readonly string[] RussianEndings =
    [
        "иями", "ями", "ами", "ого", "его", "ому", "ему", "ыми", "ими",
        "ая", "яя", "ое", "ее", "ые", "ие", "ый", "ий", "ой", "ую", "юю",
        "ать", "ять", "ить", "еть", "ешь", "ишь", "ете", "ите", "али", "яли",
        "или", "ела", "ала", "ила", "ли", "ла", "ло", "ом", "ем", "ов", "ев",
        "ам", "ям", "ах", "ях", "а", "я", "ы", "и", "у", "ю", "е"
    ];

    private readonly List<Example> _examples = [];

    public IntentExampleMatcher(string datasetPath)
    {
        if (!File.Exists(datasetPath))
            return;

        foreach (var line in File.ReadLines(datasetPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var text = root.GetProperty("text").GetString();
                var label = root.GetProperty("label").GetString();

                if (string.IsNullOrWhiteSpace(text)
                    || string.IsNullOrWhiteSpace(label)
                    || label is "unknown" or "clarify")
                    continue;

                var tokens = Tokenize(text);
                if (tokens.Length >= 2)
                    _examples.Add(new Example(label, tokens));
            }
            catch (JsonException)
            {
                // A malformed training row must not prevent the assistant from starting.
            }
            catch (KeyNotFoundException)
            {
                // Ignore incomplete rows; the classifier remains available.
            }
        }
    }

    public bool TryResolve(string utterance, out string label, out float similarity)
    {
        if (TryResolveExplicitNetworkIntent(utterance, out label))
        {
            similarity = 1f;
            return true;
        }

        return TryResolveCore(utterance, 0.58f, 0.12f, out label, out similarity);
    }

    public bool TryResolveFast(string utterance, out string label, out float similarity)
    {
        if (TryResolveExplicitNetworkIntent(utterance, out label))
        {
            similarity = 1f;
            return ActionCatalog.TryGet(label, out _);
        }

        if (!TryResolveCore(utterance, 0.95f, 0.20f, out label, out similarity))
            return false;

        // Only execute fast matches that map to a real, registered command.
        // Conversational labels still go through the regular intent pipeline.
        return ActionCatalog.TryGet(label, out _);
    }

    public string GetSuggestions(string utterance) =>
        "Не удалось уверенно сопоставить фразу с доступной командой. Ничего не выполнено. " +
        "Повтори запрос короче или произнеси «помощь» для списка возможностей.";

    private static bool TryResolveExplicitNetworkIntent(string utterance, out string label)
    {
        var text = utterance.ToLowerInvariant().Replace('ё', 'е');
        bool Has(params string[] terms) => terms.Any(text.Contains);

        var hasPhoneTarget = Has("для телефона", "с телефона", "на телефоне", "смартфон", "мобильн", "мобил");
        var hasDockerContext = Has("докер", "docker", "контейнер");
        var hasAddressContext = Has("адрес", "ip", "айпи", "я и пи", "сайт", "ссылк", "порт", "доступ");

        if ((hasPhoneTarget || hasDockerContext) && hasAddressContext)
        {
            label = "network.get_phone_access";
            return true;
        }

        var hasInterface = Has("интерфейс");
        var hasNetworkContext = Has("сет", "сеть", "ip", "айпи", "я и пи", "wi-fi", "wifi", "вайфай");
        var hasAddress = Has("адрес", "подключ", "ip", "айпи", "я и пи", "wi-fi", "wifi", "вайфай");

        if (hasNetworkContext && (hasInterface || hasAddress))
        {
            label = "network.get_status";
            return true;
        }

        label = string.Empty;
        return false;
    }

    private bool TryResolveCore(
        string utterance,
        float minimumSimilarity,
        float minimumMargin,
        out string label,
        out float similarity)
    {
        label = string.Empty;
        similarity = 0;

        var query = Tokenize(utterance);
        if (query.Length < 2 || !query.Any(token => !token.StartsWith('$')))
            return false;

        var ranked = _examples
            .GroupBy(example => example.Label, StringComparer.Ordinal)
            .Select(group => new
            {
                Label = group.Key,
                Score = group.Max(example => Similarity(query, example.Tokens))
            })
            .OrderByDescending(item => item.Score)
            .ToArray();

        if (ranked.Length == 0 || ranked[0].Score < minimumSimilarity)
            return false;

        var runnerUp = ranked.Length > 1 ? ranked[1].Score : 0;
        if (ranked[0].Score - runnerUp < minimumMargin)
            return false;

        label = ranked[0].Label;
        similarity = ranked[0].Score;
        return true;
    }

    private static string[] Tokenize(string text)
    {
        var normalized = Regex.Replace(
            text.ToLowerInvariant().Replace('ё', 'е'),
            @"(?<![а-яёa-z0-9])(?:я\s+и\s+пи|ай[\s-]*пи)(?![а-яёa-z0-9])",
            "ip",
            RegexOptions.CultureInvariant);

        return WordPattern.Matches(normalized)
            .Select(match => Canonicalize(match.Value))
            .Where(token => token.Length >= 2 && !StopWords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string Canonicalize(string token)
    {
        if (StartsWithAny(token, "гамм")) return "гамма";
        if (StartsWithAny(token, "яркост")) return "яркость";
        if (StartsWithAny(token, "громк", "звук", "аудио")) return "звук";
        if (StartsWithAny(token, "экран")) return "экран";
        if (StartsWithAny(token, "таймер")) return "таймер";
        if (StartsWithAny(token, "процесс")) return "процесс";
        if (StartsWithAny(token, "блокнот")) return "блокнот";
        if (StartsWithAny(token, "калькулятор")) return "калькулятор";
        if (StartsWithAny(token, "проводник")) return "проводник";
        if (StartsWithAny(token, "браузер")) return "браузер";
        if (StartsWithAny(token, "настройк")) return "настройки";

        if (StartsWithAny(token, "включ", "вруб", "активир", "примен", "давай", "сдела"))
            return "$activate";
        if (StartsWithAny(token, "подним", "увелич", "прибав", "повыс", "поярч", "освет", "светл", "ярче", "погромч", "громче"))
            return "$increase";
        if (StartsWithAny(token, "выключ", "отключ", "выруб", "убер", "сним", "заглуш"))
            return "$disable";
        if (StartsWithAny(token, "верн", "восстанов", "сброс", "возврат", "обычн", "норм", "исходн", "стандарт", "откат"))
            return "$restore";
        if (StartsWithAny(token, "откро", "откры", "запуст", "перейд", "зайд"))
            return "$open";
        if (StartsWithAny(token, "закро", "закры", "заверш", "останов", "убей"))
            return "$close";
        if (StartsWithAny(token, "покаж", "вывед", "перечис", "назов"))
            return "$list";
        if (StartsWithAny(token, "заблок", "блокиру"))
            return "$lock";
        if (StartsWithAny(token, "убав", "уменьш", "сниз", "тише"))
            return "$decrease";
        if (StartsWithAny(token, "постав", "установ", "выстав"))
            return "$set";

        foreach (var ending in RussianEndings)
        {
            if (token.Length > ending.Length + 3 && token.EndsWith(ending, StringComparison.Ordinal))
                return token[..^ending.Length];
        }

        return token;
    }

    private static bool StartsWithAny(string value, params string[] prefixes) =>
        prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal));

    private static float Similarity(string[] left, string[] right)
    {
        var matchedRight = new bool[right.Length];
        var matches = 0;

        foreach (var token in left)
        {
            var bestIndex = -1;
            var bestScore = 0f;

            for (var i = 0; i < right.Length; i++)
            {
                if (matchedRight[i])
                    continue;

                var score = TokenSimilarity(token, right[i]);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0 && bestScore >= 0.76f)
            {
                matchedRight[bestIndex] = true;
                matches++;
            }
        }

        if (matches == 0)
            return 0;

        var precision = (float)matches / left.Length;
        var recall = (float)matches / right.Length;
        return 2 * precision * recall / (precision + recall);
    }

    private static float TokenSimilarity(string left, string right)
    {
        if (left == right)
            return 1;

        if (left.StartsWith('$') || right.StartsWith('$'))
            return 0;

        if (Math.Abs(left.Length - right.Length) > 2)
            return 0;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        var distance = previous[right.Length];
        return 1f - (float)distance / Math.Max(left.Length, right.Length);
    }

    private sealed record Example(string Label, string[] Tokens);
}
