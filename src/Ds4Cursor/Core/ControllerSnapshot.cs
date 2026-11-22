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
