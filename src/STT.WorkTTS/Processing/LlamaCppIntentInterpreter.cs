using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using STT.WorkTTS.Configuration;

namespace STT.WorkTTS.Processing;

public sealed class LlamaCppIntentInterpreter : IDisposable
{
    private const string SchemaJson = """
    {
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

    public async Task<IntentCommand> InterpretAsync(
        string transcription,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcription))
            return new IntentCommand { Action = "unknown" };

        var now = DateTimeOffset.Now;
        var systemPrompt = BuildSystemPrompt(now);

        var request = new
        {
            model = _settings.ModelAlias,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = transcription }
            },
            temperature = 0,
            max_tokens = 180,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "windows_voice_intent",
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
            throw new InvalidDataException("Нейросеть вернула пустой ответ.");

        try
        {
            return JsonSerializer.Deserialize<IntentCommand>(content, JsonOptions)
                ?? throw new InvalidDataException("Не удалось разобрать JSON намерения.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Нейросеть вернула некорректный JSON: {Limit(content, 300)}", ex);
        }
    }

    private static string BuildSystemPrompt(DateTimeOffset now) =>
        $"""
        Ты — модуль распознавания намерений голосового помощника Windows.
        Получаешь одну русскую транскрипцию с микрофона. Верни только объект по заданной JSON Schema.

        Локальное время компьютера: {now:yyyy-MM-dd HH:mm:ss zzz}.
        Часовой пояс компьютера: {TimeZoneInfo.Local.Id}.

        Твоя задача — выбрать одно действие и извлечь его аргументы. Не отвечай пользователю обычным текстом.
        Не выдумывай значения. Если запрос неполный или неоднозначный, выбери clarify и задай короткий вопрос.

        Правила:
        - «который час», «сколько времени» -> system.get_time.
        - «какая дата», «какое сегодня число» -> system.get_date.
        - запрос одновременно о дате и времени -> system.get_datetime.
        - «таймер на пять минут», «напомни через 30 секунд» -> timer.start; duration_seconds должен быть точным числом секунд.
        - «отмени таймер» -> timer.cancel. Если указан номер таймера, укажи timer_id.
        - «открой блокнот» -> app.open с app_id=notepad.
        - «открой калькулятор» -> app.open с app_id=calculator.
        - «открой проводник» -> app.open с app_id=explorer.
        - «открой браузер» -> app.open с app_id=browser.
        - «открой настройки Windows» -> app.open с app_id=settings.
        - явный адрес сайта -> url.open с исходным URL. Если схема не указана, добавь https://.
        - «покажи рабочий стол», «сверни все окна» -> window.show_desktop.
        - просьба установить громкость в процентах -> audio.set_volume и volume_percent.
        - «сделай тише/громче на N процентов» -> audio.adjust_volume и volume_delta: отрицательное/положительное число.
        - если пользователь говорит только «сделай тише» или «громче», используй volume_delta=-10 или volume_delta=10.
        - «выключи звук» -> audio.mute; «включи звук» -> audio.unmute.
        - «заблокируй компьютер» -> system.lock.
        - если просят выполнить команду PowerShell, CMD, скрипт, удалить/изменить файлы, выключить/перезагрузить ПК, менять реестр или обходить безопасность, выбери clarify и сообщи, что такое действие пока не разрешено.
        - не превращай вопрос или обсуждение действия в просьбу выполнить его.
        - если фраза не соответствует доступным действиям, выбери unknown.
        - не добавляй пояснения, Markdown или текст вне JSON.
        """;

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    public void Dispose() => _httpClient.Dispose();
}
