namespace RepoOPS.Agents.Models;

public sealed class SupervisorRun
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public string? WorkspaceRoot { get; set; }
    public bool AutoPilotEnabled { get; set; } = true;
    public int AutoStepCount { get; set; }
    public int MaxAutoSteps { get; set; } = 6;
    public bool PendingAutoStepRequested { get; set; }
    public bool PendingAutoStepRunVerification { get; set; } = true;
    public string? PendingAutoStepInstruction { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<AgentWorkerSession> Workers { get; set; } = [];
    public List<SupervisorDecisionEntry> Decisions { get; set; } = [];
    public string? LatestSummary { get; set; }
    public string? LastSupervisorCommandPreview { get; set; }
    public RunVerificationRecord? LastVerification { get; set; }
}

public sealed class AgentWorkerSession
{
    public string WorkerId { get; set; } = Guid.NewGuid().ToString("N");
    public string RoleId { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string? RoleDescription { get; set; }
    public string? Icon { get; set; }
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
    public string Status { get; set; } = "idle";
    public string? WorkspacePath { get; set; }
    public string? CurrentTask { get; set; }
    public string? LastPrompt { get; set; }
    public string? EffectiveCommandPreview { get; set; }
    public string? LastSummary { get; set; }
    public string? LastReportedStatus { get; set; }
    public string? LastNextStep { get; set; }
    public bool HasStructuredReport { get; set; }
    public string? LastOutputPreview { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? ExitCode { get; set; }
}

public sealed class SupervisorDecisionEntry
{
    public string DecisionId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "note";
    public string Summary { get; set; } = string.Empty;
}

public sealed class CreateSupervisorRunRequest
{
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string? WorkspaceRoot { get; set; }
    public List<string> RoleIds { get; set; } = [];
    public bool AutoStart { get; set; } = true;
    public bool AutoPilotEnabled { get; set; } = true;
    public int MaxAutoSteps { get; set; } = 6;
}

public sealed class ContinueWorkerRequest
{
    public string? Prompt { get; set; }
}

public sealed class AskSupervisorRequest
{
    public string? ExtraInstruction { get; set; }
}

public sealed class AutoStepRequest
{
    public string? ExtraInstruction { get; set; }
    public bool RunVerificationFirst { get; set; } = true;
}

public sealed class RunVerificationRequest
{
    public string? Command { get; set; }
}

public sealed class RunVerificationRecord
{
    public string VerificationId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public bool Passed { get; set; }
    public int? ExitCode { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    public string? Summary { get; set; }
    public string? OutputPreview { get; set; }
}
