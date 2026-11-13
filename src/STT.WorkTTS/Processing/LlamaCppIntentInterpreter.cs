using System.Net.Http.Json;
using System.Text.Json;
using STT.WorkTTS.Configuration;

namespace STT.WorkTTS.Processing;

public sealed class LlamaCppIntentInterpreter : IDisposable
{
    private const string SchemaJson = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "actions": {
          "type": "array",
          "minItems": 0,
          "maxItems": 8,
          "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
              "action": {
                "type": "string",
                "enum": [
                  "system.get_time", "system.get_date", "system.get_datetime",
                  "timer.start", "timer.cancel",
                  "app.open", "url.open",
                  "window.show_desktop",
                  "audio.set_volume", "audio.adjust_volume", "audio.mute", "audio.unmute",
                  "system.lock",
                  "process.list", "process.close",
                  "assistant.list_capabilities", "assistant.greeting",
                  "assistant.answer", "assistant.generate_text",
                  "unknown", "clarify"
                ]
              },
              "arguments": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "duration_seconds": { "type": ["integer", "null"], "minimum": 1, "maximum": 86400 },
                  "label": { "type": ["string", "null"] },
                  "timer_id": { "type": ["string", "null"] },
                  "app_id": {
                    "type": ["string", "null"],
                    "enum": ["notepad", "calculator", "explorer", "browser", "settings", null]
                  },
                  "url": { "type": ["string", "null"] },
                  "volume_percent": { "type": ["integer", "null"], "minimum": 0, "maximum": 100 },
                  "volume_delta": { "type": ["integer", "null"], "minimum": -100, "maximum": 100 },
                  "text_prompt": { "type": ["string", "null"] },
                  "process_name": { "type": ["string", "null"], "minLength": 1, "maxLength": 64, "pattern": "^[A-Za-z0-9_.-]+$" }
                },
                "required": [
                  "duration_seconds", "label", "timer_id", "app_id",
                  "url", "volume_percent", "volume_delta", "text_prompt", "process_name"
                ]
              },
              "clarification": { "type": ["string", "null"] }
            },
            "required": ["action", "arguments", "clarification"]
          }
        },
        "clarification": { "type": ["string", "null"] }
      },
      "required": ["actions", "clarification"]
    }
    """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly IntentSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly JsonElement _schema;

    public LlamaCppIntentInterpreter(IntentSettings settings)
    {
        _settings = settings;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        };

        using var schemaDocument = JsonDocument.Parse(SchemaJson);
        _schema = schemaDocument.RootElement.Clone();
    }

    public async Task<IntentPlan> InterpretAsync(
        string transcription,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcription))
            return new IntentPlan();

        var now = DateTimeOffset.Now;
        var request = new
        {
            model = _settings.ModelAlias,
            messages = new object[]
            {
                new { role = "system", content = BuildSystemPrompt(now) },
                new { role = "user", content = transcription }
            },
            temperature = 0,
            max_tokens = 256,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "windows_action_plan",
                    strict = true,
                    schema = _schema
                }
            }
        };

        using var response = await _httpClient.PostAsJsonAsync(
            _settings.Endpoint,
            request,
            cancellationToken);

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"llama-server вернул HTTP {(int)response.StatusCode}: {Limit(responseText, 500)}");
        }

        using var document = JsonDocument.Parse(responseText);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("Нейросеть вернула пустой план действий.");

        var choice = document.RootElement.GetProperty("choices")[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var finish)
            ? finish.GetString()
            : null;
        if (finishReason == "length")
            throw new InvalidDataException("Модель оборвала план: достигнут лимит выходных токенов.");

        try
        {
            var plan = JsonSerializer.Deserialize<IntentPlan>(content, JsonOptions)
                ?? throw new InvalidDataException("Не удалось разобрать план действий.");
            IntentPlanValidator.Validate(transcription, plan);
            return plan;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Модель вернула некорректный JSON: {Limit(content, 500)}", ex);
        }
    }

    private static string BuildSystemPrompt(DateTimeOffset now) =>
        $"""
        Ты — быстрый маршрутизатор русских голосовых команд Windows. Не общайся и не выполняй действия сам.
        Классифицируй только то, что действительно сказано. Не достраивай неразборчивую речь догадками.
        Одна ясная просьба = одно действие. Не добавляй действий, о которых пользователь не просил.
        Если запрос плохо распознан, содержит неизвестную программу или несколько неясных действий,
        используй unknown/clarify вместо выдумывания времени, приложений, URL или параметров.
        Верни только JSON по схеме. Без рассуждений и Markdown.

        Правила:
        - actions — массив отдельных действий, максимум 8. Не теряй повторные и составные просьбы.
        - Заполняй только известные аргументы; неизвестные значения — null. Все не относящиеся к действию поля должны быть null.
        - Не подставляй "unknown" или "null" строкой: unknown — только action, в arguments все поля null.
        - Не выдумывай время, приложения, ссылки и параметры. Неподдерживаемую системную команду отмечай unknown.
        - Если пользователь явно говорит «выполни в CMD» или «выполни в командной строке», буквальную строку обработает отдельный парсер; не переписывай её в JSON и не подменяй другим действием.
        - Не запускай PowerShell, скрипты, .bat/.ps1 и произвольные ярлыки через intent-план.
        - Короткое уточнение — только когда без него нельзя выбрать действие.
        - Для обычного приветствия используй assistant.greeting; для списка возможностей — assistant.list_capabilities.

        Маршрутизация:
        - Вопрос, просьба объяснить/рассказать/ответить/обсудить -> assistant.answer.
          text_prompt — точный смысл вопроса, без выполнения команды.
        - Просьба написать/составить/подготовить письмо, статью, доклад, заявление,
          сообщение, план или другой самостоятельный текст -> assistant.generate_text.
          text_prompt — полное ТЗ к тексту со всеми указанными деталями и тоном.
        - Не генерируй сам длинный текст в JSON: передай задачу через text_prompt.
        - Время -> system.get_time; дата -> system.get_date; дата и время -> system.get_datetime.
        - Таймер -> timer.start; duration_seconds — точная длительность.
        - Отмена таймера -> timer.cancel; timer_id=null означает последний таймер.
        - Блокнот/Калькулятор/Проводник/браузер/настройки -> app.open с app_id:
          notepad/calculator/explorer/browser/settings.
        - Сайт или ссылка -> url.open; только http/https URL.
        - Рабочий стол -> window.show_desktop.
        - Громкость -> audio.set_volume или audio.adjust_volume; mute/unmute -> audio.mute/audio.unmute.
        - Заблокировать ПК -> system.lock; подтверждение запрашивает исполнитель, не модель.
        - Показать запущенные процессы -> process.list.
        - Корректно закрыть одно приложение -> process.close; process_name — точное имя процесса латиницей без .exe.
          Никогда не заменяй обычное закрытие на принудительный taskkill. При неоднозначной цели используй clarify.
        - Который час сейчас: локальное время ПК {now:yyyy-MM-dd HH:mm:ss zzz}, зона {TimeZoneInfo.Local.Id}.

        Возвращай действия в исходном порядке. Если запрос содержит действие Windows и генерацию текста,
        создай отдельные элементы actions для обоих. Не подменяй генерацию текста обычным чатом.
        """;

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    public void Dispose() => _httpClient.Dispose();
}
