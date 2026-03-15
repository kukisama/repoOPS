using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace RepoOPS.Agents.Services;

internal static class RunCsvLogService
{
    private static readonly ConcurrentDictionary<string, object> s_fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding s_utf8WithBom = new(true);

    public static string? Append(string? workspaceRoot, string? runId, string type, string content)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var logPath = GetLogPath(workspaceRoot, runId);
        var fileLock = s_fileLocks.GetOrAdd(logPath, _ => new object());
        lock (fileLock)
        {
            var fileInfo = new FileInfo(logPath);
            Directory.CreateDirectory(fileInfo.DirectoryName!);

            var writeHeader = !fileInfo.Exists || fileInfo.Length == 0;
            using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, s_utf8WithBom);
            if (writeHeader)
            {
                writer.WriteLine("时间戳,类型,事件内容");
            }

            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            writer.WriteLine(string.Join(",",
                EscapeCsv(timestamp),
                EscapeCsv(Flatten(type)),
                EscapeCsv(Flatten(content))));
        }

        return logPath;
    }

    public static string BuildDetails(params (string Key, object? Value)[] fields)
    {
        var parts = new List<string>();
        foreach (var (key, value) in fields)
        {
            if (value is null)
            {
                continue;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalized = Flatten(text);
            parts.Add(string.IsNullOrWhiteSpace(key) ? normalized : $"{key}={normalized}");
        }

        return string.Join("；", parts);
    }

    public static string GetLogPath(string workspaceRoot, string runId)
    {
        var normalizedWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        return Path.Combine(normalizedWorkspaceRoot, ".repoops", "logs", "runs", $"{runId}.log");
    }

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Flatten(string value)
        => value
            .Replace("\r\n", " | ", StringComparison.Ordinal)
            .Replace("\n", " | ", StringComparison.Ordinal)
            .Replace("\r", " | ", StringComparison.Ordinal)
            .Trim();
}
