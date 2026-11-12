namespace STT.WorkTTS.Processing;

public sealed class TimerService : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CancellationTokenSource> _active = [];
    private readonly Action<string> _onElapsed;
    private int _nextId;
    private bool _disposed;

    public TimerService(Action<string> onElapsed)
    {
        _onElapsed = onElapsed;
    }

    public string Start(int durationSeconds, string label)
    {
        if (durationSeconds is < 1 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));

        string id;
        CancellationTokenSource source;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id = Interlocked.Increment(ref _nextId).ToString();
            source = new CancellationTokenSource();
            _active.Add(id, source);
        }

        _ = RunAsync(id, durationSeconds, label, source);
        return id;
    }

    public bool Cancel(string? timerId)
    {
        CancellationTokenSource? source = null;

        lock (_sync)
        {
            if (_disposed || _active.Count == 0)
                return false;

            var id = string.IsNullOrWhiteSpace(timerId)
                ? _active.Keys.Last()
                : timerId;

            if (!_active.Remove(id, out source))
                return false;

            source.Cancel();
            return true;
        }
    }

    public int ActiveCount
    {
        get { lock (_sync) return _active.Count; }
    }

    private async Task RunAsync(
        string id,
        int durationSeconds,
        string label,
        CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds), source.Token);
            _onElapsed($"Таймер «{label}» завершён.");
        }
        catch (OperationCanceledException)
        {
            // A cancelled timer is an expected result.
        }
        finally
        {
            lock (_sync)
                _active.Remove(id);
            source.Dispose();
        }
    }

    public void Dispose()
    {
        List<CancellationTokenSource> sources;

        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            sources = _active.Values.ToList();
            foreach (var source in sources)
                source.Cancel();
            _active.Clear();
        }
    }
}
