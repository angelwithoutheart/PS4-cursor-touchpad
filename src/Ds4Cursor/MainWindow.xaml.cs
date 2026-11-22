using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Ds4Cursor.Core;

namespace Ds4Cursor;

public partial class MainWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly DispatcherTimer _uiTimer;

    public MainWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        DataContext = runtime.ViewModel;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _uiTimer.Tick += OnUiTick;

        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _runtime.ViewModel.Refresh();
        StartRendering();
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        _runtime.ViewModel.Refresh();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            StartRendering();
        }
        else
        {
            StopRendering();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _runtime.HideToTray();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_runtime.IsExiting)
        {
            StopRendering();
            return;
        }

        e.Cancel = true;
        _runtime.Exit();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _runtime.HideToTray();
        }
    }

    private void StartRendering()
    {
        if (!_uiTimer.IsEnabled)
        {
            _uiTimer.Start();
        }
    }

    private void StopRendering()
    {
        if (_uiTimer.IsEnabled)
        {
            _uiTimer.Stop();
        }
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        _runtime.HideToTray();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _runtime.Exit();
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        _runtime.ResetSettings();
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        _runtime.OpenConfigFolder();
    }
}
