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
