using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RepoOPS.Agents.Models;
using RepoOPS.Hubs;

namespace RepoOPS.Agents.Services;

public sealed partial class AgentSupervisorService(
    AgentRoleConfigService roleConfigService,
    AssistantPlanStore assistantPlanStore,
    SupervisorRunStore runStore,
    RunVerificationService verificationService,
    IHubContext<TaskHub> hubContext,
    ILogger<AgentSupervisorService> logger)
{
    private const string AgentsLaneId = "lane_agents";
    private const string ControlLaneId = "lane_control";
    private const string VerificationLaneId = "lane_verification";
    private const string CoordinatorSurfaceId = "surface:coordinator";
    private const string VerificationSurfaceId = "surface:verification";
    private const string PromptArgumentToken = "__REPOOPS_PROMPT__";
    private static readonly Regex QuotedWindowsPathRegex = new("(?<quote>[\"'])(?<path>[A-Za-z]:\\\\[^\"'\\r\\n]+)\\k<quote>", RegexOptions.Compiled);
    private static readonly Regex BareWindowsPathRegex = new(@"(?<![\w/])(?<path>[A-Za-z]:\\[^\s""'<>|]+)", RegexOptions.Compiled);
    private static readonly Regex RelativePathRegex = new(@"(?<![\w/])(?<path>\.{1,2}[\\/][^\s""'<>|]+)", RegexOptions.Compiled);

    private readonly AgentRoleConfigService _roleConfigService = roleConfigService;
    private readonly AssistantPlanStore _assistantPlanStore = assistantPlanStore;
    private readonly SupervisorRunStore _runStore = runStore;
    private readonly RunVerificationService _verificationService = verificationService;
    private readonly IHubContext<TaskHub> _hubContext = hubContext;
    private readonly ILogger<AgentSupervisorService> _logger = logger;
    private readonly ConcurrentDictionary<string, WorkerProcessHandle> _activeProcesses = new();
    private readonly ConcurrentDictionary<string, byte> _autoAdvancingRuns = new();
    private readonly ConcurrentDictionary<string, SupervisorLiveState> _activeSupervisorStreams = new();

    public AgentRoleCatalog GetRoles() => _roleConfigService.Load();

    public AgentRoleCatalog SaveRoles(AgentRoleCatalog catalog)
    {
        var normalized = NormalizeAndValidateRoleCatalog(catalog);
        _roleConfigService.Save(normalized);
        return _roleConfigService.Load();
    }

    public SupervisorSettings SaveSettings(SupervisorSettings settings)
    {
        var catalog = _roleConfigService.Load();
        catalog.Settings = settings;
        var normalized = NormalizeAndValidateRoleCatalog(catalog);
        _roleConfigService.Save(normalized);
        return _roleConfigService.Load().Settings ?? new SupervisorSettings();
    }

    public IReadOnlyList<SupervisorRun> GetRuns() => _runStore.GetAll();

    public SupervisorRun? GetRun(string runId) => _runStore.Get(runId);

    public RunSnapshot GetRunSnapshot(string runId)
    {
        var run = RequireRun(runId);
        var executionStateChanged = SyncAssistantExecutionState(run);
        if (executionStateChanged)
        {
            PersistRun(run);
        }

        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();
        EnsureRunLayout(run, settings);
        RecalculateAttentionAggregates(run, settings);
        return BuildSnapshot(run, settings);
    }

    public IReadOnlyList<ExecutionSurface> GetSurfaces(string runId)
    {
        return GetRunSnapshot(runId).Surfaces;
    }

    public IReadOnlyList<ExecutionLane> GetLanes(string runId)
    {
        return GetRunSnapshot(runId).Lanes;
    }

    public IReadOnlyList<AttentionEvent> GetAttention(string runId)
    {
        return GetRunSnapshot(runId).Attention;
    }

    public async Task<RunSnapshot> FocusSurfaceIntentAsync(string runId, string surfaceId, bool acknowledgeRelatedAttention = true)
    {
        var run = RequireRun(runId);
        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();
        EnsureRunLayout(run, settings);
        run.ActiveSurfaceId = surfaceId;
        run.ActiveLaneId = ResolveLaneIdForSurface(run, surfaceId, settings);

        if (acknowledgeRelatedAttention && settings.AutoAcknowledgeAttentionOnFocus)
        {
            AcknowledgeSurfaceAttention(run, surfaceId);
            RecalculateAttentionAggregates(run, settings);
        }

        PersistRun(run);
        await BroadcastRunUpdatedAsync(run);
        return BuildSnapshot(run, settings);
    }

    public async Task<RunSnapshot> AcknowledgeAttentionAsync(string runId, string eventId)
    {
        var run = RequireRun(runId);
        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();
        var attention = run.Attention.FirstOrDefault(item => string.Equals(item.EventId, eventId, StringComparison.OrdinalIgnoreCase));
        if (attention is null)
        {
            throw new InvalidOperationException($"Attention event '{eventId}' not found.");
        }

        attention.IsRead = true;
        attention.AcknowledgedAt ??= DateTime.UtcNow;
        RecalculateAttentionAggregates(run, settings);
        PersistRun(run);
        await _hubContext.Clients.All.SendAsync("AttentionAcknowledged", run.RunId, attention.EventId);
        await BroadcastRunUpdatedAsync(run);
        return BuildSnapshot(run, settings);
    }

    public async Task<RunSnapshot> AcknowledgeAllAttentionAsync(string runId)
    {
        var run = RequireRun(runId);
        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();

        foreach (var attention in run.Attention.Where(item => !item.IsRead))
        {
            attention.IsRead = true;
            attention.AcknowledgedAt ??= DateTime.UtcNow;
        }

        RecalculateAttentionAggregates(run, settings);
        PersistRun(run);
        await _hubContext.Clients.All.SendAsync("AttentionAcknowledgedAll", run.RunId);
        await BroadcastRunUpdatedAsync(run);
        return BuildSnapshot(run, settings);
    }

    public async Task<RunSnapshot> ResolveAttentionAsync(string runId, string eventId)
    {
        var run = RequireRun(runId);
        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();
        var attention = run.Attention.FirstOrDefault(item => string.Equals(item.EventId, eventId, StringComparison.OrdinalIgnoreCase));
        if (attention is null)
        {
            throw new InvalidOperationException($"Attention event '{eventId}' not found.");
        }

        attention.IsRead = true;
        attention.IsResolved = true;
        attention.AcknowledgedAt ??= DateTime.UtcNow;
        attention.ResolvedAt = DateTime.UtcNow;
        RecalculateAttentionAggregates(run, settings);
        PersistRun(run);
        await _hubContext.Clients.All.SendAsync("AttentionResolved", run.RunId, attention.EventId);
        await BroadcastRunUpdatedAsync(run);
        return BuildSnapshot(run, settings);
    }

    private static AttentionEvent RaiseAttention(
        SupervisorRun run,
        SupervisorSettings settings,
        string surfaceId,
        string? workerId,
        string kind,
        string level,
        string title,
        string message)
    {
        if (!settings.EnableAttentionTracking)
        {
            return new AttentionEvent
            {
                RunId = run.RunId,
                SurfaceId = surfaceId,
                WorkerId = workerId,
                Kind = kind,
                Level = level,
                Title = title,
                Message = message
            };
        }

        var existing = run.Attention.FirstOrDefault(item =>
            !item.IsResolved
            && string.Equals(item.SurfaceId, surfaceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Title, title, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Level = level;
            existing.Message = message;
            existing.IsRead = false;
            existing.AcknowledgedAt = null;
            return existing;
        }

        var created = new AttentionEvent
        {
            RunId = run.RunId,
            SurfaceId = surfaceId,
            WorkerId = workerId,
            Kind = kind,
            Level = level,
            Title = title,
            Message = message,
            IsRead = false,
            IsResolved = false,
            CreatedAt = DateTime.UtcNow
        };

        run.Attention.Add(created);
        return created;
    }

    private static void ApplyWorkerAttentionRules(SupervisorRun run, AgentWorkerSession worker, bool timedOut, SupervisorSettings settings)
    {
        var surfaceId = GetWorkerSurfaceId(worker.WorkerId);
        if (timedOut)
        {
            RaiseAttention(run, settings, surfaceId, worker.WorkerId, "worker-timeout", "error", $"{worker.RoleName} 已超时", "该 worker 已超过超时时间并被终止，请检查其进度与上下文。");
            return;
        }

        if (string.Equals(worker.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var failureMessage = worker.LastSummary ?? "该 worker 执行失败，需要人工查看。";
            if ((worker.LastOutputPreview ?? string.Empty).Contains("Permission denied and could not request permission from user", StringComparison.OrdinalIgnoreCase)
                || failureMessage.Contains("Permission denied and could not request permission from user", StringComparison.OrdinalIgnoreCase))
            {
                RaiseAttention(
                    run,
                    settings,
                    surfaceId,
                    worker.WorkerId,
                    "worker-permission-denied",
                    "error",
                    $"{worker.RoleName} 无法申请执行权限",
                    "当前是通过后台 gh 子进程执行，RepoOPS 本身没有内置授权弹窗；即使去掉 --no-ask-user，也只有外层运行环境本身支持交互审批时才可能弹出确认。请改为人工执行、放宽角色权限，或使用支持交互审批的运行方式。"
                );
                return;
            }

            RaiseAttention(run, settings, surfaceId, worker.WorkerId, "worker-failed", "error", $"{worker.RoleName} 执行失败", failureMessage);
            return;
        }

        var reportedStatus = (worker.LastReportedStatus ?? string.Empty).Trim().ToLowerInvariant();
        if (reportedStatus is "needs-human" or "needs-review" or "review" or "blocked" or "needs-verification" or "needs-attention")
        {
            RaiseAttention(run, settings, surfaceId, worker.WorkerId, $"worker-{reportedStatus}", "warning", $"{worker.RoleName} 需要关注", worker.LastNextStep ?? worker.LastSummary ?? "该 worker 请求下一步指导或人工确认。");
        }
    }

    private static AgentRoleCatalog NormalizeAndValidateRoleCatalog(AgentRoleCatalog catalog)
    {
        var roles = catalog?.Roles ?? [];
        if (roles.Count == 0)
        {
            throw new InvalidOperationException("At least one role must be defined.");
        }

        var inputSettings = catalog?.Settings ?? new SupervisorSettings();
        var normalizedSettings = new SupervisorSettings
        {
            SupervisorModel = string.IsNullOrWhiteSpace(inputSettings.SupervisorModel) ? "gpt-5.4" : inputSettings.SupervisorModel.Trim(),
            SupervisorPromptPrefix = string.IsNullOrWhiteSpace(inputSettings.SupervisorPromptPrefix) ? null : inputSettings.SupervisorPromptPrefix.Trim(),
            RoleProposalPromptPrefix = string.IsNullOrWhiteSpace(inputSettings.RoleProposalPromptPrefix) ? null : inputSettings.RoleProposalPromptPrefix.Trim(),
            DefaultModel = string.IsNullOrWhiteSpace(inputSettings.DefaultModel) ? "gpt-5.4" : inputSettings.DefaultModel.Trim(),
            DefaultMaxAutoSteps = Math.Max(1, inputSettings.DefaultMaxAutoSteps),
            DefaultAutoPilotEnabled = inputSettings.DefaultAutoPilotEnabled,
            MaxConcurrentWorkers = Math.Max(1, inputSettings.MaxConcurrentWorkers),
            WorkerTimeoutMinutes = Math.Max(1, inputSettings.WorkerTimeoutMinutes),
            AllowWorkerPermissionRequests = inputSettings.AllowWorkerPermissionRequests,
            DefaultVerificationCommand = string.IsNullOrWhiteSpace(inputSettings.DefaultVerificationCommand) ? null : inputSettings.DefaultVerificationCommand.Trim(),
            OutputBufferMaxChars = Math.Max(1000, inputSettings.OutputBufferMaxChars),
            DecisionHistoryLimit = Math.Max(10, inputSettings.DecisionHistoryLimit),
            DefaultWorkspaceRoot = string.IsNullOrWhiteSpace(inputSettings.DefaultWorkspaceRoot) ? null : inputSettings.DefaultWorkspaceRoot.Trim(),
            EnvironmentVariables = NormalizeDictionary(inputSettings.EnvironmentVariables),
            EnableAttentionTracking = inputSettings.EnableAttentionTracking,
            AutoCreateDefaultLanes = inputSettings.AutoCreateDefaultLanes,
            EnableCoordinatorSurface = inputSettings.EnableCoordinatorSurface,
            EnableVerificationSurface = inputSettings.EnableVerificationSurface,
            AutoAcknowledgeAttentionOnFocus = inputSettings.AutoAcknowledgeAttentionOnFocus,
            ShowCompletedSurfaces = inputSettings.ShowCompletedSurfaces,
            SuggestFocusOnAttention = inputSettings.SuggestFocusOnAttention,
            MaxAttentionEvents = Math.Max(10, inputSettings.MaxAttentionEvents),
            DefaultLayoutMode = string.IsNullOrWhiteSpace(inputSettings.DefaultLayoutMode) ? "lanes" : inputSettings.DefaultLayoutMode.Trim(),
            AgentLaneName = string.IsNullOrWhiteSpace(inputSettings.AgentLaneName) ? "Agents" : inputSettings.AgentLaneName.Trim(),
            ControlLaneName = string.IsNullOrWhiteSpace(inputSettings.ControlLaneName) ? "Coordinator" : inputSettings.ControlLaneName.Trim(),
            VerificationLaneName = string.IsNullOrWhiteSpace(inputSettings.VerificationLaneName) ? "Verification" : inputSettings.VerificationLaneName.Trim(),
            DefaultRunDetailTab = string.IsNullOrWhiteSpace(inputSettings.DefaultRunDetailTab) ? "workspace" : inputSettings.DefaultRunDetailTab.Trim(),
            DefaultSettingsTab = string.IsNullOrWhiteSpace(inputSettings.DefaultSettingsTab) ? "orchestration" : inputSettings.DefaultSettingsTab.Trim()
        };

        var normalizedRoles = new List<AgentRoleDefinition>(roles.Count);
        var seenRoleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < roles.Count; index++)
        {
            var role = roles[index] ?? new AgentRoleDefinition();
            var roleId = role.RoleId.Trim();
            var name = role.Name.Trim();
            var promptTemplate = role.PromptTemplate.Trim();

            if (string.IsNullOrWhiteSpace(roleId))
            {
                throw new InvalidOperationException($"Role #{index + 1} is missing a roleId.");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"Role '{roleId}' is missing a display name.");
            }

            if (string.IsNullOrWhiteSpace(promptTemplate))
            {
                throw new InvalidOperationException($"Role '{roleId}' is missing a prompt template.");
            }

            if (!seenRoleIds.Add(roleId))
            {
                throw new InvalidOperationException($"Role ID '{roleId}' is duplicated.");
            }

            normalizedRoles.Add(new AgentRoleDefinition
            {
                RoleId = roleId,
                Name = name,
                Description = string.IsNullOrWhiteSpace(role.Description) ? null : role.Description.Trim(),
                Icon = string.IsNullOrWhiteSpace(role.Icon) ? null : role.Icon.Trim(),
                PromptTemplate = promptTemplate,
                Model = string.IsNullOrWhiteSpace(role.Model) ? normalizedSettings.DefaultModel : role.Model.Trim(),
                AllowAllTools = role.AllowAllTools,
                AllowAllPaths = role.AllowAllPaths,
                AllowAllUrls = role.AllowAllUrls,
                WorkspacePath = string.IsNullOrWhiteSpace(role.WorkspacePath) ? "." : role.WorkspacePath.Trim(),
                AllowedUrls = NormalizeList(role.AllowedUrls),
                AllowedTools = NormalizeList(role.AllowedTools),
                DeniedTools = NormalizeList(role.DeniedTools),
                AllowedPaths = NormalizeList(role.AllowedPaths),
                EnvironmentVariables = NormalizeDictionary(role.EnvironmentVariables)
            });
        }

        return new AgentRoleCatalog
        {
            Settings = normalizedSettings,
            Roles = normalizedRoles
        };
    }

    private SupervisorRun AddDecision(SupervisorRun run, string kind, string summary)
    {
        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var limit = Math.Max(10, settings.DecisionHistoryLimit);

        run.Decisions.Add(new SupervisorDecisionEntry
        {
            Kind = kind,
            Summary = summary,
            CreatedAt = DateTime.UtcNow
        });

        if (run.Decisions.Count > limit)
        {
            run.Decisions = run.Decisions[^limit..];
        }

        return run;
    }

    private void PersistRun(SupervisorRun run)
    {
        _runStore.Upsert(run);
    }

    private async Task BroadcastRunUpdatedAsync(SupervisorRun run)
    {
        await _hubContext.Clients.All.SendAsync("RunUpdated", run);
        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();
        var snapshotRun = _runStore.Get(run.RunId) ?? run;
        EnsureRunLayout(snapshotRun, settings);
        RecalculateAttentionAggregates(snapshotRun, settings);
        await _hubContext.Clients.All.SendAsync("RunSnapshotUpdated", BuildSnapshot(snapshotRun, settings));
    }

    private SupervisorLiveState? GetSupervisorLiveState(string runId)
        => _activeSupervisorStreams.TryGetValue(runId, out var liveState) ? liveState : null;

    private async Task BroadcastSupervisorStreamStartedAsync(string runId, string title, string commandPreview)
    {
        var liveState = new SupervisorLiveState(title, commandPreview);
        _activeSupervisorStreams[runId] = liveState;

        await _hubContext.Clients.All.SendAsync("SupervisorStreamStarted", runId, new
        {
            title,
            commandPreview,
            status = "running",
            updatedAt = liveState.UpdatedAt
        });
    }

    private async Task BroadcastSupervisorStreamChunkAsync(string runId, string chunk, bool isError)
    {
        if (string.IsNullOrEmpty(chunk) || !_activeSupervisorStreams.TryGetValue(runId, out var liveState))
        {
            return;
        }

        var emittedChunk = isError ? PrefixStderrChunk(chunk) : chunk;
        var preview = liveState.Append(emittedChunk, 6000);

        await _hubContext.Clients.All.SendAsync("SupervisorStreamChunk", runId, new
        {
            title = liveState.Title,
            commandPreview = liveState.CommandPreview,
            chunk = emittedChunk,
            preview,
            isError,
            status = "running",
            updatedAt = liveState.UpdatedAt
        });
    }

    private async Task BroadcastSupervisorStreamCompletedAsync(string runId, bool failed, string? finalOutput, string? error)
    {
        if (!_activeSupervisorStreams.TryRemove(runId, out var liveState))
        {
            return;
        }

        var preview = !string.IsNullOrWhiteSpace(finalOutput)
            ? TrimOutput(finalOutput)
            : liveState.GetPreview(6000);

        await _hubContext.Clients.All.SendAsync("SupervisorStreamCompleted", runId, new
        {
            title = liveState.Title,
            commandPreview = liveState.CommandPreview,
            status = failed ? "failed" : "completed",
            preview,
            error,
            updatedAt = DateTime.UtcNow
        });
    }

    private static string ExtractSummary(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "No response captured.";
        }

        var lines = output
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(8)
            .ToList();

        return string.Join(" ", lines);
    }

    private static WorkerReport ParseWorkerReport(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new WorkerReport(false, null, "No response captured.", null);
        }

        var lines = output
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        string? status = null;
        string? summary = null;
        string? next = null;

        foreach (var line in lines)
        {
            if (status is null && line.StartsWith("STATUS", StringComparison.OrdinalIgnoreCase))
            {
                status = ExtractMarkerValue(line);
                continue;
            }

            if (summary is null && line.StartsWith("SUMMARY", StringComparison.OrdinalIgnoreCase))
            {
                summary = ExtractMarkerValue(line);
                continue;
            }

            if (next is null && line.StartsWith("NEXT", StringComparison.OrdinalIgnoreCase))
            {
                next = ExtractMarkerValue(line);
            }
        }

        var hasStructuredReport = !(string.IsNullOrWhiteSpace(status) && string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(next));
        return new WorkerReport(
            hasStructuredReport,
            status,
            string.IsNullOrWhiteSpace(summary) ? ExtractSummary(output) : summary,
            next);
    }

    private static string ExtractMarkerValue(string line)
    {
        var index = line.IndexOf(':');
        if (index < 0)
        {
            index = line.IndexOf('：');
        }

        return index < 0 ? line.Trim() : line[(index + 1)..].Trim();
    }

    private static string TrimOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        output = output.Trim();
        return output.Length <= 2000 ? output : output[^2000..];
    }

    private static string PrefixStderrChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return string.Empty;
        }

        var normalized = chunk.Replace("\r\n", "\n").Replace("\r", "\n");
        return string.Join("\n", normalized.Split('\n').Select(line => $"[stderr] {line}"));
    }

    private static string CreateTitleFromGoal(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return $"Run {DateTime.Now:HHmmss}";
        }

        var trimmed = goal.Trim();
        return trimmed.Length <= 48 ? trimmed : trimmed[..48] + "…";
    }

    private static SupervisorPlan? TryParsePlan(string rawOutput, SupervisorRun? run = null)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return null;
        }

        var json = ExtractJsonObject(rawOutput.Trim());
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<SupervisorPlan>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed is not null && (!string.IsNullOrWhiteSpace(parsed.Summary) || parsed.MarkCompleted || parsed.RunVerification || parsed.Actions.Count > 0))
            {
                return parsed;
            }
        }
        catch
        {
            // Fall through and try a richer compatibility parser.
        }

        return run is null ? null : TryParseRichSupervisorPlan(json, run);
    }

    private static SupervisorPlan? TryParseRichSupervisorPlan(string json, SupervisorRun run)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var plan = new SupervisorPlan
            {
                Summary = TryGetString(root, "summary")
            };

            if (root.TryGetProperty("runVerification", out var runVerificationElement) && runVerificationElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                plan.RunVerification = runVerificationElement.GetBoolean();
            }

            if (root.TryGetProperty("markCompleted", out var markCompletedElement) && markCompletedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                plan.MarkCompleted = markCompletedElement.GetBoolean();
            }

            var nextRoundPlan = root.TryGetProperty("next_round_plan", out var nextRoundPlanElement) && nextRoundPlanElement.ValueKind == JsonValueKind.Object
                ? nextRoundPlanElement
                : default;
            var decision = root.TryGetProperty("decision", out var decisionElement) && decisionElement.ValueKind == JsonValueKind.Object
                ? decisionElement
                : default;

            if (string.IsNullOrWhiteSpace(plan.Summary))
            {
                var roundTitle = nextRoundPlan.ValueKind == JsonValueKind.Object ? TryGetString(nextRoundPlan, "round_title") : null;
                var primaryGoal = nextRoundPlan.ValueKind == JsonValueKind.Object ? TryGetString(nextRoundPlan, "primary_goal") : null;
                var decisionReasons = decision.ValueKind == JsonValueKind.Object
                    ? ReadStringArray(decision, "reason")
                    : [];
                plan.Summary = string.Join(" ", new[]
                {
                    string.IsNullOrWhiteSpace(roundTitle) ? null : $"当前应继续执行 {roundTitle}。",
                    string.IsNullOrWhiteSpace(primaryGoal) ? null : $"本轮目标：{primaryGoal}",
                    decisionReasons.Count > 0 ? $"判断依据：{string.Join("；", decisionReasons)}" : null
                }.Where(item => !string.IsNullOrWhiteSpace(item)));
            }

            if (nextRoundPlan.ValueKind == JsonValueKind.Object)
            {
                plan.RunVerification |= TryGetBoolean(nextRoundPlan, "requires_verification");

                if (nextRoundPlan.TryGetProperty("roles", out var rolesElement) && rolesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rolePlan in rolesElement.EnumerateArray())
                    {
                        if (rolePlan.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var roleId = TryGetString(rolePlan, "role_id") ?? TryGetString(rolePlan, "roleId");
                        if (string.IsNullOrWhiteSpace(roleId))
                        {
                            continue;
                        }

                        var worker = run.Workers.FirstOrDefault(item => string.Equals(item.RoleId, roleId, StringComparison.OrdinalIgnoreCase));
                        if (worker is null)
                        {
                            continue;
                        }

                        var tasks = ReadStringArray(rolePlan, "tasks");
                        var outputArtifacts = ReadStringArray(rolePlan, "output_artifacts");
                        var writeAccess = TryGetString(rolePlan, "write_access") ?? "md-only";
                        var roundTitle = TryGetString(nextRoundPlan, "round_title") ?? $"第 {TryGetInt(nextRoundPlan, "round_number") ?? 1} 轮";
                        var primaryGoal = TryGetString(nextRoundPlan, "primary_goal") ?? run.AssistantActiveRoundObjective ?? run.Goal;
                        var promptLines = new List<string>
                        {
                            $"当前执行 AI 助手计划：{roundTitle}",
                            $"本轮目标：{primaryGoal}",
                            $"你的执行方式：{writeAccess}"
                        };

                        if (outputArtifacts.Count > 0)
                        {
                            promptLines.Add($"本轮必须产出的交付物：{string.Join("，", outputArtifacts)}");
                        }

                        if (tasks.Count > 0)
                        {
                            promptLines.Add("请按下面任务顺序推进：");
                            promptLines.AddRange(tasks.Select(item => $"- {item}"));
                        }

                        var mode = string.Equals(worker.Status, "idle", StringComparison.OrdinalIgnoreCase)
                            ? "start"
                            : string.Equals(worker.Status, "running", StringComparison.OrdinalIgnoreCase)
                                ? "continue"
                                : string.Equals(worker.Status, "completed", StringComparison.OrdinalIgnoreCase) || string.Equals(worker.Status, "failed", StringComparison.OrdinalIgnoreCase) || string.Equals(worker.Status, "stopped", StringComparison.OrdinalIgnoreCase)
                                    ? "restart"
                                    : "continue";

                        plan.Actions.Add(new SupervisorWorkerAction
                        {
                            WorkerId = worker.WorkerId,
                            Mode = mode,
                            Prompt = string.Join(Environment.NewLine, promptLines)
                        });
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(plan.Summary) || plan.MarkCompleted || plan.RunVerification || plan.Actions.Count > 0)
            {
                return plan;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : false;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    private static string FindWorkspaceRoot(SupervisorSettings? settings = null)
    {
        if (settings is not null && !string.IsNullOrWhiteSpace(settings.DefaultWorkspaceRoot))
        {
            var configuredRoot = Path.GetFullPath(settings.DefaultWorkspaceRoot);
            if (Directory.Exists(configuredRoot))
            {
                return configuredRoot;
            }
        }

        return AgentRoleConfigService.GetBaseDir();
    }

    private static string ResolveExecutionRoot(SupervisorSettings? settings, string? configuredPath)
    {
        var defaultRoot = FindWorkspaceRoot(settings);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return defaultRoot;
        }

        var rawPath = configuredPath.Trim();
        var fullPath = Path.GetFullPath(Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(defaultRoot, rawPath));

        if (!Path.IsPathRooted(rawPath) && !IsSubPathOf(fullPath, defaultRoot))
        {
            throw new InvalidOperationException($"Workspace root '{configuredPath}' must stay inside '{defaultRoot}'.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Workspace root '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    private static WorkspaceBootstrapResult InitializeTaskWorkspace(string executionRoot, string goal, string? requestedWorkspaceName)
    {
        var normalizedExecutionRoot = Path.GetFullPath(executionRoot);
        Directory.CreateDirectory(normalizedExecutionRoot);
        var referencedDirectories = ExtractReferencedDirectories([goal], normalizedExecutionRoot, null);

        var workspaceName = EnsureUniqueWorkspaceName(normalizedExecutionRoot, BuildWorkspaceName(goal, requestedWorkspaceName));
        var workspacePath = Path.Combine(normalizedExecutionRoot, workspaceName);
        Directory.CreateDirectory(workspacePath);
        TryInitializeGitRepository(workspacePath);
        EnsureWorkspaceDefinitionFile(workspacePath, workspaceName, referencedDirectories);
        EnsureCopilotInstructions(workspacePath);
        EnsureGitIgnore(workspacePath);
        WriteWorkspaceMetadata(workspacePath, workspaceName, goal, normalizedExecutionRoot, referencedDirectories);

        return new WorkspaceBootstrapResult(normalizedExecutionRoot, workspacePath, workspaceName, false);
    }

    private static WorkspaceBootstrapResult UseManualWorkspace(string executionRoot)
    {
        var normalizedPath = Path.GetFullPath(executionRoot);
        return new WorkspaceBootstrapResult(normalizedPath, normalizedPath, Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), true);
    }

    private static string EnsureUniqueWorkspaceName(string executionRoot, string preferredName)
    {
        var candidate = preferredName;
        var index = 1;
        while (Directory.Exists(Path.Combine(executionRoot, candidate)))
        {
            candidate = $"{preferredName}-{index++}";
        }

        return candidate;
    }

    private static string BuildWorkspaceName(string goal, string? requestedWorkspaceName)
    {
        var explicitName = NormalizeWorkspaceName(requestedWorkspaceName);
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        var keywordName = NormalizeWorkspaceName(goal);
        if (!string.IsNullOrWhiteSpace(keywordName))
        {
            return keywordName;
        }

        return $"task-{CreateStableSuffix(goal)}";
    }

    private static string NormalizeWorkspaceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lowered = value.Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        var lastWasDash = false;

        foreach (var ch in lowered)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasDash = false;
                continue;
            }

            if (sb.Length == 0 || lastWasDash)
            {
                continue;
            }

            sb.Append('-');
            lastWasDash = true;
        }

        var sanitized = sb.ToString().Trim('-');
        if (sanitized.Length > 24)
        {
            sanitized = sanitized[..24].Trim('-');
        }

        if (!string.IsNullOrWhiteSpace(sanitized))
        {
            return sanitized;
        }

        var fallback = $"task-{CreateStableSuffix(lowered)}";
        return fallback.Length > 24 ? fallback[..24].Trim('-') : fallback;
    }

    private static string CreateStableSuffix(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes)[..6].ToLowerInvariant();
    }

    private static void TryInitializeGitRepository(string workspacePath)
    {
        if (Directory.Exists(Path.Combine(workspacePath, ".git")))
        {
            return;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workspacePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("init");
            process.Start();
            process.WaitForExit(5000);
        }
        catch
        {
            // Ignore bootstrap git failures; the workspace should still be usable.
        }
    }

    private static void EnsureGitIgnore(string workspacePath)
    {
        var gitIgnorePath = Path.Combine(workspacePath, ".gitignore");
        if (File.Exists(gitIgnorePath))
        {
            return;
        }

        File.WriteAllText(gitIgnorePath, "*.user\r\n*.tmp\r\nbin/\r\nobj/\r\n");
    }

    private static void EnsureWorkspaceDefinitionFile(string workspacePath, string workspaceName, IReadOnlyCollection<string>? additionalDirectories)
    {
        var workspaceFilePath = Path.Combine(workspacePath, $"{workspaceName}.code-workspace");
        if (File.Exists(workspaceFilePath))
        {
            return;
        }

        var folders = new List<Dictionary<string, string?>>
        {
            new()
            {
                ["path"] = "."
            }
        };

        foreach (var directory in NormalizeList(additionalDirectories))
        {
            folders.Add(new Dictionary<string, string?>
            {
                ["name"] = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                ["path"] = ToWorkspaceFolderPath(workspacePath, directory)
            });
        }

        var payload = JsonSerializer.Serialize(new
        {
            folders,
            settings = new Dictionary<string, object?>
            {
                ["files.exclude"] = new Dictionary<string, bool>
                {
                    ["**/bin"] = true,
                    ["**/obj"] = true
                }
            }
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(workspaceFilePath, payload, new UTF8Encoding(false));
    }

    private static void EnsureCopilotInstructions(string workspacePath)
    {
        var githubDir = Path.Combine(workspacePath, ".github");
        var instructionsPath = Path.Combine(githubDir, "copilot-instructions.md");
        var legacyInstructionsPath = Path.Combine(workspacePath, "copilot-instructions.md");

        Directory.CreateDirectory(githubDir);

        if (!File.Exists(instructionsPath) && File.Exists(legacyInstructionsPath))
        {
            File.Move(legacyInstructionsPath, instructionsPath);
            return;
        }

        if (File.Exists(instructionsPath))
        {
            return;
        }

        File.WriteAllText(instructionsPath, GetDefaultCopilotInstructionsContent());
    }

    private static string EnsureRoundHistoryDocument(SupervisorRun run)
    {
        var workspaceRoot = string.IsNullOrWhiteSpace(run.WorkspaceRoot)
            ? AgentRoleConfigService.GetBaseDir()
            : run.WorkspaceRoot!;

        var documentPath = string.IsNullOrWhiteSpace(run.RoundHistoryDocumentPath)
            ? Path.Combine(workspaceRoot, "repoops-round-history.md")
            : run.RoundHistoryDocumentPath!;

        documentPath = Path.GetFullPath(documentPath);
        var parentDirectory = Path.GetDirectoryName(documentPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        if (!File.Exists(documentPath))
        {
            File.WriteAllText(documentPath, BuildRoundHistoryHeader(run), new UTF8Encoding(false));
        }

        run.RoundHistoryDocumentPath = documentPath;
        return documentPath;
    }

    private static string BuildRoundHistoryHeader(SupervisorRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RepoOPS 协作轮次记录");
        sb.AppendLine();
        sb.AppendLine($"- Run 标题：{run.Title}");
        sb.AppendLine($"- 总目标：{run.Goal}");
        sb.AppendLine($"- 工作区：{run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir()}");
        sb.AppendLine($"- 创建时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("> 说明：每次进入一轮调度时，都会把当下各角色已落库的状态、原始输出摘录，以及超管本轮原始返回按轮追加到这里，方便直接追查历史。");
        sb.AppendLine();
        return sb.ToString();
    }

    private void TryAppendRoundHistoryEntry(
        SupervisorRun run,
        int roundNumber,
        string trigger,
        string? extraInstruction,
        string supervisorRawOutput,
        SupervisorPlan? structuredPlan)
    {
        try
        {
            var documentPath = EnsureRoundHistoryDocument(run);
            var entry = BuildRoundHistoryEntry(run, roundNumber, trigger, extraInstruction, supervisorRawOutput, structuredPlan);
            File.AppendAllText(documentPath, entry, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append round history for run {RunId}", run.RunId);
        }
    }

    private static string BuildRoundHistoryEntry(
        SupervisorRun run,
        int roundNumber,
        string trigger,
        string? extraInstruction,
        string supervisorRawOutput,
        SupervisorPlan? structuredPlan)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## 第 {Math.Max(1, roundNumber)} 轮");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 触发方式：{FormatRoundTrigger(trigger)}");
        sb.AppendLine($"- Run 状态：{run.Status}");

        if (!string.IsNullOrWhiteSpace(extraInstruction))
        {
            sb.AppendLine($"- 追加问题 / 额外要求：{extraInstruction.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(run.LatestSummary))
        {
            sb.AppendLine($"- 上一轮已落库结论：{run.LatestSummary}");
        }

        if (run.LastVerification is not null)
        {
            sb.AppendLine($"- 最近验证：status={run.LastVerification.Status}; passed={run.LastVerification.Passed}; summary={run.LastVerification.Summary}");
        }

        sb.AppendLine();
        sb.AppendLine("### 角色现状（按已落库内容摘录）");

        foreach (var worker in run.Workers)
        {
            sb.AppendLine();
            sb.AppendLine($"#### {worker.RoleName}");
            sb.AppendLine($"- 状态：{worker.Status}");
            sb.AppendLine($"- 汇报状态：{worker.LastReportedStatus ?? "—"}");
            sb.AppendLine($"- 当前任务：{worker.CurrentTask ?? "—"}");
            sb.AppendLine($"- 下一步：{worker.LastNextStep ?? "—"}");
            sb.AppendLine($"- 摘要：{worker.LastSummary ?? "—"}");

            if (!string.IsNullOrWhiteSpace(worker.LastOutputPreview))
            {
                sb.AppendLine();
                sb.AppendLine("原始输出摘录：");
                sb.AppendLine("```text");
                sb.AppendLine(worker.LastOutputPreview);
                sb.AppendLine("```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("### 超管本轮原始输出");
        sb.AppendLine();
        sb.AppendLine("```text");
        sb.AppendLine(string.IsNullOrWhiteSpace(supervisorRawOutput) ? "<empty>" : supervisorRawOutput.TrimEnd());
        sb.AppendLine("```");

        if (structuredPlan is not null)
        {
            sb.AppendLine();
            sb.AppendLine("### 超管解析后的动作");
            sb.AppendLine();
            sb.AppendLine($"- runVerification：{structuredPlan.RunVerification}");
            sb.AppendLine($"- markCompleted：{structuredPlan.MarkCompleted}");
            sb.AppendLine($"- summary：{structuredPlan.Summary ?? "—"}");

            if (structuredPlan.Actions.Count > 0)
            {
                sb.AppendLine();
                foreach (var action in structuredPlan.Actions)
                {
                    var worker = run.Workers.FirstOrDefault(item => string.Equals(item.WorkerId, action.WorkerId, StringComparison.OrdinalIgnoreCase));
                    var workerName = worker?.RoleName ?? action.WorkerId;
                    sb.AppendLine($"- {workerName} · {action.Mode} · {action.Prompt ?? "<no prompt>"}");
                }
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string FormatRoundTrigger(string trigger)
        => (trigger ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "ask-supervisor" => "用户主动询问调度器",
            "auto-step" => "自动推进",
            _ => string.IsNullOrWhiteSpace(trigger) ? "未知" : trigger
        };

    private static string GetDefaultCopilotInstructionsContent()
    {
        return "# Workspace Rules\r\n\r\n## Scope\r\n- Work only inside the current workspace root unless the user explicitly expands it.\r\n- If the user explicitly mentions a directory or file path that exists outside the current workspace root, RepoOPS may grant runtime access to that location by appending `--add-dir` for the resolved directory. Do not assume access to unstated external paths.\r\n- When an external directory is needed for ongoing work, keep the `.code-workspace` file aligned when practical, or ask the user to bring the needed files into the workspace.\r\n- Keep notes and prompts concise.\r\n\r\n## 变更日志规范\r\n- 根据自己 agent 的名字创建日志文件，例如 `开发1-变更日志.md`、`开发2-变更日志.md`。\r\n- 仅追加，不删改历史。新日志追加到文末。\r\n- 格式固定：\r\n\r\n```\r\n## YYYY-MM-DD HH:mm:ss\r\n\r\n### 实现目标\r\n- （变更 / 修复 / 优化目标）\r\n\r\n### 变更内容\r\n- （改动点）\r\n\r\n### 验证结果（可选）\r\n\r\n### 后续计划（可选）\r\n```\r\n\r\n## 关键事项补充\r\n- 允许追加新的简短章节，并注明 `added by <agent-name>`。\r\n";
    }

    private static void WriteWorkspaceMetadata(string workspacePath, string workspaceName, string goal, string executionRoot, IReadOnlyCollection<string>? additionalDirectories)
    {
        var metadataPath = Path.Combine(workspacePath, ".repoops-workspace.json");
        var payload = JsonSerializer.Serialize(new
        {
            workspaceName,
            workspacePath,
            workspaceFilePath = Path.Combine(workspacePath, $"{workspaceName}.code-workspace"),
            executionRoot,
            goal,
            additionalDirectories = NormalizeList(additionalDirectories),
            createdAt = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(metadataPath, payload);
    }

    private static string ResolveWorkspacePath(string workspaceRoot, string? configuredPath)
    {
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(workspaceRoot) ? Directory.GetCurrentDirectory() : workspaceRoot);
        var rawPath = string.IsNullOrWhiteSpace(configuredPath) ? "." : configuredPath.Trim();
        var fullPath = Path.GetFullPath(Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(root, rawPath));

        if (!IsSubPathOf(fullPath, root))
        {
            throw new InvalidOperationException($"Workspace path '{configuredPath}' must stay inside the repository root '{root}'.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Workspace path '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    private static string ResolveRunWorkspaceRoot(string repositoryRoot, string? configuredPath)
    {
        return ResolveWorkspacePath(repositoryRoot, configuredPath);
    }

    private static string ResolveAllowedPath(string workspaceRoot, string configuredPath)
    {
        return ResolveWorkspacePath(workspaceRoot, configuredPath);
    }

    private static bool MergeAdditionalAllowedDirectories(SupervisorRun run, string? workspaceRoot, params string?[] texts)
    {
        if (run is null)
        {
            return false;
        }

        var merged = NormalizeList(run.AdditionalAllowedDirectories);
        var extracted = ExtractReferencedDirectories(texts, run.ExecutionRoot ?? workspaceRoot, workspaceRoot);
        var changed = false;

        foreach (var directory in extracted)
        {
            if (merged.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            merged.Add(directory);
            changed = true;
        }

        if (changed)
        {
            run.AdditionalAllowedDirectories = merged;
        }

        return changed;
    }

    private static List<string> ExtractReferencedDirectories(IEnumerable<string?>? texts, string? baseDirectory, string? workspaceRoot)
    {
        var results = new List<string>();
        if (texts is null)
        {
            return results;
        }

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (var candidate in EnumeratePathCandidates(text))
            {
                var normalized = NormalizeReferencedDirectoryCandidate(candidate, baseDirectory);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(workspaceRoot) && IsSubPathOf(normalized, workspaceRoot))
                {
                    continue;
                }

                if (!results.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(normalized);
                }
            }
        }

        return results;
    }

    private static IEnumerable<string> EnumeratePathCandidates(string text)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in QuotedWindowsPathRegex.Matches(text))
        {
            var value = match.Groups["path"].Value;
            if (yielded.Add(value))
            {
                yield return value;
            }
        }

        foreach (Match match in BareWindowsPathRegex.Matches(text))
        {
            var value = match.Groups["path"].Value;
            if (yielded.Add(value))
            {
                yield return value;
            }
        }

        foreach (Match match in RelativePathRegex.Matches(text))
        {
            var value = match.Groups["path"].Value;
            if (yielded.Add(value))
            {
                yield return value;
            }
        }
    }

    private static string? NormalizeReferencedDirectoryCandidate(string rawCandidate, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawCandidate))
        {
            return null;
        }

        var candidate = rawCandidate
            .Trim()
            .Trim('"', '\'')
            .TrimEnd('.', ',', ';', ':', '!', '?', '。', '，', '；', '：', '）', ')', '】', ']', '>');

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var fullPath = Path.IsPathRooted(candidate)
                ? Path.GetFullPath(candidate)
                : string.IsNullOrWhiteSpace(baseDirectory)
                    ? null
                    : Path.GetFullPath(Path.Combine(baseDirectory, candidate));

            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return null;
            }

            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }

            if (File.Exists(fullPath))
            {
                return Path.GetDirectoryName(fullPath);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ToWorkspaceFolderPath(string workspacePath, string directory)
    {
        try
        {
            var relative = Path.GetRelativePath(workspacePath, directory);
            return string.IsNullOrWhiteSpace(relative) ? "." : relative;
        }
        catch
        {
            return directory;
        }
    }

    private static bool IsSubPathOf(string candidatePath, string rootPath)
    {
        var normalizedCandidate = EnsureTrailingSeparator(Path.GetFullPath(candidatePath));
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static List<string> NormalizeList(IEnumerable<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    private static Dictionary<string, string> NormalizeDictionary(IDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var result = new Dictionary<string, string>();
        foreach (var kvp in values)
        {
            var key = kvp.Key?.Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = kvp.Value ?? string.Empty;
            }
        }

        return result;
    }

    private static string BuildCommandPreview(ProcessStartInfo startInfo)
    {
        if (OperatingSystem.IsWindows())
        {
            return BuildPowerShellReplayCommand(startInfo);
        }

        var arguments = startInfo.ArgumentList.Select(QuoteArgument);
        return string.Join(" ", new[] { startInfo.FileName }.Concat(arguments));
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(string workingDirectory, string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    private static string BuildPromptFileReplayCommand(string workingDirectory, string promptFilePath, string executable, IReadOnlyList<string> arguments)
    {
        var renderedArgs = arguments.Select(argument =>
            string.Equals(argument, PromptArgumentToken, StringComparison.Ordinal)
                ? "$prompt"
                : RenderPowerShellArgument(argument));

        return string.Join(Environment.NewLine, new[]
        {
            "$utf8NoBom = [System.Text.UTF8Encoding]::new($false)",
            "$OutputEncoding = $utf8NoBom",
            "[Console]::InputEncoding = $utf8NoBom",
            "[Console]::OutputEncoding = $utf8NoBom",
            $"Set-Location -LiteralPath {QuotePowerShellLiteral(workingDirectory)}",
            $"$promptPath = {QuotePowerShellLiteral(promptFilePath)}",
            "$prompt = Get-Content -LiteralPath $promptPath -Raw -Encoding utf8",
            $"& {QuotePowerShellLiteral(executable)} {string.Join(" ", renderedArgs)}"
        });
    }

    private static PromptArtifact CreateWorkerPromptArtifact(SupervisorRun run, AgentWorkerSession worker, string workspaceRoot, string prompt)
    {
        var roleToken = SanitizeFileToken(worker.RoleId, "worker");
        var roleNameToken = SanitizeFileToken(worker.RoleName, roleToken);
        var sessionToken = SanitizeFileToken(worker.SessionId, "session");
        var folderPath = Path.Combine(workspaceRoot, ".repoops", "prompts", run.RunId, "workers", $"{roleToken}-{roleNameToken}");
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{sessionToken}-{Guid.NewGuid():N}.prompt.md";

        return CreatePromptArtifact(
            folderPath,
            fileName,
            prompt,
            new
            {
                runId = run.RunId,
                type = "worker",
                workerId = worker.WorkerId,
                roleId = worker.RoleId,
                roleName = worker.RoleName,
                sessionId = worker.SessionId,
                workspaceRoot,
                createdAt = DateTime.UtcNow
            });
    }

    private static PromptArtifact CreateSupervisorPromptArtifact(SupervisorRun run, string workspaceRoot, string liveTitle, string prompt)
    {
        var titleToken = SanitizeFileToken(liveTitle, "supervisor");
        var folderPath = Path.Combine(workspaceRoot, ".repoops", "prompts", run.RunId, "coordinator");
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{titleToken}-{Guid.NewGuid():N}.prompt.md";

        return CreatePromptArtifact(
            folderPath,
            fileName,
            prompt,
            new
            {
                runId = run.RunId,
                type = "coordinator",
                liveTitle,
                workspaceRoot,
                createdAt = DateTime.UtcNow
            });
    }

    private static PromptArtifact CreatePromptArtifact(string folderPath, string fileName, string prompt, object metadata)
    {
        Directory.CreateDirectory(folderPath);

        var promptPath = Path.Combine(folderPath, fileName);
        File.WriteAllText(promptPath, prompt ?? string.Empty, new UTF8Encoding(false));

        var metadataPath = Path.ChangeExtension(promptPath, ".json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true
        }), new UTF8Encoding(false));

        return new PromptArtifact(promptPath, metadataPath);
    }

    private static string SanitizeFileToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var sb = new StringBuilder();
        var lastWasDash = false;

        foreach (var ch in value.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
                continue;
            }

            if (lastWasDash || sb.Length == 0)
            {
                continue;
            }

            sb.Append('-');
            lastWasDash = true;
        }

        var sanitized = sb.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return fallback;
        }

        return sanitized.Length <= 48 ? sanitized : sanitized[..48].Trim('-');
    }

    private static string BuildPowerShellReplayCommand(ProcessStartInfo startInfo)
    {
        var args = startInfo.ArgumentList.ToList();
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
        {
            lines.Add($"Set-Location -LiteralPath {QuotePowerShellLiteral(startInfo.WorkingDirectory)}");
        }

        var promptIndex = args.FindIndex(item => string.Equals(item, "-p", StringComparison.Ordinal));
        if (promptIndex >= 0 && promptIndex + 1 < args.Count)
        {
            var prompt = args[promptIndex + 1] ?? string.Empty;
            lines.Add("$prompt = @'");
            lines.Add(prompt.Replace("\r\n", "\n").Replace("\r", "\n"));
            lines.Add("'@");
            args[promptIndex + 1] = "$prompt";
        }

        var renderedArgs = args.Select(RenderPowerShellArgument);
        lines.Add($"& {QuotePowerShellLiteral(startInfo.FileName)} {string.Join(" ", renderedArgs)}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string QuoteArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        return argument.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'' or '&' or '(' or ')' or ';')
            ? $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
            : argument;
    }

    private static string RenderPowerShellArgument(string argument)
    {
        if (string.Equals(argument, "$prompt", StringComparison.Ordinal))
        {
            return argument;
        }

        return QuotePowerShellLiteral(argument);
    }

    private static string QuotePowerShellLiteral(string? argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "''";
        }

        return $"'{argument.Replace("'", "''")}'";
    }

    private sealed record WorkerProcessHandle(Process Process, StringBuilder OutputBuffer);

    private sealed record PromptArtifact(string PromptPath, string MetadataPath);

    private sealed record CopilotLaunchPlan(ProcessStartInfo StartInfo, string CommandPreview)
    {
        public Process CreateProcess() => new() { StartInfo = StartInfo };
    }

    private sealed class SupervisorLiveState(string title, string commandPreview)
    {
        private readonly object _syncRoot = new();
        private readonly StringBuilder _buffer = new();

        public string Title { get; } = title;
        public string CommandPreview { get; } = commandPreview;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; private set; } = DateTime.UtcNow;

        public string Append(string chunk, int maxChars)
        {
            lock (_syncRoot)
            {
                _buffer.Append(chunk);
                if (_buffer.Length > maxChars)
                {
                    _buffer.Remove(0, _buffer.Length - maxChars);
                }

                UpdatedAt = DateTime.UtcNow;
                return _buffer.ToString();
            }
        }

        public string GetPreview(int maxChars)
        {
            lock (_syncRoot)
            {
                var text = _buffer.ToString();
                return text.Length <= maxChars ? text : text[^maxChars..];
            }
        }
    }

    private sealed record OneShotResult(string Output, string CommandPreview);

    private sealed record WorkerReport(bool HasStructuredReport, string? Status, string Summary, string? Next);

    private sealed class SupervisorPlan
    {
        public string? Summary { get; set; }
        public bool RunVerification { get; set; }
        public bool MarkCompleted { get; set; }
        public List<SupervisorWorkerAction> Actions { get; set; } = [];
    }

    private sealed class SupervisorWorkerAction
    {
        public string WorkerId { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string? Prompt { get; set; }
    }

    private sealed record WorkspaceBootstrapResult(string ExecutionRoot, string WorkspacePath, string WorkspaceName, bool UsesManualWorkspaceRoot);
}
