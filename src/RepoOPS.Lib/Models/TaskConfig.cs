namespace RepoOPS.Models;

public sealed class TaskConfig
{
    public string? ScriptsBasePath { get; set; }
    public string? DefaultWorkingDirectory { get; set; }
    public List<TaskGroup> Groups { get; set; } = [];
}
