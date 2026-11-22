using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Ds4Cursor.Core;

namespace Ds4Cursor;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private AppRuntime? _runtime;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\Ds4Cursor.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "DS4 Cursor уже запущен. Закройте предыдущий экземпляр приложения.",
                "DS4 Cursor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _runtime = new AppRuntime();
            _runtime.Start();
        }
        catch (Exception ex)
        {
            AppLog.Write("Не удалось запустить приложение: " + ex);
            MessageBox.Show(
                "Не удалось запустить DS4 Cursor.\n\n" + ex.Message,
                "DS4 Cursor",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("Необработанная ошибка интерфейса: " + e.Exception);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            AppLog.Write("Необработанная ошибка процесса: " + ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Write("Необработанная ошибка задачи: " + e.Exception);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _runtime?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("Ошибка завершения: " + ex);
        }

        _runtime = null;

        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Мьютекс уже освобождён или принадлежит другому потоку.
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;

        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        base.OnExit(e);
    }
}
