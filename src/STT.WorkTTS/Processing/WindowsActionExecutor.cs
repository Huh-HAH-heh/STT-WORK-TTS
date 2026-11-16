using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace STT.WorkTTS.Processing;

public sealed class WindowsActionExecutor : IDisposable
{
    private const byte VkLeftWindows = 0x5B;
    private const byte VkD = 0x44;
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPreviousTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;
    private const uint KeyUp = 0x0002;

    private readonly TimerService _timers;
    private readonly WindowsProcessManager _processes;
    private readonly ConsoleCommandExecutor _console;
    private readonly PendingConfirmationManager _confirmations = new();
    private readonly DisplayGammaController _displayGamma = new();

    public string? GammaRecoveryNotice => _displayGamma.RecoveryNotice;

    public WindowsActionExecutor(TimerService timers)
    {
        _timers = timers;
        _processes = new WindowsProcessManager();
        _console = new ConsoleCommandExecutor(_processes);
    }

    public async Task<CommandResult> ExecuteAsync(
        IntentCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ActionCatalog.RequiresConfirmation(command.Action))
            return PrepareConfirmation(command);

        return await ExecuteImmediateAsync(command, cancellationToken);
    }

    public async Task<CommandResult?> HandleConfirmationAsync(
        string recognizedText,
        CancellationToken cancellationToken = default)
    {
        var decision = _confirmations.Resolve(recognizedText, out var pending);
        switch (decision)
        {
            case ConfirmationDecision.None:
                return null;
            case ConfirmationDecision.Cancelled:
                return new CommandResult("Действие отменено.");
            case ConfirmationDecision.Expired:
                return new CommandResult("Время подтверждения истекло. Команда не выполнялась.");
            case ConfirmationDecision.Confirmed:
                if (pending is null)
                    return new CommandResult("Подтверждение потеряло связанное действие. Ничего не выполнено.");
                return await ExecuteConfirmedAsync(pending, cancellationToken);
            default:
                return null;
        }
    }

    private CommandResult PrepareConfirmation(IntentCommand command)
    {
        ProcessIdentity? target = null;
        string preview;

        switch (command.Action)
        {
            case "system.lock":
                preview = "WinAPI: user32!LockWorkStation() — заблокировать текущую рабочую станцию.";
                break;

            case "process.close":
            {
                var processIndex = command.Arguments.ProcessIndex;
                var name = command.Arguments.ProcessName;
                string error;

                if (processIndex is int index)
                {
                    if (!_processes.TryGetIndexedProcess(index, out target, out error))
                        return new CommandResult(error);

                    preview = $"Выбран процесс №{index}: {target!.ProcessName} (PID {target.ProcessId}).";
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return new CommandResult("Не указано имя или индекс процесса.");

                    if (!_processes.TryCaptureUniqueByName(name, out target, out error))
                        return new CommandResult(error);

                    preview = $"WinAPI: CloseMainWindow() для {target!.ProcessName} (PID {target.ProcessId}).";
                }
                if (target!.ExecutablePath is not null)
                    preview += $"{Environment.NewLine}Файл: {target.ExecutablePath}";
                preview += $"{Environment.NewLine}Будет отправлен обычный запрос закрытия окна. Принудительного завершения не будет.";
                break;
            }

            case "console.run":
            {
                var commandLine = command.Arguments.CommandLine;
                if (!_console.TryValidate(commandLine ?? string.Empty, out var validation, out var error))
                    return new CommandResult("CMD-команда отклонена.", error);

                target = validation!.TargetProcess;
                preview = $"CMD (буквальная строка, без переписывания):{Environment.NewLine}{commandLine}";
                if (target is not null)
                {
                    preview += $"{Environment.NewLine}Цель: {target.ProcessName} (PID {target.ProcessId}).";
                    if (target.ExecutablePath is not null)
                        preview += $"{Environment.NewLine}Файл: {target.ExecutablePath}";
                }
                if (validation.RiskNotice is not null)
                    preview += $"{Environment.NewLine}{validation.RiskNotice}";
                break;
            }

            default:
                return new CommandResult($"Для действия {command.Action} не настроен сценарий подтверждения.");
        }

        _confirmations.TryCreate(command, preview, target, out var message);
        return new CommandResult(message);
    }

    private async Task<CommandResult> ExecuteConfirmedAsync(
        PendingConfirmation pending,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (pending.Command.Action)
        {
            case "system.lock":
                return LockWorkstation();

            case "process.close":
                if (pending.TargetProcess is null)
                    return new CommandResult("Не найдена сохранённая цель процесса. Ничего не выполнено.");
                return _processes.CloseGracefully(pending.TargetProcess);

            case "console.run":
            {
                var commandLine = pending.Command.Arguments.CommandLine;
                if (!_console.TryValidate(commandLine ?? string.Empty, out var validation, out var error))
                    return new CommandResult("CMD-команда больше не проходит проверку.", error);

                if (pending.TargetProcess is not null)
                {
                    Process? target = null;
                    var targetError = string.Empty;
                    if (validation!.TargetProcess is null
                        || validation.TargetProcess.ProcessId != pending.TargetProcess.ProcessId
                        || !_processes.TryResolveSameProcess(pending.TargetProcess, out target, out targetError))
                    {
                        return new CommandResult(targetError.Length == 0
                            ? "Цель команды изменилась после подтверждения. Действие отменено."
                            : targetError);
                    }

                    target!.Dispose();
                }

                using (PerformanceLog.Measure("cmd.execute", $"command={validation!.CommandName}"))
                    return await _console.ExecuteAsync(commandLine!, cancellationToken);
            }

            default:
                return new CommandResult("Неизвестный тип подтверждённого действия.");
        }
    }

    private Task<CommandResult> ExecuteImmediateAsync(
        IntentCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = command.Arguments;

        try
        {
            var result = command.Action switch
            {
                "system.get_time" => new CommandResult($"Сейчас {DateTime.Now:HH:mm}."),
                "system.get_date" => new CommandResult(FormatDate(DateTime.Now)),
                "system.get_datetime" => new CommandResult(
                    $"{FormatDate(DateTime.Now)} Сейчас {DateTime.Now:HH:mm}."),
                "system.get_status" => GetSystemStatus(),
                "system.get_battery" => GetBatteryStatus(),
                "network.get_status" => GetNetworkStatus(),
                "window.get_active" => GetActiveWindowStatus(),
                "timer.start" => StartTimer(args.DurationSeconds, args.Label),
                "timer.cancel" => CancelTimer(args.TimerId),
                "app.open" => OpenApplication(args.AppId),
                "url.open" => OpenUrl(args.Url),
                "window.show_desktop" => ShowDesktop(),
                "media.play_pause" => SendMediaKey(VkMediaPlayPause, "Переключаю воспроизведение или паузу."),
                "media.next_track" => SendMediaKey(VkMediaNextTrack, "Переключаю на следующий трек."),
                "media.previous_track" => SendMediaKey(VkMediaPreviousTrack, "Возвращаю предыдущий трек."),
                "audio.set_volume" => SetVolume(args.VolumePercent),
                "audio.adjust_volume" => AdjustVolume(args.VolumeDelta),
                "audio.mute" => SetMute(true),
                "audio.unmute" => SetMute(false),
                "display.gamma_night" => _displayGamma.EnableNightMode(),
                "display.gamma_restore" => _displayGamma.RestoreOriginal(),
                "process.list" => _processes.ListProcesses(),
                "assistant.list_capabilities" => LocalCommandProcessor.GetCapabilities(),
                "assistant.greeting" => new CommandResult("Привет. Я готова принимать команды."),
                "clarify" => new CommandResult(
                    string.IsNullOrWhiteSpace(command.Clarification)
                        ? "Уточни, пожалуйста, что именно нужно сделать."
                        : command.Clarification),
                _ => new CommandResult("Я пока не умею выполнять такую команду.")
            };

            return Task.FromResult(result);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            ArgumentException or
            COMException or
            Win32Exception)
        {
            return Task.FromResult(new CommandResult(
                "Не получилось выполнить действие Windows.",
                ex.Message));
        }
    }

    public void Dispose() => _displayGamma.Dispose();

    private CommandResult StartTimer(int? durationSeconds, string? label)
    {
        if (durationSeconds is null or < 1 or > 86400)
            return new CommandResult("Укажи длительность таймера от одной секунды до 24 часов.");

        var title = string.IsNullOrWhiteSpace(label) ? "Таймер" : label.Trim();
        var id = _timers.Start(durationSeconds.Value, title);
        return new CommandResult(
            $"Таймер «{title}» запущен на {FormatDuration(durationSeconds.Value)}. Номер таймера {id}.");
    }

    private CommandResult CancelTimer(string? timerId) =>
        _timers.Cancel(timerId)
            ? new CommandResult("Таймер отменён.")
            : new CommandResult("Активный таймер с таким номером не найден.");

    private static CommandResult OpenApplication(string? appId)
    {
        var target = appId?.ToLowerInvariant() switch
        {
            "notepad" => "notepad.exe",
            "calculator" => "calc.exe",
            "explorer" => "explorer.exe",
            "browser" => "https://www.google.com",
            "settings" => "ms-settings:",
            _ => null
        };

        if (target is null)
            return new CommandResult("Это приложение пока не входит в список разрешённых.");

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });

        return new CommandResult(appId switch
        {
            "notepad" => "Открываю блокнот.",
            "calculator" => "Открываю калькулятор.",
            "explorer" => "Открываю проводник.",
            "browser" => "Открываю браузер.",
            "settings" => "Открываю параметры Windows.",
            _ => "Приложение запущено."
        });
    }

    private static CommandResult OpenUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new CommandResult("Открою только корректные HTTP- или HTTPS-адреса.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });

        return new CommandResult($"Открываю {uri.Host}.");
    }

    private static CommandResult SendMediaKey(byte virtualKey, string message)
    {
        KeybdEvent(virtualKey, 0, 0, UIntPtr.Zero);
        KeybdEvent(virtualKey, 0, KeyUp, UIntPtr.Zero);
        return new CommandResult(message);
    }

    private static CommandResult ShowDesktop()
    {
        KeybdEvent(VkLeftWindows, 0, 0, UIntPtr.Zero);
        KeybdEvent(VkD, 0, 0, UIntPtr.Zero);
        KeybdEvent(VkD, 0, KeyUp, UIntPtr.Zero);
        KeybdEvent(VkLeftWindows, 0, KeyUp, UIntPtr.Zero);
        return new CommandResult("Показываю рабочий стол.");
    }

    private static CommandResult SetVolume(int? percent)
    {
        if (percent is null or < 0 or > 100)
            return new CommandResult("Укажи громкость от 0 до 100 процентов.");

        using var device = GetDefaultAudioDevice();
        device.AudioEndpointVolume.MasterVolumeLevelScalar = percent.Value / 100f;
        return new CommandResult($"Громкость установлена на {percent.Value} процентов.");
    }

    private static CommandResult AdjustVolume(int? delta)
    {
        if (delta is null or < -100 or > 100)
            return new CommandResult("Не удалось определить, на сколько изменить громкость.");

        using var device = GetDefaultAudioDevice();
        var current = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
        var next = Math.Clamp(current + delta.Value, 0f, 100f);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100f;
        return new CommandResult($"Громкость: {Math.Round(next)} процентов.");
    }

    private static CommandResult SetMute(bool muted)
    {
        using var device = GetDefaultAudioDevice();
        device.AudioEndpointVolume.Mute = muted;
        return new CommandResult(muted ? "Звук выключен." : "Звук включён.");
    }

    private static MMDevice GetDefaultAudioDevice()
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    private static CommandResult LockWorkstation() =>
        LockWorkStationNative()
            ? new CommandResult("Рабочая станция заблокирована.")
            : new CommandResult("Windows не разрешила заблокировать рабочую станцию.");

    private static CommandResult GetSystemStatus()
    {
        var memory = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref memory))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var usedMemory = memory.TotalPhysical - memory.AvailablePhysical;
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var driveSummary = GetSystemDriveSummary();

        var details =
            $"Компьютер: {Environment.MachineName}.{Environment.NewLine}" +
            $"ОС: {RuntimeInformation.OSDescription}.{Environment.NewLine}" +
            $"Логических процессоров: {Environment.ProcessorCount}.{Environment.NewLine}" +
            $"ОЗУ: занято {FormatBytes(usedMemory)} из {FormatBytes(memory.TotalPhysical)}, " +
            $"свободно {FormatBytes(memory.AvailablePhysical)} ({memory.MemoryLoad}% занято).{Environment.NewLine}" +
            $"Время работы: {FormatUptime(uptime)}.{driveSummary}";

        return new CommandResult(details);
    }

    private static CommandResult GetBatteryStatus()
    {
        if (!GetSystemPowerStatus(out var status))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if ((status.BatteryFlag & 0x80) != 0)
            return new CommandResult("Windows сообщает, что батарея в этом устройстве не обнаружена. Компьютер, вероятно, работает от сети.");

        var percent = status.BatteryLifePercent == 255
            ? "уровень заряда неизвестен"
            : $"заряд {status.BatteryLifePercent}%";

        var charging = (status.BatteryFlag & 0x08) != 0 ? ", батарея заряжается" : string.Empty;
        var power = status.ACLineStatus switch
        {
            0 => "питание от батареи",
            1 => "подключено питание от сети",
            _ => "источник питания не определён"
        };

        return new CommandResult($"Состояние питания: {power}; {percent}{charging}.");
    }

    private static CommandResult GetNetworkStatus()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up
                    && item.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                    and not NetworkInterfaceType.Tunnel)
                .Select(item =>
                {
                    var addresses = item.GetIPProperties().UnicastAddresses
                        .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(address.Address))
                        .Select(address => address.Address.ToString())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

                    var kind = item.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                        NetworkInterfaceType.Ethernet
                            or NetworkInterfaceType.FastEthernetFx
                            or NetworkInterfaceType.FastEthernetT => "Ethernet",
                        _ => item.NetworkInterfaceType.ToString()
                    };

                    return $"{item.Name} ({kind}): " +
                        (addresses.Length == 0 ? "IPv4-адрес не назначен" : string.Join(", ", addresses));
                })
                .Take(6)
                .ToArray();

            if (interfaces.Length == 0)
                return new CommandResult("Активные сетевые интерфейсы не обнаружены.");

            var header = NetworkInterface.GetIsNetworkAvailable()
                ? "Windows сообщает, что сетевое подключение доступно."
                : "Windows не обнаружила активное сетевое подключение.";

            return new CommandResult(
                header + Environment.NewLine + string.Join(Environment.NewLine, interfaces));
        }
        catch (NetworkInformationException ex)
        {
            return new CommandResult("Не удалось получить сведения о сетевых интерфейсах.", ex.Message);
        }
    }

    private static CommandResult GetActiveWindowStatus()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
            return new CommandResult("Не удалось определить активное окно.");

        var titleLength = Math.Clamp(GetWindowTextLength(handle), 0, 2047);
        var titleBuffer = new StringBuilder(Math.Max(titleLength + 1, 2));
        _ = GetWindowText(handle, titleBuffer, titleBuffer.Capacity);

        _ = GetWindowThreadProcessId(handle, out var processId);
        var processName = "неизвестный процесс";
        if (processId > 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch (ArgumentException)
            {
                processName = "процесс уже завершился";
            }
        }

        var title = string.IsNullOrWhiteSpace(titleBuffer.ToString())
            ? "без заголовка"
            : titleBuffer.ToString();

        return new CommandResult(
            $"Активное окно: {title}.{Environment.NewLine}Процесс: {processName} (PID {processId}).");
    }

    private static string GetSystemDriveSummary()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(root))
                return string.Empty;

            var drive = new DriveInfo(root);
            return !drive.IsReady
                ? string.Empty
                : $"{Environment.NewLine}Диск {root}: свободно {FormatBytes((ulong)drive.AvailableFreeSpace)} " +
                    $"из {FormatBytes((ulong)drive.TotalSize)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string FormatBytes(ulong bytes) =>
        $"{bytes / (1024d * 1024d * 1024d):0.0} ГБ";

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{uptime.Days} дн. {uptime.Hours} ч. {uptime.Minutes} мин.";
        if (uptime.TotalHours >= 1)
            return $"{uptime.Hours} ч. {uptime.Minutes} мин.";
        return $"{Math.Max(0, uptime.Minutes)} мин.";
    }

    private static string FormatDate(DateTime value)
    {
        var months = new[]
        {
            "января", "февраля", "марта", "апреля", "мая", "июня",
            "июля", "августа", "сентября", "октября", "ноября", "декабря"
        };
        return $"Сегодня {value.Day} {months[value.Month - 1]} {value.Year} года.";
    }

    private static string FormatDuration(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        var parts = new List<string>();
        if (span.Hours > 0) parts.Add($"{span.Hours} ч.");
        if (span.Minutes > 0) parts.Add($"{span.Minutes} мин.");
        if (span.Seconds > 0) parts.Add($"{span.Seconds} сек.");
        return string.Join(" ", parts);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll", EntryPoint = "LockWorkStation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStationNative();
}
