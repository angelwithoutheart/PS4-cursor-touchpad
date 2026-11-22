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
