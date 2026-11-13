using System.Diagnostics;
using System.Text.RegularExpressions;

namespace STT.WorkTTS.Processing;

public sealed record ProcessIdentity(
    int ProcessId,
    string ProcessName,
    long StartTimeUtcTicks,
    string? ExecutablePath);

public sealed class WindowsProcessManager
{
    private const int MaxIndexedProcesses = 30;
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "lsass", "services", "smss",
        "system", "registry", "idle", "svchost"
    };

    private readonly object _snapshotSync = new();
    private Dictionary<int, ProcessIdentity> _indexedProcesses = [];
    private DateTimeOffset _snapshotExpiresAt;

    public CommandResult ListProcesses()
    {
        var rows = new List<(ProcessIdentity Identity, long MemoryMb, string Window)>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var identity = new ProcessIdentity(
                    process.Id,
                    process.ProcessName,
                    process.StartTime.ToUniversalTime().Ticks,
                    TryGetExecutablePath(process));

                rows.Add((
                    identity,
                    process.WorkingSet64 / (1024 * 1024),
                    LimitWindowTitle(SafeWindowTitle(process))));
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or UnauthorizedAccessException)
            {
                // Processes can exit or deny inspection during enumeration.
            }
            finally
            {
                process.Dispose();
            }
        }

        var selected = rows
            .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Window))
            .ThenByDescending(x => x.MemoryMb)
            .ThenBy(x => x.Identity.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxIndexedProcesses)
            .ToArray();

        var snapshot = new Dictionary<int, ProcessIdentity>();
        for (var i = 0; i < selected.Length; i++)
            snapshot[i + 1] = selected[i].Identity;

        lock (_snapshotSync)
        {
            _indexedProcesses = snapshot;
            _snapshotExpiresAt = DateTimeOffset.UtcNow.Add(SnapshotLifetime);
        }

        if (selected.Length == 0)
            return new CommandResult("Не удалось получить доступные для индексации процессы.");

        var lines = selected.Select((row, index) =>
        {
            var protectedMark = IsProtected(row.Identity.ProcessName) ? " [защищён]" : string.Empty;
            var window = string.IsNullOrWhiteSpace(row.Window) ? string.Empty : $" — {row.Window}";
            return $"{index + 1}. {row.Identity.ProcessName} (PID {row.Identity.ProcessId}, {row.MemoryMb} MB){protectedMark}{window}";
        });

        var suffix = rows.Count > selected.Length
            ? $" Показаны первые {selected.Length} из {rows.Count}."
            : string.Empty;

        return new CommandResult(
            $"Активные процессы. Скажи «закрой процесс номер три» или «заверши процесс номер 3». " +
            $"Индексы действуют 2 минуты и относятся только к этому списку.{suffix}{Environment.NewLine}" +
            string.Join(Environment.NewLine, lines));
    }

    public bool TryGetIndexedProcess(
        int index,
        out ProcessIdentity? identity,
        out string message)
    {
        identity = null;
        ProcessIdentity? selected;

        lock (_snapshotSync)
        {
            if (_indexedProcesses.Count == 0 || DateTimeOffset.UtcNow >= _snapshotExpiresAt)
            {
                _indexedProcesses.Clear();
                message = "Список процессов устарел. Скажи «покажи активные процессы» и повтори команду.";
                return false;
            }

            if (!_indexedProcesses.TryGetValue(index, out selected))
            {
                message = $"В последнем списке нет процесса с номером {index}. Обнови список процессов.";
                return false;
            }
        }

        if (selected is null)
        {
            message = "Не удалось получить выбранный процесс. Обнови список.";
            return false;
        }

        if (!TryResolveSameProcess(selected, out var process, out message))
            return false;

        process!.Dispose();
        identity = selected;
        return true;
    }

    public bool TryCaptureUniqueByName(
        string processName,
        out ProcessIdentity? identity,
        out string message)
    {
        identity = null;
        var normalized = Path.GetFileNameWithoutExtension(processName.Trim());
        if (normalized.Length is < 1 or > 64
            || !Regex.IsMatch(normalized, @"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant))
        {
            message = "Имя процесса должно быть точным латинским именем, например Discord или notepad.";
            return false;
        }

        if (IsProtected(normalized))
        {
            message = "Этот системный процесс защищён от закрытия голосовыми командами.";
            return false;
        }

        var processes = Process.GetProcessesByName(normalized);
        try
        {
            if (processes.Length == 0)
            {
                message = $"Процесс «{normalized}» не найден.";
                return false;
            }

            if (processes.Length > 1)
            {
                var ids = string.Join(", ", processes.Select(x => x.Id));
                message = $"Найдено несколько процессов «{normalized}» (PID: {ids}). Уточни конкретный PID или используй индекс из списка; действие не выполнено.";
                return false;
            }

            return TryCapture(processes[0].Id, out identity, out message);
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public bool TryCapture(
        int processId,
        out ProcessIdentity? identity,
        out string message)
    {
        identity = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (IsProtected(process.ProcessName))
            {
                message = "Этот системный процесс защищён от завершения.";
                return false;
            }

            var startTime = process.StartTime.ToUniversalTime().Ticks;
            var path = TryGetExecutablePath(process);
            identity = new ProcessIdentity(process.Id, process.ProcessName, startTime, path);
            message = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            message = $"Процесс с PID {processId} уже завершился или не существует.";
            return false;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or UnauthorizedAccessException)
        {
            message = $"Не удалось безопасно проверить процесс {processId}: {ex.Message}";
            return false;
        }
    }

    public bool TryResolveSameProcess(
        ProcessIdentity expected,
        out Process? process,
        out string message)
    {
        process = null;
        Process? candidate = null;

        try
        {
            // Open once, then verify and operate through the same process handle.
            candidate = Process.GetProcessById(expected.ProcessId);
            var currentName = candidate.ProcessName;

            if (IsProtected(currentName))
            {
                message = "Этот системный процесс защищён от завершения.";
                return false;
            }

            var currentStartTime = candidate.StartTime.ToUniversalTime().Ticks;
            var currentPath = TryGetExecutablePath(candidate);
            if (currentStartTime != expected.StartTimeUtcTicks
                || !string.Equals(currentName, expected.ProcessName, StringComparison.OrdinalIgnoreCase)
                || (expected.ExecutablePath is not null
                    && !string.Equals(currentPath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            {
                message = "Процесс с этим PID изменился после запроса. Повторно найди его; действие отменено.";
                return false;
            }

            process = candidate;
            candidate = null;
            message = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            message = $"Процесс с PID {expected.ProcessId} завершился до выполнения команды.";
            return false;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or UnauthorizedAccessException)
        {
            message = $"Не удалось повторно проверить процесс {expected.ProcessId}: {ex.Message}";
            return false;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    public CommandResult CloseGracefully(ProcessIdentity expected)
    {
        if (!TryResolveSameProcess(expected, out var process, out var error))
            return new CommandResult(error);

        using (process)
        {
            if (!process!.CloseMainWindow())
                return new CommandResult(
                    $"У процесса {expected.ProcessName} (PID {expected.ProcessId}) нет доступного главного окна. Принудительно не завершал.");

            if (process.WaitForExit(2500))
                return new CommandResult($"Окно {expected.ProcessName} (PID {expected.ProcessId}) закрыто.");

            return new CommandResult(
                $"Окну {expected.ProcessName} отправлен запрос на закрытие, но процесс ещё работает. Принудительное завершение не выполнялось.");
        }
    }

    public static bool IsProtected(string processName) =>
        ProtectedProcesses.Contains(Path.GetFileNameWithoutExtension(processName));

    private static string SafeWindowTitle(Process process)
    {
        try { return process.MainWindowTitle; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return string.Empty; }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return null; }
    }

    private static string LimitWindowTitle(string value) =>
        value.Length <= 64 ? value : value[..61] + "...";
}
