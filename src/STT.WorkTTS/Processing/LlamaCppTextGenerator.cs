using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using STT.WorkTTS.Configuration;

namespace STT.WorkTTS.Processing;

public sealed class LlamaCppTextGenerator : IDisposable
{
    private readonly TextGenerationSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly string _rootDirectory;
    private readonly string _healthEndpoint;
    private readonly SemaphoreSlim _startupLock = new(1, 1);

    public LlamaCppTextGenerator(TextGenerationSettings settings, string appDirectory)
    {
        _settings = settings;
        _rootDirectory = FindProjectRoot(appDirectory);
        var endpoint = new Uri(settings.Endpoint);
        _healthEndpoint = $"{endpoint.Scheme}://{endpoint.Authority}/health";
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        };
    }

    public bool Enabled => _settings.Enabled;
    public bool OpenGeneratedText => _settings.OpenGeneratedText;

    public Task<string> AnswerAsync(string prompt, CancellationToken cancellationToken = default) =>
        GenerateAsync(prompt, isDocument: false, cancellationToken);

    public Task<string> GenerateDocumentAsync(string prompt, CancellationToken cancellationToken = default) =>
        GenerateAsync(prompt, isDocument: true, cancellationToken);

    public async Task EnsureServerReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            throw new InvalidOperationException("Генерация Qwen отключена в appsettings.json.");

        if (await IsServerHealthyAsync(cancellationToken))
            return;

        await _startupLock.WaitAsync(cancellationToken);
        try
        {
            if (await IsServerHealthyAsync(cancellationToken))
                return;

            var modelPath = ResolveProjectPath(_settings.ModelPath);
            if (!File.Exists(modelPath))
                throw new FileNotFoundException(
                    $"Не найдена модель Qwen 4B: {modelPath}. Запусти Start.cmd для установки модели.");

            var scriptPath = ResolveProjectPath(_settings.StartupScript);
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Не найден сценарий запуска Qwen: {scriptPath}");

            var serverPath = ResolveServerPath();
            var endpoint = new Uri(_settings.Endpoint);
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _rootDirectory
            };
            foreach (var argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", scriptPath,
                "-ServerPath", serverPath,
                "-Port", endpoint.Port.ToString(),
                "-Background",
                "-StartupTimeoutSeconds", _settings.StartupTimeoutSeconds.ToString()
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить PowerShell для запуска Qwen 4B.");

            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds + 15));
            await process.WaitForExitAsync(startupTimeout.Token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Сценарий запуска Qwen завершился с кодом {process.ExitCode}.");

            if (!await IsServerHealthyAsync(cancellationToken))
                throw new HttpRequestException("Qwen 4B запущен, но endpoint /health не отвечает.");
        }
        finally
        {
            _startupLock.Release();
        }
    }

    private async Task<string> GenerateAsync(
        string prompt,
        bool isDocument,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidDataException("Пустой запрос к генератору Qwen.");

        await EnsureServerReadyAsync(cancellationToken);

        var systemPrompt = isDocument
            ? "Ты — редактор и автор текстов. Создай законченный текст строго по заданию пользователя. Пиши на русском, если не указан другой язык. Соблюдай заданные факты, адресата, стиль, структуру и объём. Не добавляй выдуманные факты. Не упоминай, что ты ИИ. Верни только готовый текст без вступления от себя."
            : "Ты — голосовой помощник Windows. Кратко и точно ответь на вопрос пользователя по-русски, обычно в 1–3 предложениях. Не выдумывай факты. Не используй Markdown, если он не нужен для ответа.";

        var request = new
        {
            model = _settings.ModelAlias,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = prompt }
            },
            temperature = isDocument ? 0.55 : 0.35,
            max_tokens = isDocument ? _settings.MaxDocumentTokens : _settings.MaxAnswerTokens
        };

        using var response = await _httpClient.PostAsJsonAsync(
            _settings.Endpoint,
            request,
            cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Qwen вернул HTTP {(int)response.StatusCode}: {Limit(responseText, 500)}");

        using var document = JsonDocument.Parse(responseText);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("Qwen вернул пустой текст.");

        return StripThinking(content).Trim();
    }

    private async Task<bool> IsServerHealthyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await _httpClient.GetAsync(_healthEndpoint, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private string ResolveProjectPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(_rootDirectory, relativePath));

    private string ResolveServerPath()
    {
        var configured = Environment.ExpandEnvironmentVariables(_settings.ServerPath);
        if (Path.IsPathRooted(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "llama-server.exe");
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(profile, ".docker", "bin", "inference", "llama-server.exe");
        if (File.Exists(fallback))
            return fallback;

        throw new FileNotFoundException(
            "Не найден llama-server.exe. Добавь его в PATH или установи в %USERPROFILE%\\.docker\\bin\\inference.");
    }

    private static string FindProjectRoot(string appDirectory)
    {
        for (var current = new DirectoryInfo(appDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Start.cmd"))
                || Directory.Exists(Path.Combine(current.FullName, ".git"))
                || File.Exists(Path.Combine(current.FullName, "tools", "intent", "Start-IntentModel.ps1")))
                return current.FullName;
        }

        return Path.GetFullPath(appDirectory);
    }

    private static string StripThinking(string content)
    {
        var start = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            var end = content.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                return content[..start].Trim();
            content = content.Remove(start, end + "</think>".Length - start);
            start = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        }

        return content;
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    public void Dispose()
    {
        _httpClient.Dispose();
        _startupLock.Dispose();
    }
}
