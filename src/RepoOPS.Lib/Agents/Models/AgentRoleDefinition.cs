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
}

public sealed class AgentRoleCatalog
{
    public List<AgentRoleDefinition> Roles { get; set; } = [];
}
