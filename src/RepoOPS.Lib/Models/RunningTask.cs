using System.Diagnostics;

namespace RepoOPS.Models;

public sealed class RunningTask
{
    public required string ExecutionId { get; init; }
    public required TaskItem Task { get; init; }
    public DateTime StartedAt { get; init; }
    public int? ExitCode { get; set; }
    public bool IsRunning { get; set; } = true;
    public Process? Process { get; set; }
    public CancellationTokenSource CancellationSource { get; } = new();
}
