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
