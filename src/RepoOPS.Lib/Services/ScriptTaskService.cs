using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RepoOPS.Hubs;
using RepoOPS.Models;

namespace RepoOPS.Services;

/// <summary>
/// Service for executing PowerShell scripts and managing running tasks.
/// </summary>
public sealed class ScriptTaskService(
    IHubContext<TaskHub> hubContext,
    ConfigService configService,
    ILogger<ScriptTaskService> logger)
{
    private readonly ConcurrentDictionary<string, RunningTask> _runningTasks = new();

    public async Task<string> StartTaskAsync(string taskId)
    {
        var config = configService.LoadConfig();
        var taskItem = FindTask(config, taskId);

        if (taskItem == null)
        {
            throw new InvalidOperationException($"Task '{taskId}' not found in configuration.");
        }

        var executionId = $"{taskId}_{DateTime.UtcNow:yyyyMMddHHmmssff}";

        // Auto-detect: if script ends with .ps1 → PowerShell; otherwise → native command
        var isPowerShell = taskItem.Script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        string? scriptPath = null;

        if (isPowerShell)
        {
            var basePath = configService.GetScriptsBasePath(config);
            scriptPath = ConfigService.ResolveScriptPath(taskItem.Script, basePath);

            if (!File.Exists(scriptPath))
            {
                await hubContext.Clients.All.SendAsync("TaskOutput", executionId,
                    $"\x1b[31mError: Script file not found: {scriptPath}\x1b[0m\r\n");
                throw new FileNotFoundException($"Script not found: {scriptPath}");
            }
        }

        // Resolve working directory
        string workingDir;
        if (!string.IsNullOrWhiteSpace(taskItem.WorkingDirectory))
        {
            workingDir = taskItem.WorkingDirectory;
        }
        else if (!string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory))
        {
            workingDir = config.DefaultWorkingDirectory;
        }
        else if (scriptPath != null)
        {
            workingDir = Path.GetDirectoryName(scriptPath) ?? ".";
        }
        else
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            workingDir = exeDir;
        }

        if (!Path.IsPathRooted(workingDir))
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            workingDir = Path.GetFullPath(Path.Combine(exeDir, workingDir));
        }

        var runningTask = new RunningTask
        {
            ExecutionId = executionId,
            Task = taskItem,
            StartedAt = DateTime.UtcNow
        };

        _runningTasks.TryAdd(executionId, runningTask);

        await hubContext.Clients.All.SendAsync("TaskStarted", executionId, taskItem.Name);

        if (isPowerShell)
        {
            _ = Task.Run(() => ExecuteScriptAsync(runningTask, scriptPath!, workingDir));
        }
        else
        {
            _ = Task.Run(() => ExecuteCommandAsync(runningTask, workingDir));
        }

        return executionId;
    }

    public async Task StopTaskAsync(string executionId)
    {
        if (_runningTasks.TryGetValue(executionId, out var runningTask))
        {
            await runningTask.CancellationSource.CancelAsync();

            try
            {
                if (runningTask.Process is { HasExited: false } process)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error killing process for task {ExecutionId}.", executionId);
            }

            runningTask.IsRunning = false;
            await hubContext.Clients.All.SendAsync("TaskOutput", executionId,
                "\r\n\x1b[33m[Task cancelled by user]\x1b[0m\r\n");
            await hubContext.Clients.All.SendAsync("TaskCompleted", executionId, -1);
        }
    }

    public IReadOnlyList<RunningTaskInfo> GetRunningTasks()
    {
        return _runningTasks.Values
            .Where(t => t.IsRunning)
            .Select(t => new RunningTaskInfo(t.ExecutionId, t.Task.Name, t.StartedAt))
            .ToList();
    }

    /// <summary>
    /// Execute a native command directly (non-.ps1), e.g. adb, python, dotnet, etc.
    /// The 'script' field can be just an executable name ("adb") or a full command line
    /// ("adb -s 12345 install -r"). The first token is used as FileName, the rest
    /// is prepended to 'arguments'. Stdout/stderr are captured and streamed in real time.
    /// </summary>
    private async Task ExecuteCommandAsync(RunningTask runningTask, string workingDir)
    {
        var executionId = runningTask.ExecutionId;
        var cancellationToken = runningTask.CancellationSource.Token;

        try
        {
            // Split script field: first token = executable, rest = arguments prefix
            var (command, scriptArgs) = ParseCommandLine(runningTask.Task.Script);
            var extraArgs = runningTask.Task.Arguments ?? string.Empty;
            var arguments = string.IsNullOrWhiteSpace(scriptArgs)
                ? extraArgs
                : string.IsNullOrWhiteSpace(extraArgs)
                    ? scriptArgs
                    : $"{scriptArgs} {extraArgs}";

            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            startInfo.Environment["TERM"] = "xterm-256color";
            startInfo.Environment["PYTHONUTF8"] = "1";

            await hubContext.Clients.All.SendAsync("TaskOutput", executionId,
                $"\x1b[36m> {command} {arguments}\x1b[0m\r\n\x1b[36m> Working directory: {workingDir}\x1b[0m\r\n\r\n",
                cancellationToken);

            using var process = new Process { StartInfo = startInfo };
            runningTask.Process = process;

            process.Start();

            var stdoutTask = ReadStreamAsync(process.StandardOutput, executionId, cancellationToken);
            var stderrTask = ReadStreamAsync(process.StandardError, executionId, cancellationToken, isError: true);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(cancellationToken);

            runningTask.ExitCode = process.ExitCode;
            runningTask.IsRunning = false;

            var exitColor = process.ExitCode == 0 ? "\x1b[32m" : "\x1b[31m";
            await hubContext.Clients.All.SendAsync("TaskOutput", executionId,
                $"\r\n{exitColor}[Process exited with code {process.ExitCode}]\x1b[0m\r\n",
                cancellationToken);

            await hubContext.Clients.All.SendAsync("TaskCompleted", executionId, process.ExitCode,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Task {ExecutionId} was cancelled.", executionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing task {ExecutionId}.", executionId);
            runningTask.IsRunning = false;
            await hubContext.Clients.All.SendAsync("TaskOutput", executionId,
                $"\r\n\x1b[31m[Error: {ex.Message}]\x1b[0m\r\n");
            await hubContext.Clients.All.SendAsync("TaskCompleted", executionId, -1);
        }
        finally
        {
            _ = Task.Delay(TimeSpan.FromMinutes(30)).ContinueWith(_ =>
            {
                _runningTasks.TryRemove(executionId, out RunningTask? _);
            });
        }
    }

    private async Task ExecuteScriptAsync(RunningTask runningTask, string scriptPath, string workingDir)
    {
        var executionId = runningTask.ExecutionId;
        var cancellationToken = runningTask.CancellationSource.Token;

        try
        {
            // Use -EncodedCommand to set UTF-8 output encoding before running the script.
            // On Chinese Windows, pwsh defaults to OEM codepage (GBK/936) for redirected stdout,
            // causing CJK characters to become '??' when read as UTF-8.
            // Use *>&1 to merge ALL PowerShell streams (including Write-Host / Information stream)
            // into stdout as plain text, preventing CLIXML serialization on stderr.
            var escapedPath = scriptPath.Replace("'", "''");
            var psCommand = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                            "$OutputEncoding = [System.Text.Encoding]::UTF8; " +
                            $"& '{escapedPath}'";
            if (!string.IsNullOrWhiteSpace(runningTask.Task.Arguments))
            {
                psCommand += $" {runningTask.Task.Arguments}";
            }
            psCommand += " *>&1; exit $LASTEXITCODE";

            var encodedCommand = Convert.ToBase64String(
                System.Text.Encoding.Unicode.GetBytes(psCommand));
            var arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}";

            // Display-friendly version for the terminal UI
            var displayArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
            if (!string.IsNullOrWhiteSpace(runningTask.Task.Arguments))
            {
                displayArgs += $" {runningTask.Task.Arguments}";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            startInfo.Environment["TERM"] = "xterm-256color";

            await hubContext.Clients.All.SendAsync("TaskOutput", executionId,
                $"\x1b[36m> pwsh {displayArgs}\x1b[0m\r\n\x1b[36m> Working directory: {workingDir}\x1b[0m\r\n\r\n",
                cancellationToken);

            using var process = new Process { StartInfo = startInfo };
            runningTask.Process = process;

            process.Start();

            // All PS streams are merged to stdout via *>&1
            var stdoutTask = ReadStreamAsync(process.StandardOutput, executionId, cancellationToken);
            // Drain stderr to prevent buffer deadlock (should be empty thanks to *>&1)
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await stdoutTask;
            await stderrTask;
            await process.WaitForExitAsync(cancellationToken);

            runningTask.ExitCode = process.ExitCode;
            runningTask.IsRunning = false;

            var exitColor = process.ExitCode == 0 ? "\x1b[32m" : "\x1b[31m";
            await hubContext.Clients.All.SendAsync("TaskOutput", executionId,
                $"\r\n{exitColor}[Process exited with code {process.ExitCode}]\x1b[0m\r\n",
                cancellationToken);

            await hubContext.Clients.All.SendAsync("TaskCompleted", executionId, process.ExitCode,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Task {ExecutionId} was cancelled.", executionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing task {ExecutionId}.", executionId);
            runningTask.IsRunning = false;
            await hubContext.Clients.All.SendAsync("TaskOutput", executionId,
                $"\r\n\x1b[31m[Error: {ex.Message}]\x1b[0m\r\n");
            await hubContext.Clients.All.SendAsync("TaskCompleted", executionId, -1);
        }
        finally
        {
            _ = Task.Delay(TimeSpan.FromMinutes(30)).ContinueWith(_ =>
            {
                _runningTasks.TryRemove(executionId, out RunningTask? _);
            });
        }
    }

    private async Task ReadStreamAsync(StreamReader reader, string executionId,
        CancellationToken cancellationToken, bool isError = false)
    {
        var buffer = new char[1024];
        int charsRead;

        while ((charsRead = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var text = new string(buffer, 0, charsRead);
            text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");

            if (isError)
            {
                text = $"\x1b[31m{text}\x1b[0m";
            }

            await hubContext.Clients.All.SendAsync("TaskOutput", executionId, text, cancellationToken);
        }
    }

    private static TaskItem? FindTask(TaskConfig config, string taskId)
    {
        return config.Groups
            .SelectMany(g => g.Tasks)
            .FirstOrDefault(t => t.Id == taskId);
    }

    /// <summary>
    /// Parse a command line string into (executable, remaining arguments).
    /// Handles quoted executables like: "C:\Program Files\adb.exe" -s 12345
    /// </summary>
    private static (string Command, string Arguments) ParseCommandLine(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input))
            return (string.Empty, string.Empty);

        string command;
        string rest;

        if (input.StartsWith('"'))
        {
            // Quoted executable path: "C:\path\to\exe" args...
            var endQuote = input.IndexOf('"', 1);
            if (endQuote > 0)
            {
                command = input[1..endQuote];
                rest = input[(endQuote + 1)..].TrimStart();
            }
            else
            {
                command = input[1..];
                rest = string.Empty;
            }
        }
        else
        {
            // Simple: first space-separated token
            var spaceIdx = input.IndexOf(' ');
            if (spaceIdx > 0)
            {
                command = input[..spaceIdx];
                rest = input[(spaceIdx + 1)..].TrimStart();
            }
            else
            {
                command = input;
                rest = string.Empty;
            }
        }

        return (command, rest);
    }
}

public sealed record RunningTaskInfo(string ExecutionId, string TaskName, DateTime StartedAt);
