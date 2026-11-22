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
