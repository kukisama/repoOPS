namespace RepoOPS.Agents.Models;

public sealed class SupervisorRun
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public int RoundNumber { get; set; }
    public string? ExecutionRoot { get; set; }
    public string? WorkspaceRoot { get; set; }
    public string? WorkspaceName { get; set; }
    public List<string> AdditionalAllowedDirectories { get; set; } = [];
    public string? RoundHistoryDocumentPath { get; set; }
    public bool UsesManualWorkspaceRoot { get; set; }
    public bool AutoPilotEnabled { get; set; } = true;
    public int AutoStepCount { get; set; }
    public int MaxAutoSteps { get; set; } = 6;
    public bool PendingAutoStepRequested { get; set; }
    public bool PendingAutoStepRunVerification { get; set; } = true;
    public string? PendingAutoStepInstruction { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? ActiveLaneId { get; set; }
    public string? ActiveSurfaceId { get; set; }
    public string? FocusSuggestionSurfaceId { get; set; }
    public List<AgentWorkerSession> Workers { get; set; } = [];
    public List<ExecutionLane> Lanes { get; set; } = [];
    public List<AttentionEvent> Attention { get; set; } = [];
    public List<SupervisorDecisionEntry> Decisions { get; set; } = [];
    public string? LatestSummary { get; set; }
    public string? LastSupervisorCommandPreview { get; set; }
    public string? AssistantPlanId { get; set; }
    public string? AssistantPlanSummary { get; set; }
    public string? AssistantSkillFilePath { get; set; }
    public string? AssistantSkillSummary { get; set; }
    public int? AssistantPlanningBatchSize { get; set; }
    public int? AssistantMaxRounds { get; set; }
    public bool AssistantFullAuto { get; set; }
    public int? AssistantActiveRoundNumber { get; set; }
    public string? AssistantActiveRoundTitle { get; set; }
    public string? AssistantActiveRoundObjective { get; set; }
    public string? AssistantActiveWriterRoleId { get; set; }
    public string? AssistantActiveWriterWorkerId { get; set; }
    public RunVerificationRecord? LastVerification { get; set; }
    public List<RunVerificationRecord> VerificationHistory { get; set; } = [];
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
    public bool NeedsAttention { get; set; }
    public int UnreadCount { get; set; }
    public string? AttentionLevel { get; set; }
    public string? LastAttentionMessage { get; set; }
    public int? AssistantAssignedRoundNumber { get; set; }
    public string? AssistantAssignedRoundTitle { get; set; }
    public string? AssistantRoundObjective { get; set; }
    public bool AssistantCanWriteCode { get; set; }
    public string? AssistantOutputKind { get; set; }
    public string? AssistantRoleMode { get; set; }
    public DateTime? LastSurfaceActivityAt { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? ExitCode { get; set; }
}

public sealed class ExecutionLane
{
    public string LaneId { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Collapsed { get; set; }
    public List<string> SurfaceIds { get; set; } = [];
    public string? LatestAttentionLevel { get; set; }
}

public sealed class ExecutionSurface
{
    public string SurfaceId { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public string LaneId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = "idle";
    public string DisplayName { get; set; } = string.Empty;
    public string? RoleId { get; set; }
    public string? WorkerId { get; set; }
    public string? VerificationId { get; set; }
    public string? SessionId { get; set; }
    public string? WorkspacePath { get; set; }
    public string? CurrentTask { get; set; }
    public string? CommandPreview { get; set; }
    public string? LastSummary { get; set; }
    public string? LastReportedStatus { get; set; }
    public string? LastNextStep { get; set; }
    public string? LastOutputPreview { get; set; }
    public bool NeedsAttention { get; set; }
    public int UnreadCount { get; set; }
    public string? AttentionLevel { get; set; }
    public string? LastAttentionMessage { get; set; }
    public int? AssistantAssignedRoundNumber { get; set; }
    public string? AssistantAssignedRoundTitle { get; set; }
    public string? AssistantRoundObjective { get; set; }
    public bool AssistantCanWriteCode { get; set; }
    public string? AssistantOutputKind { get; set; }
    public string? AssistantRoleMode { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? ExitCode { get; set; }
}

public sealed class AttentionEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public string? SurfaceId { get; set; }
    public string? WorkerId { get; set; }
    public string Kind { get; set; } = "note";
    public string Level { get; set; } = "neutral";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public bool IsResolved { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public sealed class RunSnapshot
{
    public SupervisorRun Run { get; set; } = new();
    public List<ExecutionLane> Lanes { get; set; } = [];
    public List<ExecutionSurface> Surfaces { get; set; } = [];
    public List<AttentionEvent> Attention { get; set; } = [];
    public List<SupervisorDecisionEntry> Decisions { get; set; } = [];
    public List<RunVerificationRecord> Verifications { get; set; } = [];
    public AssistantPlan? AssistantPlan { get; set; }
    public List<AssistantArtifactStatus> AssistantArtifacts { get; set; } = [];
    public string? RoundHistoryContent { get; set; }
    public RunSnapshotSummary Summary { get; set; } = new();
}

public sealed class AssistantArtifactStatus
{
    public string RoundId { get; set; } = string.Empty;
    public int RoundNumber { get; set; }
    public string ArtifactName { get; set; } = string.Empty;
    public string ArtifactPath { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool IsRoundDeliverable { get; set; }
    public bool IsRoleOutput { get; set; }
    public string? RoleId { get; set; }
    public string? RoleName { get; set; }
}

public sealed class RunSnapshotSummary
{
    public int RunningSurfaces { get; set; }
    public int QueuedSurfaces { get; set; }
    public int CompletedSurfaces { get; set; }
    public int FailedSurfaces { get; set; }
    public int NeedsAttention { get; set; }
    public int UnreadAttention { get; set; }
    public int ResolvedAttention { get; set; }
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
    public string? WorkspaceName { get; set; }
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

public sealed class SurfaceFocusIntentRequest
{
    public bool AcknowledgeRelatedAttention { get; set; } = true;
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
