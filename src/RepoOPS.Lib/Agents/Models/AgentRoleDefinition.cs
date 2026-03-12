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
    public string DefaultModel { get; set; } = "gpt-5.4";
    public int DefaultMaxAutoSteps { get; set; } = 6;
    public bool DefaultAutoPilotEnabled { get; set; } = true;
    public int MaxConcurrentWorkers { get; set; } = 4;
    public int WorkerTimeoutMinutes { get; set; } = 30;
    public string? DefaultVerificationCommand { get; set; }
    public int OutputBufferMaxChars { get; set; } = 12000;
    public int DecisionHistoryLimit { get; set; } = 40;
    public string? DefaultWorkspaceRoot { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
}

public sealed class AgentRoleCatalog
{
    public SupervisorSettings Settings { get; set; } = new();
    public List<AgentRoleDefinition> Roles { get; set; } = [];
}
