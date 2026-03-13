namespace RepoOPS.Agents.Models;

public sealed class AssistantPlan
{
    public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public string? WorkspaceRoot { get; set; }
    public string? ExecutionRoot { get; set; }
    public bool FullAutoEnabled { get; set; } = true;
    public int MaxRounds { get; set; } = 9;
    public int PlanningBatchSize { get; set; } = 3;
    public int InitialRoundCount { get; set; } = 3;
    public string Summary { get; set; } = string.Empty;
    public string? StrategySummary { get; set; }
    public List<string> SelectedRoleIds { get; set; } = [];
    public List<string> SharingProtocol { get; set; } = [];
    public List<string> SkillDirectives { get; set; } = [];
    public List<AssistantRoundPlan> Rounds { get; set; } = [];
    public string? ArtifactDirectory { get; set; }
    public string? PlanMarkdownPath { get; set; }
    public string? PlanJsonPath { get; set; }
    public string? SkillFilePath { get; set; }
    public string? LinkedRunId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AssistantRoundPlan
{
    public string RoundId { get; set; } = Guid.NewGuid().ToString("N");
    public int RoundNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Objective { get; set; } = string.Empty;
    public string ExecutionMode { get; set; } = "sequential";
    public int MaxActiveRoles { get; set; } = 3;
    public int MaxWriters { get; set; } = 1;
    public bool RequiresCodeChanges { get; set; }
    public bool RequiresVerification { get; set; }
    public string? CompletionCriteria { get; set; }
    public string? HandoffNotes { get; set; }
    public List<string> Deliverables { get; set; } = [];
    public List<AssistantRoleAssignment> Roles { get; set; } = [];
}

public sealed class AssistantRoleAssignment
{
    public string RoleId { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string Responsibility { get; set; } = string.Empty;
    public bool CanWriteCode { get; set; }
    public string OutputKind { get; set; } = "md";
    public List<string> InputArtifacts { get; set; } = [];
    public string? OutputArtifact { get; set; }
    public string? CollaborationNotes { get; set; }
}

public sealed class GenerateAssistantPlanRequest
{
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string? WorkspaceRoot { get; set; }
    public string? ClientStreamId { get; set; }
    public bool FullAutoEnabled { get; set; } = true;
    public int MaxRounds { get; set; } = 9;
    public int PlanningBatchSize { get; set; } = 3;
    public int InitialRoundCount { get; set; } = 3;
    public List<string> SelectedRoleIds { get; set; } = [];
}

public sealed class CreateRunFromAssistantPlanRequest
{
    public bool AutoStart { get; set; } = true;
}
