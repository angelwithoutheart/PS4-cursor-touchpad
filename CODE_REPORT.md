# DS4 Cursor — полный код изменённых файлов

Ниже приведены полные версии всех файлов, изменённых при переводе управления на тачпад DualShock 4.

## 1. src\Ds4Cursor\Core\ControllerSnapshot.cs

```csharp
namespace Ds4Cursor.Core;

[Flags]
public enum Ds4Buttons : uint
{
    None = 0,
    Square = 1u << 0,
    Cross = 1u << 1,
    Circle = 1u << 2,
    Triangle = 1u << 3,
    L1 = 1u << 4,
    R1 = 1u << 5,
    L2 = 1u << 6,
    R2 = 1u << 7,
    Share = 1u << 8,
    Options = 1u << 9,
    L3 = 1u << 10,
    R3 = 1u << 11,
    Ps = 1u << 12,
    Touchpad = 1u << 13,
    DpadUp = 1u << 14,
    DpadDown = 1u << 15,
    DpadLeft = 1u << 16,
    DpadRight = 1u << 17
}

/// <summary>
/// Одно касание сенсорной панели DualShock 4.
/// Координаты приведены к матрице 1920 x 942.
/// </summary>
public readonly struct TouchPoint
{
    public readonly bool IsActive;
    public readonly byte TrackId;
    public readonly int X;
    public readonly int Y;

    public TouchPoint(bool isActive, byte trackId, int x, int y)
    {
        IsActive = isActive;
        TrackId = trackId;
        X = x;
        Y = y;
    }

    public static TouchPoint Empty => new(false, 0, 0, 0);
}

/// <summary>
/// Неизменяемый снимок состояния контроллера. Создаётся без выделения памяти в куче.
/// </summary>
public readonly struct ControllerSnapshot
{
    public readonly bool Connected;
    public readonly bool IsBluetooth;
    public readonly Ds4Buttons Buttons;
    public readonly byte BatteryLevel;
    public readonly bool Charging;
    public readonly string DeviceName;
    public readonly TouchPoint Touch1;
    public readonly TouchPoint Touch2;
    public readonly bool IsTouchpadButtonDown;
    public readonly byte L2Value;
    public readonly byte R2Value;

    public ControllerSnapshot(
        bool connected,
        bool isBluetooth,
        Ds4Buttons buttons,
        byte batteryLevel,
        bool charging,
        string deviceName,
        TouchPoint touch1,
        TouchPoint touch2,
        bool isTouchpadButtonDown,
        byte l2Value,
        byte r2Value)
    {
        Connected = connected;
        IsBluetooth = isBluetooth;
        Buttons = buttons;
        BatteryLevel = batteryLevel;
        Charging = charging;
        DeviceName = deviceName;
        Touch1 = touch1;
        Touch2 = touch2;
        IsTouchpadButtonDown = isTouchpadButtonDown;
        L2Value = l2Value;
        R2Value = r2Value;
    }

    public int BatteryPercent => BatteryLevel > 10 ? 100 : BatteryLevel * 10;

    public bool IsTouching => Touch1.IsActive || Touch2.IsActive;

    public static ControllerSnapshot Disconnected => new(
        false,
        false,
        Ds4Buttons.None,
        0,
        false,
        string.Empty,
        TouchPoint.Empty,
        TouchPoint.Empty,
        false,
        0,
        0);
}
```

## 2. src\Ds4Cursor\Core\ControllerManager.cs

```csharp
using System.Diagnostics;
using System.IO;
using System.Threading;
using HidSharp;

namespace Ds4Cursor.Core;

public sealed class ControllerConnectionEventArgs : EventArgs
{
    public ControllerConnectionEventArgs(bool connected, string deviceName, bool isBluetooth)
    {
        Connected = connected;
        DeviceName = deviceName;
        IsBluetooth = isBluetooth;
    }

    public bool Connected { get; }
    public string DeviceName { get; }
    public bool IsBluetooth { get; }
}

/// <summary>
/// Чтение DualShock 4 напрямую через HID (USB и Bluetooth), без DS4Windows.
/// Отдельный фоновый поток, автоматическое переподключение, без событий на каждый отчёт.
/// </summary>
public sealed class ControllerManager : IDisposable
{
    private const int SonyVendorId = 0x054C;
    private const byte ReportUsb = 0x01;
    private const byte ReportBluetooth = 0x11;
    private const int BluetoothOffset = 2;
    private const int BluetoothReportLength = 78;
    private const byte FeatureFullModeReportId = 0x02;
    private const int FeatureFullModeLength = 37;

    private static readonly int[] KnownProductIds = { 0x05C4, 0x09CC, 0x0BA0 };

    private readonly object _lifecycleLock = new();
    private readonly object _streamLock = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Thread? _thread;
    private HidStream? _stream;
    private volatile bool _running;
    private volatile bool _disposed;

    private volatile bool _connected;
    private volatile bool _bluetooth;
    private int _buttons;
    private int _batteryLevel;
    private int _charging;
    private int _touchpadDown;
    private int _l2;
    private int _r2;
    private int _touch1Active;
    private int _touch1Id;
    private int _touch1X;
    private int _touch1Y;
    private int _touch2Active;
    private int _touch2Id;
    private int _touch2X;
    private int _touch2Y;
    private volatile string _deviceName = string.Empty;

    private bool _lastNotifiedConnected;
    private string _lastNotifiedName = string.Empty;
    private long _lastErrorLogTicks;
    private long _lastFullModeRequestTicks;

    public event EventHandler<ControllerConnectionEventArgs>? ConnectionChanged;

    public ControllerSnapshot GetSnapshot()
    {
        var touch1 = new TouchPoint(
            Volatile.Read(ref _touch1Active) != 0,
            (byte)Volatile.Read(ref _touch1Id),
            Volatile.Read(ref _touch1X),
            Volatile.Read(ref _touch1Y));

        var touch2 = new TouchPoint(
            Volatile.Read(ref _touch2Active) != 0,
            (byte)Volatile.Read(ref _touch2Id),
            Volatile.Read(ref _touch2X),
            Volatile.Read(ref _touch2Y));

        return new ControllerSnapshot(
            _connected,
            _bluetooth,
            (Ds4Buttons)(uint)Volatile.Read(ref _buttons),
            (byte)Volatile.Read(ref _batteryLevel),
            Volatile.Read(ref _charging) != 0,
            _deviceName,
            touch1,
            touch2,
            Volatile.Read(ref _touchpadDown) != 0,
            (byte)Volatile.Read(ref _l2),
            (byte)Volatile.Read(ref _r2));
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_running || _disposed)
            {
                return;
            }

            _running = true;
            _thread = new Thread(ReaderLoop)
            {
                IsBackground = true,
                Name = "DS4 HID Reader",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
            AppLog.Write("Служба HID запущена.");
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lifecycleLock)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            try
            {
                _shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Источник уже освобождён.
            }

            thread = _thread;
        }

        lock (_streamLock)
        {
            try
            {
                _stream?.Dispose();
            }
            catch
            {
                // Поток мог быть уже закрыт устройством.
            }

            _stream = null;
        }

        if (thread != null && thread != Thread.CurrentThread)
        {
            try
            {
                thread.Join(1500);
            }
            catch (ThreadStateException)
            {
                // Поток уже завершён.
            }
        }

        lock (_lifecycleLock)
        {
            _thread = null;
        }

        SetDisconnected();
        AppLog.Write("Служба HID остановлена.");
    }

    private void ReaderLoop()
    {
        var token = _shutdown.Token;

        while (!token.IsCancellationRequested && _running)
        {
            HidStream? stream = null;
            try
            {
                var device = FindController();
                if (device == null)
                {
                    SetDisconnected();
                    Wait(token, 1200);
                    continue;
                }

                if (!device.TryOpen(out stream) || stream is null)
                {
                    LogError(new IOException("Не удалось открыть HID-устройство " + device.GetFriendlyName()));
                    Wait(token, 1000);
                    continue;
                }

                lock (_streamLock)
                {
                    if (token.IsCancellationRequested)
                    {
                        stream.Dispose();
                        break;
                    }

                    _stream = stream;
                }

                stream.ReadTimeout = Timeout.Infinite;

                var maxInput = device.GetMaxInputReportLength();
                var bluetooth = LooksLikeBluetooth(device, maxInput);
                if (bluetooth)
                {
                    TryEnableFullMode(stream, force: true);
                }

                var buffer = new byte[Math.Max(Math.Max(maxInput, 64), BluetoothReportLength)];
                var firstReport = true;

                while (!token.IsCancellationRequested && _running)
                {
                    int count;
                    try
                    {
                        count = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                    {
                        break;
                    }

                    if (count <= 0)
                    {
                        continue;
                    }

                    // Bluetooth в простом режиме присылает короткий отчёт 0x01.
                    // Повторно просим расширенный отчёт 0x11 через Feature Report 0x02.
                    if (buffer[0] == ReportUsb && bluetooth && count < 32)
                    {
                        TryEnableFullMode(stream, force: false);
                        continue;
                    }

                    if (!ParseReport(buffer, count))
                    {
                        continue;
                    }

                    if (firstReport)
                    {
                        firstReport = false;
                        SetConnected(device.GetFriendlyName());
                    }
                }
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
            finally
            {
                lock (_streamLock)
                {
                    if (ReferenceEquals(_stream, stream))
                    {
                        _stream = null;
                    }
                }

                try
                {
                    stream?.Dispose();
                }
                catch
                {
                    // Устройство уже отключено.
                }

                SetDisconnected();
            }

            Wait(token, 900);
        }
    }

    private static HidDevice? FindController()
    {
        try
        {
            HidDevice? knownFallback = null;

            foreach (var device in DeviceList.Local.GetHidDevices())
            {
                if (device.VendorID != SonyVendorId ||
                    !IsKnownProduct(device.ProductID) ||
                    device.GetMaxInputReportLength() < 32)
                {
                    continue;
                }

                if (LooksLikeController(GetName(device)))
                {
                    return device;
                }

                knownFallback ??= device;
            }

            return knownFallback;
        }
        catch (Exception ex)
        {
            AppLog.Write("Ошибка перечисления HID-устройств: " + ex.Message);
            return null;
        }
    }

    private static string GetName(HidDevice device)
    {
        try
        {
            return device.GetFriendlyName() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsKnownProduct(int productId)
    {
        foreach (var known in KnownProductIds)
        {
            if (known == productId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeController(string name)
    {
        return name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)
               || name.Contains("DualShock", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Gamepad", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Sony", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBluetooth(HidDevice device, int maxInputReportLength)
    {
        if (maxInputReportLength >= BluetoothReportLength)
        {
            return true;
        }

        try
        {
            var path = device.DevicePath ?? string.Empty;
            return path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                   || path.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void TryEnableFullMode(HidStream stream, bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now - _lastFullModeRequestTicks < Stopwatch.Frequency)
        {
            return;
        }

        _lastFullModeRequestTicks = now;

        try
        {
            var feature = new byte[FeatureFullModeLength];
            feature[0] = FeatureFullModeReportId;
            stream.GetFeature(feature);
            AppLog.Write("Bluetooth: запрошен расширенный отчёт 0x11.");
        }
        catch (Exception ex)
        {
            AppLog.Write("Bluetooth: не удалось включить расширенный режим: " + ex.Message);
        }
    }

    private bool ParseReport(byte[] buffer, int count)
    {
        int offset;
        bool bluetooth;

        if (buffer[0] == ReportUsb)
        {
            offset = 0;
            bluetooth = false;
        }
        else if (buffer[0] == ReportBluetooth)
        {
            offset = BluetoothOffset;
            bluetooth = true;
        }
        else
        {
            return false;
        }

        // Минимальный полный отчёт: системные кнопки, L2/R2, батарея и два касания.
        if (count < offset + 43)
        {
            return false;
        }

        var buttonsByte = buffer[offset + 5];
        var shoulderByte = buffer[offset + 6];
        var systemByte = buffer[offset + 7];
        var l2 = buffer[offset + 8];
        var r2 = buffer[offset + 9];
        var batteryByte = buffer[offset + 30];

        var buttons = ParseButtons(buttonsByte, shoulderByte, systemByte);
        var touchpadDown = (systemByte & 0x02) != 0;
        var touch1 = ParseTouch(buffer, offset + 35);
        var touch2 = ParseTouch(buffer, offset + 39);
        var batteryLevel = (byte)Math.Min(10, batteryByte & 0x0F);
        var charging = (batteryByte & 0x10) != 0;

        Volatile.Write(ref _buttons, (int)buttons);
        Volatile.Write(ref _batteryLevel, batteryLevel);
        Volatile.Write(ref _charging, charging ? 1 : 0);
        Volatile.Write(ref _touchpadDown, touchpadDown ? 1 : 0);
        Volatile.Write(ref _l2, l2);
        Volatile.Write(ref _r2, r2);
        Volatile.Write(ref _touch1Active, touch1.IsActive ? 1 : 0);
        Volatile.Write(ref _touch1Id, touch1.TrackId);
        Volatile.Write(ref _touch1X, touch1.X);
        Volatile.Write(ref _touch1Y, touch1.Y);
        Volatile.Write(ref _touch2Active, touch2.IsActive ? 1 : 0);
        Volatile.Write(ref _touch2Id, touch2.TrackId);
        Volatile.Write(ref _touch2X, touch2.X);
        Volatile.Write(ref _touch2Y, touch2.Y);
        _bluetooth = bluetooth;

        return true;
    }

    private static TouchPoint ParseTouch(byte[] buffer, int offset)
    {
        var b0 = buffer[offset];
        var b1 = buffer[offset + 1];
        var b2 = buffer[offset + 2];
        var b3 = buffer[offset + 3];

        var active = (b0 & 0x80) == 0;
        var id = (byte)(b0 & 0x7F);
        var x = ((b2 & 0x0F) << 8) | b1;
        var y = (b3 << 4) | (b2 >> 4);

        if (active)
        {
            x = Math.Clamp(x, 0, 1919);
            y = Math.Clamp(y, 0, 941);
        }

        return new TouchPoint(active, id, x, y);
    }

    private static Ds4Buttons ParseButtons(byte buttonsByte, byte shoulderByte, byte systemByte)
    {
        var buttons = Ds4Buttons.None;

        if ((buttonsByte & 0x10) != 0) buttons |= Ds4Buttons.Square;
        if ((buttonsByte & 0x20) != 0) buttons |= Ds4Buttons.Cross;
        if ((buttonsByte & 0x40) != 0) buttons |= Ds4Buttons.Circle;
        if ((buttonsByte & 0x80) != 0) buttons |= Ds4Buttons.Triangle;

        switch (buttonsByte & 0x0F)
        {
            case 0:
                buttons |= Ds4Buttons.DpadUp;
                break;
            case 1:
                buttons |= Ds4Buttons.DpadUp | Ds4Buttons.DpadRight;
                break;
            case 2:
                buttons |= Ds4Buttons.DpadRight;
                break;
            case 3:
                buttons |= Ds4Buttons.DpadDown | Ds4Buttons.DpadRight;
                break;
            case 4:
                buttons |= Ds4Buttons.DpadDown;
                break;
            case 5:
                buttons |= Ds4Buttons.DpadDown | Ds4Buttons.DpadLeft;
                break;
            case 6:
                buttons |= Ds4Buttons.DpadLeft;
                break;
            case 7:
                buttons |= Ds4Buttons.DpadUp | Ds4Buttons.DpadLeft;
                break;
        }

        if ((shoulderByte & 0x01) != 0) buttons |= Ds4Buttons.L1;
        if ((shoulderByte & 0x02) != 0) buttons |= Ds4Buttons.R1;
        if ((shoulderByte & 0x04) != 0) buttons |= Ds4Buttons.L2;
        if ((shoulderByte & 0x08) != 0) buttons |= Ds4Buttons.R2;
        if ((shoulderByte & 0x10) != 0) buttons |= Ds4Buttons.Share;
        if ((shoulderByte & 0x20) != 0) buttons |= Ds4Buttons.Options;
        if ((shoulderByte & 0x40) != 0) buttons |= Ds4Buttons.L3;
        if ((shoulderByte & 0x80) != 0) buttons |= Ds4Buttons.R3;

        if ((systemByte & 0x01) != 0) buttons |= Ds4Buttons.Ps;
        if ((systemByte & 0x02) != 0) buttons |= Ds4Buttons.Touchpad;

        return buttons;
    }

    private void SetConnected(string name)
    {
        if (_connected)
        {
            return;
        }

        _connected = true;
        _deviceName = string.IsNullOrWhiteSpace(name) ? "DualShock 4" : name;
        AppLog.Write("Контроллер подключён: " + _deviceName + (_bluetooth ? " (Bluetooth)" : " (USB)"));
        RaiseConnectionChanged(true);
    }

    private void SetDisconnected()
    {
        var wasConnected = _connected;
        _connected = false;
        _bluetooth = false;
        _deviceName = string.Empty;

        Volatile.Write(ref _buttons, 0);
        Volatile.Write(ref _batteryLevel, 0);
        Volatile.Write(ref _charging, 0);
        Volatile.Write(ref _touchpadDown, 0);
        Volatile.Write(ref _l2, 0);
        Volatile.Write(ref _r2, 0);
        Volatile.Write(ref _touch1Active, 0);
        Volatile.Write(ref _touch1Id, 0);
        Volatile.Write(ref _touch1X, 0);
        Volatile.Write(ref _touch1Y, 0);
        Volatile.Write(ref _touch2Active, 0);
        Volatile.Write(ref _touch2Id, 0);
        Volatile.Write(ref _touch2X, 0);
        Volatile.Write(ref _touch2Y, 0);

        if (wasConnected)
        {
            AppLog.Write("Контроллер отключён.");
        }

        RaiseConnectionChanged(false);
    }

    private void RaiseConnectionChanged(bool connected)
    {
        if (_lastNotifiedConnected == connected &&
            string.Equals(_lastNotifiedName, _deviceName, StringComparison.Ordinal))
        {
            return;
        }

        _lastNotifiedConnected = connected;
        _lastNotifiedName = _deviceName;

        try
        {
            ConnectionChanged?.Invoke(this, new ControllerConnectionEventArgs(connected, _deviceName, _bluetooth));
        }
        catch (Exception ex)
        {
            AppLog.Write("Ошибка обработчика подключения: " + ex);
        }
    }

    private void LogError(Exception ex)
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _lastErrorLogTicks < Stopwatch.Frequency * 10)
        {
            return;
        }

        _lastErrorLogTicks = now;
        AppLog.Write("Ошибка HID: " + ex.Message);
    }

    private static void Wait(CancellationToken token, int milliseconds)
    {
        try
        {
            token.WaitHandle.WaitOne(milliseconds);
        }
        catch (ObjectDisposedException)
        {
            // Приложение завершается.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _shutdown.Dispose();
    }
}
```

## 3. src\Ds4Cursor\Core\AppSettings.cs

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Ds4Cursor.Core;

/// <summary>
/// Пользовательские настройки тачпада. Все значения нормализуются при записи.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    public const int MinTickRate = 125;
    public const int MaxTickRate = 250;
    public const double MinTouchSensitivity = 0.5;
    public const double MaxTouchSensitivity = 4.0;
    public const double MinTouchAcceleration = 0.0;
    public const double MaxTouchAcceleration = 3.0;
    public const double MinL2SlowdownFactor = 0.1;
    public const double MaxL2SlowdownFactor = 1.0;
    public const int MinClickFreezeMs = 0;
    public const int MaxClickFreezeMs = 120;
    public const double MinScrollSpeed = 1.0;
    public const double MaxScrollSpeed = 20.0;

    private bool _enabled = true;
    private int _tickRate = 200;
    private double _touchSensitivity = 1.6;
    private double _touchAcceleration = 1.2;
    private double _l2SlowdownFactor = 0.3;
    private int _clickFreezeMs = 50;
    private double _scrollSpeed = 7.0;
    private bool _tapToClick = true;
    private bool _cornerRightClick = true;
    private bool _twoFingerScroll = true;
    private bool _invertScroll;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public int TickRate
    {
        get => _tickRate;
        set => SetField(ref _tickRate, Math.Clamp(value, MinTickRate, MaxTickRate));
    }

    public double TouchSensitivity
    {
        get => _touchSensitivity;
        set => SetField(ref _touchSensitivity, Math.Clamp(value, MinTouchSensitivity, MaxTouchSensitivity));
    }

    public double TouchAcceleration
    {
        get => _touchAcceleration;
        set => SetField(ref _touchAcceleration, Math.Clamp(value, MinTouchAcceleration, MaxTouchAcceleration));
    }

    public double L2SlowdownFactor
    {
        get => _l2SlowdownFactor;
        set
        {
            if (SetField(ref _l2SlowdownFactor, Math.Clamp(value, MinL2SlowdownFactor, MaxL2SlowdownFactor)))
            {
                OnPropertyChanged(nameof(L2SlowdownPercent));
            }
        }
    }

    public int ClickFreezeMs
    {
        get => _clickFreezeMs;
        set => SetField(ref _clickFreezeMs, Math.Clamp(value, MinClickFreezeMs, MaxClickFreezeMs));
    }

    public double ScrollSpeed
    {
        get => _scrollSpeed;
        set => SetField(ref _scrollSpeed, Math.Clamp(value, MinScrollSpeed, MaxScrollSpeed));
    }

    public bool TapToClick
    {
        get => _tapToClick;
        set => SetField(ref _tapToClick, value);
    }

    public bool CornerRightClick
    {
        get => _cornerRightClick;
        set => SetField(ref _cornerRightClick, value);
    }

    public bool TwoFingerScroll
    {
        get => _twoFingerScroll;
        set => SetField(ref _twoFingerScroll, value);
    }

    public bool InvertScroll
    {
        get => _invertScroll;
        set => SetField(ref _invertScroll, value);
    }

    [JsonIgnore]
    public double L2SlowdownPercent => Math.Round(_l2SlowdownFactor * 100.0);

    public void Normalize()
    {
        TickRate = Math.Clamp(_tickRate, MinTickRate, MaxTickRate);
        TouchSensitivity = Math.Clamp(_touchSensitivity, MinTouchSensitivity, MaxTouchSensitivity);
        TouchAcceleration = Math.Clamp(_touchAcceleration, MinTouchAcceleration, MaxTouchAcceleration);
        L2SlowdownFactor = Math.Clamp(_l2SlowdownFactor, MinL2SlowdownFactor, MaxL2SlowdownFactor);
        ClickFreezeMs = Math.Clamp(_clickFreezeMs, MinClickFreezeMs, MaxClickFreezeMs);
        ScrollSpeed = Math.Clamp(_scrollSpeed, MinScrollSpeed, MaxScrollSpeed);
    }

    public void Reset()
    {
        Enabled = true;
        TickRate = 200;
        TouchSensitivity = 1.6;
        TouchAcceleration = 1.2;
        L2SlowdownFactor = 0.3;
        ClickFreezeMs = 50;
        ScrollSpeed = 7.0;
        TapToClick = true;
        CornerRightClick = true;
        TwoFingerScroll = true;
        InvertScroll = false;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
```

## 4. src\Ds4Cursor\Core\EngineTelemetry.cs

```csharp
namespace Ds4Cursor.Core;

/// <summary>
/// Разделяемые снимки для интерфейса. Поля обновляются без блокировок.
/// </summary>
public sealed class EngineTelemetry
{
    public volatile bool SleepMode;
    public volatile bool Connected;
    public volatile bool IsBluetooth;
    public volatile bool Charging;
    public volatile bool TouchpadDown;
    public volatile bool ClickFreeze;
    public volatile bool Dragging;
    public volatile bool Scrolling;
    public volatile int BatteryPercent;
    public volatile int Buttons;

    // Сборка x64: выровненные 8-байтовые чтения и записи double атомарны.
    // Согласованность между полями не критична: интерфейс обновляется с частотой 30 Гц.
    public double Touch1X;
    public double Touch1Y;
    public double Touch2X;
    public double Touch2Y;
    public bool Touch1Active;
    public bool Touch2Active;
    public double L2Value;
    public double SpeedFactor = 1.0;
    public double MeasuredHz;
}
```

## 5. src\Ds4Cursor\Core\InputEngine.cs

```csharp
using System.Diagnostics;
using System.Threading;

namespace Ds4Cursor.Core;

internal sealed class EngineConfig
{
    public readonly int TickRate;
    public readonly double Sensitivity;
    public readonly double Acceleration;
    public readonly double L2SlowdownFactor;
    public readonly int ClickFreezeMs;
    public readonly double ScrollSpeed;
    public readonly bool TapToClick;
    public readonly bool CornerRightClick;
    public readonly bool TwoFingerScroll;
    public readonly bool InvertScroll;
    public readonly bool Enabled;

    public EngineConfig(AppSettings settings)
    {
        TickRate = Math.Clamp(settings.TickRate, AppSettings.MinTickRate, AppSettings.MaxTickRate);
        Sensitivity = Math.Clamp(settings.TouchSensitivity, AppSettings.MinTouchSensitivity, AppSettings.MaxTouchSensitivity);
        Acceleration = Math.Clamp(settings.TouchAcceleration, AppSettings.MinTouchAcceleration, AppSettings.MaxTouchAcceleration);
        L2SlowdownFactor = Math.Clamp(settings.L2SlowdownFactor, AppSettings.MinL2SlowdownFactor, AppSettings.MaxL2SlowdownFactor);
        ClickFreezeMs = Math.Clamp(settings.ClickFreezeMs, AppSettings.MinClickFreezeMs, AppSettings.MaxClickFreezeMs);
        ScrollSpeed = Math.Clamp(settings.ScrollSpeed, AppSettings.MinScrollSpeed, AppSettings.MaxScrollSpeed);
        TapToClick = settings.TapToClick;
        CornerRightClick = settings.CornerRightClick;
        TwoFingerScroll = settings.TwoFingerScroll;
        InvertScroll = settings.InvertScroll;
        Enabled = settings.Enabled;
    }
}

/// <summary>
/// Движок эмуляции тачпада DualShock 4: отдельный поток с точным периодом,
/// баллистика, стабилизация клика, жесты и прямой SendInput без лишних обёрток.
/// </summary>
public sealed class InputEngine : IDisposable
{
    private const int TouchpadWidth = 1920;
    private const int TouchpadHeight = 942;
    private const int RightClickZoneX = 1440;
    private const int RightClickZoneY = 550;
    private const double TapMaxDurationMs = 180.0;
    private const double TwoFingerTapMaxDurationMs = 200.0;
    private const double TapMaxDistance = 20.0;
    private const double DragUnlockDistance = 25.0;
    private const double L2Threshold = 30.0;
    private const double ClickCompensationMs = 35.0;
    private const double HistoryMs = 60.0;
    private const double ScrollDeadband = 1.5;
    private const double ScrollGestureThreshold = 10.0;
    private const double MaxBallisticSpeed = 8000.0;
    private const double MaxBallisticFactor = 6.0;
    private const double MaxPixelsPerTick = 800.0;
    private const int MaxClickCompensation = 32;

    private readonly ControllerManager _controller;
    private readonly InputSender _sender = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly EngineTelemetry _telemetry = new();
    private readonly Queue<MoveSample> _history = new(64);

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _disposed;
    private volatile bool _sleepMode;
    private volatile EngineConfig _config;

    private bool _leftDown;
    private bool _rightDown;
    private bool _clickIsRight;
    private bool _clickFrozen;
    private bool _dragUnlocked;
    private bool _scrolling;
    private long _clickFreezeUntil;
    private int _pressX;
    private int _pressY;
    private int _lastTouchX = TouchpadWidth / 2;
    private int _lastTouchY = TouchpadHeight / 2;

    private bool _prevTouchpadDown;
    private bool _prevOneFingerActive;
    private bool _prevTwoFingerActive;
    private bool _suppressTapUntilRelease;

    private bool _tapCandidate;
    private long _tapStart;
    private int _tapStartX;
    private int _tapStartY;

    private bool _twoFingerTapCandidate;
    private bool _twoFingerHasPrev;
    private bool _scrollSentThisGesture;
    private long _twoFingerStart;
    private int _twoFingerStartX1;
    private int _twoFingerStartY1;
    private int _twoFingerStartX2;
    private int _twoFingerStartY2;
    private double _twoFingerPrevX;
    private double _twoFingerPrevY;
    private double _twoFingerTravel;

    private bool _cursorHasPrev;
    private int _cursorSource;
    private int _cursorTrackId;
    private int _cursorPrevX;
    private int _cursorPrevY;
    private double _cursorRemainderX;
    private double _cursorRemainderY;
    private double _scrollRemainderX;
    private double _scrollRemainderY;

    private Ds4Buttons _previousButtons;
    private bool _previousL3;
    private bool _l3Suppressed;
    private bool _killChordActive;
    private bool _killChordConsumed;
    private long _killChordStart;

    private int _volumeDirection;
    private long _nextVolumeRepeat;

    private long _lastProcess;
    private long _measureStart;
    private int _measuredTicks;

    public InputEngine(ControllerManager controller, AppSettings settings)
    {
        _controller = controller;
        _config = new EngineConfig(settings);
    }

    public event Action<bool>? SleepModeChanged;

    public EngineTelemetry Telemetry => _telemetry;

    public bool SleepMode => _sleepMode;

    public void UpdateConfig(AppSettings settings)
    {
        _config = new EngineConfig(settings);
        _wake.Set();
    }

    public void Start()
    {
        if (_thread != null || _disposed)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "DS4 Input Engine",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
        AppLog.Write("Движок ввода запущен.");
    }

    private void Loop()
    {
        NativeMethods.TimeBeginPeriod(1);
        _lastProcess = Stopwatch.GetTimestamp();
        _measureStart = _lastProcess;
        _measuredTicks = 0;
        long next = _lastProcess;

        try
        {
            while (_running)
            {
                var now = Stopwatch.GetTimestamp();
                var waitMs = (next - now) * 1000.0 / Stopwatch.Frequency;

                if (waitMs > 0)
                {
                    // Ждём событием, без SpinWait и busy-loop. Разрешение таймера — 1 мс.
                    _wake.WaitOne((int)Math.Ceiling(waitMs));
                    now = Stopwatch.GetTimestamp();
                }

                var dt = (now - _lastProcess) / (double)Stopwatch.Frequency;
                _lastProcess = now;
                if (dt > 0.05)
                {
                    dt = 0.05;
                }
                else if (dt < 0.001)
                {
                    dt = 0.001;
                }

                try
                {
                    Tick(dt);
                }
                catch (Exception ex)
                {
                    AppLog.Write("Ошибка такта движка: " + ex.Message);
                }

                _measuredTicks++;
                var measureElapsed = now - _measureStart;
                if (measureElapsed >= Stopwatch.Frequency)
                {
                    _telemetry.MeasuredHz = _measuredTicks * (double)Stopwatch.Frequency / measureElapsed;
                    _measuredTicks = 0;
                    _measureStart = now;
                }

                var hz = Math.Clamp(_config.TickRate, AppSettings.MinTickRate, AppSettings.MaxTickRate);
                var period = Stopwatch.Frequency / hz;
                next += period;

                if (next < now - period * 2)
                {
                    next = now + period;
                }
            }
        }
        finally
        {
            ReleaseMouseButtons();
            NativeMethods.TimeEndPeriod(1);
        }
    }

    private void Tick(double dt)
    {
        var snapshot = _controller.GetSnapshot();
        var config = _config;

        UpdateTelemetry(snapshot);
        UpdateKillSwitch(snapshot, config);

        if (!snapshot.Connected)
        {
            ReleaseMouseButtons();
            ResetTouchState(snapshot);
            _previousButtons = Ds4Buttons.None;
            _telemetry.SpeedFactor = 1.0;
            return;
        }

        if (!config.Enabled || _sleepMode)
        {
            ReleaseMouseButtons();
            ResetTouchState(snapshot);
            _previousButtons = snapshot.Buttons;
            _telemetry.SpeedFactor = 1.0;
            return;
        }

        HandleButtons(snapshot);
        HandleTouchpad(snapshot, config, dt);

        _previousButtons = snapshot.Buttons;
    }

    private void UpdateTelemetry(ControllerSnapshot snapshot)
    {
        _telemetry.Connected = snapshot.Connected;
        _telemetry.IsBluetooth = snapshot.IsBluetooth;
        _telemetry.Charging = snapshot.Charging;
        _telemetry.BatteryPercent = snapshot.BatteryPercent;
        _telemetry.Buttons = (int)snapshot.Buttons;
        _telemetry.Touch1Active = snapshot.Touch1.IsActive;
        _telemetry.Touch2Active = snapshot.Touch2.IsActive;
        _telemetry.Touch1X = snapshot.Touch1.IsActive ? snapshot.Touch1.X : 0;
        _telemetry.Touch1Y = snapshot.Touch1.IsActive ? snapshot.Touch1.Y : 0;
        _telemetry.Touch2X = snapshot.Touch2.IsActive ? snapshot.Touch2.X : 0;
        _telemetry.Touch2Y = snapshot.Touch2.IsActive ? snapshot.Touch2.Y : 0;
        _telemetry.TouchpadDown = snapshot.IsTouchpadButtonDown;
        _telemetry.L2Value = snapshot.L2Value / 255.0;
        _telemetry.SleepMode = _sleepMode;
    }

    private void UpdateKillSwitch(ControllerSnapshot snapshot, EngineConfig config)
    {
        var l3 = (snapshot.Buttons & Ds4Buttons.L3) != 0;
        var r3 = (snapshot.Buttons & Ds4Buttons.R3) != 0;
        var both = l3 && r3;
        var now = Stopwatch.GetTimestamp();

        if (both)
        {
            if (!_killChordActive)
            {
                _killChordActive = true;
                _killChordConsumed = false;
                _killChordStart = now;
            }

            _l3Suppressed = true;

            if (!_killChordConsumed && now - _killChordStart >= Stopwatch.Frequency / 2)
            {
                _killChordConsumed = true;
                ToggleSleepMode();
            }
        }
        else
        {
            _killChordActive = false;
        }

        if (l3 && !_previousL3)
        {
            _l3Suppressed = false;
        }
        else if (!l3 && _previousL3)
        {
            if (!_l3Suppressed && !_killChordConsumed && config.Enabled && !_sleepMode)
            {
                SystemActions.ToggleOnScreenKeyboard();
            }

            _l3Suppressed = false;
            _killChordConsumed = false;
        }

        _previousL3 = l3;
    }

    private void ToggleSleepMode()
    {
        _sleepMode = !_sleepMode;
        _telemetry.SleepMode = _sleepMode;
        ReleaseMouseButtons();
        ResetTouchState(_controller.GetSnapshot());
        _previousButtons = _controller.GetSnapshot().Buttons;

        AppLog.Write(_sleepMode ? "Режим игры включён." : "Режим игры выключен.");

        try
        {
            SleepModeChanged?.Invoke(_sleepMode);
        }
        catch (Exception ex)
        {
            AppLog.Write("Ошибка обработчика режима игры: " + ex.Message);
        }
    }

    private void HandleButtons(ControllerSnapshot snapshot)
    {
        var pressed = snapshot.Buttons & ~_previousButtons;
        var layer = (snapshot.Buttons & Ds4Buttons.L1) != 0;
        var now = Stopwatch.GetTimestamp();

        HandleDpadLayer(snapshot, pressed, layer, now);

        if ((pressed & Ds4Buttons.R1) != 0)
        {
            _sender.AltTab();
        }

        if ((pressed & Ds4Buttons.Options) != 0)
        {
            _sender.Enter();
        }

        if ((pressed & Ds4Buttons.Share) != 0)
        {
            _sender.Backspace();
        }

        if (!layer)
        {
            return;
        }

        if ((pressed & Ds4Buttons.Cross) != 0)
        {
            _sender.MediaPlayPause();
        }

        if ((pressed & Ds4Buttons.Triangle) != 0)
        {
            _sender.ShowDesktop();
        }

        if ((pressed & Ds4Buttons.Circle) != 0)
        {
            _sender.CloseTab();
        }
    }

    private void HandleDpadLayer(ControllerSnapshot snapshot, Ds4Buttons pressed, bool layer, long now)
    {
        if (!layer)
        {
            _volumeDirection = 0;
            _nextVolumeRepeat = 0;
            return;
        }

        var up = (snapshot.Buttons & Ds4Buttons.DpadUp) != 0;
        var down = (snapshot.Buttons & Ds4Buttons.DpadDown) != 0;
        var direction = up ? 1 : down ? -1 : 0;

        if (direction != _volumeDirection)
        {
            _volumeDirection = direction;
            if (direction > 0)
            {
                _sender.VolumeUp();
                _nextVolumeRepeat = now + Stopwatch.Frequency * 400 / 1000;
            }
            else if (direction < 0)
            {
                _sender.VolumeDown();
                _nextVolumeRepeat = now + Stopwatch.Frequency * 400 / 1000;
            }
            else
            {
                _nextVolumeRepeat = 0;
            }
        }
        else if (direction != 0 && now >= _nextVolumeRepeat)
        {
            if (direction > 0)
            {
                _sender.VolumeUp();
            }
            else
            {
                _sender.VolumeDown();
            }

            _nextVolumeRepeat = now + Stopwatch.Frequency * 110 / 1000;
        }

        if ((pressed & Ds4Buttons.DpadLeft) != 0)
        {
            _sender.MediaPrevious();
        }

        if ((pressed & Ds4Buttons.DpadRight) != 0)
        {
            _sender.MediaNext();
        }
    }

    private void HandleTouchpad(ControllerSnapshot snapshot, EngineConfig config, double dt)
    {
        var t1 = snapshot.Touch1;
        var t2 = snapshot.Touch2;
        var activeCount = (t1.IsActive ? 1 : 0) + (t2.IsActive ? 1 : 0);
        var now = Stopwatch.GetTimestamp();

        if (t1.IsActive)
        {
            _lastTouchX = t1.X;
            _lastTouchY = t1.Y;
        }
        else if (t2.IsActive)
        {
            _lastTouchX = t2.X;
            _lastTouchY = t2.Y;
        }

        if (snapshot.IsTouchpadButtonDown && !_prevTouchpadDown)
        {
            BeginPhysicalClick(t1, t2, config, now);
        }
        else if (!snapshot.IsTouchpadButtonDown && _prevTouchpadDown)
        {
            EndPhysicalClick();
        }

        _prevTouchpadDown = snapshot.IsTouchpadButtonDown;

        var twoFingers = t1.IsActive && t2.IsActive;

        if (twoFingers)
        {
            _suppressTapUntilRelease = true;
            _tapCandidate = false;

            if (!_prevTwoFingerActive)
            {
                _twoFingerTapCandidate = true;
                _twoFingerStart = now;
                _twoFingerStartX1 = t1.X;
                _twoFingerStartY1 = t1.Y;
                _twoFingerStartX2 = t2.X;
                _twoFingerStartY2 = t2.Y;
                _twoFingerHasPrev = false;
                _twoFingerTravel = 0;
                _scrollSentThisGesture = false;
                _scrollRemainderX = 0;
                _scrollRemainderY = 0;
            }
            else if (_twoFingerTapCandidate)
            {
                if (Distance(t1.X, t1.Y, _twoFingerStartX1, _twoFingerStartY1) > TapMaxDistance ||
                    Distance(t2.X, t2.Y, _twoFingerStartX2, _twoFingerStartY2) > TapMaxDistance ||
                    now - _twoFingerStart > MsToTicks(TwoFingerTapMaxDurationMs))
                {
                    _twoFingerTapCandidate = false;
                }
            }

            if (config.TwoFingerScroll)
            {
                var avgX = (t1.X + t2.X) * 0.5;
                var avgY = (t1.Y + t2.Y) * 0.5;

                if (!_twoFingerHasPrev)
                {
                    _twoFingerPrevX = avgX;
                    _twoFingerPrevY = avgY;
                    _twoFingerHasPrev = true;
                }
                else
                {
                    var dx = avgX - _twoFingerPrevX;
                    var dy = avgY - _twoFingerPrevY;
                    _twoFingerPrevX = avgX;
                    _twoFingerPrevY = avgY;

                    var effectiveX = ApplyScrollDeadband(dx);
                    var effectiveY = ApplyScrollDeadband(dy);

                    if (effectiveX != 0 || effectiveY != 0)
                    {
                        if (!_scrollSentThisGesture)
                        {
                            // Микродрожание пальцев не должно запускать скролл и отменять тап.
                            _twoFingerTravel += Math.Abs(effectiveX) + Math.Abs(effectiveY);
                            if (_twoFingerTravel >= ScrollGestureThreshold)
                            {
                                _twoFingerTapCandidate = false;
                                _scrollSentThisGesture = true;
                            }
                        }

                        if (_scrollSentThisGesture)
                        {
                            if (config.InvertScroll)
                            {
                                effectiveX = -effectiveX;
                                effectiveY = -effectiveY;
                            }

                            // Скорость 10 соответствует коэффициенту 1,0: одна wheel-единица на единицу панели.
                            var factor = config.ScrollSpeed / 10.0;
                            _scrollRemainderX += effectiveX * factor;
                            _scrollRemainderY += effectiveY * factor;

                            var wheelX = (int)_scrollRemainderX;
                            var wheelY = (int)_scrollRemainderY;

                            if (wheelX != 0)
                            {
                                _sender.WheelDelta(wheelX, true);
                                _scrollRemainderX -= wheelX;
                            }

                            if (wheelY != 0)
                            {
                                _sender.WheelDelta(wheelY, false);
                                _scrollRemainderY -= wheelY;
                            }
                        }
                    }
                }
            }

            _scrolling = config.TwoFingerScroll && _scrollSentThisGesture;
            ResetCursorTracking();
        }
        else
        {
            if (_prevTwoFingerActive)
            {
                if (_twoFingerTapCandidate &&
                    !_scrollSentThisGesture &&
                    now - _twoFingerStart <= MsToTicks(TwoFingerTapMaxDurationMs) &&
                    !snapshot.IsTouchpadButtonDown)
                {
                    _sender.RightClick();
                }

                _twoFingerTapCandidate = false;
            }

            _twoFingerHasPrev = false;
            _twoFingerTravel = 0;
            _scrolling = false;

            if (activeCount == 1)
            {
                var touch = t1.IsActive ? t1 : t2;

                if (!_prevOneFingerActive)
                {
                    _tapCandidate = config.TapToClick &&
                                    !_suppressTapUntilRelease &&
                                    !snapshot.IsTouchpadButtonDown;
                    _tapStart = now;
                    _tapStartX = touch.X;
                    _tapStartY = touch.Y;
                }
                else if (_tapCandidate &&
                         Distance(touch.X, touch.Y, _tapStartX, _tapStartY) > TapMaxDistance)
                {
                    _tapCandidate = false;
                }

                ApplyCursor(t1, t2, config, dt, snapshot);
            }
            else
            {
                if (activeCount == 0)
                {
                    if (_prevOneFingerActive &&
                        _tapCandidate &&
                        now - _tapStart <= MsToTicks(TapMaxDurationMs) &&
                        !snapshot.IsTouchpadButtonDown)
                    {
                        _sender.LeftClick();
                    }

                    _tapCandidate = false;
                    _suppressTapUntilRelease = false;
                    ResetCursorTracking();
                    _cursorRemainderX = 0;
                    _cursorRemainderY = 0;
                    _scrollRemainderX = 0;
                    _scrollRemainderY = 0;
                }
                else
                {
                    ResetCursorTracking();
                }
            }
        }

        _prevOneFingerActive = activeCount == 1;
        _prevTwoFingerActive = twoFingers;

        if (_clickFrozen && now >= _clickFreezeUntil)
        {
            _clickFrozen = false;
            if (_leftDown && !_clickIsRight)
            {
                _dragUnlocked = true;
            }
        }

        _telemetry.ClickFreeze = _clickFrozen;
        _telemetry.Dragging = _leftDown && _dragUnlocked;
        _telemetry.Scrolling = _scrolling;
    }

    private void ApplyCursor(TouchPoint t1, TouchPoint t2, EngineConfig config, double dt, ControllerSnapshot snapshot)
    {
        TouchPoint touch;
        int source;

        if (t1.IsActive)
        {
            touch = t1;
            source = 1;
        }
        else if (t2.IsActive)
        {
            touch = t2;
            source = 2;
        }
        else
        {
            ResetCursorTracking();
            return;
        }

        if (!_cursorHasPrev || _cursorSource != source || _cursorTrackId != touch.TrackId)
        {
            _cursorSource = source;
            _cursorTrackId = touch.TrackId;
            _cursorPrevX = touch.X;
            _cursorPrevY = touch.Y;
            _cursorHasPrev = true;
            return;
        }

        var dx = touch.X - _cursorPrevX;
        var dy = touch.Y - _cursorPrevY;
        _cursorPrevX = touch.X;
        _cursorPrevY = touch.Y;

        if (_clickFrozen)
        {
            if (_leftDown &&
                !_clickIsRight &&
                Distance(touch.X, touch.Y, _pressX, _pressY) > DragUnlockDistance)
            {
                _clickFrozen = false;
                _dragUnlocked = true;
            }
            else
            {
                return;
            }
        }

        if (dx == 0 && dy == 0)
        {
            return;
        }

        var speed = Math.Sqrt((double)dx * dx + (double)dy * dy) / Math.Max(dt, 0.001);
        if (speed > MaxBallisticSpeed)
        {
            speed = MaxBallisticSpeed;
        }

        var normalizedSpeed = speed / 2000.0;
        var ballistic = 1.0 + config.Acceleration * normalizedSpeed * normalizedSpeed;
        if (ballistic > MaxBallisticFactor)
        {
            ballistic = MaxBallisticFactor;
        }

        var l2Factor = 1.0;

        if (snapshot.L2Value > L2Threshold)
        {
            var t = (snapshot.L2Value - L2Threshold) / (255.0 - L2Threshold);
            t = t * t * (3.0 - 2.0 * t);
            l2Factor = 1.0 - (1.0 - config.L2SlowdownFactor) * t;
        }

        var factor = config.Sensitivity * ballistic * l2Factor;
        var moveX = dx * factor;
        var moveY = dy * factor;

        if (moveX > MaxPixelsPerTick)
        {
            moveX = MaxPixelsPerTick;
        }
        else if (moveX < -MaxPixelsPerTick)
        {
            moveX = -MaxPixelsPerTick;
        }

        if (moveY > MaxPixelsPerTick)
        {
            moveY = MaxPixelsPerTick;
        }
        else if (moveY < -MaxPixelsPerTick)
        {
            moveY = -MaxPixelsPerTick;
        }

        _cursorRemainderX += moveX;
        _cursorRemainderY += moveY;

        var pixelX = (int)_cursorRemainderX;
        var pixelY = (int)_cursorRemainderY;

        if (pixelX != 0 || pixelY != 0)
        {
            _sender.MoveRelative(pixelX, pixelY);
            _cursorRemainderX -= pixelX;
            _cursorRemainderY -= pixelY;
            RecordHistory(pixelX, pixelY, Stopwatch.GetTimestamp());
        }

        _telemetry.SpeedFactor = factor;
    }

    private void BeginPhysicalClick(TouchPoint t1, TouchPoint t2, EngineConfig config, long now)
    {
        var x = t1.IsActive ? t1.X : t2.IsActive ? t2.X : _lastTouchX;
        var y = t1.IsActive ? t1.Y : t2.IsActive ? t2.Y : _lastTouchY;

        _pressX = x;
        _pressY = y;
        _clickIsRight = config.CornerRightClick && x >= RightClickZoneX && y >= RightClickZoneY;
        _dragUnlocked = false;
        _tapCandidate = false;
        _twoFingerTapCandidate = false;
        _suppressTapUntilRelease = true;

        var freeze = config.ClickFreezeMs > 0;
        if (freeze)
        {
            // Компенсируем микросдвиг до нажатия, чтобы приложение не увидело drag.
            CompensateClickShift(now);
        }

        if (_clickIsRight)
        {
            if (_leftDown)
            {
                _sender.SetMouseButton(MouseButton.Left, false);
                _leftDown = false;
            }

            _sender.SetMouseButton(MouseButton.Right, true);
            _rightDown = true;
        }
        else
        {
            if (_rightDown)
            {
                _sender.SetMouseButton(MouseButton.Right, false);
                _rightDown = false;
            }

            _sender.SetMouseButton(MouseButton.Left, true);
            _leftDown = true;
        }

        _clickFrozen = freeze;
        if (freeze)
        {
            _clickFreezeUntil = now + MsToTicks(config.ClickFreezeMs);
        }
        else if (_leftDown && !_clickIsRight)
        {
            _dragUnlocked = true;
        }
    }

    private void EndPhysicalClick()
    {
        if (_leftDown)
        {
            _sender.SetMouseButton(MouseButton.Left, false);
            _leftDown = false;
        }

        if (_rightDown)
        {
            _sender.SetMouseButton(MouseButton.Right, false);
            _rightDown = false;
        }

        _clickFrozen = false;
        _dragUnlocked = false;
        _clickIsRight = false;
    }

    private void RecordHistory(int dx, int dy, long now)
    {
        _history.Enqueue(new MoveSample(now, dx, dy));

        var cutoff = now - MsToTicks(HistoryMs);
        while (_history.Count > 0 && _history.Peek().Time < cutoff)
        {
            _history.Dequeue();
        }
    }

    private void CompensateClickShift(long now)
    {
        var cutoff = now - MsToTicks(ClickCompensationMs);
        var sumX = 0;
        var sumY = 0;

        foreach (var sample in _history)
        {
            if (sample.Time >= cutoff)
            {
                sumX += sample.Dx;
                sumY += sample.Dy;
            }
        }

        sumX = Math.Clamp(sumX, -MaxClickCompensation, MaxClickCompensation);
        sumY = Math.Clamp(sumY, -MaxClickCompensation, MaxClickCompensation);

        if (sumX != 0 || sumY != 0)
        {
            _sender.MoveRelative(-sumX, -sumY);
        }

        _history.Clear();
    }

    private void ResetCursorTracking()
    {
        _cursorHasPrev = false;
        _cursorSource = 0;
    }

    private void ResetTouchState(ControllerSnapshot snapshot)
    {
        ResetCursorTracking();
        _cursorRemainderX = 0;
        _cursorRemainderY = 0;
        _scrollRemainderX = 0;
        _scrollRemainderY = 0;
        _history.Clear();

        _tapCandidate = false;
        _twoFingerTapCandidate = false;
        _twoFingerHasPrev = false;
        _twoFingerTravel = 0;
        _scrollSentThisGesture = false;
        _scrolling = false;
        _clickFrozen = false;
        _dragUnlocked = false;
        _clickIsRight = false;

        _prevTouchpadDown = snapshot.IsTouchpadButtonDown;
        _prevOneFingerActive = (snapshot.Touch1.IsActive ? 1 : 0) + (snapshot.Touch2.IsActive ? 1 : 0) == 1;
        _prevTwoFingerActive = snapshot.Touch1.IsActive && snapshot.Touch2.IsActive;
        _suppressTapUntilRelease = snapshot.Touch1.IsActive || snapshot.Touch2.IsActive;

        _telemetry.ClickFreeze = false;
        _telemetry.Dragging = false;
        _telemetry.Scrolling = false;
    }

    private void ReleaseMouseButtons()
    {
        if (_leftDown)
        {
            _sender.SetMouseButton(MouseButton.Left, false);
            _leftDown = false;
        }

        if (_rightDown)
        {
            _sender.SetMouseButton(MouseButton.Right, false);
            _rightDown = false;
        }

        _clickFrozen = false;
        _dragUnlocked = false;
        _clickIsRight = false;
    }

    private static double ApplyScrollDeadband(double value)
    {
        if (value > ScrollDeadband)
        {
            return value - ScrollDeadband;
        }

        if (value < -ScrollDeadband)
        {
            return value + ScrollDeadband;
        }

        return 0;
    }

    private static long MsToTicks(double milliseconds)
    {
        return (long)(Stopwatch.Frequency * milliseconds / 1000.0);
    }

    private static double Distance(int x1, int y1, int x2, int y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }

    private readonly struct MoveSample
    {
        public readonly long Time;
        public readonly int Dx;
        public readonly int Dy;

        public MoveSample(long time, int dx, int dy)
        {
            Time = time;
            Dx = dx;
            Dy = dy;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running = false;
        _wake.Set();

        if (_thread != null && _thread != Thread.CurrentThread)
        {
            try
            {
                _thread.Join(1500);
            }
            catch (ThreadStateException)
            {
                // Поток уже завершён.
            }
        }

        _thread = null;
        ReleaseMouseButtons();
        _wake.Dispose();
        AppLog.Write("Движок ввода остановлен.");
    }
}
```

## 6. src\Ds4Cursor\Core\InputSender.cs

```csharp
using System.Runtime.InteropServices;

namespace Ds4Cursor.Core;

internal enum MouseButton
{
    Left,
    Right,
    Middle
}

/// <summary>
/// Тонкая обёртка над SendInput. Буфер ввода переиспользуется, горячий путь не создаёт мусора.
/// </summary>
internal sealed class InputSender
{
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();
    private readonly NativeMethods.INPUT[] _buffer = new NativeMethods.INPUT[8];
    private int _count;

    public void MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        AddMouse(NativeMethods.MouseEventMove, dx, dy, 0);
        Flush();
    }

    public void SetMouseButton(MouseButton button, bool down)
    {
        var flags = button switch
        {
            MouseButton.Left => down ? NativeMethods.MouseEventLeftDown : NativeMethods.MouseEventLeftUp,
            MouseButton.Right => down ? NativeMethods.MouseEventRightDown : NativeMethods.MouseEventRightUp,
            MouseButton.Middle => down ? NativeMethods.MouseEventMiddleDown : NativeMethods.MouseEventMiddleUp,
            _ => 0u
        };

        if (flags == 0)
        {
            return;
        }

        AddMouse(flags, 0, 0, 0);
        Flush();
    }

    public void LeftClick()
    {
        AddMouse(NativeMethods.MouseEventLeftDown, 0, 0, 0);
        AddMouse(NativeMethods.MouseEventLeftUp, 0, 0, 0);
        Flush();
    }

    public void RightClick()
    {
        AddMouse(NativeMethods.MouseEventRightDown, 0, 0, 0);
        AddMouse(NativeMethods.MouseEventRightUp, 0, 0, 0);
        Flush();
    }

    public void DoubleClick()
    {
        AddMouse(NativeMethods.MouseEventLeftDown, 0, 0, 0);
        AddMouse(NativeMethods.MouseEventLeftUp, 0, 0, 0);
        AddMouse(NativeMethods.MouseEventLeftDown, 0, 0, 0);
        AddMouse(NativeMethods.MouseEventLeftUp, 0, 0, 0);
        Flush();
    }

    public void Wheel(int notches, bool horizontal)
    {
        if (notches == 0)
        {
            return;
        }

        WheelDelta(unchecked(notches * 120), horizontal);
    }

    public void WheelDelta(int delta, bool horizontal)
    {
        if (delta == 0)
        {
            return;
        }

        AddMouse(horizontal ? NativeMethods.MouseEventHWheel : NativeMethods.MouseEventWheel,
            0, 0, unchecked((uint)delta));
        Flush();
    }

    public void KeyTap(ushort virtualKey, bool extended = false)
    {
        AddKey(virtualKey, false, extended);
        AddKey(virtualKey, true, extended);
        Flush();
    }

    public void AltTab()
    {
        AddKey(NativeMethods.VkMenu, false, false);
        AddKey(NativeMethods.VkTab, false, false);
        AddKey(NativeMethods.VkTab, true, false);
        AddKey(NativeMethods.VkMenu, true, false);
        Flush();
    }

    public void ShowDesktop()
    {
        AddKey(NativeMethods.VkLWin, false, true);
        AddKey(NativeMethods.VkD, false, false);
        AddKey(NativeMethods.VkD, true, false);
        AddKey(NativeMethods.VkLWin, true, true);
        Flush();
    }

    public void CloseTab()
    {
        AddKey(NativeMethods.VkControl, false, false);
        AddKey(NativeMethods.VkW, false, false);
        AddKey(NativeMethods.VkW, true, false);
        AddKey(NativeMethods.VkControl, true, false);
        Flush();
    }

    public void VolumeUp() => KeyTap(NativeMethods.VkVolumeUp, true);

    public void VolumeDown() => KeyTap(NativeMethods.VkVolumeDown, true);

    public void MediaNext() => KeyTap(NativeMethods.VkMediaNextTrack, true);

    public void MediaPrevious() => KeyTap(NativeMethods.VkMediaPrevTrack, true);

    public void MediaPlayPause() => KeyTap(NativeMethods.VkMediaPlayPause, true);

    public void Enter() => KeyTap(NativeMethods.VkReturn);

    public void Backspace() => KeyTap(NativeMethods.VkBack);

    private void AddMouse(uint flags, int dx, int dy, uint data)
    {
        if (_count >= _buffer.Length)
        {
            Flush();
        }

        _buffer[_count++] = new NativeMethods.INPUT
        {
            type = NativeMethods.InputMouse,
            U = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = data,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private void AddKey(ushort virtualKey, bool up, bool extended)
    {
        if (_count >= _buffer.Length)
        {
            Flush();
        }

        uint flags = 0;
        if (up)
        {
            flags |= NativeMethods.KeyEventKeyUp;
        }

        if (extended)
        {
            flags |= NativeMethods.KeyEventExtendedKey;
        }

        _buffer[_count++] = new NativeMethods.INPUT
        {
            type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private void Flush()
    {
        if (_count == 0)
        {
            return;
        }

        NativeMethods.SendInput((uint)_count, _buffer, InputSize);
        _count = 0;
    }
}
```

## 7. src\Ds4Cursor\Core\MainViewModel.cs

```csharp
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Ds4Cursor.Core;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly ControllerManager _controller;
    private readonly InputEngine _engine;

    private bool _disposed;
    private string _modeState = "Waiting";
    private string _modeTitle = "ОЖИДАНИЕ";
    private string _modeSubtitle = "Подключите DualShock 4 по USB или Bluetooth";
    private string _connectionText = "НЕ ПОДКЛЮЧЁН";
    private string _connectionDetail = "Ожидание HID-устройства";
    private string _transportText = "—";
    private string _batteryText = "—";
    private string _measuredHzText = "0,0 Гц";
    private string _speedFactorText = "1,00x";
    private string _tickRateText = "200 Гц";
    private string _touch1Text = "—";
    private string _touch2Text = "—";
    private string _touchpadStateText = "—";
    private bool _isConnected;
    private double _touch1X;
    private double _touch1Y;
    private double _touch2X;
    private double _touch2Y;
    private bool _touch1Active;
    private bool _touch2Active;
    private bool _isTouchpadDown;
    private double _l2Percent;
    private double _measuredHz;
    private double _speedFactor = 1.0;

    public MainViewModel(AppSettings settings, ControllerManager controller, InputEngine engine)
    {
        Settings = settings;
        _controller = controller;
        _engine = engine;
        _tickRateText = settings.TickRate.ToString(Russian) + " Гц";
        Settings.PropertyChanged += OnSettingsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppSettings Settings { get; }

    public string ModeState
    {
        get => _modeState;
        private set => SetString(ref _modeState, value, nameof(ModeState));
    }

    public string ModeTitle
    {
        get => _modeTitle;
        private set => SetString(ref _modeTitle, value, nameof(ModeTitle));
    }

    public string ModeSubtitle
    {
        get => _modeSubtitle;
        private set => SetString(ref _modeSubtitle, value, nameof(ModeSubtitle));
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => SetString(ref _connectionText, value, nameof(ConnectionText));
    }

    public string ConnectionDetail
    {
        get => _connectionDetail;
        private set => SetString(ref _connectionDetail, value, nameof(ConnectionDetail));
    }

    public string TransportText
    {
        get => _transportText;
        private set => SetString(ref _transportText, value, nameof(TransportText));
    }

    public string BatteryText
    {
        get => _batteryText;
        private set => SetString(ref _batteryText, value, nameof(BatteryText));
    }

    public string MeasuredHzText
    {
        get => _measuredHzText;
        private set => SetString(ref _measuredHzText, value, nameof(MeasuredHzText));
    }

    public string SpeedFactorText
    {
        get => _speedFactorText;
        private set => SetString(ref _speedFactorText, value, nameof(SpeedFactorText));
    }

    public string TickRateText
    {
        get => _tickRateText;
        private set => SetString(ref _tickRateText, value, nameof(TickRateText));
    }

    public string Touch1Text
    {
        get => _touch1Text;
        private set => SetString(ref _touch1Text, value, nameof(Touch1Text));
    }

    public string Touch2Text
    {
        get => _touch2Text;
        private set => SetString(ref _touch2Text, value, nameof(Touch2Text));
    }

    public string TouchpadStateText
    {
        get => _touchpadStateText;
        private set => SetString(ref _touchpadStateText, value, nameof(TouchpadStateText));
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected == value)
            {
                return;
            }

            _isConnected = value;
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    public double Touch1X
    {
        get => _touch1X;
        private set => SetDouble(ref _touch1X, value, nameof(Touch1X), 0.01);
    }

    public double Touch1Y
    {
        get => _touch1Y;
        private set => SetDouble(ref _touch1Y, value, nameof(Touch1Y), 0.01);
    }

    public double Touch2X
    {
        get => _touch2X;
        private set => SetDouble(ref _touch2X, value, nameof(Touch2X), 0.01);
    }

    public double Touch2Y
    {
        get => _touch2Y;
        private set => SetDouble(ref _touch2Y, value, nameof(Touch2Y), 0.01);
    }

    public bool Touch1Active
    {
        get => _touch1Active;
        private set => SetBool(ref _touch1Active, value, nameof(Touch1Active));
    }

    public bool Touch2Active
    {
        get => _touch2Active;
        private set => SetBool(ref _touch2Active, value, nameof(Touch2Active));
    }

    public bool IsTouchpadDown
    {
        get => _isTouchpadDown;
        private set => SetBool(ref _isTouchpadDown, value, nameof(IsTouchpadDown));
    }

    public double L2Percent
    {
        get => _l2Percent;
        private set => SetDouble(ref _l2Percent, value, nameof(L2Percent), 0.2);
    }

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _controller.GetSnapshot();
        var telemetry = _engine.Telemetry;

        IsConnected = snapshot.Connected;
        Touch1Active = telemetry.Touch1Active;
        Touch2Active = telemetry.Touch2Active;
        Touch1X = telemetry.Touch1X;
        Touch1Y = telemetry.Touch1Y;
        Touch2X = telemetry.Touch2X;
        Touch2Y = telemetry.Touch2Y;
        IsTouchpadDown = telemetry.TouchpadDown;
        L2Percent = telemetry.L2Value * 100.0;

        Touch1Text = telemetry.Touch1Active ? FormatPoint(telemetry.Touch1X, telemetry.Touch1Y) : "—";
        Touch2Text = telemetry.Touch2Active ? FormatPoint(telemetry.Touch2X, telemetry.Touch2Y) : "—";
        TouchpadStateText = ComputeTouchpadState(snapshot, telemetry);

        ConnectionText = snapshot.Connected ? "ПОДКЛЮЧЁН" : "НЕ ПОДКЛЮЧЁН";
        ConnectionDetail = snapshot.Connected
            ? (snapshot.IsBluetooth ? "Bluetooth" : "USB") + " / " +
              (string.IsNullOrWhiteSpace(snapshot.DeviceName) ? "DualShock 4" : snapshot.DeviceName)
            : "Ожидание HID-устройства";
        TransportText = snapshot.Connected ? (snapshot.IsBluetooth ? "Bluetooth" : "USB") : "—";

        if (snapshot.Connected)
        {
            BatteryText = snapshot.Charging
                ? snapshot.BatteryPercent.ToString(Russian) + "% / заряд"
                : snapshot.BatteryPercent.ToString(Russian) + "%";
        }
        else
        {
            BatteryText = "—";
        }

        if (Math.Abs(telemetry.MeasuredHz - _measuredHz) >= 0.1)
        {
            _measuredHz = telemetry.MeasuredHz;
            MeasuredHzText = _measuredHz.ToString("0.0", Russian) + " Гц";
        }

        if (Math.Abs(telemetry.SpeedFactor - _speedFactor) >= 0.005)
        {
            _speedFactor = telemetry.SpeedFactor;
            SpeedFactorText = _speedFactor.ToString("0.00", Russian) + "x";
        }

        var state = !Settings.Enabled
            ? "Paused"
            : telemetry.SleepMode
                ? "Sleep"
                : snapshot.Connected
                    ? "Active"
                    : "Waiting";

        if (state != _modeState)
        {
            ModeState = state;
            switch (state)
            {
                case "Active":
                    ModeTitle = "УПРАВЛЕНИЕ";
                    ModeSubtitle = "Тачпад управляет курсором";
                    break;
                case "Paused":
                    ModeTitle = "ПАУЗА";
                    ModeSubtitle = "Ввод с тачпада остановлен";
                    break;
                case "Sleep":
                    ModeTitle = "РЕЖИМ ИГРЫ";
                    ModeSubtitle = "Тачпад не управляет курсором";
                    break;
                default:
                    ModeTitle = "ОЖИДАНИЕ";
                    ModeSubtitle = "Подключите DualShock 4 по USB или Bluetooth";
                    break;
            }
        }
    }

    private string ComputeTouchpadState(ControllerSnapshot snapshot, EngineTelemetry telemetry)
    {
        if (!snapshot.Connected)
        {
            return "НЕТ СВЯЗИ";
        }

        if (!Settings.Enabled)
        {
            return "ПАУЗА";
        }

        if (telemetry.SleepMode)
        {
            return "ИГРА";
        }

        if (telemetry.Scrolling)
        {
            return "СКРОЛЛ";
        }

        if (telemetry.Dragging)
        {
            return "ПЕРЕТАСКИВАНИЕ";
        }

        if (telemetry.ClickFreeze)
        {
            return "СТАБИЛИЗАЦИЯ";
        }

        if (telemetry.TouchpadDown)
        {
            return "КЛИК";
        }

        if (telemetry.Touch1Active && telemetry.Touch2Active)
        {
            return "2 ПАЛЬЦА";
        }

        if (telemetry.Touch1Active || telemetry.Touch2Active)
        {
            return "КАСАНИЕ";
        }

        return "ГОТОВ";
    }

    private static string FormatPoint(double x, double y)
    {
        return Math.Round(x).ToString("0", Russian) + ", " + Math.Round(y).ToString("0", Russian);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.TickRate))
        {
            TickRateText = Settings.TickRate.ToString(Russian) + " Гц";
        }
    }

    private void SetString(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void SetDouble(ref double field, double value, string propertyName, double epsilon)
    {
        if (Math.Abs(field - value) < epsilon)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void SetBool(ref bool field, bool value, string propertyName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Settings.PropertyChanged -= OnSettingsChanged;
    }
}
```

## 8. src\Ds4Cursor\Controls\StickVisualizer.cs

```csharp
using System.Windows;
using System.Windows.Media;

namespace Ds4Cursor.Controls;

/// <summary>
/// Векторный визуализатор сенсорной панели DualShock 4 (1920 x 942).
/// Не создаёт кистей и перьев в OnRender, не содержит текста и не запускает таймеров.
/// </summary>
public sealed class StickVisualizer : FrameworkElement
{
    public static readonly DependencyProperty Touch1XProperty = DependencyProperty.Register(
        nameof(Touch1X), typeof(double), typeof(StickVisualizer),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Touch1YProperty = DependencyProperty.Register(
        nameof(Touch1Y), typeof(double), typeof(StickVisualizer),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Touch1ActiveProperty = DependencyProperty.Register(
        nameof(Touch1Active), typeof(bool), typeof(StickVisualizer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Touch2XProperty = DependencyProperty.Register(
        nameof(Touch2X), typeof(double), typeof(StickVisualizer),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Touch2YProperty = DependencyProperty.Register(
        nameof(Touch2Y), typeof(double), typeof(StickVisualizer),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Touch2ActiveProperty = DependencyProperty.Register(
        nameof(Touch2Active), typeof(bool), typeof(StickVisualizer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsTouchpadDownProperty = DependencyProperty.Register(
        nameof(IsTouchpadDown), typeof(bool), typeof(StickVisualizer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowRightZoneProperty = DependencyProperty.Register(
        nameof(ShowRightZone), typeof(bool), typeof(StickVisualizer),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double PadWidth = 1920.0;
    private const double PadHeight = 942.0;
    private const double RightZoneX = 1440.0;
    private const double RightZoneY = 550.0;

    private static readonly Brush FillBrush = CreateBrush(0x10, 0x10, 0x13);
    private static readonly Brush PressedFillBrush = CreateBrush(0x3B, 0x82, 0xF6, 0x20);
    private static readonly Brush ZoneFillBrush = CreateBrush(0x3B, 0x82, 0xF6, 0x0A);
    private static readonly Brush Touch1Brush = CreateBrush(0x3B, 0x82, 0xF6);
    private static readonly Brush Touch2Brush = CreateBrush(0xFA, 0xFA, 0xFA);
    private static readonly Brush TouchCoreBrush = CreateBrush(0x0F, 0x0F, 0x11);
    private static readonly Pen BorderPen = CreatePen(0x27, 0x27, 0x2A, 1.0);
    private static readonly Pen PressedPen = CreatePen(0x3B, 0x82, 0xF6, 1.0);
    private static readonly Pen InnerPen = CreatePen(0x1F, 0x1F, 0x23, 1.0);
    private static readonly Pen TickPen = CreatePen(0x3F, 0x3F, 0x46, 1.0);
    private static readonly Pen ZonePen = CreateDashedPen(0x3F, 0x3F, 0x46, 1.0);
    private static readonly Pen Touch1Pen = CreatePen(0x3B, 0x82, 0xF6, 1.0);
    private static readonly Pen Touch2Pen = CreatePen(0xFA, 0xFA, 0xFA, 1.0);

    public double Touch1X
    {
        get => (double)GetValue(Touch1XProperty);
        set => SetValue(Touch1XProperty, value);
    }

    public double Touch1Y
    {
        get => (double)GetValue(Touch1YProperty);
        set => SetValue(Touch1YProperty, value);
    }

    public bool Touch1Active
    {
        get => (bool)GetValue(Touch1ActiveProperty);
        set => SetValue(Touch1ActiveProperty, value);
    }

    public double Touch2X
    {
        get => (double)GetValue(Touch2XProperty);
        set => SetValue(Touch2XProperty, value);
    }

    public double Touch2Y
    {
        get => (double)GetValue(Touch2YProperty);
        set => SetValue(Touch2YProperty, value);
    }

    public bool Touch2Active
    {
        get => (bool)GetValue(Touch2ActiveProperty);
        set => SetValue(Touch2ActiveProperty, value);
    }

    public bool IsTouchpadDown
    {
        get => (bool)GetValue(IsTouchpadDownProperty);
        set => SetValue(IsTouchpadDownProperty, value);
    }

    public bool ShowRightZone
    {
        get => (bool)GetValue(ShowRightZoneProperty);
        set => SetValue(ShowRightZoneProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 16 || height <= 12)
        {
            return;
        }

        var availableWidth = width - 8.0;
        var availableHeight = height - 8.0;
        var aspect = PadWidth / PadHeight;
        var panelWidth = availableWidth;
        var panelHeight = panelWidth / aspect;

        if (panelHeight > availableHeight)
        {
            panelHeight = availableHeight;
            panelWidth = panelHeight * aspect;
        }

        if (panelWidth <= 8.0 || panelHeight <= 8.0)
        {
            return;
        }

        var panel = new Rect(
            (width - panelWidth) / 2.0,
            (height - panelHeight) / 2.0,
            panelWidth,
            panelHeight);

        drawingContext.DrawRoundedRectangle(FillBrush, BorderPen, panel, 2.0, 2.0);

        if (IsTouchpadDown)
        {
            drawingContext.DrawRoundedRectangle(PressedFillBrush, PressedPen, panel, 2.0, 2.0);
        }

        var inner = new Rect(panel.X + 1.0, panel.Y + 1.0, panel.Width - 2.0, panel.Height - 2.0);
        drawingContext.DrawRoundedRectangle(null, InnerPen, inner, 1.0, 1.0);

        DrawCornerTicks(drawingContext, panel);

        if (ShowRightZone)
        {
            var zone = new Rect(
                panel.X + panel.Width * (RightZoneX / PadWidth),
                panel.Y + panel.Height * (RightZoneY / PadHeight),
                panel.Width * (1.0 - RightZoneX / PadWidth) - 1.0,
                panel.Height * (1.0 - RightZoneY / PadHeight) - 1.0);

            if (zone.Width > 2.0 && zone.Height > 2.0)
            {
                drawingContext.DrawRectangle(ZoneFillBrush, null, zone);
                drawingContext.DrawRectangle(null, ZonePen, zone);
            }
        }

        if (Touch1Active)
        {
            DrawTouch(drawingContext, panel, Touch1X, Touch1Y, Touch1Brush, Touch1Pen);
        }

        if (Touch2Active)
        {
            DrawTouch(drawingContext, panel, Touch2X, Touch2Y, Touch2Brush, Touch2Pen);
        }
    }

    private static void DrawCornerTicks(DrawingContext drawingContext, Rect panel)
    {
        var length = Math.Min(8.0, Math.Min(panel.Width, panel.Height) * 0.08);
        var inset = 4.0;
        var left = panel.Left + inset;
        var right = panel.Right - inset;
        var top = panel.Top + inset;
        var bottom = panel.Bottom - inset;

        drawingContext.DrawLine(TickPen, new Point(left, top), new Point(left + length, top));
        drawingContext.DrawLine(TickPen, new Point(left, top), new Point(left, top + length));
        drawingContext.DrawLine(TickPen, new Point(right, top), new Point(right - length, top));
        drawingContext.DrawLine(TickPen, new Point(right, top), new Point(right, top + length));
        drawingContext.DrawLine(TickPen, new Point(left, bottom), new Point(left + length, bottom));
        drawingContext.DrawLine(TickPen, new Point(left, bottom), new Point(left, bottom - length));
        drawingContext.DrawLine(TickPen, new Point(right, bottom), new Point(right - length, bottom));
        drawingContext.DrawLine(TickPen, new Point(right, bottom), new Point(right, bottom - length));
    }

    private static void DrawTouch(
        DrawingContext drawingContext,
        Rect panel,
        double x,
        double y,
        Brush fill,
        Pen ring)
    {
        var nx = Math.Clamp(x, 0.0, PadWidth - 1.0) / (PadWidth - 1.0);
        var ny = Math.Clamp(y, 0.0, PadHeight - 1.0) / (PadHeight - 1.0);
        var point = new Point(panel.X + nx * panel.Width, panel.Y + ny * panel.Height);

        drawingContext.DrawEllipse(null, ring, point, 7.0, 7.0);
        drawingContext.DrawEllipse(fill, null, point, 3.5, 3.5);
        drawingContext.DrawEllipse(TouchCoreBrush, null, point, 1.25, 1.25);
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(byte red, byte green, byte blue, double thickness)
    {
        var pen = new Pen(CreateBrush(red, green, blue), thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen CreateDashedPen(byte red, byte green, byte blue, double thickness)
    {
        var pen = new Pen(CreateBrush(red, green, blue), thickness)
        {
            DashStyle = new DashStyle(new double[] { 2.0, 2.5 }, 0.0),
            DashCap = PenLineCap.Flat
        };
        pen.Freeze();
        return pen;
    }
}
```

## 9. src\Ds4Cursor\MainWindow.xaml

```xml
<Window x:Class="Ds4Cursor.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:shell="clr-namespace:System.Windows.Shell;assembly=PresentationFramework"
        xmlns:controls="clr-namespace:Ds4Cursor.Controls"
        Title="DS4 Cursor"
        Width="800"
        Height="650"
        MinWidth="740"
        MinHeight="610"
        WindowStartupLocation="CenterScreen"
        WindowStyle="None"
        ResizeMode="CanResize"
        Background="{StaticResource BgBrush}"
        Foreground="{StaticResource TextBrush}"
        FontFamily="{StaticResource UiFont}"
        FontSize="12"
        Language="ru-RU"
        UseLayoutRounding="True"
        SnapsToDevicePixels="True"
        TextOptions.TextFormattingMode="Display"
        TextOptions.TextRenderingMode="Grayscale">

    <shell:WindowChrome.WindowChrome>
        <shell:WindowChrome CaptionHeight="40"
                            ResizeBorderThickness="5"
                            CornerRadius="0"
                            GlassFrameThickness="0"
                            UseAeroCaptionButtons="False" />
    </shell:WindowChrome.WindowChrome>

    <Border Background="{StaticResource BgBrush}"
            BorderBrush="{StaticResource BorderBrush}"
            BorderThickness="1">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="40" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>

            <!-- Заголовок окна -->
            <Border Grid.Row="0"
                    Background="{StaticResource SurfaceBrush}"
                    BorderBrush="{StaticResource BorderBrush}"
                    BorderThickness="0,0,0,1">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>

                    <StackPanel Grid.Column="0"
                                Orientation="Horizontal"
                                Margin="12,0,0,0"
                                VerticalAlignment="Center">
                        <Path Style="{StaticResource IconPathStyle}"
                              Width="18"
                              Height="18"
                              Stroke="{StaticResource AccentBrush}"
                              Data="M6 12h4M8 10v4M15 11h.01M18 9h.01M7.5 6h9A4.5 4.5 0 0 1 21 10.5v1A4.5 4.5 0 0 1 16.5 16c-1.2 0-2.3-.5-3.1-1.3L12 13.3l-1.4 1.4A4.3 4.3 0 0 1 7.5 16 4.5 4.5 0 0 1 3 11.5v-1A4.5 4.5 0 0 1 7.5 6Z" />
                        <TextBlock Text="DS4 CURSOR"
                                   Margin="8,0,0,0"
                                   FontSize="11.5"
                                   FontWeight="SemiBold"
                                   VerticalAlignment="Center" />
                        <TextBlock Text="1.0"
                                   Margin="8,1,0,0"
                                   FontFamily="{StaticResource MonoFont}"
                                   FontSize="9"
                                   Foreground="{StaticResource MutedBrush}"
                                   VerticalAlignment="Center" />
                    </StackPanel>

                    <StackPanel Grid.Column="2"
                                Orientation="Horizontal"
                                Margin="0,0,10,0"
                                VerticalAlignment="Center">
                        <Ellipse Style="{StaticResource StatusDotStyle}" />
                        <TextBlock Text="{Binding ModeTitle}"
                                   Margin="6,0,0,0"
                                   FontFamily="{StaticResource MonoFont}"
                                   FontSize="9"
                                   Foreground="{StaticResource MutedBrush}"
                                   VerticalAlignment="Center" />
                    </StackPanel>

                    <Button Grid.Column="3"
                            Style="{StaticResource WindowButtonStyle}"
                            Click="MinimizeToTray_Click"
                            ToolTip="Свернуть в трей"
                            shell:WindowChrome.IsHitTestVisibleInChrome="True">
                        <Path Style="{StaticResource IconPathStyle}"
                              Width="12"
                              Height="12"
                              Stroke="{StaticResource MutedBrush}"
                              Data="M5 12h14" />
                    </Button>

                    <Button Grid.Column="4"
                            Style="{StaticResource WindowButtonStyle}"
                            Click="Close_Click"
                            ToolTip="Закрыть"
                            shell:WindowChrome.IsHitTestVisibleInChrome="True">
                        <Path Style="{StaticResource IconPathStyle}"
                              Width="12"
                              Height="12"
                              Stroke="{StaticResource MutedBrush}"
                              Data="M18 6 6 18M6 6l12 12" />
                    </Button>
                </Grid>
            </Border>

            <!-- Двухколоночный пульт -->
            <Grid Grid.Row="1" Margin="12">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="10" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>

                <!-- Левая колонка -->
                <Grid Grid.Column="0">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="8" />
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="8" />
                        <RowDefinition Height="*" />
                    </Grid.RowDefinitions>

                    <!-- Состояние -->
                    <Border Grid.Row="0" Style="{StaticResource CardBorderStyle}" Padding="12">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                            </Grid.RowDefinitions>

                            <Grid>
                                <TextBlock Style="{StaticResource SectionLabelStyle}" Text="КОНТРОЛЛЕР" />
                                <Border Style="{StaticResource KeyCapStyle}"
                                        HorizontalAlignment="Right"
                                        Padding="5,1,5,1">
                                    <TextBlock Text="HID"
                                               FontFamily="{StaticResource MonoFont}"
                                               FontSize="8.5"
                                               Foreground="{StaticResource MutedBrush}" />
                                </Border>
                            </Grid>

                            <Grid Grid.Row="1" Margin="0,7,0,0">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="1.35*" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="*" />
                                </Grid.ColumnDefinitions>

                                <StackPanel Grid.Column="0">
                                    <TextBlock Style="{StaticResource SectionLabelStyle}" FontSize="9" Text="СОСТОЯНИЕ" />
                                    <StackPanel Orientation="Horizontal" Margin="0,3,0,0">
                                        <Ellipse Style="{StaticResource ConnectionDotStyle}" />
                                        <TextBlock Text="{Binding ConnectionText}"
                                                   Margin="6,0,0,0"
                                                   FontSize="11"
                                                   FontWeight="SemiBold" />
                                    </StackPanel>
                                </StackPanel>

                                <StackPanel Grid.Column="1">
                                    <TextBlock Style="{StaticResource SectionLabelStyle}" FontSize="9" Text="КАНАЛ" />
                                    <TextBlock Text="{Binding TransportText}"
                                               Style="{StaticResource MonoTextStyle}"
                                               Margin="0,3,0,0" />
                                </StackPanel>

                                <StackPanel Grid.Column="2">
                                    <TextBlock Style="{StaticResource SectionLabelStyle}" FontSize="9" Text="БАТАРЕЯ" />
                                    <TextBlock Text="{Binding BatteryText}"
                                               Style="{StaticResource MonoTextStyle}"
                                               Margin="0,3,0,0"
                                               TextTrimming="CharacterEllipsis" />
                                </StackPanel>
                            </Grid>

                            <Grid Grid.Row="2" Margin="0,7,0,0">
                                <TextBlock Text="{Binding ConnectionDetail}"
                                           Style="{StaticResource MutedTextStyle}"
                                           FontSize="10"
                                           Margin="0,0,8,0"
                                           TextTrimming="CharacterEllipsis" />
                                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                                    <TextBlock Text="{Binding TickRateText}"
                                               Style="{StaticResource MonoMutedStyle}"
                                               FontSize="9" />
                                    <TextBlock Text=" / "
                                               Style="{StaticResource MonoMutedStyle}"
                                               FontSize="9" />
                                    <TextBlock Text="{Binding MeasuredHzText}"
                                               Style="{StaticResource MonoMutedStyle}"
                                               FontSize="9" />
                                </StackPanel>
                            </Grid>
                        </Grid>
                    </Border>

                    <!-- Тачпад -->
                    <Border Grid.Row="2" Style="{StaticResource CardBorderStyle}" Padding="12">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                            </Grid.RowDefinitions>

                            <Grid>
                                <TextBlock Style="{StaticResource SectionLabelStyle}" Text="ТАЧПАД" />
                                <TextBlock Text="1920 × 942"
                                           Style="{StaticResource MonoMutedStyle}"
                                           FontSize="9"
                                           HorizontalAlignment="Right"
                                           ToolTip="Матрица сенсорной панели DualShock 4" />
                            </Grid>

                            <controls:StickVisualizer Grid.Row="1"
                                                      Height="142"
                                                      Margin="0,6,0,0"
                                                      HorizontalAlignment="Stretch"
                                                      Touch1X="{Binding Touch1X}"
                                                      Touch1Y="{Binding Touch1Y}"
                                                      Touch1Active="{Binding Touch1Active}"
                                                      Touch2X="{Binding Touch2X}"
                                                      Touch2Y="{Binding Touch2Y}"
                                                      Touch2Active="{Binding Touch2Active}"
                                                      IsTouchpadDown="{Binding IsTouchpadDown}"
                                                      ShowRightZone="{Binding Settings.CornerRightClick}"
                                                      IsHitTestVisible="False" />

                            <Grid Grid.Row="2" Margin="0,6,0,0">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>

                                <StackPanel Grid.Column="0" Orientation="Horizontal">
                                    <TextBlock Text="T1 "
                                               Style="{StaticResource MonoMutedStyle}"
                                               FontSize="9" />
                                    <TextBlock Text="{Binding Touch1Text}"
                                               Style="{StaticResource MonoTextStyle}"
                                               FontSize="9" />
                                </StackPanel>

                                <StackPanel Grid.Column="1" Orientation="Horizontal">
                                    <TextBlock Text="T2 "
                                               Style="{StaticResource MonoMutedStyle}"
                                               FontSize="9" />
                                    <TextBlock Text="{Binding Touch2Text}"
                                               Style="{StaticResource MonoTextStyle}"
                                               FontSize="9" />
                                </StackPanel>

                                <TextBlock Grid.Column="2"
                                           Text="{Binding TouchpadStateText}"
                                           Style="{StaticResource MonoMutedStyle}"
                                           FontSize="9"
                                           HorizontalAlignment="Right" />
                            </Grid>

                            <Grid Grid.Row="3" Margin="0,7,0,0">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="22" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="40" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Text="L2"
                                           Style="{StaticResource MonoMutedStyle}"
                                           FontSize="9"
                                           ToolTip="Снайперское замедление курсора" />
                                <ProgressBar Grid.Column="1"
                                             Style="{StaticResource ThinProgressBarStyle}"
                                             Value="{Binding L2Percent, Mode=OneWay}"
                                             VerticalAlignment="Center"
                                             Margin="0,0,8,0" />
                                <TextBlock Grid.Column="2"
                                           Text="{Binding L2Percent, StringFormat={}{0:0}%}"
                                           Style="{StaticResource MonoMutedStyle}"
                                           FontSize="9"
                                           HorizontalAlignment="Right" />
                            </Grid>
                        </Grid>
                    </Border>

                    <!-- Назначения -->
                    <Border Grid.Row="4" Style="{StaticResource CardBorderStyle}" Padding="0">
                        <Grid Margin="12,10,8,10">
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="*" />
                            </Grid.RowDefinitions>

                            <Grid>
                                <TextBlock Style="{StaticResource SectionLabelStyle}" Text="НАЗНАЧЕНИЯ" />
                                <TextBlock Text="СЛОЙ L1"
                                           Style="{StaticResource MonoMutedStyle}"
                                           FontSize="9"
                                           HorizontalAlignment="Right" />
                            </Grid>

                            <ScrollViewer Grid.Row="1"
                                          Margin="0,6,0,0"
                                          VerticalScrollBarVisibility="Auto"
                                          HorizontalScrollBarVisibility="Disabled"
                                          Focusable="False">
                                <StackPanel Margin="0,0,6,0">
                                    <TextBlock Text="БАЗА"
                                               Style="{StaticResource SectionLabelStyle}"
                                               FontSize="8.5"
                                               Margin="0,0,0,4" />

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="ТАЧПАД" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Курсор и левый клик" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="УГОЛ ПАНЕЛИ" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Правый клик" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="1 ПАЛЕЦ" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Лёгкий тап — левый клик" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="2 ПАЛЬЦА" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Скролл и правый клик" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="L2" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Снайперское замедление" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="R1" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Alt+Tab" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="OPTIONS" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Enter" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="SHARE" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Backspace" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="L3" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Экранная клавиатура" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,7">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="L3 + R3" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Режим игры, удержание 0,5 с" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <TextBlock Text="СЛОЙ L1"
                                               Style="{StaticResource SectionLabelStyle}"
                                               FontSize="8.5"
                                               Margin="0,0,0,4" />

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="L1 + ВВЕРХ/ВНИЗ" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Громкость системы" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="L1 + ВЛЕВО/ВПРАВО" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Предыдущий / следующий трек" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="L1 + КРЕСТ" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Воспроизведение / пауза" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="L1 + ТРЕУГОЛЬНИК" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Свернуть все окна (Win+D)" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>

                                    <Grid Margin="0,0,0,3">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="130" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <Border Style="{StaticResource KeyCapStyle}">
                                            <TextBlock Text="L1 + КРУГ" Style="{StaticResource MonoMutedStyle}" FontSize="8.5" />
                                        </Border>
                                        <TextBlock Grid.Column="1" Text="Закрыть вкладку (Ctrl+W)" Style="{StaticResource BodyTextStyle}" FontSize="10.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                                    </Grid>
                                </StackPanel>
                            </ScrollViewer>
                        </Grid>
                    </Border>
                </Grid>

                <!-- Правая колонка -->
                <Grid Grid.Column="2">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="8" />
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="8" />
                        <RowDefinition Height="*" />
                    </Grid.RowDefinitions>

                    <!-- Режим -->
                    <Border Grid.Row="0" Style="{StaticResource CardBorderStyle}" Padding="12">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                            </Grid.RowDefinitions>

                            <Grid>
                                <TextBlock Style="{StaticResource SectionLabelStyle}" Text="РЕЖИМ" />
                                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                                    <TextBlock Text="СКОРОСТЬ "
                                               Style="{StaticResource MonoMutedStyle}"
                                               FontSize="9" />
                                    <TextBlock Text="{Binding SpeedFactorText}"
                                               Style="{StaticResource MonoTextStyle}"
                                               FontSize="9" />
                                </StackPanel>
                            </Grid>

                            <Grid Grid.Row="1" Margin="0,7,0,0">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="*" />
                                </Grid.ColumnDefinitions>
                                <Ellipse Style="{StaticResource StatusDotStyle}"
                                         Width="7"
                                         Height="7"
                                         Margin="0,3,0,0"
                                         VerticalAlignment="Top" />
                                <StackPanel Grid.Column="1" Margin="8,0,0,0">
                                    <TextBlock Text="{Binding ModeTitle}"
                                               FontSize="17"
                                               FontWeight="SemiBold"
                                               TextTrimming="CharacterEllipsis" />
                                    <TextBlock Text="{Binding ModeSubtitle}"
                                               Style="{StaticResource MutedTextStyle}"
                                               FontSize="10.5"
                                               Margin="0,2,0,0"
                                               TextTrimming="CharacterEllipsis" />
                                </StackPanel>
                            </Grid>

                            <Grid Grid.Row="2" Margin="0,9,0,0">
                                <CheckBox Style="{StaticResource ToggleSwitchStyle}"
                                          Content="Эмуляция"
                                          IsChecked="{Binding Settings.Enabled, Mode=TwoWay}"
                                          VerticalAlignment="Center" />
                                <Button HorizontalAlignment="Right"
                                        Style="{StaticResource FlatButtonStyle}"
                                        Padding="9,5"
                                        Click="MinimizeToTray_Click"
                                        ToolTip="Свернуть в трей">
                                    <StackPanel Orientation="Horizontal">
                                        <Path Style="{StaticResource IconPathStyle}"
                                              Width="12"
                                              Height="12"
                                              Data="M5 12h14" />
                                        <TextBlock Text="В трей" Margin="6,0,0,0" FontSize="10.5" />
                                    </StackPanel>
                                </Button>
                            </Grid>
                        </Grid>
                    </Border>

                    <!-- Настройки -->
                    <Border Grid.Row="2" Style="{StaticResource CardBorderStyle}" Padding="12">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                            </Grid.RowDefinitions>

                            <Grid>
                                <TextBlock Style="{StaticResource SectionLabelStyle}" Text="НАСТРОЙКА ТАЧПАДА" />
                                <Button HorizontalAlignment="Right"
                                        Style="{StaticResource IconButtonStyle}"
                                        Width="24"
                                        Height="22"
                                        Click="ResetSettings_Click"
                                        ToolTip="Сбросить настройки">
                                    <Path Style="{StaticResource IconPathStyle}"
                                          Width="12"
                                          Height="12"
                                          Data="M21 12a9 9 0 1 1-2.64-6.36M21 3v6h-6" />
                                </Button>
                            </Grid>

                            <StackPanel Grid.Row="1" Margin="0,6,0,0">
                                <Grid Margin="0,0,0,4">
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="16" />
                                    </Grid.RowDefinitions>
                                    <Grid>
                                        <TextBlock Text="Чувствительность" Style="{StaticResource MutedTextStyle}" FontSize="10.5" />
                                        <TextBlock Text="{Binding Settings.TouchSensitivity, StringFormat={}{0:0.0}x}"
                                                   Style="{StaticResource MonoTextStyle}"
                                                   FontSize="10"
                                                   HorizontalAlignment="Right" />
                                    </Grid>
                                    <Slider Grid.Row="1"
                                            Style="{StaticResource FlatSliderStyle}"
                                            Minimum="0.5"
                                            Maximum="4.0"
                                            TickFrequency="0.1"
                                            SmallChange="0.1"
                                            LargeChange="0.5"
                                            IsSnapToTickEnabled="True"
                                            Value="{Binding Settings.TouchSensitivity, Mode=TwoWay}"
                                            Margin="0,2,0,0" />
                                </Grid>

                                <Grid Margin="0,0,0,4">
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="16" />
                                    </Grid.RowDefinitions>
                                    <Grid>
                                        <TextBlock Text="Ускорение (баллистика)" Style="{StaticResource MutedTextStyle}" FontSize="10.5" />
                                        <TextBlock Text="{Binding Settings.TouchAcceleration, StringFormat={}{0:0.0}}"
                                                   Style="{StaticResource MonoTextStyle}"
                                                   FontSize="10"
                                                   HorizontalAlignment="Right" />
                                    </Grid>
                                    <Slider Grid.Row="1"
                                            Style="{StaticResource FlatSliderStyle}"
                                            Minimum="0.0"
                                            Maximum="3.0"
                                            TickFrequency="0.1"
                                            SmallChange="0.1"
                                            LargeChange="0.5"
                                            IsSnapToTickEnabled="True"
                                            Value="{Binding Settings.TouchAcceleration, Mode=TwoWay}"
                                            Margin="0,2,0,0" />
                                </Grid>

                                <Grid Margin="0,0,0,4">
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="16" />
                                    </Grid.RowDefinitions>
                                    <Grid>
                                        <TextBlock Text="Множитель замедления L2" Style="{StaticResource MutedTextStyle}" FontSize="10.5" />
                                        <TextBlock Text="{Binding Settings.L2SlowdownPercent, StringFormat={}{0:0}%}"
                                                   Style="{StaticResource MonoTextStyle}"
                                                   FontSize="10"
                                                   HorizontalAlignment="Right" />
                                    </Grid>
                                    <Slider Grid.Row="1"
                                            Style="{StaticResource FlatSliderStyle}"
                                            Minimum="0.1"
                                            Maximum="1.0"
                                            TickFrequency="0.05"
                                            SmallChange="0.05"
                                            LargeChange="0.1"
                                            IsSnapToTickEnabled="True"
                                            Value="{Binding Settings.L2SlowdownFactor, Mode=TwoWay}"
                                            Margin="0,2,0,0" />
                                </Grid>

                                <Grid Margin="0,0,0,4">
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="16" />
                                    </Grid.RowDefinitions>
                                    <Grid>
                                        <TextBlock Text="Стабилизация клика" Style="{StaticResource MutedTextStyle}" FontSize="10.5" />
                                        <TextBlock Text="{Binding Settings.ClickFreezeMs, StringFormat={}{0:0} мс}"
                                                   Style="{StaticResource MonoTextStyle}"
                                                   FontSize="10"
                                                   HorizontalAlignment="Right" />
                                    </Grid>
                                    <Slider Grid.Row="1"
                                            Style="{StaticResource FlatSliderStyle}"
                                            Minimum="0"
                                            Maximum="120"
                                            TickFrequency="5"
                                            SmallChange="5"
                                            LargeChange="10"
                                            IsSnapToTickEnabled="True"
                                            Value="{Binding Settings.ClickFreezeMs, Mode=TwoWay}"
                                            Margin="0,2,0,0" />
                                </Grid>

                                <Grid Margin="0,0,0,4">
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="16" />
                                    </Grid.RowDefinitions>
                                    <Grid>
                                        <TextBlock Text="Скорость прокрутки" Style="{StaticResource MutedTextStyle}" FontSize="10.5" />
                                        <TextBlock Text="{Binding Settings.ScrollSpeed, StringFormat={}{0:0.0}}"
                                                   Style="{StaticResource MonoTextStyle}"
                                                   FontSize="10"
                                                   HorizontalAlignment="Right" />
                                    </Grid>
                                    <Slider Grid.Row="1"
                                            Style="{StaticResource FlatSliderStyle}"
                                            Minimum="1.0"
                                            Maximum="20.0"
                                            TickFrequency="0.5"
                                            SmallChange="0.5"
                                            LargeChange="2.0"
                                            IsSnapToTickEnabled="True"
                                            Value="{Binding Settings.ScrollSpeed, Mode=TwoWay}"
                                            Margin="0,2,0,0" />
                                </Grid>

                                <Grid>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="16" />
                                    </Grid.RowDefinitions>
                                    <Grid>
                                        <TextBlock Text="Частота опроса" Style="{StaticResource MutedTextStyle}" FontSize="10.5" />
                                        <TextBlock Text="{Binding Settings.TickRate, StringFormat={}{0} Гц}"
                                                   Style="{StaticResource MonoTextStyle}"
                                                   FontSize="10"
                                                   HorizontalAlignment="Right" />
                                    </Grid>
                                    <Slider Grid.Row="1"
                                            Style="{StaticResource FlatSliderStyle}"
                                            Minimum="125"
                                            Maximum="250"
                                            TickFrequency="5"
                                            SmallChange="5"
                                            LargeChange="25"
                                            IsSnapToTickEnabled="True"
                                            Value="{Binding Settings.TickRate, Mode=TwoWay}"
                                            Margin="0,2,0,0" />
                                </Grid>

                                <Grid Margin="0,7,0,0">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="0.48*" />
                                        <ColumnDefinition Width="0.52*" />
                                    </Grid.ColumnDefinitions>
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="Auto" />
                                    </Grid.RowDefinitions>

                                    <CheckBox Grid.Row="0"
                                              Grid.Column="0"
                                              Style="{StaticResource ToggleSwitchStyle}"
                                              Content="Тап 1 пальцем для клика"
                                              FontSize="9.5"
                                              ToolTip="Лёгкий тап одним пальцем — левый клик"
                                              IsChecked="{Binding Settings.TapToClick, Mode=TwoWay}" />

                                    <CheckBox Grid.Row="0"
                                              Grid.Column="1"
                                              Style="{StaticResource ToggleSwitchStyle}"
                                              Content="Правый клик в углу панели"
                                              FontSize="9.5"
                                              ToolTip="Правый клик при нажатии в правом нижнем углу панели"
                                              IsChecked="{Binding Settings.CornerRightClick, Mode=TwoWay}" />

                                    <CheckBox Grid.Row="1"
                                              Grid.Column="0"
                                              Margin="0,5,0,0"
                                              Style="{StaticResource ToggleSwitchStyle}"
                                              Content="Скролл 2 пальцами"
                                              FontSize="9.5"
                                              ToolTip="Двухпальцевый скролл страниц"
                                              IsChecked="{Binding Settings.TwoFingerScroll, Mode=TwoWay}" />

                                    <CheckBox Grid.Row="1"
                                              Grid.Column="1"
                                              Margin="0,5,0,0"
                                              Style="{StaticResource ToggleSwitchStyle}"
                                              Content="Инверсия скролла"
                                              FontSize="9.5"
                                              ToolTip="Поменять направление прокрутки"
                                              IsChecked="{Binding Settings.InvertScroll, Mode=TwoWay}" />
                                </Grid>
                            </StackPanel>
                        </Grid>
                    </Border>

                    <!-- Системная информация -->
                    <Border Grid.Row="4"
                            Style="{StaticResource CardBorderStyle}"
                            Padding="12"
                            VerticalAlignment="Top">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                                <RowDefinition Height="Auto" />
                            </Grid.RowDefinitions>

                            <Grid>
                                <TextBlock Style="{StaticResource SectionLabelStyle}" Text="СИСТЕМА" />
                                <TextBlock Text="HIDSHARP 2.6.4 / TOUCHPAD"
                                           Style="{StaticResource MonoMutedStyle}"
                                           FontSize="8.5"
                                           HorizontalAlignment="Right" />
                            </Grid>

                            <TextBlock Grid.Row="1"
                                       Margin="0,5,0,0"
                                       Style="{StaticResource MutedTextStyle}"
                                       FontSize="10"
                                       Text="Настройки: %APPDATA%\Ds4Cursor"
                                       TextTrimming="CharacterEllipsis" />

                            <Grid Grid.Row="2" Margin="0,8,0,0">
                                <Button Style="{StaticResource FlatButtonStyle}"
                                        Padding="8,5"
                                        Click="OpenConfigFolder_Click"
                                        ToolTip="Открыть папку настроек">
                                    <StackPanel Orientation="Horizontal">
                                        <Path Style="{StaticResource IconPathStyle}"
                                              Width="12"
                                              Height="12"
                                              Data="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z" />
                                        <TextBlock Text="Открыть папку" Margin="6,0,0,0" FontSize="10.5" />
                                    </StackPanel>
                                </Button>
                                <TextBlock Text="L3 + R3 / 0,5 с"
                                           Style="{StaticResource MonoMutedStyle}"
                                           FontSize="8.5"
                                           HorizontalAlignment="Right"
                                           VerticalAlignment="Center"
                                           ToolTip="Удерживайте L3 и R3 полсекунды для режима игры" />
                            </Grid>
                        </Grid>
                    </Border>
                </Grid>
            </Grid>
        </Grid>
    </Border>
</Window>
```

## 10. src\Ds4Cursor\Core\AppRuntime.cs

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Ds4Cursor.Views;

namespace Ds4Cursor.Core;

/// <summary>
/// Связывает настройки, HID-контроллер, движок ввода, окно, трей и OSD.
/// </summary>
public sealed class AppRuntime : IDisposable
{
    private readonly DispatcherTimer _saveTimer;
    private bool _disposed;

    public AppRuntime()
    {
        Settings = SettingsStore.Load();
        Controller = new ControllerManager();
        Engine = new InputEngine(Controller, Settings);
        ViewModel = new MainViewModel(Settings, Controller, Engine);

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _saveTimer.Tick += OnSaveTimerTick;

        Settings.PropertyChanged += OnSettingsChanged;
        Controller.ConnectionChanged += OnConnectionChanged;
        Engine.SleepModeChanged += OnSleepModeChanged;
    }

    public AppSettings Settings { get; }
    public ControllerManager Controller { get; }
    public InputEngine Engine { get; }
    public MainViewModel ViewModel { get; }
    public MainWindow? MainWindow { get; private set; }
    public TrayIcon? Tray { get; private set; }
    public OsdWindow? Osd { get; private set; }
    public bool IsExiting { get; private set; }

    public void Start()
    {
        Controller.Start();
        Engine.Start();

        Tray = new TrayIcon(this);
        Osd = new OsdWindow();
        MainWindow = new MainWindow(this);

        UpdateTray();
        MainWindow.Show();
        MainWindow.Activate();

        AppLog.Write("Приложение запущено.");
    }

    public void ToggleEnabled()
    {
        if (IsExiting)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(ToggleEnabled);
            return;
        }

        Settings.Enabled = !Settings.Enabled;
    }

    public void HideToTray()
    {
        if (IsExiting)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(HideToTray);
            return;
        }

        if (MainWindow == null)
        {
            return;
        }

        MainWindow.Hide();
        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }
    }

    public void ShowMainWindow()
    {
        if (IsExiting)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(ShowMainWindow);
            return;
        }

        if (MainWindow == null)
        {
            return;
        }

        if (!MainWindow.IsVisible)
        {
            MainWindow.Show();
        }

        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
        MainWindow.Focus();
    }

    public void ResetSettings()
    {
        if (IsExiting)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(ResetSettings);
            return;
        }

        Settings.Reset();
        Engine.UpdateConfig(Settings);
        ScheduleSave();
        ShowOsd("НАСТРОЙКИ СБРОШЕНЫ", "Параметры тачпада возвращены к значениям по умолчанию");
    }

    public void OpenConfigFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLog.ConfigFolder);
            Process.Start(new ProcessStartInfo(AppLog.ConfigFolder)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Write("Не удалось открыть папку настроек: " + ex.Message);
            ShowOsd("ОШИБКА", "Не удалось открыть папку настроек");
        }
    }

    public void ShowOsd(string title, string subtitle)
    {
        if (IsExiting)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ShowOsd(title, subtitle));
            return;
        }

        Osd ??= new OsdWindow();
        Osd.ShowMessage(title, subtitle);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Engine.UpdateConfig(Settings);

        if (e.PropertyName == nameof(AppSettings.Enabled))
        {
            UpdateTray();
            ShowOsd(
                Settings.Enabled ? "ЭМУЛЯЦИЯ ВКЛЮЧЕНА" : "ПАУЗА",
                Settings.Enabled ? "Курсор и клики с тачпада активны" : "Ввод с тачпада остановлен");
        }

        ScheduleSave();
    }

    private void OnConnectionChanged(object? sender, ControllerConnectionEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        dispatcher.BeginInvoke(UpdateTray);
    }

    private void OnSleepModeChanged(bool sleeping)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            if (IsExiting)
            {
                return;
            }

            UpdateTray();
            ShowOsd(
                sleeping ? "РЕЖИМ ИГРЫ" : "УПРАВЛЕНИЕ",
                sleeping ? "Тачпад не управляет курсором" : "Тачпад снова активен");
        });
    }

    private void UpdateTray()
    {
        Tray?.UpdateState(Settings.Enabled, Engine.SleepMode);
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        SettingsStore.Save(Settings);
    }

    public void Exit()
    {
        if (IsExiting)
        {
            return;
        }

        var app = Application.Current;
        var dispatcher = app?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Exit);
            return;
        }

        IsExiting = true;
        AppLog.Write("Завершение приложения.");
        _saveTimer.Stop();
        SettingsStore.Save(Settings);
        Cleanup();

        if (dispatcher != null)
        {
            dispatcher.BeginInvoke(() => app!.Shutdown(), DispatcherPriority.Normal);
        }
    }

    private void Cleanup()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Settings.PropertyChanged -= OnSettingsChanged;
        Controller.ConnectionChanged -= OnConnectionChanged;
        Engine.SleepModeChanged -= OnSleepModeChanged;
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimerTick;

        try
        {
            Engine.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("Ошибка остановки движка: " + ex);
        }

        try
        {
            Controller.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("Ошибка остановки HID: " + ex);
        }

        try
        {
            Tray?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("Ошибка удаления иконки трея: " + ex);
        }

        Tray = null;

        try
        {
            Osd?.CloseForExit();
        }
        catch (Exception ex)
        {
            AppLog.Write("Ошибка закрытия OSD: " + ex);
        }

        Osd = null;

        ViewModel.Dispose();
        MainWindow = null;
    }

    public void Dispose()
    {
        Cleanup();
    }
}
```

## 11. src\Ds4Cursor\Views\OsdWindow.xaml

```xml
<Window x:Class="Ds4Cursor.Views.OsdWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="DS4 Cursor"
        Width="336"
        Height="76"
        WindowStyle="None"
        AllowsTransparency="True"
        Background="Transparent"
        ShowInTaskbar="False"
        ShowActivated="False"
        Topmost="True"
        ResizeMode="NoResize"
        Focusable="False"
        IsHitTestVisible="False"
        Opacity="0"
        Language="ru-RU"
        FontFamily="Segoe UI Variable Text, Segoe UI"
        TextOptions.TextFormattingMode="Display"
        TextOptions.TextRenderingMode="Grayscale"
        UseLayoutRounding="True"
        SnapsToDevicePixels="True">
    <Border Background="#18181B"
            BorderBrush="#27272A"
            BorderThickness="1"
            CornerRadius="2">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="3" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>

            <Border Background="#3B82F6" />

            <StackPanel Grid.Column="1" Margin="13,9,14,9" VerticalAlignment="Center">
                <TextBlock x:Name="TitleText"
                           Text="ТАЧПАД"
                           FontSize="12"
                           FontWeight="SemiBold"
                           Foreground="#FAFAFA"
                           TextTrimming="CharacterEllipsis" />
                <TextBlock x:Name="SubtitleText"
                           Text="Курсор и клики активны"
                           Margin="0,3,0,0"
                           FontSize="10.5"
                           Foreground="#A1A1AA"
                           TextTrimming="CharacterEllipsis" />
            </StackPanel>
        </Grid>
    </Border>
</Window>
```

## 12. README.md

````markdown
# DS4 Cursor

Утилита для Windows, которая превращает сенсорную панель DualShock 4 в полноценный тачпад: перемещает курсор, выполняет клики, перетаскивание и двухпальцевую прокрутку. Контроллер читается напрямую через HID (USB или Bluetooth), без DS4Windows и без эмуляции виртуального геймпада. Ввод отправляется в систему через `SendInput`.

## Возможности

- Прямое чтение DualShock 4 через HidSharp: USB (отчёт `0x01`) и Bluetooth (отчёт `0x11`).
- Автоматическое подключение, отключение и переподключение контроллера.
- Управление курсором одним касанием сенсорной панели с привязкой к `TrackId`.
- Баллистическое ускорение, дробное накопление пикселей и защита от рывков на границах панели.
- Снайперское замедление на L2 с плавной кривой и настраиваемым множителем.
- Стабилизация клика: компенсация микро-сдвига, короткая заморозка курсора и разблокировка перетаскивания при заметном смещении.
- Физический клик тачпада: левая кнопка с удержанием и перетаскиванием, правая кнопка в нижнем правом углу панели.
- Жесты: тап одним пальцем, тап двумя пальцами для правого клика, двухпальцевая прокрутка с блокировкой курсора.
- Слой L1 для дополнительных действий.
- Режим игры: удержание L3 + R3 в течение 0,5 с отключает эмуляцию и пропускает ввод в игры.
- Иконка в системном трее, компактное OSD при смене режима, тёмный интерфейс.
- Настройки сохраняются в `%APPDATA%\Ds4Cursor\settings.json`, журнал пишется в `%APPDATA%\Ds4Cursor\app.log`.

## Требования

- Windows 10 или Windows 11, x64.
- .NET 8 SDK для сборки. Готовая single-file сборка SDK не требует.
- DualShock 4 (CUH-ZCT1 или CUH-ZCT2), подключённый по USB или Bluetooth.
- DS4Windows и другие программы, перехватывающие HID-устройство, должны быть закрыты.

## Сборка и запуск

```powershell
dotnet build Ds4Cursor.sln -c Debug
dotnet run --project src\Ds4Cursor\Ds4Cursor.csproj
```

Если в системе несколько SDK, версия зафиксирована в `global.json`.

## Публикация одним файлом

```powershell
dotnet publish src\Ds4Cursor\Ds4Cursor.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish
```

Результат: `publish\Ds4Cursor.exe` (self-contained, примерно 63–67 МБ). Его можно запускать на машине без установленного .NET.

## Управление

### Сенсорная панель

| Ввод | Действие |
| --- | --- |
| Одно касание | Перемещение курсора |
| Короткий тап одним пальцем | Левый клик |
| Нажатие тачпада | Левая кнопка мыши, удержание для перетаскивания |
| Нажатие тачпада в правом нижнем углу | Правая кнопка мыши |
| Короткий тап двумя пальцами | Правый клик |
| Движение двумя пальцами | Прокрутка по вертикали и горизонтали |
| L2 | Снайперское замедление курсора |

Границы правого нижнего угла: `X >= 1440` и `Y >= 550` в координатах панели `1920 × 942`. Зона выделена на визуализаторе и может быть отключена в настройках.

### Кнопки контроллера

| Ввод | Действие |
| --- | --- |
| R1 | Alt+Tab |
| Options | Enter |
| Share | Backspace |
| L3 | Экранная клавиатура Windows |
| L3 + R3 (удержание 0,5 с) | Режим игры: эмуляция приостановлена |

### Слой L1 (удерживать L1)

| Ввод | Действие |
| --- | --- |
| Крест | Воспроизведение / пауза |
| Круг | Закрыть вкладку (Ctrl+W) |
| Треугольник | Свернуть все окна (Win+D) |
| Крестовина вверх / вниз | Громкость системы, с автоповтором |
| Крестовина влево / вправо | Предыдущий / следующий трек |

Крест, круг, квадрат и треугольник без слоя L1 больше не вызывают клики мыши: все клики выполняются сенсорной панелью.

## Настройки

В окне доступны:

- чувствительность тачпада (0,5–4,0x, по умолчанию 1,6x);
- ускорение (баллистика) (0–3,0, по умолчанию 1,2);
- множитель замедления L2 (10–100%, по умолчанию 30%);
- стабилизация клика (0–120 мс, по умолчанию 50 мс);
- скорость прокрутки (1–20, по умолчанию 7);
- частота опроса (125–250 Гц, по умолчанию 200 Гц);
- переключатель «Тап 1 пальцем для клика»;
- переключатель «Правый клик в углу панели»;
- переключатель «Скролл 2 пальцами»;
- переключатель «Инверсия скролла».

Кнопка сброса возвращает значения по умолчанию. Изменения применяются на лету и сохраняются автоматически. Кнопка «В трей» скрывает окно; повторный запуск окна выполняется двойным щелчком по иконке в трее или через пункт «Открыть настройки».

## Как работает стабилизация клика

Перед нажатием тачпада движок хранит историю перемещений за последние 60 мс. В момент клика компенсируется микро-сдвиг за последние 35 мс, после чего курсор замораживается на заданное время (по умолчанию 50 мс). Если палец смещается более чем на 25 единиц панели, заморозка снимается и начинается обычное перетаскивание. Это позволяет нажимать на небольшие элементы без случайного смещения курсора.

## Формат HID-отчётов

- USB: Input Report `0x01`, 64 байта, смещение данных `0`.
- Bluetooth: Feature Report `0x02` переводит контроллер в расширенный режим, затем принимается Input Report `0x11`, 78 байт, смещение данных `2`.
- Координаты панели: `1920 × 942`.
- Кнопка тачпада: `report[offset + 7] & 0x02`.
- L2: `report[offset + 8]`.
- Батарея: `report[offset + 30]`, младшие 4 бита — уровень, бит `0x10` — зарядка.
- Касание 1: `offset + 35`, касание 2: `offset + 39`. Старший бит первого байта — признак неактивного касания, младшие 7 бит — `TrackId`.

## Архитектура

- `Core\ControllerSnapshot.cs` — неизменяемый снимок состояния, точки касания и кнопки.
- `Core\ControllerManager.cs` — фоновый поток HidSharp, поиск контроллера Sony, разбор USB/Bluetooth-отчётов, батарея, горячее подключение.
- `Core\InputEngine.cs` — поток опроса, дельты касаний, баллистика, L2, стабилизация клика, зоны клика, жесты, прокрутка и режим игры.
- `Core\InputSender.cs` — переиспользуемый буфер `SendInput` для мыши и клавиатуры, без выделений в горячем пути.
- `Core\AppSettings.cs` — параметры тачпада и переключатели жестов.
- `Core\EngineTelemetry.cs` — снимки для интерфейса без блокировок.
- `Core\AppRuntime.cs` — связывает настройки, контроллер, движок, окно, трей и OSD.
- `Core\MainViewModel.cs` — телеметрия для интерфейса: состояние, батарея, частота, координаты T1/T2, режим.
- `Controls\StickVisualizer.cs` — векторный визуализатор сенсорной панели с зоной правого клика и точками касаний.
- `Views\OsdWindow.xaml` — OSD без активации и без появления в Alt+Tab.
- `Themes\Theme.xaml` — палитра и стили интерфейса.

Поток HID блокируется на чтении отчётов и не нагружает процессор. Поток движка просыпается по событию с разрешением таймера 1 мс, обрабатывает снимок состояния и отправляет ввод только при наличии изменений. Интерфейс обновляется с частотой 30 Гц и останавливает таймер, когда окно скрыто.

## Ограничения

- Приложение не работает с играми, которые блокируют `SendInput` или требуют античит-совместимый виртуальный геймпад. Для таких игр используйте режим игры (L3 + R3), чтобы контроллер оставался доступен игре напрямую.
- Одновременная работа с DS4Windows не поддерживается: программа монопольно открывает HID-устройство.
- Для запуска поверх окон с повышенными правами может потребоваться запуск DS4 Cursor от имени администратора.
- Если контроллер не найден, закройте программы, перехватывающие HID, проверьте USB-кабель или сопряжение Bluetooth и журнал `%APPDATA%\Ds4Cursor\app.log`.
````

