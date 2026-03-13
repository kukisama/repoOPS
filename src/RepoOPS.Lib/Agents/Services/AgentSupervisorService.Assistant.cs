using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RepoOPS.Agents.Models;

namespace RepoOPS.Agents.Services;

public sealed partial class AgentSupervisorService
{
    public IReadOnlyList<AssistantPlan> GetAssistantPlans() => _assistantPlanStore.GetAll();

    public AssistantPlan? GetAssistantPlan(string planId) => _assistantPlanStore.Get(planId);

    public async Task<AssistantPlan> GenerateAssistantPlanAsync(GenerateAssistantPlanRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Goal))
        {
            throw new InvalidOperationException("Goal is required.");
        }

        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var selectedRoleIds = (request.SelectedRoleIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedRoles = catalog.Roles
            .Where(role => selectedRoleIds.Count == 0 || selectedRoleIds.Contains(role.RoleId))
            .ToList();

        if (selectedRoles.Count == 0)
        {
            selectedRoles = catalog.Roles.ToList();
        }

        if (selectedRoles.Count == 0)
        {
            throw new InvalidOperationException("At least one role must exist before generating an AI assistant plan.");
        }

        var executionRoot = ResolveExecutionRoot(settings, request.WorkspaceRoot);
        var streamRunId = string.IsNullOrWhiteSpace(request.ClientStreamId)
            ? Guid.NewGuid().ToString("N")
            : request.ClientStreamId.Trim();
        var tempRun = new SupervisorRun
        {
            RunId = streamRunId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? CreateTitleFromGoal(request.Goal) : request.Title.Trim(),
            Goal = request.Goal.Trim(),
            ExecutionRoot = executionRoot,
            WorkspaceRoot = executionRoot,
            WorkspaceName = Path.GetFileName(executionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            AdditionalAllowedDirectories = ExtractReferencedDirectories([request.Goal, request.WorkspaceRoot], executionRoot, executionRoot),
            Workers = selectedRoles.Select(role => new AgentWorkerSession
            {
                WorkerId = role.RoleId,
                RoleId = role.RoleId,
                RoleName = role.Name,
                RoleDescription = role.Description,
                CurrentTask = role.Description,
                Status = "idle"
            }).ToList()
        };

        var prompt = BuildAssistantPlanPrompt(tempRun, selectedRoles, request, settings);
        var result = await ExecuteOneShotAsync(prompt, tempRun, "AI 助手：正在设计首批轮次方案");
        var parsed = TryParseAssistantPlan(result.Output, selectedRoles);
        var plan = NormalizeAssistantPlan(parsed, request, selectedRoles, executionRoot, tempRun.Title, tempRun.Goal);
        WriteAssistantArtifacts(plan);
        return _assistantPlanStore.Upsert(plan);
    }

    public AssistantPlan SaveAssistantPlan(AssistantPlan plan)
    {
        if (plan is null || string.IsNullOrWhiteSpace(plan.PlanId))
        {
            throw new InvalidOperationException("Invalid assistant plan.");
        }

        var catalog = _roleConfigService.Load();
        var roles = catalog.Roles.ToList();
        var executionRoot = string.IsNullOrWhiteSpace(plan.ExecutionRoot)
            ? ResolveExecutionRoot(catalog.Settings ?? new SupervisorSettings(), plan.WorkspaceRoot)
            : Path.GetFullPath(plan.ExecutionRoot);
        var normalized = NormalizeAssistantPlan(plan, new GenerateAssistantPlanRequest
        {
            Title = plan.Title,
            Goal = plan.Goal,
            WorkspaceRoot = plan.WorkspaceRoot,
            FullAutoEnabled = plan.FullAutoEnabled,
            MaxRounds = plan.MaxRounds,
            PlanningBatchSize = plan.PlanningBatchSize,
            InitialRoundCount = plan.InitialRoundCount,
            SelectedRoleIds = plan.SelectedRoleIds
        }, roles, executionRoot, plan.Title, plan.Goal);
        normalized.PlanId = plan.PlanId;
        normalized.CreatedAt = plan.CreatedAt == default ? DateTime.UtcNow : plan.CreatedAt;
        normalized.LinkedRunId = plan.LinkedRunId;
        WriteAssistantArtifacts(normalized);
        return _assistantPlanStore.Upsert(normalized);
    }

    public async Task<SupervisorRun> CreateRunFromAssistantPlanAsync(string planId, bool autoStart = true)
    {
        var plan = _assistantPlanStore.Get(planId) ?? throw new InvalidOperationException($"Assistant plan '{planId}' not found.");
        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();
        var roleIds = InferSelectedRoleIds(plan);
        if (roleIds.Count == 0)
        {
            throw new InvalidOperationException("Assistant plan does not reference any usable roles.");
        }

        var run = await CreateRunAsync(new CreateSupervisorRunRequest
        {
            Title = plan.Title,
            Goal = plan.Goal,
            WorkspaceRoot = plan.WorkspaceRoot,
            WorkspaceName = null,
            RoleIds = roleIds,
            AutoStart = false,
            AutoPilotEnabled = plan.FullAutoEnabled,
            MaxAutoSteps = plan.MaxRounds
        });

        run = RequireRun(run.RunId);
        run.AssistantPlanId = plan.PlanId;
        run.AssistantPlanSummary = plan.Summary;
        run.AssistantSkillFilePath = plan.SkillFilePath;
        run.AssistantSkillSummary = BuildAssistantSkillSummary(plan);
        run.AssistantPlanningBatchSize = plan.PlanningBatchSize;
        run.AssistantMaxRounds = plan.MaxRounds;
        run.AssistantFullAuto = plan.FullAutoEnabled;
        SyncAssistantExecutionState(run);
        run = AddDecision(run, "assistant-plan-linked", $"Linked AI assistant plan '{plan.Title}' ({plan.PlanId}).");
        PersistRun(run);
        await BroadcastRunUpdatedAsync(run);

        plan.LinkedRunId = run.RunId;
        plan.Status = autoStart ? "running" : "approved";
        WriteAssistantArtifacts(plan);
        _assistantPlanStore.Upsert(plan);

        if (autoStart)
        {
            var firstRound = plan.Rounds
                .OrderBy(item => item.RoundNumber)
                .FirstOrDefault();
            var shouldRunVerificationFirst = firstRound?.RequiresVerification == true
                && !string.IsNullOrWhiteSpace(settings.DefaultVerificationCommand);
            await AutoStepAsync(run.RunId, BuildAssistantPlanOverrideInstruction(plan), runVerificationFirst: shouldRunVerificationFirst);
            return RequireRun(run.RunId);
        }

        return run;
    }

    internal void TryWriteAssistantRoundArtifacts(SupervisorRun run, int roundNumber, string trigger, string? extraInstruction, string supervisorRawOutput, bool wrotePlanFile)
    {
        if (string.IsNullOrWhiteSpace(run.AssistantPlanId) || string.IsNullOrWhiteSpace(run.WorkspaceRoot))
        {
            return;
        }

        try
        {
            var planDirectory = Path.Combine(run.WorkspaceRoot, ".repoops", "assistant-rounds");
            Directory.CreateDirectory(planDirectory);
            var roundFilePath = Path.Combine(planDirectory, $"round-{Math.Max(1, roundNumber):00}.md");
            if (File.Exists(roundFilePath) && wrotePlanFile)
            {
                return;
            }

            var lines = new List<string>
            {
                $"# Round {Math.Max(1, roundNumber):00}",
                string.Empty,
                $"- Trigger: {FormatRoundTrigger(trigger)}",
                $"- Goal: {run.Goal}",
                $"- Assistant plan: {run.AssistantPlanId}",
                $"- Batch size: {run.AssistantPlanningBatchSize}",
                $"- Max rounds: {run.AssistantMaxRounds}",
                string.Empty,
                "## Supervisor summary",
                string.Empty,
                string.IsNullOrWhiteSpace(run.LatestSummary) ? "(empty)" : run.LatestSummary!,
                string.Empty,
                "## Extra instruction",
                string.Empty,
                string.IsNullOrWhiteSpace(extraInstruction) ? "(none)" : extraInstruction!,
                string.Empty,
                "## Worker handoff",
                string.Empty
            };

            foreach (var worker in run.Workers)
            {
                lines.Add($"### {worker.RoleName}");
                lines.Add($"- Status: {worker.Status}");
                lines.Add($"- Summary: {worker.LastSummary ?? "—"}");
                lines.Add($"- Next: {worker.LastNextStep ?? "—"}");
                lines.Add($"- Output preview: {(string.IsNullOrWhiteSpace(worker.LastOutputPreview) ? "—" : worker.LastOutputPreview)}");
                lines.Add(string.Empty);
            }

            lines.Add("## Raw supervisor output");
            lines.Add(string.Empty);
            lines.Add("```text");
            lines.Add(string.IsNullOrWhiteSpace(supervisorRawOutput) ? "<empty>" : supervisorRawOutput.TrimEnd());
            lines.Add("```");

            File.WriteAllText(roundFilePath, string.Join(Environment.NewLine, lines), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write assistant round artifact for run {RunId}", run.RunId);
        }
    }

    private string BuildAssistantPlanPrompt(SupervisorRun run, IReadOnlyCollection<AgentRoleDefinition> roles, GenerateAssistantPlanRequest request, SupervisorSettings settings)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.SupervisorPromptPrefix))
        {
            sb.AppendLine(settings.SupervisorPromptPrefix);
            sb.AppendLine();
        }

        sb.AppendLine("你是 RepoOPS 的 AI 助手总策划。你的任务不是直接写代码，而是先为这个项目设计一份高价值的首批轮次方案。只能输出 JSON，不要输出 Markdown 或代码块。");
        sb.AppendLine($"任务目标：{run.Goal}");
        sb.AppendLine($"项目目录：{run.WorkspaceRoot}");
        sb.AppendLine($"首次请设计 {Math.Max(1, request.InitialRoundCount)} 个轮次；每批固定 {Math.Max(1, request.PlanningBatchSize)} 轮；总轮次上限 {Math.Max(3, request.MaxRounds)}。如果前三轮后仍不满意，可以再规划下一批，直到最多 {Math.Max(3, request.MaxRounds)} 轮。");
        sb.AppendLine($"全自动模式：{(request.FullAutoEnabled ? "开启" : "关闭")}");
        sb.AppendLine("要求：必须考虑不同角色如何共享情报、每轮交付物如何交接、超管如何自己总结并沉淀为 skill 指令文件。每轮都要有一个明确 Markdown 交付物，方便后续角色读取，避免上下文无限堆叠。");
        sb.AppendLine("要求：首次输出要有价值，不要泛泛而谈；优先让第一轮做低成本高价值的摸底或架构判断，再交给更专业角色处理。超管可自行决定是否需要写代码、编译、测试，但请在轮次设计里明确写出判断依据。");
        sb.AppendLine("硬规则：每轮最多安排 3 个角色，且最多只允许 1 个角色真正写代码；如果有多人参与，除了写代码的那一个，其余角色只能输出 Markdown 文件，作为下一轮研究资料或交付给用户的文档（例如 README、开发手册、使用说明）。");
        sb.AppendLine("可用角色：");
        foreach (var role in roles)
        {
            sb.AppendLine($"- roleId={role.RoleId}; name={role.Name}; description={role.Description}; workspacePath={role.WorkspacePath}");
        }

        sb.AppendLine();
        sb.AppendLine("JSON schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"summary\": \"一句话概括这套方案的价值\",");
        sb.AppendLine("  \"strategySummary\": \"说明为什么先这样拆 3 轮，以及后续如何按 3 轮批次推进到最多 9 轮\",");
        sb.AppendLine("  \"sharingProtocol\": [\"角色之间如何共享情报\"],");
        sb.AppendLine("  \"skillDirectives\": [\"给超管/项目沉淀的 skill 指令\"],");
        sb.AppendLine("  \"rounds\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"roundNumber\": 1,");
        sb.AppendLine("      \"title\": \"这一轮的短标题\",");
        sb.AppendLine("      \"objective\": \"本轮目标\",");
        sb.AppendLine("      \"executionMode\": \"sequential|parallel|hybrid\",");
        sb.AppendLine("      \"maxActiveRoles\": 3,");
        sb.AppendLine("      \"maxWriters\": 1,");
        sb.AppendLine("      \"requiresCodeChanges\": false,");
        sb.AppendLine("      \"requiresVerification\": false,");
        sb.AppendLine("      \"completionCriteria\": \"本轮什么时候算完成\",");
        sb.AppendLine("      \"handoffNotes\": \"给下一轮的交接说明\",");
        sb.AppendLine("      \"deliverables\": [\"round-01-brief.md\"],");
        sb.AppendLine("      \"roles\": [");
        sb.AppendLine("        {");
        sb.AppendLine("          \"roleId\": \"planner\",");
        sb.AppendLine("          \"responsibility\": \"该角色本轮做什么\",");
        sb.AppendLine("          \"canWriteCode\": false,");
        sb.AppendLine("          \"outputKind\": \"md\",");
        sb.AppendLine("          \"inputArtifacts\": [\"读取哪些情报\"],");
        sb.AppendLine("          \"outputArtifact\": \"round-01-planner.md\",");
        sb.AppendLine("          \"collaborationNotes\": \"如何与别的角色共享情报\"");
        sb.AppendLine("        }");
        sb.AppendLine("      ]");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("要求：只输出首批轮次（通常 3 轮）；每轮 1~3 个角色；每轮 maxWriters 必须为 1；若有角色 canWriteCode=true，则只能有一个；其余角色 outputKind 必须为 md；如果你认为第一轮就需要验证，必须说清楚为什么。只输出 JSON。");
        return sb.ToString();
    }

    private AssistantPlan? TryParseAssistantPlan(string rawOutput, IReadOnlyCollection<AgentRoleDefinition> roles)
    {
        var json = ExtractJsonObject(rawOutput);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var draft = JsonSerializer.Deserialize<AssistantPlanDraft>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (draft is null)
            {
                return null;
            }

            var roleMap = roles.ToDictionary(role => role.RoleId, StringComparer.OrdinalIgnoreCase);
            var plan = new AssistantPlan
            {
                Summary = draft.Summary ?? string.Empty,
                StrategySummary = draft.StrategySummary,
                SharingProtocol = NormalizeList(draft.SharingProtocol),
                SkillDirectives = NormalizeList(draft.SkillDirectives),
                Rounds = (draft.Rounds ?? [])
                    .OrderBy(item => item.RoundNumber)
                    .Select(item => new AssistantRoundPlan
                    {
                        RoundNumber = Math.Max(1, item.RoundNumber),
                        Title = item.Title?.Trim() ?? string.Empty,
                        Objective = item.Objective?.Trim() ?? string.Empty,
                        ExecutionMode = string.IsNullOrWhiteSpace(item.ExecutionMode) ? "sequential" : item.ExecutionMode.Trim(),
                        MaxActiveRoles = item.MaxActiveRoles <= 0 ? 3 : item.MaxActiveRoles,
                        MaxWriters = item.MaxWriters <= 0 ? 1 : item.MaxWriters,
                        RequiresCodeChanges = item.RequiresCodeChanges,
                        RequiresVerification = item.RequiresVerification,
                        CompletionCriteria = string.IsNullOrWhiteSpace(item.CompletionCriteria) ? null : item.CompletionCriteria.Trim(),
                        HandoffNotes = string.IsNullOrWhiteSpace(item.HandoffNotes) ? null : item.HandoffNotes.Trim(),
                        Deliverables = NormalizeList(item.Deliverables),
                        Roles = (item.Roles ?? [])
                            .Where(role => !string.IsNullOrWhiteSpace(role.RoleId) && roleMap.ContainsKey(role.RoleId))
                            .Select(role => new AssistantRoleAssignment
                            {
                                RoleId = role.RoleId!.Trim(),
                                RoleName = roleMap[role.RoleId!].Name,
                                Responsibility = role.Responsibility?.Trim() ?? string.Empty,
                                CanWriteCode = role.CanWriteCode,
                                OutputKind = string.IsNullOrWhiteSpace(role.OutputKind) ? "md" : role.OutputKind.Trim().ToLowerInvariant(),
                                InputArtifacts = NormalizeList(role.InputArtifacts),
                                OutputArtifact = string.IsNullOrWhiteSpace(role.OutputArtifact) ? null : role.OutputArtifact.Trim(),
                                CollaborationNotes = string.IsNullOrWhiteSpace(role.CollaborationNotes) ? null : role.CollaborationNotes.Trim()
                            })
                            .ToList()
                    })
                    .ToList()
            };

            plan.SelectedRoleIds = InferSelectedRoleIds(plan);
            return plan;
        }
        catch
        {
            return null;
        }
    }

    private AssistantPlan NormalizeAssistantPlan(AssistantPlan? source, GenerateAssistantPlanRequest request, IReadOnlyCollection<AgentRoleDefinition> roles, string executionRoot, string title, string goal)
    {
        var roleMap = roles.ToDictionary(role => role.RoleId, StringComparer.OrdinalIgnoreCase);
        var plan = source ?? new AssistantPlan();
        plan.Title = string.IsNullOrWhiteSpace(title) ? CreateTitleFromGoal(goal) : title.Trim();
        plan.Goal = goal.Trim();
        plan.ExecutionRoot = executionRoot;
        plan.WorkspaceRoot = string.IsNullOrWhiteSpace(request.WorkspaceRoot) ? executionRoot : Path.GetFullPath(request.WorkspaceRoot);
        plan.Status = string.IsNullOrWhiteSpace(plan.Status) ? "draft" : plan.Status;
        plan.FullAutoEnabled = request.FullAutoEnabled;
        plan.MaxRounds = Math.Clamp(request.MaxRounds <= 0 ? 9 : request.MaxRounds, 3, 9);
        plan.PlanningBatchSize = Math.Clamp(request.PlanningBatchSize <= 0 ? 3 : request.PlanningBatchSize, 1, 3);
        plan.InitialRoundCount = Math.Clamp(request.InitialRoundCount <= 0 ? 3 : request.InitialRoundCount, 1, plan.PlanningBatchSize);
        plan.Summary = string.IsNullOrWhiteSpace(plan.Summary) ? "AI 助手已给出一份可执行的首批轮次方案。" : plan.Summary.Trim();
        plan.StrategySummary = string.IsNullOrWhiteSpace(plan.StrategySummary)
            ? $"先按 {plan.InitialRoundCount} 轮启动，之后按每批 {plan.PlanningBatchSize} 轮滚动推进，最多 {plan.MaxRounds} 轮。"
            : plan.StrategySummary.Trim();
        plan.SharingProtocol = NormalizeList(plan.SharingProtocol);
        if (plan.SharingProtocol.Count == 0)
        {
            plan.SharingProtocol =
            [
                "每轮必须生成 Markdown 交付物，作为下一轮优先读取的情报源。",
                "超管每轮先看上一轮摘要、未完成项和验证结果，再决定是否新增下一批轮次。",
                "角色之间共享的是工件、摘要和验证结果，而不是无限堆叠的完整对话历史。"
            ];
        }

        plan.SkillDirectives = NormalizeList(plan.SkillDirectives);
        if (plan.SkillDirectives.Count == 0)
        {
            plan.SkillDirectives =
            [
                "默认按 3 轮一批推进，最多 9 轮。",
                "每轮都必须产出独立 Markdown 交付物，不覆盖历史。",
                "若当前批次末尾仍未达成目标，则先总结再规划下一批。",
                "只有在需要真实证据时才触发编译/测试，避免机械重复验证。"
            ];
        }

        plan.Rounds = (plan.Rounds ?? [])
            .OrderBy(item => item.RoundNumber)
            .Take(plan.InitialRoundCount)
            .Select((round, index) =>
            {
                round.RoundNumber = index + 1;
                round.Title = string.IsNullOrWhiteSpace(round.Title) ? $"第 {index + 1} 轮" : round.Title.Trim();
                round.Objective = string.IsNullOrWhiteSpace(round.Objective) ? "补充这一轮的目标" : round.Objective.Trim();
                round.ExecutionMode = string.IsNullOrWhiteSpace(round.ExecutionMode) ? "sequential" : round.ExecutionMode.Trim().ToLowerInvariant();
                round.MaxActiveRoles = Math.Clamp(round.MaxActiveRoles <= 0 ? 3 : round.MaxActiveRoles, 1, 3);
                round.MaxWriters = 1;
                round.Deliverables = NormalizeList(round.Deliverables);
                if (round.Deliverables.Count == 0)
                {
                    round.Deliverables.Add($"round-{index + 1:00}-summary.md");
                }

                round.Roles = (round.Roles ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.RoleId) && roleMap.ContainsKey(item.RoleId))
                    .Take(round.MaxActiveRoles)
                    .Select(item =>
                    {
                        item.RoleName = roleMap[item.RoleId].Name;
                        item.Responsibility = string.IsNullOrWhiteSpace(item.Responsibility) ? "补充这一轮的角色职责" : item.Responsibility.Trim();
                        item.InputArtifacts = NormalizeList(item.InputArtifacts);
                        item.OutputKind = string.IsNullOrWhiteSpace(item.OutputKind) ? "md" : item.OutputKind.Trim().ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(item.OutputArtifact))
                        {
                            item.OutputArtifact = $"round-{index + 1:00}-{item.RoleId}.md";
                        }

                        return item;
                    })
                    .ToList();

                if (round.Roles.Count == 0)
                {
                    var fallbackRole = roles.First();
                    round.Roles.Add(new AssistantRoleAssignment
                    {
                        RoleId = fallbackRole.RoleId,
                        RoleName = fallbackRole.Name,
                        Responsibility = fallbackRole.Description ?? "补充这一轮的角色职责",
                        CanWriteCode = round.RequiresCodeChanges,
                        OutputKind = round.RequiresCodeChanges ? "code+md" : "md",
                        InputArtifacts = ["goal"],
                        OutputArtifact = $"round-{index + 1:00}-{fallbackRole.RoleId}.md",
                        CollaborationNotes = "完成后将摘要写入本轮交付物，供下一轮读取。"
                    });
                }

                var writerAssigned = false;
                foreach (var role in round.Roles)
                {
                    if (round.RequiresCodeChanges && !writerAssigned && role.CanWriteCode)
                    {
                        writerAssigned = true;
                        role.OutputKind = "code+md";
                        continue;
                    }

                    role.CanWriteCode = false;
                    role.OutputKind = "md";
                    role.OutputArtifact ??= $"round-{index + 1:00}-{role.RoleId}.md";
                }

                if (round.RequiresCodeChanges && !writerAssigned && round.Roles.Count > 0)
                {
                    round.Roles[0].CanWriteCode = true;
                    round.Roles[0].OutputKind = "code+md";
                }

                return round;
            })
            .ToList();

        if (plan.Rounds.Count == 0)
        {
            var fallbackRole = roles.First();
            plan.Rounds =
            [
                new AssistantRoundPlan
                {
                    RoundNumber = 1,
                    Title = "第一轮：快速摸底",
                    Objective = "先快速理解问题与仓库结构，给后续实现轮次留下高价值情报。",
                    ExecutionMode = "sequential",
                    RequiresCodeChanges = false,
                    RequiresVerification = false,
                    CompletionCriteria = "形成一份可供下一轮读取的摸底摘要。",
                    HandoffNotes = "下一轮优先读取这份摘要，再决定是否开始改代码。",
                    Deliverables = ["round-01-summary.md"],
                    Roles =
                    [
                        new AssistantRoleAssignment
                        {
                            RoleId = fallbackRole.RoleId,
                            RoleName = fallbackRole.Name,
                            Responsibility = fallbackRole.Description ?? "快速摸底",
                            CanWriteCode = false,
                            OutputKind = "md",
                            InputArtifacts = ["goal"],
                            OutputArtifact = $"round-01-{fallbackRole.RoleId}.md",
                            CollaborationNotes = "将发现的问题、风险和建议记录到交付物中。"
                        }
                    ]
                }
            ];
        }

        while (plan.Rounds.Count < plan.InitialRoundCount)
        {
            plan.Rounds.Add(BuildSyntheticAssistantRound(plan.Rounds.Count + 1, roles));
        }

        plan.SelectedRoleIds = InferSelectedRoleIds(plan);
        if (plan.SelectedRoleIds.Count == 0)
        {
            plan.SelectedRoleIds = roles.Take(3).Select(role => role.RoleId).ToList();
        }

        plan.PlanId = string.IsNullOrWhiteSpace(plan.PlanId) ? Guid.NewGuid().ToString("N") : plan.PlanId;
        plan.CreatedAt = plan.CreatedAt == default ? DateTime.UtcNow : plan.CreatedAt;
        return plan;
    }

    private static List<string> InferSelectedRoleIds(AssistantPlan plan)
    {
        return (plan.SelectedRoleIds ?? [])
            .Concat((plan.Rounds ?? []).SelectMany(round => round.Roles ?? []).Select(role => role.RoleId))
            .Where(roleId => !string.IsNullOrWhiteSpace(roleId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void WriteAssistantArtifacts(AssistantPlan plan)
    {
        var artifactDirectory = Path.Combine(plan.ExecutionRoot ?? FindWorkspaceRoot(), ".repoops", "ai-assistant", plan.PlanId);
        Directory.CreateDirectory(artifactDirectory);

        plan.ArtifactDirectory = artifactDirectory;
        plan.PlanJsonPath = Path.Combine(artifactDirectory, "assistant-plan.json");
        plan.PlanMarkdownPath = Path.Combine(artifactDirectory, "assistant-plan.md");
        plan.SkillFilePath = Path.Combine(artifactDirectory, "supervisor-generated-skill.md");

        File.WriteAllText(plan.PlanJsonPath, JsonSerializer.Serialize(plan, new JsonSerializerOptions
        {
            WriteIndented = true
        }), new UTF8Encoding(false));
        File.WriteAllText(plan.PlanMarkdownPath, BuildAssistantPlanMarkdown(plan), new UTF8Encoding(false));
        File.WriteAllText(plan.SkillFilePath, BuildAssistantSkillMarkdown(plan), new UTF8Encoding(false));

        foreach (var round in plan.Rounds)
        {
            var roundPath = Path.Combine(artifactDirectory, $"round-{round.RoundNumber:00}-plan.md");
            File.WriteAllText(roundPath, BuildAssistantRoundMarkdown(plan, round), new UTF8Encoding(false));
        }
    }

    private static string BuildAssistantPlanMarkdown(AssistantPlan plan)
    {
        var lines = new List<string>
        {
            $"# {plan.Title}",
            string.Empty,
            $"- Goal: {plan.Goal}",
            $"- Full auto: {(plan.FullAutoEnabled ? "enabled" : "disabled")}",
            $"- Initial rounds: {plan.InitialRoundCount}",
            $"- Batch size: {plan.PlanningBatchSize}",
            $"- Max rounds: {plan.MaxRounds}",
            $"- Linked run: {plan.LinkedRunId ?? "(none)"}",
            string.Empty,
            "## Summary",
            string.Empty,
            plan.Summary,
            string.Empty,
            "## Strategy",
            string.Empty,
            plan.StrategySummary ?? "(none)",
            string.Empty,
            "## Sharing protocol",
            string.Empty
        };

        lines.AddRange(plan.SharingProtocol.Select(item => $"- {item}"));
        lines.Add(string.Empty);
        lines.Add("## Skill directives");
        lines.Add(string.Empty);
        lines.AddRange(plan.SkillDirectives.Select(item => $"- {item}"));
        lines.Add(string.Empty);
        lines.Add("## Rounds");

        foreach (var round in plan.Rounds.OrderBy(item => item.RoundNumber))
        {
            lines.Add(string.Empty);
            lines.Add($"### Round {round.RoundNumber:00} · {round.Title}");
            lines.Add($"- Objective: {round.Objective}");
            lines.Add($"- Execution mode: {round.ExecutionMode}");
            lines.Add($"- Max active roles: {round.MaxActiveRoles}");
            lines.Add($"- Max writers: {round.MaxWriters}");
            lines.Add($"- Requires code changes: {round.RequiresCodeChanges}");
            lines.Add($"- Requires verification: {round.RequiresVerification}");
            lines.Add($"- Completion criteria: {round.CompletionCriteria ?? "—"}");
            lines.Add($"- Handoff notes: {round.HandoffNotes ?? "—"}");
            lines.Add("- Deliverables:");
            lines.AddRange(round.Deliverables.Select(item => $"  - {item}"));
            lines.Add("- Roles:");
            foreach (var role in round.Roles)
            {
                lines.Add($"  - {role.RoleName} ({role.RoleId}) [{(role.CanWriteCode ? "writer" : "md-only")}/{role.OutputKind}]: {role.Responsibility}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildAssistantRoundMarkdown(AssistantPlan plan, AssistantRoundPlan round)
    {
        var lines = new List<string>
        {
            $"# Round {round.RoundNumber:00} · {round.Title}",
            string.Empty,
            $"- Goal: {plan.Goal}",
            $"- Objective: {round.Objective}",
            $"- Execution mode: {round.ExecutionMode}",
            $"- Max active roles: {round.MaxActiveRoles}",
            $"- Max writers: {round.MaxWriters}",
            $"- Requires code changes: {round.RequiresCodeChanges}",
            $"- Requires verification: {round.RequiresVerification}",
            $"- Completion criteria: {round.CompletionCriteria ?? "—"}",
            string.Empty,
            "## Deliverables",
            string.Empty
        };

        lines.AddRange(round.Deliverables.Select(item => $"- {item}"));
        lines.Add(string.Empty);
        lines.Add("## Role assignments");
        foreach (var role in round.Roles)
        {
            lines.Add(string.Empty);
            lines.Add($"### {role.RoleName} ({role.RoleId})");
            lines.Add($"- Responsibility: {role.Responsibility}");
            lines.Add($"- Write access: {(role.CanWriteCode ? "writer" : "md-only")}");
            lines.Add($"- Output kind: {role.OutputKind}");
            lines.Add($"- Input artifacts: {(role.InputArtifacts.Count == 0 ? "—" : string.Join(", ", role.InputArtifacts))}");
            lines.Add($"- Output artifact: {role.OutputArtifact ?? "—"}");
            lines.Add($"- Collaboration notes: {role.CollaborationNotes ?? "—"}");
        }

        lines.Add(string.Empty);
        lines.Add("## Handoff notes");
        lines.Add(string.Empty);
        lines.Add(round.HandoffNotes ?? "(none)");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildAssistantSkillMarkdown(AssistantPlan plan)
    {
        var lines = new List<string>
        {
            "# RepoOPS AI Assistant Skill",
            string.Empty,
            $"- Plan ID: {plan.PlanId}",
            $"- Goal: {plan.Goal}",
            $"- Initial rounds: {plan.InitialRoundCount}",
            $"- Batch size: {plan.PlanningBatchSize}",
            $"- Max rounds: {plan.MaxRounds}",
            string.Empty,
            "## Core strategy",
            string.Empty,
            plan.StrategySummary ?? plan.Summary,
            string.Empty,
            "## Sharing protocol",
            string.Empty
        };

        lines.AddRange(plan.SharingProtocol.Select(item => $"- {item}"));
        lines.Add(string.Empty);
        lines.Add("## Skill directives");
        lines.Add(string.Empty);
        lines.AddRange(plan.SkillDirectives.Select(item => $"- {item}"));
        lines.Add(string.Empty);
        lines.Add("## Round batch policy");
        lines.Add(string.Empty);
        lines.Add($"- Start with {plan.InitialRoundCount} high-value rounds.");
        lines.Add($"- Re-plan every {plan.PlanningBatchSize} rounds if the goal is still unresolved.");
        lines.Add($"- Hard stop at {plan.MaxRounds} rounds.");
        lines.Add("- Each round must produce a Markdown artifact and preserve previous history.");
        lines.Add("- Each round allows at most one writer; every other participant must be md-only and produce a Markdown file.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildAssistantSkillSummary(AssistantPlan plan)
    {
        var summaryItems = plan.SkillDirectives.Take(4).ToList();
        if (summaryItems.Count == 0)
        {
            summaryItems.Add($"按 {plan.PlanningBatchSize} 轮一批推进，最多 {plan.MaxRounds} 轮。");
        }

        return string.Join(" ", summaryItems);
    }

    private static AssistantRoundPlan BuildSyntheticAssistantRound(int roundNumber, IReadOnlyCollection<AgentRoleDefinition> roles)
    {
        var leadRole = roles.First();
        var title = roundNumber switch
        {
            2 => "第二轮：收敛迁移切片",
            3 => "第三轮：实现准备与验证边界",
            _ => $"第 {roundNumber} 轮：滚动推进"
        };
        var objective = roundNumber switch
        {
            2 => "基于第一轮摸底摘要，选出最适合先落地的最小迁移切片，并明确边界、依赖与风险。",
            3 => "把前两轮结论转成可执行 backlog，说明是否进入真实改码，以及一旦开工该先改哪一块。",
            _ => "基于上一轮交付物继续推进，并输出可供下一轮直接读取的 Markdown 工件。"
        };

        return new AssistantRoundPlan
        {
            RoundNumber = roundNumber,
            Title = title,
            Objective = objective,
            ExecutionMode = "sequential",
            MaxActiveRoles = 1,
            MaxWriters = 1,
            RequiresCodeChanges = false,
            RequiresVerification = false,
            CompletionCriteria = "形成一份能让超管直接判断下一步的摘要与建议。",
            HandoffNotes = "下一轮优先读取本轮摘要、待办和风险记录，再决定是否切入代码实现。",
            Deliverables = [$"round-{roundNumber:00}-summary.md"],
            Roles =
            [
                new AssistantRoleAssignment
                {
                    RoleId = leadRole.RoleId,
                    RoleName = leadRole.Name,
                    Responsibility = leadRole.Description ?? "继续整理与推进当前目标。",
                    CanWriteCode = false,
                    OutputKind = "md",
                    InputArtifacts = [$"round-{Math.Max(1, roundNumber - 1):00}-summary.md"],
                    OutputArtifact = $"round-{roundNumber:00}-{leadRole.RoleId}.md",
                    CollaborationNotes = "先读取上一轮交付物，再把本轮结论浓缩为 Markdown，供后续轮次继续接力。"
                }
            ]
        };
    }

    private string BuildAssistantPlanOverrideInstruction(AssistantPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"当前 run 绑定了 AI 助手计划 {plan.PlanId}。请优先遵守该计划的首批 {plan.InitialRoundCount} 轮设计。");
        sb.AppendLine($"必须按 {plan.PlanningBatchSize} 轮一批推进；如果当前批次结束后仍未完成目标，请先总结当前批次，再规划下一批，最多 {plan.MaxRounds} 轮。");
        sb.AppendLine("每轮都应沉淀 Markdown 交付物与清晰的 handoff 摘要，避免仅依赖长上下文对话。角色之间共享的是交付物、验证结果和超管摘要。硬规则：每轮只允许一个写代码角色，其他参与者只能输出 Markdown 文件。");
        if (plan.SharingProtocol.Count > 0)
        {
            sb.AppendLine("情报共享协议：");
            foreach (var item in plan.SharingProtocol)
            {
                sb.AppendLine($"- {item}");
            }
        }

        sb.AppendLine("首批轮次：");
        foreach (var round in plan.Rounds.OrderBy(item => item.RoundNumber))
        {
            var roleSummary = string.Join("; ", round.Roles.Select(role => $"{role.RoleName}/{role.RoleId}[{(role.CanWriteCode ? "writer" : "md-only")}/{role.OutputKind}]: {role.Responsibility}"));
            sb.AppendLine($"- Round {round.RoundNumber}: {round.Title}；目标={round.Objective}；模式={round.ExecutionMode}；最多角色={round.MaxActiveRoles}；最大写入者={round.MaxWriters}；角色={roleSummary}");
        }

        return sb.ToString();
    }

    private AssistantPlan? TryGetAssistantPlanForRun(SupervisorRun run)
    {
        if (string.IsNullOrWhiteSpace(run.AssistantPlanId))
        {
            return null;
        }

        return _assistantPlanStore.Get(run.AssistantPlanId);
    }

    private static AssistantRoundPlan? ResolveAssistantRoundPlan(SupervisorRun run, AssistantPlan? plan)
    {
        var orderedRounds = (plan?.Rounds ?? [])
            .OrderBy(item => item.RoundNumber)
            .ToList();

        if (orderedRounds.Count == 0)
        {
            return null;
        }

        var targetRoundNumber = run.RoundNumber <= 0 ? 1 : run.RoundNumber;
        return orderedRounds.LastOrDefault(item => item.RoundNumber <= targetRoundNumber)
            ?? orderedRounds[0];
    }

    private static AssistantRoleAssignment? ResolveAssistantRoleAssignment(AssistantRoundPlan? round, string roleId)
    {
        if (round is null || string.IsNullOrWhiteSpace(roleId))
        {
            return null;
        }

        return (round.Roles ?? [])
            .FirstOrDefault(item => string.Equals(item.RoleId, roleId, StringComparison.OrdinalIgnoreCase));
    }

    internal bool SyncAssistantExecutionState(SupervisorRun run)
    {
        var changed = false;
        var plan = TryGetAssistantPlanForRun(run);
        var round = ResolveAssistantRoundPlan(run, plan);
        var writerAssignment = round?.Roles?.FirstOrDefault(item => item.CanWriteCode);
        var writerWorker = writerAssignment is null
            ? null
            : run.Workers.FirstOrDefault(item => string.Equals(item.RoleId, writerAssignment.RoleId, StringComparison.OrdinalIgnoreCase));

        changed |= UpdateRunAssistantState(
            run,
            round?.RoundNumber,
            round?.Title,
            round?.Objective,
            writerAssignment?.RoleId,
            writerWorker?.WorkerId);

        foreach (var worker in run.Workers)
        {
            var assignment = ResolveAssistantRoleAssignment(round, worker.RoleId);
            changed |= UpdateWorkerAssistantState(worker, round, assignment);
        }

        return changed;
    }

    internal bool TryValidateAssistantExecutionEligibility(SupervisorRun run, AgentWorkerSession worker, out string? reason)
    {
        reason = null;
        var plan = TryGetAssistantPlanForRun(run);
        var round = ResolveAssistantRoundPlan(run, plan);
        if (plan is null || round is null)
        {
            return true;
        }

        var assignment = ResolveAssistantRoleAssignment(round, worker.RoleId);
        if (assignment is not null)
        {
            return true;
        }

        reason = $"AI 助手当前第 {round.RoundNumber} 轮“{round.Title}”未安排角色 {worker.RoleName} 参与执行。当前轮只允许已排定角色开工。";
        return false;
    }

    internal string ApplyAssistantExecutionPromptGuardrails(SupervisorRun run, AgentWorkerSession worker, string prompt)
    {
        var plan = TryGetAssistantPlanForRun(run);
        var round = ResolveAssistantRoundPlan(run, plan);
        var assignment = ResolveAssistantRoleAssignment(round, worker.RoleId);
        if (plan is null || round is null || assignment is null)
        {
            return prompt;
        }

        var writerAssignment = round.Roles.FirstOrDefault(item => item.CanWriteCode);
        var sb = new StringBuilder();
        sb.AppendLine($"当前 AI 助手执行轮次：第 {round.RoundNumber} 轮 · {round.Title}");
        sb.AppendLine($"本轮目标：{round.Objective}");
        sb.AppendLine($"你的本轮职责：{assignment.Responsibility}");
        sb.AppendLine($"你的本轮身份：{(assignment.CanWriteCode ? "writer（唯一允许写代码）" : "md-only（禁止写代码）")}");
        sb.AppendLine($"输出工件：{assignment.OutputArtifact ?? $"round-{round.RoundNumber:00}-{assignment.RoleId}.md"}");

        if (!string.IsNullOrWhiteSpace(assignment.CollaborationNotes))
        {
            sb.AppendLine($"协作要求：{assignment.CollaborationNotes}");
        }

        if (assignment.CanWriteCode)
        {
            sb.AppendLine("硬规则：本轮只有你可以修改代码、脚本、配置或测试。其他参与者只允许读取现有内容并输出 Markdown 工件。改动时必须避免扩大范围，优先围绕本轮目标收敛。" );
        }
        else
        {
            sb.AppendLine($"硬规则：本轮 writer 是 {writerAssignment?.RoleName ?? writerAssignment?.RoleId ?? "另一位角色"}。你禁止修改代码、配置、脚本、测试、项目文件；只允许阅读、分析，并输出 Markdown 工件供下一棒使用。" );
        }

        sb.AppendLine();
        sb.Append(prompt);
        return sb.ToString();
    }

    private static bool UpdateRunAssistantState(
        SupervisorRun run,
        int? activeRoundNumber,
        string? activeRoundTitle,
        string? activeRoundObjective,
        string? activeWriterRoleId,
        string? activeWriterWorkerId)
    {
        var changed = false;

        if (run.AssistantActiveRoundNumber != activeRoundNumber)
        {
            run.AssistantActiveRoundNumber = activeRoundNumber;
            changed = true;
        }

        if (!string.Equals(run.AssistantActiveRoundTitle, activeRoundTitle, StringComparison.Ordinal))
        {
            run.AssistantActiveRoundTitle = activeRoundTitle;
            changed = true;
        }

        if (!string.Equals(run.AssistantActiveRoundObjective, activeRoundObjective, StringComparison.Ordinal))
        {
            run.AssistantActiveRoundObjective = activeRoundObjective;
            changed = true;
        }

        if (!string.Equals(run.AssistantActiveWriterRoleId, activeWriterRoleId, StringComparison.Ordinal))
        {
            run.AssistantActiveWriterRoleId = activeWriterRoleId;
            changed = true;
        }

        if (!string.Equals(run.AssistantActiveWriterWorkerId, activeWriterWorkerId, StringComparison.Ordinal))
        {
            run.AssistantActiveWriterWorkerId = activeWriterWorkerId;
            changed = true;
        }

        return changed;
    }

    private static bool UpdateWorkerAssistantState(AgentWorkerSession worker, AssistantRoundPlan? round, AssistantRoleAssignment? assignment)
    {
        var changed = false;
        var assignedRoundNumber = assignment is null ? null : round?.RoundNumber;
        var assignedRoundTitle = assignment is null ? null : round?.Title;
        var roundObjective = assignment is null ? null : round?.Objective;
        var canWriteCode = assignment?.CanWriteCode ?? false;
        var outputKind = assignment?.OutputKind;
        var roleMode = assignment is null ? null : assignment.CanWriteCode ? "writer" : "md-only";

        if (worker.AssistantAssignedRoundNumber != assignedRoundNumber)
        {
            worker.AssistantAssignedRoundNumber = assignedRoundNumber;
            changed = true;
        }

        if (!string.Equals(worker.AssistantAssignedRoundTitle, assignedRoundTitle, StringComparison.Ordinal))
        {
            worker.AssistantAssignedRoundTitle = assignedRoundTitle;
            changed = true;
        }

        if (!string.Equals(worker.AssistantRoundObjective, roundObjective, StringComparison.Ordinal))
        {
            worker.AssistantRoundObjective = roundObjective;
            changed = true;
        }

        if (worker.AssistantCanWriteCode != canWriteCode)
        {
            worker.AssistantCanWriteCode = canWriteCode;
            changed = true;
        }

        if (!string.Equals(worker.AssistantOutputKind, outputKind, StringComparison.Ordinal))
        {
            worker.AssistantOutputKind = outputKind;
            changed = true;
        }

        if (!string.Equals(worker.AssistantRoleMode, roleMode, StringComparison.Ordinal))
        {
            worker.AssistantRoleMode = roleMode;
            changed = true;
        }

        return changed;
    }
}

file sealed class AssistantPlanDraft
{
    public string? Summary { get; set; }
    public string? StrategySummary { get; set; }
    public List<string>? SharingProtocol { get; set; }
    public List<string>? SkillDirectives { get; set; }
    public List<AssistantRoundDraft>? Rounds { get; set; }
}

file sealed class AssistantRoundDraft
{
    public int RoundNumber { get; set; }
    public string? Title { get; set; }
    public string? Objective { get; set; }
    public string? ExecutionMode { get; set; }
    public int MaxActiveRoles { get; set; }
    public int MaxWriters { get; set; }
    public bool RequiresCodeChanges { get; set; }
    public bool RequiresVerification { get; set; }
    public string? CompletionCriteria { get; set; }
    public string? HandoffNotes { get; set; }
    public List<string>? Deliverables { get; set; }
    public List<AssistantRoleDraft>? Roles { get; set; }
}

file sealed class AssistantRoleDraft
{
    public string? RoleId { get; set; }
    public string? Responsibility { get; set; }
    public bool CanWriteCode { get; set; }
    public string? OutputKind { get; set; }
    public List<string>? InputArtifacts { get; set; }
    public string? OutputArtifact { get; set; }
    public string? CollaborationNotes { get; set; }
}
