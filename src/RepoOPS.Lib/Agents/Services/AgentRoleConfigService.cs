using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RepoOPS.Agents.Models;

namespace RepoOPS.Agents.Services;

public sealed class AgentRoleConfigService(ILogger<AgentRoleConfigService> logger)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<AgentRoleConfigService> _logger = logger;
    private readonly string _baseDir = GetBaseDir();
    private readonly string _legacyCatalogPath = Path.Combine(GetBaseDir(), "agent-roles.json");
    private readonly string _settingsPath = Path.Combine(GetBaseDir(), "agent-settings.json");
    private readonly string _rolesDirectoryPath = Path.Combine(GetBaseDir(), "agent-roles");

    public AgentRoleCatalog Load()
    {
        try
        {
            var splitCatalog = LoadFromSplitFiles();
            if (splitCatalog is not null)
            {
                return splitCatalog;
            }

            if (File.Exists(_legacyCatalogPath))
            {
                var json = File.ReadAllText(_legacyCatalogPath);
                var legacyCatalog = JsonSerializer.Deserialize<AgentRoleCatalog>(json, s_jsonOptions);
                var normalizedLegacy = legacyCatalog ?? CreateDefaultCatalog();
                Save(normalizedLegacy);
                return normalizedLegacy;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load agent roles from {BaseDir}.", _baseDir);
        }

        var defaults = CreateDefaultCatalog();
        Save(defaults);
        return defaults;
    }

    public void Save(AgentRoleCatalog catalog)
    {
        var normalized = catalog ?? CreateDefaultCatalog();
        if (normalized.Roles.Count == 0)
        {
            normalized = CreateDefaultCatalog();
        }

        Directory.CreateDirectory(_rolesDirectoryPath);

        var settingsJson = JsonSerializer.Serialize(normalized.Settings ?? new SupervisorSettings(), s_jsonOptions);
        File.WriteAllText(_settingsPath, settingsJson);

        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in normalized.Roles.OrderBy(role => role.RoleId, StringComparer.OrdinalIgnoreCase))
        {
            var roleFilePath = Path.Combine(_rolesDirectoryPath, $"{SanitizeRoleFileName(role.RoleId)}.json");
            expectedFiles.Add(Path.GetFullPath(roleFilePath));
            var roleJson = JsonSerializer.Serialize(role, s_jsonOptions);
            File.WriteAllText(roleFilePath, roleJson);
        }

        foreach (var existingFile in Directory.GetFiles(_rolesDirectoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!expectedFiles.Contains(Path.GetFullPath(existingFile)))
            {
                File.Delete(existingFile);
            }
        }
    }

    public static string GetBaseDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                baseDir = dir;
            }
        }

        if (!File.Exists(Path.Combine(baseDir, "tasks.json")))
        {
            baseDir = Directory.GetCurrentDirectory();
        }

        return baseDir;
    }

    private AgentRoleCatalog? LoadFromSplitFiles()
    {
        var hasSettings = File.Exists(_settingsPath);
        var hasRoleDirectory = Directory.Exists(_rolesDirectoryPath);
        if (!hasSettings && !hasRoleDirectory)
        {
            return null;
        }

        var settings = hasSettings
            ? JsonSerializer.Deserialize<SupervisorSettings>(File.ReadAllText(_settingsPath), s_jsonOptions) ?? new SupervisorSettings()
            : new SupervisorSettings();

        var roles = hasRoleDirectory
            ? Directory.GetFiles(_rolesDirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => JsonSerializer.Deserialize<AgentRoleDefinition>(File.ReadAllText(path), s_jsonOptions))
                .Where(role => role is not null)
                .Select(role => role!)
                .ToList()
            : [];

        return roles.Count == 0 && !hasSettings
            ? null
            : new AgentRoleCatalog
            {
                Settings = settings,
                Roles = roles.Count > 0 ? roles : CreateDefaultCatalog().Roles
            };
    }

    private static string SanitizeRoleFileName(string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
        {
            return "role";
        }

        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var buffer = new char[roleId.Length];
        var length = 0;
        foreach (var ch in roleId.Trim())
        {
            buffer[length++] = invalidChars.Contains(ch) ? '-' : ch;
        }

        var sanitized = new string(buffer, 0, length).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "role" : sanitized;
    }

    private static AgentRoleCatalog CreateDefaultCatalog()
    {
        return new AgentRoleCatalog
        {
            Settings = new SupervisorSettings
            {
                SupervisorModel = "gpt-5.4",
                RoleProposalPromptPrefix = "Prefer concise, reusable roles. Reuse an existing role when it already matches the job. If the request explicitly mentions an existing directory or file path, RepoOPS may grant runtime access with --add-dir for that directory; otherwise treat external resources as inaccessible unless the workspace is explicitly expanded.",
                DefaultModel = "gpt-5.4",
                DefaultMaxAutoSteps = 6,
                DefaultAutoPilotEnabled = true,
                MaxConcurrentWorkers = 4,
                WorkerTimeoutMinutes = 30,
                AllowWorkerPermissionRequests = true,
                OutputBufferMaxChars = 12000,
                DecisionHistoryLimit = 40
            },
            Roles =
            [
                new AgentRoleDefinition
                {
                    RoleId = "planner",
                    Name = "Planner",
                    Description = "拆解目标、整理依赖、输出下一轮可执行子任务。",
                    Icon = "🧭",
                    PromptTemplate = "你是本项目的规划师。项目目标：{{goal}}。请先快速分析现状，再给出你负责的执行计划、风险点和推荐的下一步。若问题里显式提到某个现存目录或文件路径，RepoOPS 可能会把对应目录作为 `--add-dir` 传给 CLI；除此之外，默认不要假设外部路径可访问。必要时可查看当前工作区代码并提出明确改动建议。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。",
                    WorkspacePath = "."
                },
                new AgentRoleDefinition
                {
                    RoleId = "builder-a",
                    Name = "Builder A",
                    Description = "负责主线实现与代码改动。",
                    Icon = "🛠️",
                    PromptTemplate = "你是主线实现者。项目目标：{{goal}}。你的角色：{{roleName}}。优先完成最核心改动，直接检查当前工作区、修改代码、运行必要命令，并把结果落地。若问题里显式提到某个现存目录或文件路径，RepoOPS 可能会把对应目录作为 `--add-dir` 传给 CLI；除此之外，默认不要假设外部路径可访问。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。",
                    WorkspacePath = "."
                },
                new AgentRoleDefinition
                {
                    RoleId = "builder-b",
                    Name = "Builder B",
                    Description = "负责补充实现、边角案例或平行子任务。",
                    Icon = "🧩",
                    PromptTemplate = "你是并行实现者。项目目标：{{goal}}。你的角色：{{roleName}}。请选择与主线互补的子任务推进，必要时补充测试、文档或次要模块实现。若问题里显式提到某个现存目录或文件路径，RepoOPS 可能会把对应目录作为 `--add-dir` 传给 CLI；除此之外，默认不要假设外部路径可访问。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。",
                    WorkspacePath = "."
                },
                new AgentRoleDefinition
                {
                    RoleId = "reviewer",
                    Name = "Reviewer",
                    Description = "负责审查结果、验证风险、指出遗漏。",
                    Icon = "🔍",
                    PromptTemplate = "你是审查者。项目目标：{{goal}}。请重点检查现有改动的完整性、风险和遗漏，并给出最值得继续追问的方向。若问题里显式提到某个现存目录或文件路径，RepoOPS 可能会把对应目录作为 `--add-dir` 传给 CLI；除此之外，默认不要假设外部路径可访问。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。",
                    WorkspacePath = "."
                }
            ]
        };
    }
}
