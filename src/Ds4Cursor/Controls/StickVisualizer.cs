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
