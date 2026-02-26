using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RepoOPS.Services;

namespace RepoOPS.Hubs;

/// <summary>
/// SignalR hub for real-time task execution communication.
/// Clients connect to this hub to receive live output from running tasks.
/// </summary>
public sealed class TaskHub(ScriptTaskService taskService, ILogger<TaskHub> logger) : Hub
{
    public async Task<string> StartTask(string taskId)
    {
        logger.LogInformation("Client {ConnectionId} requested to start task {TaskId}.",
            Context.ConnectionId, taskId);

        try
        {
            var executionId = await taskService.StartTaskAsync(taskId);
            return executionId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start task {TaskId}.", taskId);
            throw;
        }
    }

    public async Task StopTask(string executionId)
    {
        logger.LogInformation("Client {ConnectionId} requested to stop task {ExecutionId}.",
            Context.ConnectionId, executionId);

        await taskService.StopTaskAsync(executionId);
    }

    public override Task OnConnectedAsync()
    {
        logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
