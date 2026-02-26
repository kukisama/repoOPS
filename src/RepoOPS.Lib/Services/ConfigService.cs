using System.Text.Json;
using Microsoft.Extensions.Logging;
using RepoOPS.Models;

namespace RepoOPS.Services;

/// <summary>
/// Service for loading and managing task configuration from tasks.json.
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _configPath;
    private readonly string _selfConfigPath;
    private readonly string _appDir;
    private readonly ILogger<ConfigService> _logger;

    /// <summary>
    /// Returns the directory where the EXE lives on disk.
    /// For single-file publish AppContext.BaseDirectory points to a temp extraction folder,
    /// so we prefer the directory of the actual process executable.
    /// </summary>
    private static string GetExeDirectory()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            var dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(dir))
                return dir;
        }
        return AppContext.BaseDirectory;
    }

    public ConfigService(ILogger<ConfigService> logger)
    {
        _logger = logger;
        _appDir = GetExeDirectory();

        // Determine base directory (exe dir or current dir)
        var baseDir = _appDir;
        if (!File.Exists(Path.Combine(baseDir, "tasks.json")))
        {
            baseDir = Directory.GetCurrentDirectory();
        }

        _configPath = Path.Combine(baseDir, "tasks.json");
        _selfConfigPath = Path.Combine(baseDir, "self.json");
    }

    /// <summary>
    /// Load config: self.json takes priority over tasks.json.
    /// self.json stores user customizations that survive reinstalls.
    /// </summary>
    public TaskConfig LoadConfig()
    {
        // Priority: self.json > tasks.json
        var loadPath = File.Exists(_selfConfigPath) ? _selfConfigPath : _configPath;

        if (!File.Exists(loadPath))
        {
            _logger.LogWarning("No configuration file found (checked {Self} and {Tasks}). Using empty config.",
                _selfConfigPath, _configPath);
            return new TaskConfig();
        }

        try
        {
            var json = File.ReadAllText(loadPath);
            var config = JsonSerializer.Deserialize<TaskConfig>(json, s_jsonOptions);
            _logger.LogInformation("Loaded configuration from {Path} with {GroupCount} groups.",
                loadPath, config?.Groups.Count ?? 0);
            return config ?? new TaskConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load configuration from {Path}.", loadPath);
            return new TaskConfig();
        }
    }

    public string GetScriptsBasePath(TaskConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ScriptsBasePath))
        {
            return Path.IsPathRooted(config.ScriptsBasePath)
                ? config.ScriptsBasePath
                : Path.GetFullPath(Path.Combine(_appDir, config.ScriptsBasePath));
        }

        return Path.Combine(_appDir, "scripts");
    }

    public static string ResolveScriptPath(string scriptPath, string basePath)
    {
        if (Path.IsPathRooted(scriptPath))
        {
            return scriptPath;
        }

        return Path.GetFullPath(Path.Combine(basePath, scriptPath));
    }

    /// <summary>
    /// Save config always writes to self.json, preserving the original tasks.json as a template.
    /// </summary>
    public void SaveConfig(TaskConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, s_jsonOptions);
            File.WriteAllText(_selfConfigPath, json);
            _logger.LogInformation("Saved configuration to {Path}.", _selfConfigPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save configuration to {Path}.", _selfConfigPath);
            throw;
        }
    }

    public string GetConfigPath()
    {
        // Report which file is actually in use
        return File.Exists(_selfConfigPath) ? _selfConfigPath : _configPath;
    }
}
