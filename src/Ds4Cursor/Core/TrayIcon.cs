using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Ds4Cursor.Core;

namespace Ds4Cursor.Core;

/// <summary>
/// Иконка в системном трее с компактным контекстным меню в стиле приложения.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppRuntime _runtime;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly Icon _icon;
    private readonly Font _menuFont;
    private bool _disposed;

    public TrayIcon(AppRuntime runtime)
    {
        _runtime = runtime;
        _icon = CreateIcon();
        _menuFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        _toggleItem = CreateItem("Пауза", (_, _) => _runtime.ToggleEnabled());
        var openItem = CreateItem("Открыть настройки", (_, _) => _runtime.ShowMainWindow());
        var exitItem = CreateItem("Выход", (_, _) => _runtime.Exit());

        _menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(0x18, 0x18, 0x1B),
            ForeColor = Color.FromArgb(0xFA, 0xFA, 0xFA),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(0, 4, 0, 4),
            Font = _menuFont,
            Renderer = new DarkMenuRenderer(),
            RenderMode = ToolStripRenderMode.Professional
        };

        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "DS4 Cursor",
            ContextMenuStrip = _menu,
            Visible = true
        };

        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
    }

    private ToolStripMenuItem CreateItem(string text, EventHandler onClick)
    {
        return new ToolStripMenuItem(text, null, onClick)
        {
            ForeColor = Color.FromArgb(0xFA, 0xFA, 0xFA),
            BackColor = Color.FromArgb(0x18, 0x18, 0x1B),
            Padding = new Padding(10, 5, 22, 5)
        };
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _runtime.ShowMainWindow();
        }
    }

    public void UpdateState(bool enabled, bool sleeping)
    {
        _toggleItem.Text = enabled ? "Пауза" : "Включить";

        var status = sleeping
            ? "Режим игры"
            : enabled
                ? "Управление включено"
                : "Пауза";

        var text = "DS4 Cursor / " + status;
        if (text.Length > 63)
        {
            text = text[..63];
        }

        _notifyIcon.Text = text;
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(1200, title, message, ToolTipIcon.None);
        }
        catch
        {
            // Уведомление не критично.
        }
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var body = new GraphicsPath();
        body.AddEllipse(4, 9, 24, 14);
        body.AddEllipse(7, 15, 8, 10);
        body.AddEllipse(17, 15, 8, 10);

        using var accent = new SolidBrush(Color.FromArgb(0x3B, 0x82, 0xF6));
        graphics.FillPath(accent, body);

        using var dark = new SolidBrush(Color.FromArgb(0x18, 0x18, 0x1B));
        graphics.FillRectangle(dark, 9, 14, 6, 2);
        graphics.FillRectangle(dark, 11, 12, 2, 6);
        graphics.FillEllipse(dark, 19, 13, 2, 2);
        graphics.FillEllipse(dark, 22, 15, 2, 2);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.MouseDoubleClick -= OnMouseDoubleClick;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _menuFont.Dispose();
        _icon.Dispose();
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.FromArgb(0xFA, 0xFA, 0xFA);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(0x18, 0x18, 0x1B);
        public override Color ImageMarginGradientBegin => Color.FromArgb(0x18, 0x18, 0x1B);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(0x18, 0x18, 0x1B);
        public override Color ImageMarginGradientEnd => Color.FromArgb(0x18, 0x18, 0x1B);
        public override Color MenuBorder => Color.FromArgb(0x27, 0x27, 0x2A);
        public override Color MenuItemBorder => Color.FromArgb(0x27, 0x27, 0x2A);
        public override Color MenuItemSelected => Color.FromArgb(0x27, 0x27, 0x2A);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0x27, 0x27, 0x2A);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0x27, 0x27, 0x2A);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(0x1F, 0x1F, 0x23);
        public override Color MenuItemPressedGradientMiddle => Color.FromArgb(0x1F, 0x1F, 0x23);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(0x1F, 0x1F, 0x23);
        public override Color SeparatorDark => Color.FromArgb(0x27, 0x27, 0x2A);
        public override Color SeparatorLight => Color.FromArgb(0x27, 0x27, 0x2A);
    }
}
