using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using RepoOPS.Agents.Models;

namespace RepoOPS.Agents.Services;

public sealed class RunVerificationService(ILogger<RunVerificationService> logger)
{
    private readonly ILogger<RunVerificationService> _logger = logger;

    public async Task<RunVerificationRecord> ExecuteAsync(string? explicitCommand = null, string? workspaceRoot = null)
    {
        var startedAt = DateTime.UtcNow;
        var effectiveWorkspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? FindWorkspaceRoot()
            : workspaceRoot.Trim();
        var command = string.IsNullOrWhiteSpace(explicitCommand)
            ? BuildDefaultCommand(effectiveWorkspaceRoot)
            : explicitCommand.Trim();

        if (string.IsNullOrWhiteSpace(command))
        {
            return new RunVerificationRecord
            {
                VerificationId = Guid.NewGuid().ToString("N"),
                Name = "Default build verification",
                Command = "<none>",
                Status = "skipped",
                Passed = false,
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Summary = "No verification command available in the current environment."
            };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = effectiveWorkspaceRoot ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

            try
            {
                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var output = string.Concat(stdout, string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"\r\n[stderr]\r\n{stderr}");
                var passed = process.ExitCode == 0;
                var summary = passed
                    ? "Verification passed."
                    : $"Verification failed with exit code {process.ExitCode}.";

                _logger.LogInformation("Verification finished with exit code {ExitCode}.", process.ExitCode);

                return new RunVerificationRecord
                {
                    VerificationId = Guid.NewGuid().ToString("N"),
                    Name = "Workspace verification",
                    Command = command,
                    Status = passed ? "passed" : "failed",
                    Passed = passed,
                    ExitCode = process.ExitCode,
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow,
                    Summary = summary,
                    OutputPreview = TrimOutput(output)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Verification command execution failed.");
                return new RunVerificationRecord
                {
                    VerificationId = Guid.NewGuid().ToString("N"),
                    Name = "Workspace verification",
                    Command = command,
                    Status = "failed",
                    Passed = false,
                    ExitCode = -1,
                    StartedAt = startedAt,
                    CompletedAt = DateTime.UtcNow,
                    Summary = $"Verification execution failed: {ex.Message}",
                    OutputPreview = TrimOutput(ex.ToString())
                };
            }
    }

    private static string? FindWorkspaceRoot()
    {
        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        foreach (var candidate in candidates)
        {
            var current = new DirectoryInfo(candidate);
            while (current is not null)
            {
                if (current.GetFiles("*.sln").Length > 0)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        return candidates.FirstOrDefault();
    }

    private static string? BuildDefaultCommand(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        var sln = Directory.EnumerateFiles(workspaceRoot, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (sln is null)
        {
            var csproj = Directory.EnumerateFiles(workspaceRoot, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (csproj is not null)
            {
                return $"dotnet build '{csproj}'";
            }

            var packageJson = Path.Combine(workspaceRoot, "package.json");
            if (File.Exists(packageJson))
            {
                return "npm run build";
            }

            return null;
        }

        return $"dotnet build '{sln}'";
    }

    private static string TrimOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Trim();
        return text.Length <= 4000 ? text : text[^4000..];
    }
}
