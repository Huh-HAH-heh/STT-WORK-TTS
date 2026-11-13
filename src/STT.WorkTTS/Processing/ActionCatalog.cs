namespace STT.WorkTTS.Processing;

public enum ActionBackend
{
    Local,
    WinApi,
    Win32,
    Cmd
}

public enum ActionRisk
{
    ReadOnly,
    Reversible,
    ConfirmationRequired,
    Restricted
}

public sealed record ActionDefinition(
    string Name,
    string Description,
    ActionBackend Backend,
    ActionRisk Risk);

public static class ActionCatalog
{
    private static readonly Dictionary<string, ActionDefinition> Definitions =
        new(StringComparer.Ordinal)
        {
            ["system.get_time"] = new("system.get_time", "Получить локальное время", ActionBackend.Local, ActionRisk.ReadOnly),
            ["system.get_date"] = new("system.get_date", "Получить локальную дату", ActionBackend.Local, ActionRisk.ReadOnly),
            ["system.get_datetime"] = new("system.get_datetime", "Получить дату и время", ActionBackend.Local, ActionRisk.ReadOnly),
            ["timer.start"] = new("timer.start", "Запустить таймер", ActionBackend.Local, ActionRisk.Reversible),
            ["timer.cancel"] = new("timer.cancel", "Отменить таймер", ActionBackend.Local, ActionRisk.Reversible),
            ["app.open"] = new("app.open", "Открыть разрешённое приложение", ActionBackend.Win32, ActionRisk.Reversible),
            ["url.open"] = new("url.open", "Открыть HTTP(S)-адрес", ActionBackend.Win32, ActionRisk.Reversible),
            ["window.show_desktop"] = new("window.show_desktop", "Показать рабочий стол", ActionBackend.Win32, ActionRisk.Reversible),
            ["audio.set_volume"] = new("audio.set_volume", "Установить громкость", ActionBackend.WinApi, ActionRisk.Reversible),
            ["audio.adjust_volume"] = new("audio.adjust_volume", "Изменить громкость", ActionBackend.WinApi, ActionRisk.Reversible),
            ["audio.mute"] = new("audio.mute", "Выключить звук", ActionBackend.WinApi, ActionRisk.Reversible),
            ["audio.unmute"] = new("audio.unmute", "Включить звук", ActionBackend.WinApi, ActionRisk.Reversible),
            ["system.lock"] = new("system.lock", "Заблокировать рабочую станцию", ActionBackend.WinApi, ActionRisk.ConfirmationRequired),
            ["process.list"] = new("process.list", "Получить список процессов", ActionBackend.WinApi, ActionRisk.ReadOnly),
            ["process.close"] = new("process.close", "Корректно закрыть одно приложение", ActionBackend.WinApi, ActionRisk.ConfirmationRequired),
            ["console.run"] = new("console.run", "Выполнить буквальную CMD-команду из речи", ActionBackend.Cmd, ActionRisk.ConfirmationRequired)
        };

    public static IReadOnlyCollection<ActionDefinition> All => Definitions.Values;

    public static bool TryGet(string action, out ActionDefinition definition) =>
        Definitions.TryGetValue(action, out definition!);

    public static bool RequiresConfirmation(string action) =>
        TryGet(action, out var definition)
        && definition.Risk is ActionRisk.ConfirmationRequired or ActionRisk.Restricted;
}
