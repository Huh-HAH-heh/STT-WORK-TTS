using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace STT.WorkTTS.Processing;

public sealed class WindowsActionExecutor
{
    private const byte VkLeftWindows = 0x5B;
    private const byte VkD = 0x44;
    private const uint KeyUp = 0x0002;

    private readonly TimerService _timers;

    public WindowsActionExecutor(TimerService timers)
    {
        _timers = timers;
    }

    public Task<CommandResult> ExecuteAsync(
        IntentCommand command,
        CancellationToken cancellationToken = default)
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
                "timer.start" => StartTimer(args.DurationSeconds, args.Label),
                "timer.cancel" => CancelTimer(args.TimerId),
                "app.open" => OpenApplication(args.AppId),
                "url.open" => OpenUrl(args.Url),
                "window.show_desktop" => ShowDesktop(),
                "audio.set_volume" => SetVolume(args.VolumePercent),
                "audio.adjust_volume" => AdjustVolume(args.VolumeDelta),
                "audio.mute" => SetMute(true),
                "audio.unmute" => SetMute(false),
                "system.lock" => LockWorkstation(),
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
            ? new CommandResult("Блокирую компьютер.")
            : new CommandResult("Windows не разрешила заблокировать рабочую станцию.");

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

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll", EntryPoint = "LockWorkStation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStationNative();
}
