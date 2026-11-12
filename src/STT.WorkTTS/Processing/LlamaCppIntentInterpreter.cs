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
                  "assistant.list_capabilities", "assistant.greeting",
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
                  "volume_delta": { "type": ["integer", "null"], "minimum": -100, "maximum": 100 }
                },
                "required": [
                  "duration_seconds", "label", "timer_id", "app_id",
                  "url", "volume_percent", "volume_delta"
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
            max_tokens = 384,
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

        try
        {
            return JsonSerializer.Deserialize<IntentPlan>(content, JsonOptions)
                ?? throw new InvalidDataException("Не удалось разобрать план действий.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Нейросеть вернула некорректный JSON: {Limit(content, 500)}", ex);
        }
    }

    private static string BuildSystemPrompt(DateTimeOffset now) =>
        $"""
        Ты — преобразователь русской речи в план действий Windows. Ты не ведёшь беседу и не объясняешь команды.
        Твоя задача — извлечь ИЗ ВСЕЙ фразы ВСЕ запрошенные действия и вернуть JSON по схеме.

        ВАЖНО:
        - Поле actions — массив действий в исходном порядке. Не ограничивайся первым действием.
        - Каждое отдельное действие пользователя — отдельный элемент массива.
        - Не объединяй и не теряй повторные команды. Два таймера — это два timer.start.
        - Слова приветствия, «как дела», вежливые слова и связки не являются действиями: игнорируй их и продолжай разбор.
        - Запрос перечислить команды/функции/возможности добавляй как assistant.list_capabilities, даже вместе с другими действиями.
        - Если фраза содержит несколько независимых просьб, верни их все.
        - Не выдавай clarify только из-за разговорной формы или ошибки распознавания.
        - Заполняй известные аргументы. Неизвестный необязательный аргумент оставляй null.
        - Если одна просьба непонятна, сохрани остальные распознанные действия и добавь одну короткую clarification.
        - Если действий нет, верни пустой actions и clarification=null; не превращай обычное приветствие в уточняющий вопрос.

        Локальное время ПК: {now:yyyy-MM-dd HH:mm:ss zzz}.
        Часовой пояс: {TimeZoneInfo.Local.Id}.

        Доступные сценарии:
        - перечислить команды/возможности/действия -> assistant.list_capabilities.
        - одно приветствие без команд -> assistant.greeting.
        - время -> system.get_time; дата -> system.get_date; оба -> system.get_datetime.
        - таймер на N секунд/минут/часов -> timer.start; duration_seconds = точная длительность, label короткий.
        - отменить таймер -> timer.cancel; если номер не указан, timer_id=null (отменить последний).
        - открыть/запустить Блокнот -> app.open, app_id=notepad.
        - открыть/запустить Калькулятор -> app.open, app_id=calculator.
        - открыть Проводник -> app.open, app_id=explorer.
        - открыть браузер -> app.open, app_id=browser.
        - открыть настройки Windows -> app.open, app_id=settings.
        - открыть сайт/ссылку -> url.open; разрешены только http/https адреса.
        - показать рабочий стол/свернуть окна -> window.show_desktop.
        - установить громкость -> audio.set_volume, volume_percent.
        - громче/тише на N процентов -> audio.adjust_volume, volume_delta.
        - просто «громче»/«тише» -> volume_delta=10/-10.
        - выключить/включить звук -> audio.mute / audio.unmute.
        - заблокировать ПК -> system.lock.
        - расчёт, заметки и чтение заметок, если они не описаны выше, направь в unknown для локального сценария.
        - неизвестное действие -> unknown. Не выдумывай WinAPI-вызов.

        Грамматика команды важнее дословных форм слов. «Включи ещё один таймер на десять секунд» — самостоятельный второй таймер.
        Верни только JSON, без Markdown и пояснений.
        """;

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    public void Dispose() => _httpClient.Dispose();
}
