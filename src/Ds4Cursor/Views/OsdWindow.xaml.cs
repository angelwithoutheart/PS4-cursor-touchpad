using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ds4Cursor.Core;

namespace Ds4Cursor.Views;

/// <summary>
/// Немигающий OSD-индикатор режима. Не забирает фокус и не появляется в Alt+Tab.
/// </summary>
public partial class OsdWindow : Window
{
    private readonly DispatcherTimer _hideTimer;
    private bool _closing;

    public OsdWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(1550)
        };
        _hideTimer.Tick += OnHideTimerTick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        extendedStyle |= NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(extendedStyle));
    }

    public void ShowMessage(string title, string subtitle)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowMessage(title, subtitle));
            return;
        }

        if (_closing)
        {
            return;
        }

        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        PositionNearTaskbar();

        if (!IsVisible)
        {
            Show();
        }

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fadeIn);

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        FadeOut();
    }

    private void FadeOut()
    {
        var fadeOut = new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (_, _) =>
        {
            if (_closing)
            {
                return;
            }

            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
        };

        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void PositionNearTaskbar()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - Width - 20);
        Top = Math.Max(workArea.Top, workArea.Bottom - Height - 20);
    }

    public void CloseForExit()
    {
        _closing = true;
        _hideTimer.Stop();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hideTimer.Stop();
        _hideTimer.Tick -= OnHideTimerTick;
        base.OnClosed(e);
    }
}
