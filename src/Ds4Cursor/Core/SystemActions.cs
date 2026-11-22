using System.Diagnostics;

namespace Ds4Cursor.Core;

internal static class SystemActions
{
    public static void ToggleOnScreenKeyboard()
    {
        try
        {
            var processes = Process.GetProcessesByName("osk");
            if (processes.Length > 0)
            {
                foreach (var process in processes)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("Не удалось закрыть экранную клавиатуру: " + ex.Message);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                return;
            }

            Process.Start(new ProcessStartInfo("osk.exe")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Write("Не удалось открыть экранную клавиатуру: " + ex.Message);
        }
    }
}
