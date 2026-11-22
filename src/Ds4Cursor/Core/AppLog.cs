using System.Globalization;
using System.IO;

namespace Ds4Cursor.Core;

/// <summary>
/// Минимальный потокобезопасный журнал. Пишется только при ошибках и смене состояния.
/// </summary>
public static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string FolderPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ds4Cursor");

    public static string ConfigFolder => FolderPath;
    public static string LogPath => Path.Combine(FolderPath, "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(FolderPath);
                var file = new FileInfo(LogPath);
                if (file.Exists && file.Length > 512 * 1024)
                {
                    var oldPath = Path.Combine(FolderPath, "app.old.log");
                    File.Copy(LogPath, oldPath, true);
                    File.WriteAllText(LogPath, string.Empty);
                }

                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                           + "  " + message + Environment.NewLine;
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Журнал не должен влиять на работу приложения.
        }
    }
}
