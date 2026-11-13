using System.Runtime.InteropServices;

namespace STT.WorkTTS.Processing;

public sealed class DisplayGammaController : IDisposable
{
    private const int ChannelLength = 256;
    private const int RampLength = ChannelLength * 3;
    private const double NightGammaExponent = 0.42;
    private const int ReadbackTolerance = 1024;

    private readonly object _sync = new();
    private readonly string _statePath;
    private ushort[]? _originalRamp;
    private bool _nightModeActive;
    private bool _restorationPending;
    private bool _disposed;

    public DisplayGammaController()
    {
        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "STT.WorkTTS");
        _statePath = Path.Combine(stateDirectory, "display-gamma-original.bin");
        RecoverPendingState();
    }

    public string? RecoveryNotice { get; private set; }

    public CommandResult EnableNightMode()
    {
        lock (_sync)
        {
            if (_disposed)
                return new CommandResult("Контроллер гаммы уже закрыт.");

            if (_nightModeActive)
                return new CommandResult("Ночная гамма уже включена. Исходная гамма сохранена.");

            if (_restorationPending)
                return new CommandResult(
                    "Не удалось восстановить предыдущую гамму. Новое изменение не применено.",
                    RecoveryNotice);

            var deviceContext = GetDC(IntPtr.Zero);
            if (deviceContext == IntPtr.Zero)
                return new CommandResult("Не удалось получить контекст основного дисплея для чтения гаммы.");

            try
            {
                var original = new ushort[RampLength];
                if (!GetDeviceGammaRamp(deviceContext, original))
                    return new CommandResult(
                        "Видеодрайвер не поддерживает чтение гамма-таблицы через Windows GDI.");

                try
                {
                    SaveOriginalRamp(original);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new CommandResult(
                        "Не удалось сохранить исходную гамму. Экран не изменён.",
                        ex.Message);
                }

                var boosted = CreateNightRamp(original);
                if (!TrySetAndVerify(deviceContext, boosted, out var error))
                {
                    if (TrySetAndVerify(deviceContext, original, out var restoreError))
                    {
                        DeleteSavedRamp();
                        return new CommandResult(
                            "Видеодрайвер не применил ночную гамму. Исходная гамма оставлена без изменений.",
                            error);
                    }

                    _originalRamp = original;
                    _restorationPending = true;
                    RecoveryNotice = $"Не удалось подтвердить применение гаммы и восстановить исходную таблицу: {restoreError}";
                    return new CommandResult(
                        "Не удалось применить ночную гамму. Исходная таблица сохранена для повторного восстановления.",
                        RecoveryNotice);
                }

                _originalRamp = original;
                _nightModeActive = true;
                RecoveryNotice = null;
                return new CommandResult(
                    "Ночная гамма включена. Текущая гамма сохранена; тёмные полутона заметно осветлены. " +
                    "Скажи «верни обычную гамму», чтобы восстановить исходное изображение.");
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, deviceContext);
            }
        }
    }

    public CommandResult RestoreOriginal()
    {
        lock (_sync)
        {
            if (_disposed && !_nightModeActive && !_restorationPending)
                return new CommandResult("Ночная гамма уже выключена.");

            if (_originalRamp is null)
            {
                if (!TryLoadSavedRamp(out var loaded, out var loadError))
                {
                    if (loadError is not null)
                        return new CommandResult("Не удалось прочитать сохранённую гамму.", loadError);

                    return new CommandResult("Ночная гамма не включена. Исходная гамма уже используется.");
                }

                _originalRamp = loaded;
                _restorationPending = true;
            }

            if (!TryApplyRamp(_originalRamp, out var error))
            {
                _restorationPending = true;
                RecoveryNotice = error;
                return new CommandResult(
                    "Не удалось восстановить исходную гамму. Сохранённая копия оставлена для повторной попытки.",
                    error);
            }

            try
            {
                DeleteSavedRamp();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _restorationPending = true;
                RecoveryNotice = ex.Message;
                return new CommandResult(
                    "Исходная гамма восстановлена, но не удалось удалить резервную копию. При следующем запуске восстановление повторится.",
                    ex.Message);
            }

            _originalRamp = null;
            _nightModeActive = false;
            _restorationPending = false;
            RecoveryNotice = null;
            return new CommandResult("Исходная гамма экрана восстановлена.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            if (_nightModeActive || _restorationPending)
            {
                var result = RestoreOriginal();
                if (result.Warning is not null)
                    Console.Error.WriteLine($"[GAMMA] {result.Answer} {result.Warning}");
            }

            _disposed = true;
        }
    }

    private void RecoverPendingState()
    {
        if (!TryLoadSavedRamp(out var savedRamp, out var error))
        {
            RecoveryNotice = error;
            return;
        }

        if (savedRamp is null)
            return;

        _originalRamp = savedRamp;
        _restorationPending = true;

        if (TryApplyRamp(savedRamp, out var restoreError))
        {
            try
            {
                DeleteSavedRamp();
                _originalRamp = null;
                _restorationPending = false;
                RecoveryNotice = "После предыдущего завершения помощника исходная гамма автоматически восстановлена.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecoveryNotice = $"Гамма восстановлена, но резервную копию не удалось удалить: {ex.Message}";
            }

            return;
        }

        RecoveryNotice = $"Найдена резервная копия гаммы, но автоматическое восстановление не удалось: {restoreError}";
    }

    private void SaveOriginalRamp(ushort[] ramp)
    {
        var directory = Path.GetDirectoryName(_statePath)
            ?? throw new IOException("Не удалось определить каталог резервной копии гаммы.");

        Directory.CreateDirectory(directory);
        var bytes = new byte[ramp.Length * sizeof(ushort)];
        Buffer.BlockCopy(ramp, 0, bytes, 0, bytes.Length);

        var temporaryPath = _statePath + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, _statePath, overwrite: true);
    }

    private bool TryLoadSavedRamp(out ushort[]? ramp, out string? error)
    {
        ramp = null;
        error = null;

        if (!File.Exists(_statePath))
            return false;

        try
        {
            var bytes = File.ReadAllBytes(_statePath);
            if (bytes.Length != RampLength * sizeof(ushort))
            {
                error = "Файл резервной копии гаммы имеет неверный размер и не может быть применён.";
                return false;
            }

            ramp = new ushort[RampLength];
            Buffer.BlockCopy(bytes, 0, ramp, 0, bytes.Length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private void DeleteSavedRamp()
    {
        if (File.Exists(_statePath))
            File.Delete(_statePath);

        var temporaryPath = _statePath + ".tmp";
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
    }

    private static ushort[] CreateNightRamp(ushort[] original)
    {
        var result = new ushort[RampLength];

        for (var channel = 0; channel < 3; channel++)
        {
            var offset = channel * ChannelLength;
            for (var index = 0; index < ChannelLength; index++)
            {
                var normalized = original[offset + index] / 65535.0;
                var brightened = (int)Math.Round(
                    Math.Pow(normalized, NightGammaExponent) * 65535.0);

                // Keep every point within the conservative GDI gamma-ramp range.
                var identity = index * 257;
                var minimum = Math.Max(0, identity - 32768);
                var maximum = Math.Min(65535, identity + 32768);
                result[offset + index] = (ushort)Math.Clamp(brightened, minimum, maximum);
            }
        }

        return result;
    }

    private static bool TryApplyRamp(ushort[] ramp, out string error)
    {
        var deviceContext = GetDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
        {
            error = "Не удалось получить контекст основного дисплея.";
            return false;
        }

        try
        {
            return TrySetAndVerify(deviceContext, ramp, out error);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, deviceContext);
        }
    }

    private static bool TrySetAndVerify(IntPtr deviceContext, ushort[] ramp, out string error)
    {
        var actual = new ushort[RampLength];
        if (!GetDeviceGammaRamp(deviceContext, actual))
        {
            error = "Windows не смогла прочитать текущую гамма-таблицу.";
            return false;
        }

        // A driver may not support setting ramps. If the requested state is already active,
        // treat it as restored instead of failing just because SetDeviceGammaRamp is unavailable.
        if (RampMatches(actual, ramp))
        {
            error = string.Empty;
            return true;
        }

        if (!SetDeviceGammaRamp(deviceContext, ramp))
        {
            error = "Windows или видеодрайвер отклонил гамма-таблицу.";
            return false;
        }

        if (!GetDeviceGammaRamp(deviceContext, actual))
        {
            error = "Windows не смогла прочитать гамма-таблицу после применения.";
            return false;
        }

        if (!RampMatches(actual, ramp))
        {
            error = "Драйвер сообщил об успехе, но фактически не применил запрошенную гамма-таблицу.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool RampMatches(ushort[] actual, ushort[] expected)
    {
        for (var i = 0; i < RampLength; i++)
        {
            if (Math.Abs((int)actual[i] - expected[i]) > ReadbackTolerance)
                return false;
        }

        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDeviceGammaRamp(IntPtr deviceContext, [Out] ushort[] ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDeviceGammaRamp(IntPtr deviceContext, [In] ushort[] ramp);
}
