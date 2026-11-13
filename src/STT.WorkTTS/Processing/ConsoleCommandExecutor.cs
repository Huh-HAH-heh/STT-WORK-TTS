using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace STT.WorkTTS.Processing;

public sealed record ConsoleCommandValidation(
    string CommandName,
    ProcessIdentity? TargetProcess,
    bool Force,
    bool KillTree,
    string? RiskNotice);

public sealed class ConsoleCommandExecutor
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "tasklist", "taskkill", "ipconfig", "whoami", "ver", "systeminfo",
        "ping", "tracert", "nslookup", "where", "dir", "type", "cd", "echo",
        "hostname", "getmac", "netstat", "driverquery"
    };

    private readonly WindowsProcessManager _processes;

    public ConsoleCommandExecutor(WindowsProcessManager processes)
    {
        _processes = processes;
    }

    public bool TryValidate(
        string commandLine,
        out ConsoleCommandValidation? validation,
        out string message)
    {
        validation = null;
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Length > 1024)
        {
            message = "CMD-команда пустая или длиннее 1024 символов.";
            return false;
        }

        if (commandLine.Any(c => c is '&' or '|' or '<' or '>' or '^' or '%' or '!' or ';'
            or '(' or ')' or '\r' or '\n' or '\0'))
        {
            message = "Командные операторы, перенаправление, переменные и цепочки команд пока запрещены. Передай одну команду без &, |, > и подобных операторов.";
            return false;
        }

        var match = Regex.Match(
            commandLine,
            @"^\s*(?<name>[A-Za-z0-9_.-]+)(?:\s|$)",
            RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            message = "Не удалось безопасно определить имя CMD-команды.";
            return false;
        }

        var commandName = Path.GetFileNameWithoutExtension(match.Groups["name"].Value);
        if (!AllowedCommands.Contains(commandName))
        {
            message = $"Команда «{commandName}» пока не входит в разрешённый список CMD. PowerShell и запуск скриптов через этот режим отключены.";
            return false;
        }

        if (string.Equals(commandName, "taskkill", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryValidateTaskKill(commandLine, out var target, out var force, out var tree, out message))
                return false;

            var risk = force
                ? tree
                    ? "Будет принудительно завершён процесс и его дочерние процессы."
                    : "Будет принудительно завершён процесс."
                : "Windows отправит запрос на завершение процесса.";
            validation = new ConsoleCommandValidation(commandName, target, force, tree, risk);
            return true;
        }

        validation = new ConsoleCommandValidation(commandName, null, false, false, null);
        return true;
    }

    public async Task<CommandResult> ExecuteAsync(
        string commandLine,
        CancellationToken cancellationToken = default)
    {
        var shell = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(shell) || !File.Exists(shell))
            shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"chcp 65001>nul & {commandLine}");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new CommandResult("Не удалось запустить CMD.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }

                if (cancellationToken.IsCancellationRequested)
                    throw;

                return new CommandResult("CMD-команда остановлена: превышено ограничение в 20 секунд.");
            }

            var output = await outputTask;
            var error = await errorTask;
            output = Limit(output.Trim(), 10000);
            error = Limit(error.Trim(), 3000);

            var details = string.Join(
                Environment.NewLine,
                new[] { output, error }.Where(x => !string.IsNullOrWhiteSpace(x)));

            var answer = process.ExitCode == 0
                ? $"CMD завершил команду (код 0).{Environment.NewLine}{details}"
                : $"CMD завершил команду с кодом {process.ExitCode}.{Environment.NewLine}{details}";

            return new CommandResult(answer.Trim(), process.ExitCode == 0 ? null : "Команда завершилась с ненулевым кодом.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new CommandResult("Не удалось запустить CMD.", ex.Message);
        }
    }

    private bool TryValidateTaskKill(
        string commandLine,
        out ProcessIdentity? target,
        out bool force,
        out bool tree,
        out string message)
    {
        target = null;
        force = false;
        tree = false;
        message = string.Empty;

        var tokens = Regex.Matches(commandLine, @"""[^""]*""|\S+")
            .Cast<Match>()
            .Select(x => x.Value.Trim('"'))
            .ToArray();

        if (tokens.Length < 3 || !tokens[0].Equals("taskkill", StringComparison.OrdinalIgnoreCase))
        {
            message = "Для taskkill требуется точный PID: taskkill /PID 1234. Завершение по имени процесса запрещено из-за неоднозначности.";
            return false;
        }

        int? pid = null;
        for (var i = 1; i < tokens.Length; i++)
        {
            if (tokens[i].Equals("/PID", StringComparison.OrdinalIgnoreCase))
            {
                if (pid is not null || i + 1 >= tokens.Length
                    || !int.TryParse(tokens[++i], out var parsedPid) || parsedPid <= 0)
                {
                    message = "Укажи один корректный PID после /PID.";
                    return false;
                }

                pid = parsedPid;
            }
            else if (tokens[i].Equals("/F", StringComparison.OrdinalIgnoreCase))
            {
                if (force) { message = "Параметр /F указан повторно."; return false; }
                force = true;
            }
            else if (tokens[i].Equals("/T", StringComparison.OrdinalIgnoreCase))
            {
                if (tree) { message = "Параметр /T указан повторно."; return false; }
                tree = true;
            }
            else
            {
                message = $"Параметр taskkill «{tokens[i]}» запрещён. Поддерживаются только /PID, /F и /T.";
                return false;
            }
        }

        if (pid is null)
        {
            message = "Для taskkill требуется PID. Вариант /IM по имени процесса отключён.";
            return false;
        }

        if (!_processes.TryCapture(pid.Value, out target, out message))
            return false;

        return true;
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + Environment.NewLine + "... вывод обрезан";
}
