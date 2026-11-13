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
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "lsass", "services", "smss",
        "system", "registry", "idle", "svchost"
    };

    public CommandResult ListProcesses()
    {
        var rows = new List<(int Pid, string Name, long MemoryMb, string Window)>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                rows.Add((
                    process.Id,
                    process.ProcessName,
                    process.WorkingSet64 / (1024 * 1024),
                    SafeWindowTitle(process)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Process may exit or deny inspection between enumeration and reading.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (rows.Count == 0)
            return new CommandResult("Не удалось получить список процессов.");

        var lines = rows
            .OrderByDescending(x => x.MemoryMb)
            .Take(25)
            .Select(x => $"{x.Pid,7}  {x.MemoryMb,5} MB  {x.Name}" +
                (string.IsNullOrWhiteSpace(x.Window) ? string.Empty : $" — {x.Window}"));

        return new CommandResult(
            $"Процессы: найдено {rows.Count}, показано до 25 по использованию памяти.\n" +
            string.Join(Environment.NewLine, lines));
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
                message = $"Найдено несколько процессов «{normalized}» (PID: {ids}). Уточни конкретный PID; действие не выполнено.";
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
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
        if (!TryCapture(expected.ProcessId, out var current, out message))
            return false;

        if (current is null
            || current.StartTimeUtcTicks != expected.StartTimeUtcTicks
            || !string.Equals(current.ProcessName, expected.ProcessName, StringComparison.OrdinalIgnoreCase)
            || (expected.ExecutablePath is not null
                && !string.Equals(current.ExecutablePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
        {
            message = "Процесс с этим PID изменился после запроса. Повторно найди его; действие отменено.";
            return false;
        }

        try
        {
            process = Process.GetProcessById(expected.ProcessId);
            return true;
        }
        catch (ArgumentException)
        {
            message = "Процесс завершился до выполнения команды.";
            return false;
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
}
