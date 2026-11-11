using NAudio.Wave;
using STT.WorkTTS.Configuration;
using STT.WorkTTS.Domain;

namespace STT.WorkTTS.Audio;

public sealed class MicrophoneRecorder : IDisposable
{
    private readonly AudioSettings _settings;
    private readonly string _appDirectory;
    private readonly object _sync = new();
    private MemoryStream? _buffer;
    private WaveInEvent? _input;
    private readonly int _maxBytes;

    public bool LimitReached { get; private set; }

    public MicrophoneRecorder(AudioSettings settings, string appDirectory)
    {
        _settings = settings;
        _appDirectory = appDirectory;
        _maxBytes = checked(settings.SampleRate * 2 * settings.MaxRecordingSeconds);
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_input is not null)
                throw new InvalidOperationException("Запись уже запущена.");

            _buffer = new MemoryStream(Math.Min(_maxBytes, 256 * 1024));
            LimitReached = false;

            var input = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(_settings.SampleRate, 16, 1),
                BufferMilliseconds = 100
            };
            input.DataAvailable += OnDataAvailable;
            _input = input;

            try
            {
                input.StartRecording();
            }
            catch
            {
                _input = null;
                _buffer.Dispose();
                _buffer = null;
                input.Dispose();
                throw;
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_sync)
        {
            if (_buffer is null || LimitReached)
                return;

            var remaining = _maxBytes - (int)_buffer.Length;
            var accepted = Math.Min(e.BytesRecorded, remaining);
            accepted -= accepted % 2;

            if (accepted > 0)
                _buffer.Write(e.Buffer, 0, accepted);

            if (_buffer.Length >= _maxBytes)
                LimitReached = true;
        }
    }

    public AudioClip Stop()
    {
        WaveInEvent input;
        byte[] pcm;

        lock (_sync)
            input = _input ?? throw new InvalidOperationException("Запись не запущена.");

        input.StopRecording();

        lock (_sync)
        {
            pcm = _buffer?.ToArray() ?? Array.Empty<byte>();
            _input = null;
            _buffer?.Dispose();
            _buffer = null;
        }

        input.Dispose();

        if (pcm.Length == 0)
            throw new InvalidOperationException("Микрофон не передал аудиоданные.");

        string? savedPath = null;
        if (_settings.SaveRecordings)
        {
            var directory = AppPaths.Resolve(_appDirectory, _settings.RecordingDirectory);
            Directory.CreateDirectory(directory);
            savedPath = Path.Combine(directory, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

            using var writer = new WaveFileWriter(savedPath, new WaveFormat(_settings.SampleRate, 16, 1));
            writer.Write(pcm, 0, pcm.Length);
        }

        return new AudioClip(pcm, _settings.SampleRate, DateTime.Now, savedPath);
    }

    public void Dispose()
    {
        WaveInEvent? input;
        lock (_sync)
        {
            input = _input;
            _input = null;
            _buffer?.Dispose();
            _buffer = null;
        }

        if (input is null)
            return;

        try { input.StopRecording(); }
        catch { /* поток мог уже остановиться */ }
        input.Dispose();
    }
}
