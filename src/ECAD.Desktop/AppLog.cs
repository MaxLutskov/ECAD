using System.Reflection;
using System.Text;

namespace ECAD.Desktop;

public static class AppLog
{
    private static readonly object Gate = new();
    public static string LogPath { get; } = ResolvePath();

    public static void Write(string context, Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            var entry = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] ECAD {version}")
                .AppendLine(context)
                .AppendLine(exception.ToString())
                .AppendLine(new string('-', 80))
                .ToString();
            lock (Gate) File.AppendAllText(LogPath, entry, Encoding.UTF8);
        }
        catch
        {
            // Logging must never cause a second application failure.
        }
    }

    public static string ReadTail(int maxCharacters = 16000)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(LogPath)) return "Журнал поки порожній.";
                var content = File.ReadAllText(LogPath, Encoding.UTF8);
                return content.Length <= maxCharacters ? content : content[^maxCharacters..];
            }
        }
        catch (Exception ex)
        {
            return "Не вдалося прочитати журнал: " + ex.Message;
        }
    }

    private static string ResolvePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("ECAD_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) localData = AppContext.BaseDirectory;
        return Path.Combine(localData, "ECAD", "logs", "ecad.log");
    }
}
