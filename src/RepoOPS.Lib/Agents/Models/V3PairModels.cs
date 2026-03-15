namespace RepoOPS.Agents.Models;

public sealed class V3PairRun
{
	public string RunId { get; set; } = Guid.NewGuid().ToString("N");
	public string Title { get; set; } = string.Empty;
	public string Goal { get; set; } = string.Empty;
	public int InitialPlanVersion { get; set; }
	public bool AwaitingInitialApproval { get; set; }
	public string? InitialPlanStatus { get; set; }
	public int InitialPlanRejectedCount { get; set; }
	public string? LastInitialPlanReviewComment { get; set; }
	public DateTime? InitialPlanApprovedAt { get; set; }
	public string? InitialPlanRoundGoal { get; set; }
	public string? InitialPlanTaskCard { get; set; }
	public string? InitialPlanReviewFocus { get; set; }
	public string? InitialPlanSummary { get; set; }
	public string? InitialPlanOutputPath { get; set; }
	public string? StagePlanSummary { get; set; }
	public string? CurrentStageLabel { get; set; }
	public string? CurrentStageGoal { get; set; }
	public string? ArchitectureGuardrails { get; set; }
	public string? LatestChangeDecision { get; set; }
	public string Status { get; set; } = "draft"; // draft | planning | awaiting-approval | running | reviewing | completed | failed | stopped
	public int CurrentRound { get; set; }
	public int MaxRounds { get; set; } = 6;
	public string? WorkspaceRoot { get; set; }
	public string? ExecutionRoot { get; set; }
	public string? WorkspaceName { get; set; }
	public string? WorkspaceMetadataFile { get; set; }
	public string? AllowedPathsFile { get; set; }
	public string? AllowedToolsFile { get; set; }
	public string? AllowedUrlsFile { get; set; }
	public List<string> AdditionalAllowedDirectories { get; set; } = [];

	public string MainRoleId { get; set; } = string.Empty;
	public string MainRoleName { get; set; } = string.Empty;
	public string? MainRoleIcon { get; set; }
	public string SubRoleId { get; set; } = string.Empty;
	public string SubRoleName { get; set; } = string.Empty;
	public string? SubRoleIcon { get; set; }

	public string? MainThreadSessionId { get; set; }
	public string? MainThreadStatus { get; set; } = "idle";
	public string? MainThreadCommandPreview { get; set; }
	public string? SubThreadSessionId { get; set; }
	public string? SubThreadStatus { get; set; } = "idle";
	public string? SubThreadCommandPreview { get; set; }

	public string? LatestTaskCard { get; set; }
	public string? LatestReviewFocus { get; set; }
	public string? LatestSublineSummary { get; set; }
	public string? LatestMainReviewSummary { get; set; }
	public string? LatestMainDirective { get; set; }
	public string? LatestVerdict { get; set; }
	public string? LatestGoalStatus { get; set; }
	public bool GoalCompleted { get; set; }
	public string? PendingInterjectionText { get; set; }
	public DateTime? PendingInterjectionUpdatedAt { get; set; }
	public bool PendingInterjectionUseWingman { get; set; }
	public string? PendingInterjectionWingmanText { get; set; }
	public DateTime? PendingInterjectionWingmanUpdatedAt { get; set; }
	public string? LastAppliedInterjectionText { get; set; }
	public string? LastAppliedInterjectionWingmanText { get; set; }
	public DateTime? LastAppliedInterjectionAt { get; set; }
	public int? LastAppliedInterjectionRound { get; set; }
	public string? LastAppliedInterjectionPhase { get; set; }
	public bool RecoveredFromStorage { get; set; }
	public string? LastContinueInstruction { get; set; }
	public int? LastContinueRoundIncrement { get; set; }

	public List<V3PairRoundRecord> Rounds { get; set; } = [];
	public List<V3PairDecision> Decisions { get; set; } = [];

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class V3PairRoundRecord
{
	public int RoundNumber { get; set; }
	public string Status { get; set; } = "planning";
	public string? StageLabel { get; set; }
	public string? StageGoal { get; set; }
	public string? Objective { get; set; }
	public string? TaskCard { get; set; }
	public string? ReviewFocus { get; set; }
	public string? MainPlanSummary { get; set; }
	public string? MainPlanOutputPath { get; set; }
	public string? SublineStatus { get; set; }
	public string? SublineSummary { get; set; }
	public string? SublineFacts { get; set; }
	public string? SublineAdjustments { get; set; }
	public string? SublineQuestions { get; set; }
	public string? SublineNext { get; set; }
	public string? SublineOutputPath { get; set; }
	public string? ReviewVerdict { get; set; }
	public bool ContinueRequested { get; set; }
	public string? GoalStatus { get; set; }
	public bool GoalCompleted { get; set; }
	public string? ReviewSummary { get; set; }
	public string? ChangeDecision { get; set; }
	public string? ReviewDirective { get; set; }
	public string? ReviewOutputPath { get; set; }
	public DateTime StartedAt { get; set; } = DateTime.UtcNow;
	public DateTime? CompletedAt { get; set; }
}

public sealed class V3PairDecision
{
	public string DecisionId { get; set; } = Guid.NewGuid().ToString("N");
	public string Kind { get; set; } = "note";
	public string Summary { get; set; } = string.Empty;
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class CreateV3PairRunRequest
{
	public string Goal { get; set; } = string.Empty;
	public string? Title { get; set; }
	public string? WorkspaceRoot { get; set; }
	public string? WorkspaceName { get; set; }
	public int MaxRounds { get; set; } = 6;
	public bool AutoStart { get; set; } = true;
	public string? MainRoleId { get; set; }
	public string? SubRoleId { get; set; }
}

public sealed class UpdateV3InterjectionRequest
{
	public string? Text { get; set; }
	public bool UseWingman { get; set; }
}

public sealed class ContinueV3PairRunRequest
{
	public string? Instruction { get; set; }
	public int? AdditionalRounds { get; set; }
}

public sealed class RejectV3InitialPlanRequest
{
	public string? Comment { get; set; }
}

public sealed class V3PairRunSnapshot
{
	public V3PairRun Run { get; set; } = new();
	public List<V3PairRoundRecord> Rounds { get; set; } = [];
	public List<V3PairDecision> Decisions { get; set; } = [];
}
