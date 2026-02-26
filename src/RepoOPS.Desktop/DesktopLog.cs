using System.Text;

namespace RepoOPS.Desktop;

internal static class DesktopLog
{
    private static readonly object Sync = new();
    private static string? _file;

    public static void Initialize(string filePath)
    {
        _file = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory);
        Info("Logger initialized");
    }

    public static string GetLogFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepoOPS", "Logs");
        var file = $"repoops-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log";
        return Path.Combine(dir, file);
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(Exception ex, string message) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        if (_file is null)
        {
            return;
        }

        lock (Sync)
        {
            var sb = new StringBuilder();
            sb.Append(DateTimeOffset.Now.ToString("O"));
            sb.Append(' ');
            sb.Append(level);
            sb.Append(' ');
            sb.Append(message);
            if (ex is not null)
            {
                sb.AppendLine();
                sb.Append(ex);
            }
            sb.AppendLine();

            File.AppendAllText(_file, sb.ToString(), Encoding.UTF8);
        }
    }
}
