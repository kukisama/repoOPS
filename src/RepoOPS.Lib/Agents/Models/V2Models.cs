namespace RepoOPS.Agents.Models;

/// <summary>
/// A V2 orchestration run — self-driving, template-based multi-agent execution.
/// </summary>
public sealed class V2Run
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = "draft"; // draft | planning | running | reviewing | completed | failed
    public int CurrentRound { get; set; }
    public int MaxRounds { get; set; } = 6;
    public string? WorkspaceRoot { get; set; }
    public string? ExecutionRoot { get; set; }
    public List<string> AdditionalAllowedDirectories { get; set; } = [];

    /// <summary>Main thread PTY session id (if running).</summary>
    public string? MainThreadSessionId { get; set; }
    public string? MainThreadStatus { get; set; } // idle | running | waiting | completed

    /// <summary>Workers spawned per round.</summary>
    public List<V2Worker> Workers { get; set; } = [];

    /// <summary>Round-by-round audit trail.</summary>
    public List<V2RoundRecord> Rounds { get; set; } = [];

    /// <summary>Decision log.</summary>
    public List<V2Decision> Decisions { get; set; } = [];

    public bool ReviewForced { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class V2Worker
{
    public string WorkerId { get; set; } = Guid.NewGuid().ToString("N");
    public string RoleId { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "idle"; // idle | running | completed | failed | stopped
    public int AssignedRound { get; set; }
    public string? LastPrompt { get; set; }
    public string? ResultMarkdown { get; set; }
    public string? LastOutputPreview { get; set; }
    public string? PtySessionId { get; set; }
    public string? CommandPreview { get; set; }
    public int? ExitCode { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class V2RoundRecord
{
    public int RoundNumber { get; set; }
    public string Phase { get; set; } = "dispatch"; // dispatch | waiting | review | complete
    public string? MainThreadSummary { get; set; }
    public List<V2WorkerResult> WorkerResults { get; set; } = [];
    public string? ReviewerVerdict { get; set; }
    public bool AllReportedDone { get; set; }
    public bool ReviewPassed { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public sealed class V2WorkerResult
{
    public string WorkerId { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? ResultMarkdown { get; set; }
    public int? ExitCode { get; set; }
}

public sealed class V2Decision
{
    public string DecisionId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "note"; // run-created | round-started | worker-dispatched | round-completed | review-triggered | run-completed
    public string Summary { get; set; } = string.Empty;
}

// ── API request/response models ──

public sealed class CreateV2RunRequest
{
    public string Goal { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? WorkspaceRoot { get; set; }
    public int MaxRounds { get; set; } = 6;
    public bool AutoStart { get; set; } = true;
}

public sealed class V2RunSnapshot
{
    public V2Run Run { get; set; } = new();
    public List<V2Worker> Workers { get; set; } = [];
    public List<V2RoundRecord> Rounds { get; set; } = [];
    public List<V2Decision> Decisions { get; set; } = [];
}
