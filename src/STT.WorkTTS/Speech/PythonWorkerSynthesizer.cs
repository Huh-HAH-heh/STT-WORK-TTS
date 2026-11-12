using System.Diagnostics;
using System.Text;
using System.Text.Json;
using STT.WorkTTS.Audio;
using STT.WorkTTS.Configuration;
using STT.WorkTTS;

namespace STT.WorkTTS.Speech;

/// <summary>
/// Keeps a local Python TTS model alive between utterances.
/// C# owns the application pipeline and audio playback; Python is only an isolated model adapter.
/// </summary>
public sealed class PythonWorkerSynthesizer : ISpeechSynthesizer
{
    private readonly TtsSettings _settings;
    private readonly string _appDirectory;
    private readonly string _engine;
    private readonly string _voice;
    private readonly string _pythonExecutable;
    private readonly string _workerScript;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private Task<string>? _stderrTask;

    public PythonWorkerSynthesizer(TtsSettings settings, string appDirectory)
    {
        _settings = settings;
        _appDirectory = appDirectory;
        _engine = settings.Engine.ToLowerInvariant();
        _voice = settings.Voice;

        var (pythonPath, workerPath) = _engine switch
        {
            "silero" => (settings.SileroPythonExecutable, settings.SileroWorkerScript),
            "xvibe" => (settings.XvibePythonExecutable, settings.XvibeWorkerScript),
            _ => throw new ArgumentException($"Unsupported Python TTS engine: {settings.Engine}")
        };

        _pythonExecutable = AppPaths.Resolve(appDirectory, pythonPath);
        _workerScript = AppPaths.Resolve(appDirectory, workerPath);

        if (!File.Exists(_pythonExecutable))
        {
            var installCommand = _engine == "silero" ? "-Engine silero" : "-Engine xvibe";
            throw new FileNotFoundException(
                $"Не найден Python для {_engine}: {_pythonExecutable}. " +
                $"Установи движок командой: .\\tools\\tts-lab\\Install-TtsLab.ps1 {installCommand}");
        }

        if (!File.Exists(_workerScript))
            throw new FileNotFoundException($"Не найден адаптер TTS: {_workerScript}");
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || (!_settings.PlayAudio && !_settings.SaveSpeech))
            return;

        await _gate.WaitAsync(cancellationToken);
        string? outputPath = null;
        var requestSent = false;

        try
        {
            var outputDirectory = _settings.SaveSpeech
                ? AppPaths.Resolve(_appDirectory, _settings.SpeechDirectory)
                : Path.GetTempPath();
            Directory.CreateDirectory(outputDirectory);
            outputPath = Path.Combine(outputDirectory, $"sttworktts_{Guid.NewGuid():N}.wav");

            await EnsureStartedAsync(cancellationToken);

            var request = JsonSerializer.Serialize(new
            {
                text,
                voice = _voice,
                output_path = outputPath
            });

            var workerTimer = Stopwatch.StartNew();
            await _input!.WriteLineAsync(request.AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
            requestSent = true;

            var responseLine = await _output!.ReadLineAsync(cancellationToken);
            workerTimer.Stop();
            PerformanceLog.Record("tts.worker.synthesize_and_write", workerTimer.Elapsed.TotalMilliseconds, $"engine={_engine};voice={_voice}");
            requestSent = false;
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                var detail = await GetWorkerErrorAsync();
                throw new InvalidOperationException(
                    $"TTS worker {_engine} завершился без ответа. {detail}");
            }

            using var response = JsonDocument.Parse(responseLine);
            var root = response.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var errorValue)
                    ? errorValue.GetString()
                    : "неизвестная ошибка";
                throw new InvalidOperationException($"TTS {_engine}/{_voice}: {error}");
            }

            if (!File.Exists(outputPath))
                throw new InvalidOperationException(
                    $"TTS {_engine} сообщил об успехе, но WAV не создан: {outputPath}");

            if (_settings.PlayAudio)
            {
                using (PerformanceLog.Measure("tts.audio.playback", $"engine={_engine}"))
                    await AudioPlayer.PlayWavAsync(outputPath, cancellationToken);
            }
        }
        catch
        {
            // A cancelled read or malformed response can desynchronise the JSON-lines protocol.
            if (requestSent)
                StopWorker();
            throw;
        }
        finally
        {
            if (!_settings.SaveSpeech && outputPath is not null)
            {
                try { File.Delete(outputPath); }
                catch (IOException) { /* Temporary audio cleanup must not interrupt the app. */ }
            }

            _gate.Release();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
            return;

        StopWorker();

        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonExecutable,
            WorkingDirectory = Path.GetDirectoryName(_workerScript)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(_workerScript);
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Не удалось запустить Python TTS worker: {_engine}.");
        }

        _process = process;
        _input = process.StandardInput;
        _output = process.StandardOutput;
        _stderrTask = PumpStandardErrorAsync(process.StandardError);

        var startupTimer = Stopwatch.StartNew();
        while (true)
        {
            string? readyLine;
            try
            {
                var remaining = TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds) - startupTimer.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException(
                        $"Загрузка TTS {_engine}/{_voice} превысила {_settings.StartupTimeoutSeconds} секунд.");

                readyLine = await _output.ReadLineAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                var detail = await GetWorkerErrorAsync();
                StopWorker();
                throw new TimeoutException(
                    $"Не дождался готовности TTS {_engine}/{_voice} за {_settings.StartupTimeoutSeconds} секунд. " +
                    $"Проверь интернет и логи загрузки модели. {detail}");
            }
            catch
            {
                StopWorker();
                throw;
            }

            if (string.IsNullOrWhiteSpace(readyLine))
            {
                var detail = await GetWorkerErrorAsync();
                StopWorker();
                throw new InvalidOperationException(
                    $"TTS worker {_engine} завершился до готовности. {detail}");
            }

            try
            {
                using var message = JsonDocument.Parse(readyLine);
                var root = message.RootElement;

                if (root.TryGetProperty("status", out var status))
                {
                    Console.WriteLine($"[TTS] {status.GetString()}");
                    continue;
                }

                if (!root.TryGetProperty("ready", out var isReady))
                    throw new InvalidDataException("Worker sent an unknown startup message.");

                if (isReady.GetBoolean())
                    return;

                var error = root.TryGetProperty("error", out var errorValue)
                    ? errorValue.GetString()
                    : "модель не сообщила причину";
                var details = await GetWorkerErrorAsync();
                StopWorker();
                throw new InvalidOperationException($"Не удалось загрузить модель {_engine}: {error}. {details}");
            }
            catch (JsonException ex)
            {
                var detail = await GetWorkerErrorAsync();
                StopWorker();
                throw new InvalidDataException(
                    $"TTS worker {_engine} вернул некорректный ответ при запуске. {detail}", ex);
            }
        }
    }

    private static async Task<string> PumpStandardErrorAsync(StreamReader reader)
    {
        var buffer = new StringBuilder();

        while (await reader.ReadLineAsync() is { } line)
        {
            Console.Error.WriteLine($"[TTS] {line}");
            buffer.AppendLine(line);

            if (buffer.Length > 12000)
                buffer.Remove(0, buffer.Length - 8000);
        }

        return buffer.ToString();
    }

    private async Task<string> GetWorkerErrorAsync()
    {
        if (_stderrTask is null)
            return string.Empty;

        try
        {
            if (!_stderrTask.IsCompleted && _process is { HasExited: true })
                await _stderrTask.WaitAsync(TimeSpan.FromSeconds(2));

            if (_stderrTask.IsCompletedSuccessfully)
            {
                var error = _stderrTask.Result.Trim();
                return error.Length <= 2000 ? error : error[^2000..];
            }
        }
        catch (TimeoutException)
        {
            // The process may still be shutting down.
        }

        return "Подробности смотри в выводе установщика и Python-окружения.";
    }

    private void StopWorker()
    {
        var process = _process;
        _process = null;
        _input = null;
        _output = null;
        _stderrTask = null;

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.WriteLine("{\"shutdown\":true}");
                    process.StandardInput.Flush();
                    process.StandardInput.Close();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }

                if (!process.WaitForExit(2000))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        StopWorker();
        _gate.Dispose();
    }
}
