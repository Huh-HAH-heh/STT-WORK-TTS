using System.Diagnostics;
using System.Text;
using STT.WorkTTS.Audio;
using STT.WorkTTS.Configuration;

namespace STT.WorkTTS.Speech;

public sealed class PiperCliSynthesizer : ISpeechSynthesizer
{
    private readonly TtsSettings _settings;
    private readonly string _appDirectory;

    public PiperCliSynthesizer(TtsSettings settings, string appDirectory)
    {
        _settings = settings;
        _appDirectory = appDirectory;
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || (!_settings.PlayAudio && !_settings.SaveSpeech))
            return;

        var executable = AppPaths.Resolve(_appDirectory, _settings.PiperExecutable);
        var model = AppPaths.Resolve(_appDirectory, _settings.PiperModel);

        if (!File.Exists(executable))
            throw new FileNotFoundException($"Не найден Piper CLI: {executable}");
        if (!File.Exists(model) || !File.Exists(model + ".json"))
            throw new FileNotFoundException($"Не найдена голосовая модель Piper: {model}");

        var outputDirectory = _settings.SaveSpeech
            ? AppPaths.Resolve(_appDirectory, _settings.SpeechDirectory)
            : Path.GetTempPath();

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, $"sttworktts_{Guid.NewGuid():N}.wav");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add("--output_file");
            startInfo.ArgumentList.Add(outputPath);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить Piper.");

            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteLineAsync(text.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);

            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Piper завершился с кодом {process.ExitCode}: {error.Trim()}");
            if (!File.Exists(outputPath))
                throw new InvalidOperationException("Piper завершился, но не создал WAV-файл.");

            if (_settings.PlayAudio)
                await AudioPlayer.PlayWavAsync(outputPath, cancellationToken);
        }
        finally
        {
            if (!_settings.SaveSpeech)
            {
                try { File.Delete(outputPath); }
                catch (IOException) { /* временный файл не должен ломать работу приложения */ }
            }
        }
    }

    public void Dispose() { }
}