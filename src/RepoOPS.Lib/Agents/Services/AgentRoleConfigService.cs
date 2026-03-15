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
                var enrichedSplitCatalog = EnsureBuiltinRoles(splitCatalog);
                if (!ReferenceEquals(enrichedSplitCatalog, splitCatalog))
                {
                    Save(enrichedSplitCatalog);
                }

                return enrichedSplitCatalog;
            }

            if (File.Exists(_legacyCatalogPath))
            {
                var json = File.ReadAllText(_legacyCatalogPath);
                var legacyCatalog = JsonSerializer.Deserialize<AgentRoleCatalog>(json, s_jsonOptions);
                var normalizedLegacy = EnsureBuiltinRoles(legacyCatalog ?? CreateDefaultCatalog());
                Save(normalizedLegacy);
                return normalizedLegacy;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load agent roles from {BaseDir}.", _baseDir);
        }

        var defaults = EnsureBuiltinRoles(CreateDefaultCatalog());
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
        return EnsureBuiltinRoles(new AgentRoleCatalog
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
                    RoleId = "builder",
                    Name = "Builder",
                    Description = "唯一的执行者, 负责代码实现、文件产出与工程落地。",
                    Icon = "🛠️",
                    PromptTemplate = "你是本项目唯一的实现者。项目目标：{{goal}}。你的角色：{{roleName}}。优先完成最核心改动，直接检查当前工作区、修改代码、运行必要命令，并把结果落地。若问题里显式提到某个现存目录或文件路径，RepoOPS 可能会把对应目录作为 `--add-dir` 传给 CLI；除此之外，默认不要假设外部路径可访问。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。",
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
                },
                new AgentRoleDefinition
                {
                    RoleId = "writer",
                    Name = "Writer",
                    Description = "负责文案撰写、文档编排、内容体系搭建, 与 builder 互斥(同属执行角色, 一轮只派一个)。",
                    Icon = "✍️",
                    PromptTemplate = "你是本项目的文稿工作者。项目目标：{{goal}}。你的角色：{{roleName}}。你负责所有非代码类产出: 文档撰写、内容体系搭建、文案优化、信息架构梳理等。所有产出必须写到项目工作目录根下或其业务子目录, 绝对禁止写入 .repoops/ 或 .github/ 等隐藏目录。若问题里显式提到某个现存目录或文件路径，RepoOPS 可能会把对应目录作为 `--add-dir` 传给 CLI；除此之外，默认不要假设外部路径可访问。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。",
                    WorkspacePath = "."
                }
            ]
        });
    }

    private static AgentRoleCatalog EnsureBuiltinRoles(AgentRoleCatalog catalog)
    {
        var existingRoles = catalog.Roles ?? [];
        var mergedRoles = existingRoles.ToList();
        var changed = false;

        foreach (var builtinRole in CreateBuiltinV3Roles())
        {
            if (mergedRoles.Any(role => string.Equals(role.RoleId, builtinRole.RoleId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            mergedRoles.Add(builtinRole);
            changed = true;
        }

        if (!changed)
        {
            return catalog;
        }

        return new AgentRoleCatalog
        {
            Settings = catalog.Settings ?? new SupervisorSettings(),
            Roles = mergedRoles
        };
    }

    private static IEnumerable<AgentRoleDefinition> CreateBuiltinV3Roles()
    {
        yield return new AgentRoleDefinition
        {
            RoleId = "helmsman",
            Name = "Helm",
            Description = "AI助手V3 主线负责人：像项目经理/产品负责人一样维护总目标，必须先给出1~4阶段完整整体计划，再定义当前阶段、边界、验收与每轮复核；不直接改代码，也不轻易替子线程写死实现细节。",
            Icon = "🧠",
            PromptTemplate = "你是 Helm，AI助手V3 的主线负责人。项目目标：{{goal}}。若当前工作区存在 `Docs/ai-context/v3/README.md` 与 `Docs/ai-context/v3/routing.md`，先按路由读取当前阶段需要的角色 / 阶段 / 规则文档，再结合当前轮动态信息做判断。你像项目经理 / 产品负责人一样工作：先维护完整整体计划，再定义当前阶段、验收口径、边界与任务卡。除非存在明确硬约束，否则不要过早替子线程写死技术栈、框架、分层或实现细节。当前轮次：{{roundNumber}}。当前阶段：{{currentStage}}。当前阶段目标：{{currentStageGoal}}。阶段总览：{{stagePlanSummary}}。架构红线：{{architectureGuardrails}}。当前任务卡：{{taskCard}}。最近整改意见：{{reviewDirective}}。最近保留/修正决定：{{changeDecision}}。对子线最近交付的概括：{{partnerSummary}}。请按当前阶段要求输出。",
            WorkspacePath = "."
        };

        yield return new AgentRoleDefinition
        {
            RoleId = "pathfinder",
            Name = "Pathfinder",
            Description = "AI助手V3 子线执行者：知道总目标、整体计划、当前阶段目标与验收边界，在不偏目标的前提下自主决定实现与 UI/UX 落地，并回报事实。",
            Icon = "⚡",
            PromptTemplate = "你是 Pathfinder，AI助手V3 的子线执行者。项目目标：{{goal}}。若当前工作区存在 `Docs/ai-context/v3/README.md` 与 `Docs/ai-context/v3/routing.md`，先按路由读取当前阶段需要的角色 / 阶段 / 规则文档，再围绕当前任务卡执行。阶段总览：{{stagePlanSummary}}。当前阶段：{{currentStage}}。当前阶段目标：{{currentStageGoal}}。架构红线：{{architectureGuardrails}}。当前任务卡：{{taskCard}}。主线是 {{partnerName}}；它最近给出的判断/摘要是：{{partnerSummary}}。你在主线给定的目标、验收与边界内拥有实现自主性：可自行决定技术路径、页面结构、UI/UX 细节与低成本实现顺序，但要优先产出真实代码、验证和事实证据，并把主动微调明确报告给主线。请按当前阶段要求输出。",
            WorkspacePath = "."
        };

        yield return new AgentRoleDefinition
        {
            RoleId = "redteam-wingman",
            Name = "Redteam Wingman",
            Description = "AI助手V3 的一次性插嘴助攻手：强化用户插嘴中的边界、风险与反例提醒，供主线参考，不直接改写总目标。",
            Icon = "🦂",
            PromptTemplate = "你是 Redteam Wingman，AI助手V3 的临时助攻手。项目目标：{{goal}}。若当前工作区存在 `Docs/ai-context/v3/README.md` 与相关规则文档，先快速读取与插话吸收有关的内容，再基于当前事实放大用户插话中的边界、风险与反例提醒，但不能改写总目标，也不能凭空新增硬性需求。当前阶段={{currentStage}}；当前阶段目标={{currentStageGoal}}；阶段总览={{stagePlanSummary}}；架构红线={{architectureGuardrails}}；最近任务卡={{taskCard}}；最近主线判断={{reviewDirective}}；最近保留/修正决定={{changeDecision}}。请按当前阶段要求输出。",
            WorkspacePath = "."
        };
    }
}
