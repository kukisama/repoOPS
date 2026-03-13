using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RepoOPS.Services;

namespace RepoOPS.Hubs;

/// <summary>
/// SignalR hub for interactive PTY terminal sessions.
/// Clients connect here to start shells, send keyboard input, and receive live output.
/// </summary>
public sealed class PtyHub(PtyService ptyService, ILogger<PtyHub> logger) : Hub
{
    /// <summary>Start a new PTY session for the given task. Returns the session id.</summary>
    public string StartPtyTask(string taskId, int cols, int rows)
    {
        logger.LogInformation("Client {ConnectionId} requested PTY for task {TaskId}",
            Context.ConnectionId, taskId);
        return ptyService.StartSession(taskId, cols, rows);
    }

    /// <summary>Start a raw PTY session from a command line (used by V2 orchestrator). Returns the session id.</summary>
    public string StartRawPtySession(string commandLine, string workingDirectory, int cols, int rows)
    {
        logger.LogInformation("Client {ConnectionId} requested raw PTY session, cwd: {Cwd}",
            Context.ConnectionId, workingDirectory);
        return ptyService.StartRawSession(commandLine, workingDirectory, cols, rows);
    }

    /// <summary>Send keyboard input (raw characters) to the running PTY session.</summary>
    public Task SendPtyInput(string sessionId, string data)
        => ptyService.SendInputAsync(sessionId, data);

    /// <summary>Inform the server that the client terminal was resized.</summary>
    public void ResizePty(string sessionId, int cols, int rows)
        => ptyService.Resize(sessionId, cols, rows);

    /// <summary>Forcefully stop a running PTY session.</summary>
    public void StopPtyTask(string sessionId)
    {
        logger.LogInformation("Client {ConnectionId} stopping PTY session {SessionId}",
            Context.ConnectionId, sessionId);
        ptyService.StopSession(sessionId);
    }

    public override Task OnConnectedAsync()
    {
        logger.LogDebug("PtyHub client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogDebug("PtyHub client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
