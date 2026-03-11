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
    private readonly string _rolesPath = Path.Combine(GetBaseDir(), "agent-roles.json");

    public AgentRoleCatalog Load()
    {
        if (!File.Exists(_rolesPath))
        {
            var defaults = CreateDefaultCatalog();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_rolesPath);
            var catalog = JsonSerializer.Deserialize<AgentRoleCatalog>(json, s_jsonOptions);
            return catalog ?? CreateDefaultCatalog();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load agent roles from {Path}.", _rolesPath);
            return CreateDefaultCatalog();
        }
    }

    public void Save(AgentRoleCatalog catalog)
    {
        var normalized = catalog ?? CreateDefaultCatalog();
        if (normalized.Roles.Count == 0)
        {
            normalized = CreateDefaultCatalog();
        }

        var json = JsonSerializer.Serialize(normalized, s_jsonOptions);
        File.WriteAllText(_rolesPath, json);
    }

    private static string GetBaseDir()
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

    private static AgentRoleCatalog CreateDefaultCatalog()
    {
        return new AgentRoleCatalog
        {
            Roles =
            [
                new AgentRoleDefinition
                {
                    RoleId = "planner",
                    Name = "Planner",
                    Description = "拆解目标、整理依赖、输出下一轮可执行子任务。",
                    Icon = "🧭",
                    PromptTemplate = "你是本项目的规划师。项目目标：{{goal}}。请先快速分析现状，再给出你负责的执行计划、风险点和推荐的下一步。必要时可查看代码并提出明确改动建议。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。",
                    WorkspacePath = "."
                },
                new AgentRoleDefinition
                {
                    RoleId = "builder-a",
                    Name = "Builder A",
                    Description = "负责主线实现与代码改动。",
                    Icon = "🛠️",
                    PromptTemplate = "你是主线实现者。项目目标：{{goal}}。你的角色：{{roleName}}。优先完成最核心改动，直接检查仓库、修改代码、运行必要命令，并把结果落地。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。",
                    WorkspacePath = "."
                },
                new AgentRoleDefinition
                {
                    RoleId = "builder-b",
                    Name = "Builder B",
                    Description = "负责补充实现、边角案例或平行子任务。",
                    Icon = "🧩",
                    PromptTemplate = "你是并行实现者。项目目标：{{goal}}。你的角色：{{roleName}}。请选择与主线互补的子任务推进，必要时补充测试、文档或次要模块实现。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。",
                    WorkspacePath = "."
                },
                new AgentRoleDefinition
                {
                    RoleId = "reviewer",
                    Name = "Reviewer",
                    Description = "负责审查结果、验证风险、指出遗漏。",
                    Icon = "🔍",
                    PromptTemplate = "你是审查者。项目目标：{{goal}}。请重点检查现有改动的完整性、风险和遗漏，并给出最值得继续追问的方向。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。",
                    WorkspacePath = "."
                }
            ]
        };
    }
}
