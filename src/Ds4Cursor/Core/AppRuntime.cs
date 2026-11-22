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
