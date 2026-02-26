namespace RepoOPS.Models;

public sealed class TaskItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Script path or command to execute.
    /// If it ends with .ps1, it runs as a PowerShell script via pwsh.
    /// Otherwise, it runs as a native command directly (e.g. "adb", "python", "dotnet").
    /// </summary>
    public string Script { get; set; } = string.Empty;
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Icon { get; set; }
}
