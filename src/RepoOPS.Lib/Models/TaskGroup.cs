namespace RepoOPS.Models;

public sealed class TaskGroup
{
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public List<TaskItem> Tasks { get; set; } = [];
}
