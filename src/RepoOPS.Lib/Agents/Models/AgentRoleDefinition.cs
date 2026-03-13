namespace RepoOPS.Agents.Models;

public sealed class AgentRoleDefinition
{
    public string RoleId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string PromptTemplate { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-5.4";
    public bool AllowAllTools { get; set; } = true;
    public bool AllowAllPaths { get; set; }
    public bool AllowAllUrls { get; set; }
    public string WorkspacePath { get; set; } = ".";
    public List<string> AllowedUrls { get; set; } = [];
    public List<string> AllowedTools { get; set; } = [];
    public List<string> DeniedTools { get; set; } = [];
    public List<string> AllowedPaths { get; set; } = [];
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
}

public sealed class SupervisorSettings
{
    public string SupervisorModel { get; set; } = "gpt-5.4";
    public string? SupervisorPromptPrefix { get; set; }
    public string? RoleProposalPromptPrefix { get; set; }
    public string DefaultModel { get; set; } = "gpt-5.4";
    public int DefaultMaxAutoSteps { get; set; } = 6;
    public bool DefaultAutoPilotEnabled { get; set; } = true;
    public int MaxConcurrentWorkers { get; set; } = 4;
    public int WorkerTimeoutMinutes { get; set; } = 30;
    public bool AllowWorkerPermissionRequests { get; set; } = true;
    public bool EnableYoloMode { get; set; }
    public string? DefaultVerificationCommand { get; set; }
    public int OutputBufferMaxChars { get; set; } = 12000;
    public int DecisionHistoryLimit { get; set; } = 40;
    public string? DefaultWorkspaceRoot { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
    public bool EnableAttentionTracking { get; set; } = true;
    public bool AutoCreateDefaultLanes { get; set; } = true;
    public bool EnableCoordinatorSurface { get; set; } = true;
    public bool EnableVerificationSurface { get; set; } = true;
    public bool AutoAcknowledgeAttentionOnFocus { get; set; } = true;
    public bool ShowCompletedSurfaces { get; set; } = true;
    public bool SuggestFocusOnAttention { get; set; } = true;
    public int MaxAttentionEvents { get; set; } = 100;
    public string DefaultLayoutMode { get; set; } = "lanes";
    public string AgentLaneName { get; set; } = "Agents";
    public string ControlLaneName { get; set; } = "Coordinator";
    public string VerificationLaneName { get; set; } = "Verification";
    public string DefaultRunDetailTab { get; set; } = "workspace";
    public string DefaultSettingsTab { get; set; } = "orchestration";
}

public sealed class AgentRoleCatalog
{
    public SupervisorSettings Settings { get; set; } = new();
    public List<AgentRoleDefinition> Roles { get; set; } = [];
}

public sealed class RoleProposalRequest
{
    public string Goal { get; set; } = string.Empty;
    public string? WorkspaceRoot { get; set; }
}

public sealed class RoleProposalResponse
{
    public string Summary { get; set; } = string.Empty;
    public string RecommendedWorkspaceName { get; set; } = string.Empty;
    public List<RoleProposalExistingRole> ExistingRoles { get; set; } = [];
    public List<RoleProposalDraftRole> NewRoles { get; set; } = [];
}

public sealed class RoleProposalExistingRole
{
    public string RoleId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Reason { get; set; }
    public bool Selected { get; set; }
}

public sealed class RoleProposalDraftRole
{
    public AgentRoleDefinition Role { get; set; } = new();
    public string? Reason { get; set; }
    public bool Selected { get; set; } = true;
}
