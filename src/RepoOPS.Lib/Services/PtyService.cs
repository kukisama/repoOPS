using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RepoOPS.Hubs;

namespace RepoOPS.Services;

/// <summary>
/// Service for managing ConPTY-based interactive terminal sessions.
/// Each session runs a command inside a Windows pseudo-console and streams
/// output through SignalR while accepting keyboard input.
/// </summary>
public sealed class PtyService(
    IHubContext<PtyHub> hubContext,
    ConfigService configService,
    ILogger<PtyService> logger)
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();

    /// <summary>Fired when any PTY session completes. Args: (sessionId, exitCode).</summary>
    public event Action<string, int>? SessionCompleted;

    private sealed class SessionState
    {
        public required PtySession Session { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required string TranscriptPath { get; init; }
        public required StreamWriter TranscriptWriter { get; init; }
        public required object TranscriptSync { get; init; }
    }

    /// <summary>
    /// Start a new PTY session for the given task.
    /// Returns the session id used to identify this session in subsequent calls.
    /// </summary>
    public string StartSession(string taskId, int cols, int rows)
    {
        var config = configService.LoadConfig();
        var taskItem = config.Groups.SelectMany(g => g.Tasks).FirstOrDefault(t => t.Id == taskId);

        if (taskItem == null)
            throw new InvalidOperationException($"Task '{taskId}' not found in configuration.");

        // Build command line
        var isPowerShell = taskItem.Script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        string commandLine;

        if (isPowerShell)
        {
            var basePath = configService.GetScriptsBasePath(config);
            var scriptPath = ConfigService.ResolveScriptPath(taskItem.Script, basePath);
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Script not found: {scriptPath}");

            var args = string.IsNullOrWhiteSpace(taskItem.Arguments) ? "" : $" {taskItem.Arguments}";
            // Use pwsh with explicit UTF-8 encoding so xterm.js receives correct characters
            commandLine = $"pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -Command " +
                          $"\"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                          $"& '{scriptPath}'{args}; exit $LASTEXITCODE\"";
        }
        else
        {
            var extra = string.IsNullOrWhiteSpace(taskItem.Arguments) ? "" : $" {taskItem.Arguments}";
            commandLine = $"{taskItem.Script}{extra}";
        }

        // Resolve working directory — same chain as ScriptTaskService
        string workingDir;
        if (!string.IsNullOrWhiteSpace(taskItem.WorkingDirectory))
        {
            workingDir = taskItem.WorkingDirectory;
        }
        else if (!string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory))
        {
            workingDir = config.DefaultWorkingDirectory;
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

        var clampedCols = (short)Math.Clamp(cols, 20, 500);
        var clampedRows = (short)Math.Clamp(rows, 5, 200);

        var session = PtySession.Create(commandLine, workingDir, clampedCols, clampedRows);
        var cts = new CancellationTokenSource();
        var transcriptPath = PrepareTranscriptPath(workingDir, session.SessionId);
        var transcriptWriter = CreateTranscriptWriter(transcriptPath, session.SessionId, workingDir, commandLine);
        var state = new SessionState
        {
            Session = session,
            Cts = cts,
            TranscriptPath = transcriptPath,
            TranscriptWriter = transcriptWriter,
            TranscriptSync = new object()
        };

        _sessions.TryAdd(session.SessionId, state);

        logger.LogInformation("PTY session {SessionId} started for task {TaskId}, command: {Command}",
            session.SessionId, taskId, commandLine);
        logger.LogInformation("PTY session {SessionId} transcript: {TranscriptPath}",
            session.SessionId, transcriptPath);

        // Start background output reader
        _ = Task.Run(() => MonitorOutputAsync(state), CancellationToken.None);

        return session.SessionId;
    }

    public async Task SendInputAsync(string sessionId, string data)
    {
        if (!_sessions.TryGetValue(sessionId, out var state)) return;
        try
        {
            AppendTranscript(state, $"\r\n>>> [INPUT {DateTime.Now:O}]\r\n{data}");

            var bytes = Encoding.UTF8.GetBytes(data);
            await state.Session.InputStream.WriteAsync(bytes);
            await state.Session.InputStream.FlushAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SendInput to {SessionId} failed (session may have ended).", sessionId);
        }
    }

    /// <summary>
    /// Start a PTY session from a raw command line (not tied to a task config).
    /// Used by V2 orchestrator to launch visible terminal sessions.
    /// </summary>
    public string StartRawSession(string commandLine, string workingDirectory, int cols, int rows)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            throw new ArgumentException("commandLine is required.", nameof(commandLine));

        var workingDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory
            : workingDirectory;

        if (!Path.IsPathRooted(workingDir))
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            workingDir = Path.GetFullPath(Path.Combine(exeDir, workingDir));
        }

        var clampedCols = (short)Math.Clamp(cols, 20, 500);
        var clampedRows = (short)Math.Clamp(rows, 5, 200);

        var session = PtySession.Create(commandLine, workingDir, clampedCols, clampedRows);
        var cts = new CancellationTokenSource();
        var transcriptPath = PrepareTranscriptPath(workingDir, session.SessionId);
        var transcriptWriter = CreateTranscriptWriter(transcriptPath, session.SessionId, workingDir, commandLine);
        var state = new SessionState
        {
            Session = session,
            Cts = cts,
            TranscriptPath = transcriptPath,
            TranscriptWriter = transcriptWriter,
            TranscriptSync = new object()
        };

        _sessions.TryAdd(session.SessionId, state);

        logger.LogInformation("PTY raw session {SessionId} started, command: {Command}",
            session.SessionId, commandLine);
        logger.LogInformation("PTY raw session {SessionId} transcript: {TranscriptPath}",
            session.SessionId, transcriptPath);

        _ = Task.Run(() => MonitorOutputAsync(state), CancellationToken.None);

        return session.SessionId;
    }

    public void Resize(string sessionId, int cols, int rows)
    {
        if (!_sessions.TryGetValue(sessionId, out var state)) return;
        var c = (short)Math.Clamp(cols, 20, 500);
        var r = (short)Math.Clamp(rows, 5, 200);
        state.Session.Resize(c, r);
    }

    public string? GetTranscriptPath(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var state)
            ? state.TranscriptPath
            : null;
    }

    public void StopSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var state)) return;
        state.Cts.Cancel();
        AppendTranscript(state, $"\r\n>>> [LIFECYCLE {DateTime.Now:O}] session stopped by user\r\n");
        DisposeTranscript(state);
        state.Session.Dispose();
        state.Cts.Dispose();
        logger.LogInformation("PTY session {SessionId} stopped by user.", sessionId);
    }

    private async Task MonitorOutputAsync(SessionState state)
    {
        var sessionId = state.Session.SessionId;
        var buffer = new byte[4096];
        var completionSignaled = 0; // 0 = not yet, 1 = done (Interlocked CAS)

        // Watcher: when the child process exits, signal completion but keep the terminal open.
        // The read loop stays alive so the user can scroll back through history.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!state.Cts.IsCancellationRequested)
                {
                    var exitCode = state.Session.GetExitCode();
                    if (exitCode != null)
                    {
                        // Process exited. Give remaining output 1.5s to flush through the pipe.
                        await Task.Delay(1500);

                        if (Interlocked.CompareExchange(ref completionSignaled, 1, 0) == 0)
                        {
                            // Print a visible completion banner into the terminal
                            var icon = exitCode == 0 ? "✅" : "❌";
                            var banner = $"\r\n\x1b[36m━━━ {icon} 进程已退出 (exit code: {exitCode}) — 终端保持打开，可回滚查看历史 ━━━\x1b[0m\r\n";
                            AppendTranscript(state, $"\r\n>>> [LIFECYCLE {DateTime.Now:O}] process exited with code {(int)exitCode}\r\n");
                            await hubContext.Clients.All.SendAsync("PtyOutput", sessionId, banner,
                                cancellationToken: CancellationToken.None);

                            // Signal completion to frontend and in-proc subscribers
                            await hubContext.Clients.All.SendAsync("PtyCompleted", sessionId, (int)exitCode,
                                cancellationToken: CancellationToken.None);

                            try { SessionCompleted?.Invoke(sessionId, (int)exitCode); }
                            catch (Exception cbEx) { logger.LogWarning(cbEx, "SessionCompleted callback error for {SessionId}", sessionId); }

                            logger.LogInformation("PTY session {SessionId} process exited (code {ExitCode}), terminal kept open.",
                                sessionId, exitCode);
                        }
                        break;
                    }
                    await Task.Delay(500, state.Cts.Token);
                }
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { logger.LogDebug(ex, "Process-exit watcher for {SessionId} error.", sessionId); }
        });

        try
        {
            while (!state.Cts.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await state.Session.OutputStream.ReadAsync(
                        buffer, state.Cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (bytesRead == 0) break; // EOF — pseudo console was closed (e.g. StopSession)

                var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                AppendTranscript(state, text);
                await hubContext.Clients.All.SendAsync("PtyOutput", sessionId, text,
                    cancellationToken: CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Output monitor for {SessionId} ended with error.", sessionId);
        }

        // Read loop exited (EOF from StopSession/Dispose, or cancellation).
        // If the watcher already signaled completion, skip duplicate signaling.
        if (Interlocked.CompareExchange(ref completionSignaled, 1, 0) == 0)
        {
            state.Session.WaitForExit();
            var exitCode = state.Session.GetExitCode() ?? -1;

            await hubContext.Clients.All.SendAsync("PtyCompleted", sessionId, exitCode,
                cancellationToken: CancellationToken.None);

            try { SessionCompleted?.Invoke(sessionId, exitCode); }
            catch (Exception cbEx) { logger.LogWarning(cbEx, "SessionCompleted callback error for {SessionId}", sessionId); }

            logger.LogInformation("PTY session {SessionId} completed with exit code {ExitCode}.",
                sessionId, exitCode);
        }

        // Only clean up if the read loop ended (EOF / cancellation).
        // If the watcher handled completion and the read loop is still blocked,
        // cleanup will happen later via StopSession.
        if (_sessions.TryRemove(sessionId, out _))
        {
            DisposeTranscript(state);
            state.Session.Dispose();
            state.Cts.Dispose();
        }
    }

    private static string PrepareTranscriptPath(string workingDir, string sessionId)
    {
        var folder = Path.Combine(workingDir, ".repoops", "terminal-logs", DateTime.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{DateTime.UtcNow:HHmmssfff}-{sessionId}.log");
    }

    private static StreamWriter CreateTranscriptWriter(string transcriptPath, string sessionId, string workingDir, string commandLine)
    {
        var stream = new FileStream(transcriptPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        writer.WriteLine($">>> RepoOPS PTY transcript");
        writer.WriteLine($">>> sessionId: {sessionId}");
        writer.WriteLine($">>> startedAt: {DateTime.UtcNow:O}");
        writer.WriteLine($">>> workingDir: {workingDir}");
        writer.WriteLine($">>> commandLine: {commandLine}");
        writer.WriteLine();

        return writer;
    }

    private static void AppendTranscript(SessionState state, string text)
    {
        lock (state.TranscriptSync)
        {
            state.TranscriptWriter.Write(text);
        }
    }

    private static void DisposeTranscript(SessionState state)
    {
        lock (state.TranscriptSync)
        {
            try
            {
                state.TranscriptWriter.WriteLine();
                state.TranscriptWriter.WriteLine($">>> endedAt: {DateTime.UtcNow:O}");
                state.TranscriptWriter.Dispose();
            }
            catch
            {
                // Ignore transcript disposal errors.
            }
        }
    }
}
