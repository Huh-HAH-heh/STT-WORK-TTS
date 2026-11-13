using System.Text.RegularExpressions;

namespace STT.WorkTTS.Processing;

public sealed record PendingConfirmation(
    IntentCommand Command,
    string Preview,
    ProcessIdentity? TargetProcess,
    DateTimeOffset ExpiresAt);

public enum ConfirmationDecision
{
    None,
    Confirmed,
    Cancelled,
    Expired
}

public sealed class PendingConfirmationManager
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly object _sync = new();
    private PendingConfirmation? _pending;

    public bool TryCreate(
        IntentCommand command,
        string preview,
        ProcessIdentity? targetProcess,
        out string message)
    {
        lock (_sync)
        {
            if (_pending is not null && _pending.ExpiresAt > DateTimeOffset.UtcNow)
            {
                message = "Уже есть ожидающее подтверждение. Сначала скажи «подтверждаю» или «отмена».";
                return false;
            }

            _pending = new PendingConfirmation(
                command,
                preview,
                targetProcess,
                DateTimeOffset.UtcNow.Add(Lifetime));

            message = $"Требуется подтверждение.\n{preview}\nСкажи «подтверждаю» для выполнения или «отмена». Запрос действует 30 секунд.";
            return true;
        }
    }

    public ConfirmationDecision Resolve(string text, out PendingConfirmation? pending)
    {
        lock (_sync)
        {
            pending = null;
            if (_pending is null)
                return ConfirmationDecision.None;

            var now = DateTimeOffset.UtcNow;
            if (_pending.ExpiresAt <= now)
            {
                _pending = null;
                return IsConfirmation(text)
                    ? ConfirmationDecision.Expired
                    : ConfirmationDecision.None;
            }

            if (IsConfirmation(text))
            {
                pending = _pending;
                _pending = null;
                return ConfirmationDecision.Confirmed;
            }

            if (IsCancellation(text))
            {
                _pending = null;
                return ConfirmationDecision.Cancelled;
            }

            // A new command must never inherit approval from the previous request.
            _pending = null;
            return ConfirmationDecision.None;
        }
    }

    private static bool IsConfirmation(string text)
    {
        var value = Normalize(text);
        return value is "да" or "подтверждаю" or "подтверждаю выполнение"
            or "выполняй" or "выполнить" or "yes" or "confirm" or "ок";
    }

    private static bool IsCancellation(string text)
    {
        var value = Normalize(text);
        return value is "нет" or "отмена" or "отменить" or "не надо"
            or "no" or "cancel" or "отбой";
    }

    private static string Normalize(string text) =>
        Regex.Replace(
            text.Trim().ToLowerInvariant().Replace('ё', 'е'),
            @"[\s.!?,]+",
            " ",
            RegexOptions.CultureInvariant).Trim();
}
