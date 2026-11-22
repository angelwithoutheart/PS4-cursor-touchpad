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
