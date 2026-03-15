using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RepoOPS.Agents.Models;
using RepoOPS.Hubs;
using RepoOPS.Services;

namespace RepoOPS.Agents.Services;

public sealed class V3PairOrchestratorService : IDisposable
{
    private const string V3ContextRoot = "Docs/ai-context/v3";
    private const string DefaultMainRoleId = "helmsman";
    private const string DefaultSubRoleId = "pathfinder";
    private const string DefaultWingmanRoleId = "redteam-wingman";
    private const string DefaultExecutionModel = "gpt-5.4";

    private static readonly Regex SingleValueRegex = new(@"^(?<key>[A-Z_]+):\s*(?<value>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private const string GoalStatusComplete = "COMPLETE";
    private const string GoalStatusIncomplete = "INCOMPLETE";

    private readonly AgentRoleConfigService _roleConfigService;
    private readonly V3RunStore _runStore;
    private readonly V2WorkspaceBootstrapService _workspaceBootstrapService;
    private readonly PtyService _ptyService;
    private readonly IHubContext<TaskHub> _hubContext;
    private readonly ILogger<V3PairOrchestratorService> _logger;
    private readonly ConcurrentDictionary<string, V3PairRun> _runs = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<int>> _sessionCompletions = new();
    private readonly ConcurrentDictionary<string, int> _completedSessionExitCodes = new();

    public V3PairOrchestratorService(
        AgentRoleConfigService roleConfigService,
        V3RunStore runStore,
        V2WorkspaceBootstrapService workspaceBootstrapService,
        PtyService ptyService,
        IHubContext<TaskHub> hubContext,
        ILogger<V3PairOrchestratorService> logger)
    {
        _roleConfigService = roleConfigService;
        _runStore = runStore;
        _workspaceBootstrapService = workspaceBootstrapService;
        _ptyService = ptyService;
        _hubContext = hubContext;
        _logger = logger;

        _ptyService.SessionCompleted += OnPtySessionCompleted;

        foreach (var run in _runStore.GetAll())
        {
            _runs[run.RunId] = run;
        }
    }

    public void Dispose()
    {
        _ptyService.SessionCompleted -= OnPtySessionCompleted;
    }

    public IReadOnlyList<V3PairRun> GetRuns()
    {
        SyncRunsFromStore();
        return [.. _runs.Values.OrderByDescending(item => item.UpdatedAt)];
    }

    public V3PairRun? GetRun(string runId)
    {
        SyncRunsFromStore();
        return _runs.TryGetValue(runId, out var run) ? run : null;
    }

    public V3PairRunSnapshot GetRunSnapshot(string runId)
    {
        var run = RequireRun(runId);
        return new V3PairRunSnapshot
        {
            Run = run,
            Rounds = run.Rounds,
            Decisions = run.Decisions
        };
    }

    public async Task<V3PairRun> CreateRunAsync(CreateV3PairRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Goal))
        {
            throw new InvalidOperationException("Goal is required.");
        }

        var runId = Guid.NewGuid().ToString("N");
        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var mainRole = ResolveRole(catalog.Roles, request.MainRoleId, DefaultMainRoleId);
        var subRole = ResolveRole(catalog.Roles, request.SubRoleId, DefaultSubRoleId);
        var executionRoot = ResolveWorkspace(settings, request.WorkspaceRoot);
        var bootstrap = _workspaceBootstrapService.Bootstrap(
            executionRoot,
            request.Goal.Trim(),
            request.WorkspaceRoot,
            request.WorkspaceName,
            [mainRole, subRole],
            runId);

        var run = new V3PairRun
        {
            RunId = runId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? CreateTitle(request.Goal) : request.Title.Trim(),
            Goal = request.Goal.Trim(),
            Status = "draft",
            MaxRounds = request.MaxRounds > 0 ? request.MaxRounds : 6,
            WorkspaceRoot = bootstrap.WorkspaceRoot,
            ExecutionRoot = bootstrap.ExecutionRoot,
            WorkspaceName = bootstrap.WorkspaceName,
            WorkspaceMetadataFile = bootstrap.WorkspaceMetadataFile,
            AllowedPathsFile = bootstrap.AllowedPathsFile,
            AllowedToolsFile = bootstrap.AllowedToolsFile,
            AllowedUrlsFile = bootstrap.AllowedUrlsFile,
            MainRoleId = mainRole.RoleId,
            MainRoleName = mainRole.Name,
            MainRoleIcon = mainRole.Icon,
            SubRoleId = subRole.RoleId,
            SubRoleName = subRole.Name,
            SubRoleIcon = subRole.Icon
        };

        EnsureRunArtifactScaffold(run);
        AddDecision(run, "run-created", $"V3 run created: {run.Title}");
        LogRunEvent(run, "调度", RunCsvLogService.BuildDetails(
            ("事件", "运行已创建"),
            ("标题", run.Title),
            ("目标", run.Goal),
            ("工作区", run.WorkspaceRoot),
            ("执行根目录", run.ExecutionRoot),
            ("工作区名", run.WorkspaceName),
            ("元数据", run.WorkspaceMetadataFile),
            ("路径策略", run.AllowedPathsFile),
            ("工具策略", run.AllowedToolsFile),
            ("URL策略", run.AllowedUrlsFile),
            ("主线角色", run.MainRoleName),
            ("子线角色", run.SubRoleName),
            ("Git仓库存在", bootstrap.GitRepositoryPresent),
            ("RepoOPS执行Git初始化", bootstrap.GitInitializedByRepoOps),
            ("运行日志", RunCsvLogService.GetLogPath(run.WorkspaceRoot, run.RunId))));
        _runs[run.RunId] = run;
        await BroadcastRunUpdatedAsync(run);

        if (request.AutoStart)
        {
            _ = Task.Run(() => RunMainLoopAsync(run.RunId));
        }

        return run;
    }

    public async Task<V3PairRun> StopRunAsync(string runId)
    {
        var run = RequireRun(runId);
        run.Status = "stopped";
        run.MainThreadStatus = "stopped";
        run.SubThreadStatus = "stopped";
        run.UpdatedAt = DateTime.UtcNow;
        AddDecision(run, "run-stopped", "Run stopped by user.");
        LogRunEvent(run, "调度", RunCsvLogService.BuildDetails(("事件", "运行已停止")));

        if (!string.IsNullOrWhiteSpace(run.MainThreadSessionId))
        {
            _ptyService.StopSession(run.MainThreadSessionId);
        }

        if (!string.IsNullOrWhiteSpace(run.SubThreadSessionId))
        {
            _ptyService.StopSession(run.SubThreadSessionId);
        }

        foreach (var completion in _sessionCompletions.Values)
        {
            completion.TrySetResult(-1);
        }

        await BroadcastRunUpdatedAsync(run);
        return run;
    }

    public async Task<V3PairRun> UpsertInterjectionAsync(string runId, string text, bool useWingman = false)
    {
        var run = RequireRun(runId);
        EnsureInterjectionEditable(run);

        var normalizedText = text.Trim();
        string? wingmanText = null;

        if (useWingman)
        {
            try
            {
                var wingmanRole = ResolveRole(_roleConfigService.Load().Roles, DefaultWingmanRoleId, DefaultWingmanRoleId);
                wingmanText = await ExecuteWingmanAssistAsync(run, wingmanRole, normalizedText);
                if (!string.IsNullOrWhiteSpace(wingmanText))
                {
                    AddDecision(run, "interjection-wingman-ready", $"插话助攻稿已生成：{TrimForDecision(wingmanText)}");
                }
                else
                {
                    AddDecision(run, "interjection-wingman-empty", "已尝试生成插话助攻稿，但未得到可用内容；本次仅保留用户原话。");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate wingman interjection advice for run {RunId}.", runId);
                AddDecision(run, "interjection-wingman-failed", $"插话助攻稿生成失败，已仅保留用户原话：{TrimForDecision(ex.Message)}");
            }
        }

	    run.PendingInterjectionText = normalizedText;
        run.PendingInterjectionUpdatedAt = DateTime.UtcNow;
	    run.PendingInterjectionUseWingman = !string.IsNullOrWhiteSpace(wingmanText);
	    run.PendingInterjectionWingmanText = string.IsNullOrWhiteSpace(wingmanText) ? null : wingmanText.Trim();
	    run.PendingInterjectionWingmanUpdatedAt = run.PendingInterjectionWingmanText is null ? null : DateTime.UtcNow;
        LogRunEvent(run, "插话", RunCsvLogService.BuildDetails(
            ("事件", "插话已排队"),
            ("用户原话", normalizedText),
            ("使用助攻手", run.PendingInterjectionUseWingman),
            ("助攻稿", run.PendingInterjectionWingmanText)));
	    AddDecision(run, "interjection-queued", run.PendingInterjectionUseWingman
	        ? $"插话已排队，并附带助攻手增强稿，等待下一次主线阶段吸收：{TrimForDecision(normalizedText)}"
	        : $"插话已排队，等待下一次主线阶段吸收：{TrimForDecision(normalizedText)}");
        await BroadcastRunUpdatedAsync(run);
        return run;
    }

    public async Task<V3PairRun> ClearInterjectionAsync(string runId)
    {
        var run = RequireRun(runId);
        EnsureInterjectionEditable(run);

        if (!string.IsNullOrWhiteSpace(run.PendingInterjectionText))
        {
            AddDecision(run, "interjection-cleared", $"插话已移除：{TrimForDecision(run.PendingInterjectionText)}");
        }

        run.PendingInterjectionText = null;
        run.PendingInterjectionUpdatedAt = null;
	    run.PendingInterjectionUseWingman = false;
	    run.PendingInterjectionWingmanText = null;
	    run.PendingInterjectionWingmanUpdatedAt = null;
	    LogRunEvent(run, "插话", RunCsvLogService.BuildDetails(("事件", "插话已清除")));
        await BroadcastRunUpdatedAsync(run);
        return run;
    }

    public async Task<V3PairRun> ContinueRunAsync(string runId, string? instruction, int additionalRounds)
    {
        var run = RequireRun(runId);
        EnsureContinuationAllowed(run);

        if (additionalRounds <= 0)
        {
            throw new InvalidOperationException("继续推进增加轮次必须是大于 0 的整数。");
        }

        var normalizedInstruction = string.IsNullOrWhiteSpace(instruction) ? null : instruction.Trim();
        var previousMaxRounds = run.MaxRounds;
        run.RecoveredFromStorage = false;
        run.LastContinueInstruction = normalizedInstruction;
        run.LastContinueRoundIncrement = additionalRounds;
        run.Status = "planning";
        run.MainThreadStatus = "idle";
        run.SubThreadStatus = "idle";
        run.MaxRounds = Math.Max(run.CurrentRound + additionalRounds, run.MaxRounds + additionalRounds);

        AddDecision(run, "continue-requested", string.IsNullOrWhiteSpace(normalizedInstruction)
            ? $"用户请求基于现有事实继续推进，并新增 {additionalRounds} 轮上限（{previousMaxRounds} → {run.MaxRounds}）。"
            : $"用户请求继续推进，并新增 {additionalRounds} 轮上限（{previousMaxRounds} → {run.MaxRounds}）：{TrimForDecision(normalizedInstruction)}");
        LogRunEvent(run, "调度", RunCsvLogService.BuildDetails(
            ("事件", "继续推进已触发"),
            ("新增轮次", additionalRounds),
            ("轮次上限", $"{previousMaxRounds}->{run.MaxRounds}"),
            ("说明", normalizedInstruction)));

        await BroadcastRunUpdatedAsync(run);
        _ = Task.Run(() => RunContinuationLoopAsync(run.RunId, normalizedInstruction));
        return run;
    }

    public async Task<V3PairRun> ApproveInitialPlanAsync(string runId)
    {
        var run = RequireRun(runId);
        EnsureInitialPlanApprovalPending(run);

        run.AwaitingInitialApproval = false;
        run.InitialPlanStatus = "approved";
        run.InitialPlanApprovedAt = DateTime.UtcNow;
        run.Status = "planning";
        run.MainThreadStatus = "idle";
        run.SubThreadStatus = "idle";

        CreateNextRoundFromInitialPlan(run);
        AddDecision(run, "initial-plan-approved", $"首轮方案 v{Math.Max(1, run.InitialPlanVersion)} 已确认，开始进入子线执行。" );
        LogRunEvent(run, "调度", RunCsvLogService.BuildDetails(
            ("事件", "首轮方案已批准"),
            ("版本", $"v{Math.Max(1, run.InitialPlanVersion)}")));

        await BroadcastRunUpdatedAsync(run);
        _ = Task.Run(() => RunApprovedRoundsLoopAsync(run.RunId));
        return run;
    }

    public async Task<V3PairRun> RejectInitialPlanAsync(string runId, string comment)
    {
        var run = RequireRun(runId);
        EnsureInitialPlanApprovalPending(run);

        var normalizedComment = comment.Trim();
        run.AwaitingInitialApproval = false;
        run.InitialPlanStatus = "rejected";
        run.InitialPlanRejectedCount++;
        run.LastInitialPlanReviewComment = normalizedComment;
        run.Status = "planning";
        run.MainThreadStatus = "running";
        run.SubThreadStatus = "idle";

        AddDecision(run, "initial-plan-rejected", $"首轮方案 v{Math.Max(1, run.InitialPlanVersion)} 被打回：{TrimForDecision(normalizedComment)}");
        LogRunEvent(run, "调度", RunCsvLogService.BuildDetails(
            ("事件", "首轮方案已打回"),
            ("版本", $"v{Math.Max(1, run.InitialPlanVersion)}"),
            ("意见", normalizedComment)));
        await BroadcastRunUpdatedAsync(run);

        _ = Task.Run(() => RunInitialPlanRewriteLoopAsync(run.RunId, normalizedComment));
        return run;
    }

    public async Task DeleteRunAsync(string runId)
    {
        var run = RequireRun(runId);

        if (!string.IsNullOrWhiteSpace(run.MainThreadSessionId))
        {
            _ptyService.StopSession(run.MainThreadSessionId);
        }

        if (!string.IsNullOrWhiteSpace(run.SubThreadSessionId))
        {
            _ptyService.StopSession(run.SubThreadSessionId);
        }

        DeleteRunArtifacts(run);
        _runStore.Delete(runId);
        _runs.TryRemove(runId, out _);
        await _hubContext.Clients.All.SendAsync("V3RunDeleted", runId);
    }

    private async Task RunMainLoopAsync(string runId)
    {
        var run = RequireRun(runId);
        try
        {
            run.Status = "planning";
            run.MainThreadStatus = "running";
            run.SubThreadStatus = "idle";
            await BroadcastRunUpdatedAsync(run);

            var kickoffRole = ResolveRole(_roleConfigService.Load().Roles, run.MainRoleId, DefaultMainRoleId);
            var kickoff = await ExecuteMainlineKickoffAsync(run, kickoffRole);

            if (string.Equals(kickoff.Status, "COMPLETE", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(kickoff.TaskCard))
            {
                run.Status = "completed";
                run.MainThreadStatus = "completed";
                run.LatestMainReviewSummary = kickoff.Summary;
                run.LatestVerdict = "ACCEPT";
                run.LatestGoalStatus = GoalStatusComplete;
                run.GoalCompleted = true;
                AddDecision(run, "run-completed", string.IsNullOrWhiteSpace(kickoff.Summary) ? "主线起案后判断任务已可收口。" : kickoff.Summary);
                await BroadcastRunUpdatedAsync(run);
                return;
            }

            StageInitialPlanForApproval(run, kickoff);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "V3 run {RunId} failed.", runId);
            run.Status = "failed";
            run.MainThreadStatus = "failed";
            run.SubThreadStatus = "failed";
            AddDecision(run, "run-failed", ex.Message);
            LogRunEvent(run, "异常", RunCsvLogService.BuildDetails(("事件", "运行失败"), ("错误", ex.Message)));
        }
        finally
        {
            run.RecoveredFromStorage = false;
            run.UpdatedAt = DateTime.UtcNow;
            await BroadcastRunUpdatedAsync(run);
        }
    }

    private async Task RunInitialPlanRewriteLoopAsync(string runId, string comment)
    {
        var run = RequireRun(runId);
        try
        {
            var kickoffRole = ResolveRole(_roleConfigService.Load().Roles, run.MainRoleId, DefaultMainRoleId);
            var rewrite = await ExecuteMainlineKickoffAsync(run, kickoffRole, comment);

            if (string.Equals(rewrite.Status, "COMPLETE", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(rewrite.TaskCard))
            {
                run.Status = "completed";
                run.MainThreadStatus = "completed";
                run.LatestMainReviewSummary = rewrite.Summary;
                run.LatestVerdict = "ACCEPT";
                run.LatestGoalStatus = GoalStatusComplete;
                run.GoalCompleted = true;
                AddDecision(run, "run-completed", string.IsNullOrWhiteSpace(rewrite.Summary) ? "主线重写起案后判断任务已可收口。" : rewrite.Summary);
                await BroadcastRunUpdatedAsync(run);
                return;
            }

            StageInitialPlanForApproval(run, rewrite);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "V3 initial plan rewrite for run {RunId} failed.", runId);
            run.Status = "failed";
            run.MainThreadStatus = "failed";
            run.SubThreadStatus = "failed";
            AddDecision(run, "initial-plan-rewrite-failed", ex.Message);
            LogRunEvent(run, "异常", RunCsvLogService.BuildDetails(("事件", "首轮方案重写失败"), ("错误", ex.Message)));
            await BroadcastRunUpdatedAsync(run);
        }
        finally
        {
            run.RecoveredFromStorage = false;
            run.UpdatedAt = DateTime.UtcNow;
            await BroadcastRunUpdatedAsync(run);
        }
    }

    private async Task RunApprovedRoundsLoopAsync(string runId)
    {
        var run = RequireRun(runId);
        try
        {
            await ExecuteQueuedRoundsAsync(run);

            if (run.Status is not ("completed" or "failed" or "stopped") && !run.AwaitingInitialApproval)
            {
                run.Status = "completed";
                run.MainThreadStatus = "completed";
                AddDecision(run, "run-completed", $"Max rounds ({run.MaxRounds}) reached. 主线收口结束。");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "V3 approved execution loop for run {RunId} failed.", runId);
            run.Status = "failed";
            run.MainThreadStatus = "failed";
            run.SubThreadStatus = "failed";
            AddDecision(run, "run-failed", ex.Message);
            LogRunEvent(run, "异常", RunCsvLogService.BuildDetails(("事件", "批准后执行失败"), ("错误", ex.Message)));
        }
        finally
        {
            run.RecoveredFromStorage = false;
            run.UpdatedAt = DateTime.UtcNow;
            await BroadcastRunUpdatedAsync(run);
        }
    }

    private async Task RunContinuationLoopAsync(string runId, string? instruction)
    {
        var run = RequireRun(runId);
        try
        {
            run.Status = "planning";
            run.MainThreadStatus = "running";
            run.SubThreadStatus = "idle";
            await BroadcastRunUpdatedAsync(run);

            var mainRole = ResolveRole(_roleConfigService.Load().Roles, run.MainRoleId, DefaultMainRoleId);
            var continuation = await ExecuteMainlineContinuationAsync(run, mainRole, instruction);

            if (string.Equals(continuation.Status, "COMPLETE", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(continuation.TaskCard))
            {
                run.Status = "completed";
                run.MainThreadStatus = "completed";
                run.LatestMainReviewSummary = continuation.Summary;
                run.LatestVerdict = "ACCEPT";
                run.LatestGoalStatus = GoalStatusComplete;
                run.GoalCompleted = true;
                AddDecision(run, "continue-completed", string.IsNullOrWhiteSpace(continuation.Summary) ? "继续推进后主线判断当前事实已可收口。" : continuation.Summary);
                await BroadcastRunUpdatedAsync(run);
                return;
            }

            CreateNextRoundFromPlan(run, continuation, "continue-push");
            await ExecuteQueuedRoundsAsync(run);

            if (run.Status is not ("completed" or "failed" or "stopped"))
            {
                run.Status = "completed";
                run.MainThreadStatus = "completed";
                AddDecision(run, "run-completed", $"Max rounds ({run.MaxRounds}) reached. 主线收口结束。");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "V3 continue-push for run {RunId} failed.", runId);
            run.Status = "failed";
            run.MainThreadStatus = "failed";
            run.SubThreadStatus = "failed";
            AddDecision(run, "continue-failed", ex.Message);
            LogRunEvent(run, "异常", RunCsvLogService.BuildDetails(("事件", "继续推进失败"), ("错误", ex.Message)));
        }
        finally
        {
            run.RecoveredFromStorage = false;
            run.UpdatedAt = DateTime.UtcNow;
            await BroadcastRunUpdatedAsync(run);
        }
    }

    private async Task ExecuteQueuedRoundsAsync(V3PairRun run)
    {
        while (run.CurrentRound <= run.MaxRounds && run.Status is not ("completed" or "failed" or "stopped"))
        {
            var round = run.Rounds.LastOrDefault();
            if (round is null)
            {
                run.Status = "failed";
                AddDecision(run, "run-failed", "未能生成任务卡。");
                break;
            }

            var subRole = ResolveRole(_roleConfigService.Load().Roles, run.SubRoleId, DefaultSubRoleId);
            run.Status = "running";
            run.MainThreadStatus = "idle";
            run.SubThreadStatus = "running";
            round.Status = "running";
            await BroadcastRunUpdatedAsync(run);

            var subline = await ExecuteSublineAsync(run, round, subRole);
            round.SublineStatus = subline.Status;
            round.SublineSummary = subline.Summary;
            round.SublineFacts = subline.Facts;
            round.SublineAdjustments = subline.Adjustments;
            round.SublineQuestions = subline.Questions;
            round.SublineNext = subline.Next;
            round.SublineOutputPath = subline.OutputPath;
            run.LatestSublineSummary = subline.Summary;

            run.Status = "reviewing";
            run.MainThreadStatus = "running";
            run.SubThreadStatus = subline.ExitCode == 0 ? "completed" : "failed";
            round.Status = "reviewing";
            await BroadcastRunUpdatedAsync(run);

            var mainRole = ResolveRole(_roleConfigService.Load().Roles, run.MainRoleId, DefaultMainRoleId);
            var review = await ExecuteMainlineReviewAsync(run, round, mainRole);
            var effectiveNextTaskCard = string.IsNullOrWhiteSpace(review.NextTaskCard)
                ? BuildFallbackNextTaskCard(review)
                : review.NextTaskCard;
            round.ReviewVerdict = review.Verdict;
            round.ContinueRequested = review.ContinueRequested;
            round.GoalStatus = review.GoalStatus;
            round.GoalCompleted = review.GoalCompleted;
            round.ReviewSummary = review.Summary;
            round.ChangeDecision = review.ChangeDecision;
            round.ReviewDirective = review.GoalCompleted ? (review.NextTaskCard ?? review.Directive) : effectiveNextTaskCard;
            round.ReviewOutputPath = review.OutputPath;
            round.CompletedAt = DateTime.UtcNow;
            round.Status = review.GoalCompleted ? "completed" : "handoff";
            run.LatestMainReviewSummary = review.Summary;
            run.LatestMainDirective = review.GoalCompleted ? (review.NextTaskCard ?? review.Directive) : effectiveNextTaskCard;
            run.LatestVerdict = review.Verdict;
            run.LatestGoalStatus = review.GoalStatus;
            run.GoalCompleted = review.GoalCompleted;
            run.LatestChangeDecision = review.ChangeDecision;
            run.MainThreadStatus = "completed";
            run.SubThreadStatus = subline.ExitCode == 0 ? "completed" : "failed";
            run.LatestReviewFocus = review.NextReviewFocus;

            AddDecision(run, "main-review", $"Round {round.RoundNumber}: {review.Verdict} — {review.Summary}");

            if (review.GoalCompleted)
            {
                run.Status = "completed";
                AddDecision(run, "run-completed", string.IsNullOrWhiteSpace(review.Summary) ? "主线判断当前结果已经可以收口。" : review.Summary);
            }
            else
            {
                if (run.CurrentRound >= run.MaxRounds)
                {
                    run.Status = "completed";
                    AddDecision(run, "run-completed", $"已达到最大轮次 {run.MaxRounds}，主线在本轮复核后收口。");
                }
                else
                {
                    CreateNextRoundFromReview(run, review);
                    run.Status = "planning";
                }
            }

            await BroadcastRunUpdatedAsync(run);
        }
    }

    private async Task<MainlinePlanResult> ExecuteMainlineKickoffAsync(V3PairRun run, AgentRoleDefinition role, string? rejectionComment = null)
    {
        var pendingInterjection = ConsumePendingInterjection(run, "kickoff", 0);
        var prompt = BuildMainlineKickoffPrompt(run, role, pendingInterjection, rejectionComment);
        var execution = await ExecuteRolePhaseAsync(run, role, prompt, "V3 Kickoff", true, "kickoff");
        var result = ParseMainlinePlan(execution.OutputText);
        result.OutputPath = execution.OutputPath;
        LogRunEvent(run, "输出解析", RunCsvLogService.BuildDetails(
            ("阶段", "kickoff"),
            ("状态", result.Status),
            ("摘要", result.Summary),
            ("输出文件", execution.OutputPath)));
        return result;
    }

    private async Task<MainlinePlanResult> ExecuteMainlineContinuationAsync(V3PairRun run, AgentRoleDefinition role, string? instruction)
    {
        var pendingInterjection = ConsumePendingInterjection(run, "continue-kickoff", run.CurrentRound);
        var prompt = BuildMainlineContinuationPrompt(run, role, instruction, pendingInterjection);
        var execution = await ExecuteRolePhaseAsync(run, role, prompt, "V3 Continue Push", true, "continue-kickoff");
        var result = ParseMainlinePlan(execution.OutputText);
        result.OutputPath = execution.OutputPath;
        LogRunEvent(run, "输出解析", RunCsvLogService.BuildDetails(
            ("阶段", "continue-kickoff"),
            ("状态", result.Status),
            ("摘要", result.Summary),
            ("输出文件", execution.OutputPath)));
        return result;
    }

    private async Task<SublineResult> ExecuteSublineAsync(V3PairRun run, V3PairRoundRecord round, AgentRoleDefinition role)
    {
        var prompt = BuildSublinePrompt(run, round, role);
        var execution = await ExecuteRolePhaseAsync(run, role, prompt, $"V3 R{round.RoundNumber} Subline", false, "subline");
        var result = ParseSublineResult(execution.OutputText);
        result.OutputPath = execution.OutputPath;
        result.ExitCode = execution.ExitCode;
        LogRunEvent(run, "输出解析", RunCsvLogService.BuildDetails(
            ("阶段", $"subline-r{round.RoundNumber}"),
            ("状态", result.Status),
            ("摘要", result.Summary),
            ("退出码", execution.ExitCode),
            ("输出文件", execution.OutputPath)));
        return result;
    }

    private async Task<MainlineReviewResult> ExecuteMainlineReviewAsync(V3PairRun run, V3PairRoundRecord round, AgentRoleDefinition role)
    {
        var pendingInterjection = ConsumePendingInterjection(run, $"review-round-{round.RoundNumber}", round.RoundNumber);
        var prompt = BuildMainlineReviewPrompt(run, round, role, pendingInterjection);
        var execution = await ExecuteRolePhaseAsync(run, role, prompt, $"V3 R{round.RoundNumber} Mainline Review", true, "review");
        var result = ParseMainlineReview(execution.OutputText);
        result.OutputPath = execution.OutputPath;
        LogRunEvent(run, "输出解析", RunCsvLogService.BuildDetails(
            ("阶段", $"review-r{round.RoundNumber}"),
            ("Verdict", result.Verdict),
            ("目标状态", result.GoalStatus),
            ("继续推进", result.ContinueRequested),
            ("摘要", result.Summary),
            ("输出文件", execution.OutputPath)));
        return result;
    }

    private async Task<string?> ExecuteWingmanAssistAsync(V3PairRun run, AgentRoleDefinition role, string rawText)
    {
        var prompt = BuildWingmanAssistPrompt(run, role, rawText);
        var execution = await ExecuteSilentRolePhaseAsync(run, role, prompt, "V3 Interjection Wingman", "interjection-wingman", "wingman");
        if (execution.ExitCode != 0 && string.IsNullOrWhiteSpace(execution.OutputText))
        {
            throw new InvalidOperationException($"助攻手执行失败，退出码 {execution.ExitCode}。");
        }

        var amplifiedAdvice = ParseWingmanAssistOutput(execution.OutputText);
        return string.IsNullOrWhiteSpace(amplifiedAdvice) ? null : amplifiedAdvice;
    }

    private async Task<V3ExecutionResult> ExecuteRolePhaseAsync(V3PairRun run, AgentRoleDefinition role, string prompt, string label, bool isMainline, string phaseKey)
    {
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();
        var promptPath = CreatePromptFile(run, phaseKey, isMainline ? "mainline" : "subline", prompt);
        var outputPath = CreateOutputFile(run, phaseKey, isMainline ? "mainline" : "subline");
        var model = string.IsNullOrWhiteSpace(role.Model) ? DefaultExecutionModel : role.Model.Trim();
        var copilotArguments = BuildCopilotArguments(run, role, workspaceRoot, model);
	    var launchScript = CreateExecutionLaunchScript(
            workspaceRoot,
            promptPath,
            outputPath,
            "copilot",
            copilotArguments,
            run.RunId,
            $"{(isMainline ? "main" : "sub")}-{phaseKey}");
        LogRunEvent(run, "提示词执行", RunCsvLogService.BuildDetails(
            ("事件", "提示词已落盘并准备执行"),
            ("阶段", DescribePhase(phaseKey, isMainline ? "mainline" : "subline")),
            ("角色", role.Name),
            ("模型", model),
            ("Prompt文件", promptPath),
            ("输出文件", outputPath),
            ("启动脚本", launchScript.ScriptPath),
            ("命令预览", launchScript.CommandPreview)));

        if (isMainline)
        {
            run.MainThreadStatus = "running";
	        run.MainThreadCommandPreview = launchScript.CommandPreview;
            await _hubContext.Clients.All.SendAsync("V3MainThreadActivity", run.RunId, label, "running");
        }
        else
        {
            run.SubThreadStatus = "running";
	        run.SubThreadCommandPreview = launchScript.CommandPreview;
            await _hubContext.Clients.All.SendAsync("V3SublineActivity", run.RunId, label, "running");
        }

        await BroadcastRunUpdatedAsync(run);

        var sessionId = _ptyService.StartRawSession(launchScript.CommandLine, workspaceRoot, 120, 30);
        await _ptyService.SendInputAsync(sessionId, launchScript.InputScript);
        var transcriptPath = _ptyService.GetTranscriptPath(sessionId);
        LogRunEvent(run, "提示词执行", RunCsvLogService.BuildDetails(
            ("事件", "终端执行已启动"),
            ("阶段", DescribePhase(phaseKey, isMainline ? "mainline" : "subline")),
            ("会话", sessionId),
            ("终端日志", transcriptPath),
            ("启动脚本", launchScript.ScriptPath)));

        if (isMainline)
        {
            run.MainThreadSessionId = sessionId;
	        await _hubContext.Clients.All.SendAsync("V3MainThreadPtyStarted", run.RunId, sessionId, label, launchScript.CommandPreview);
        }
        else
        {
            run.SubThreadSessionId = sessionId;
	        await _hubContext.Clients.All.SendAsync("V3SublinePtyStarted", run.RunId, sessionId, label, launchScript.CommandPreview);
        }

        await BroadcastRunUpdatedAsync(run);

        var exitCode = await WaitForPtySessionAsync(sessionId, TimeSpan.FromMinutes(20));
        var outputText = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath) : string.Empty;
        LogRunEvent(run, exitCode == -1 ? "异常" : "提示词返回", RunCsvLogService.BuildDetails(
            ("事件", exitCode == -1 ? "终端执行超时或被取消" : "终端执行已结束"),
            ("阶段", DescribePhase(phaseKey, isMainline ? "mainline" : "subline")),
            ("会话", sessionId),
            ("退出码", exitCode),
            ("输出文件", outputPath),
            ("终端日志", transcriptPath)));

        if (isMainline)
        {
            run.MainThreadStatus = exitCode == 0 ? "completed" : "failed";
            await _hubContext.Clients.All.SendAsync("V3MainThreadActivity", run.RunId, label, exitCode == 0 ? "completed" : "failed");
        }
        else
        {
            run.SubThreadStatus = exitCode == 0 ? "completed" : "failed";
            await _hubContext.Clients.All.SendAsync("V3SublineActivity", run.RunId, label, exitCode == 0 ? "completed" : "failed");
        }

        await BroadcastRunUpdatedAsync(run);
	    return new V3ExecutionResult(sessionId, exitCode, promptPath, outputPath, outputText, launchScript.CommandPreview);
    }

    private async Task<V3ExecutionResult> ExecuteSilentRolePhaseAsync(V3PairRun run, AgentRoleDefinition role, string prompt, string label, string phaseKey, string lane)
    {
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();
        var promptPath = CreatePromptFile(run, phaseKey, lane, prompt);
        var outputPath = CreateOutputFile(run, phaseKey, lane);
        var model = string.IsNullOrWhiteSpace(role.Model) ? DefaultExecutionModel : role.Model.Trim();
        var copilotArguments = BuildCopilotArguments(run, role, workspaceRoot, model);
        var launchScript = CreateExecutionLaunchScript(
            workspaceRoot,
            promptPath,
            outputPath,
            "copilot",
            copilotArguments,
            run.RunId,
            $"silent-{SanitizeLabel(label)}");
        LogRunEvent(run, "提示词执行", RunCsvLogService.BuildDetails(
            ("事件", "静默提示词已落盘并准备执行"),
            ("阶段", DescribePhase(phaseKey, lane)),
            ("角色", role.Name),
            ("模型", model),
            ("Prompt文件", promptPath),
            ("输出文件", outputPath),
            ("启动脚本", launchScript.ScriptPath),
            ("命令预览", launchScript.CommandPreview)));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = workspaceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(launchScript.ScriptPath);

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动静默角色执行：{label}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore best-effort cleanup failures.
            }

            throw new InvalidOperationException($"静默角色执行超时：{label}");
        }

        var outputText = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath) : string.Empty;
        LogRunEvent(run, "提示词返回", RunCsvLogService.BuildDetails(
            ("事件", "静默执行已结束"),
            ("阶段", DescribePhase(phaseKey, lane)),
            ("退出码", process.ExitCode),
            ("输出文件", outputPath),
            ("启动脚本", launchScript.ScriptPath)));
        return new V3ExecutionResult(string.Empty, process.ExitCode, promptPath, outputPath, outputText, launchScript.CommandPreview);
    }

    private void OnPtySessionCompleted(string sessionId, int exitCode)
    {
        if (_sessionCompletions.TryRemove(sessionId, out var tcs))
        {
            tcs.TrySetResult(exitCode);
        }
        else
        {
            _completedSessionExitCodes[sessionId] = exitCode;
        }
    }

    private async Task<int> WaitForPtySessionAsync(string sessionId, TimeSpan timeout)
    {
        if (_completedSessionExitCodes.TryRemove(sessionId, out var completed))
        {
            return completed;
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionCompletions[sessionId] = tcs;
        using var cts = new CancellationTokenSource(timeout);
        await using var _ = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));

        try
        {
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _ptyService.StopSession(sessionId);
            return -1;
        }
    }

    private static AgentRoleDefinition ResolveRole(IReadOnlyCollection<AgentRoleDefinition> roles, string? requestedRoleId, string fallbackRoleId)
    {
        var roleId = string.IsNullOrWhiteSpace(requestedRoleId) ? fallbackRoleId : requestedRoleId.Trim();
        return roles.FirstOrDefault(item => string.Equals(item.RoleId, roleId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Role '{roleId}' was not found.");
    }

    private static string ResolveWorkspace(SupervisorSettings settings, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = Path.GetFullPath(configuredPath.Trim());
            if (Directory.Exists(path))
            {
                return path;
            }

            throw new InvalidOperationException($"Workspace '{path}' does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultWorkspaceRoot))
        {
            var path = Path.GetFullPath(settings.DefaultWorkspaceRoot);
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return AgentRoleConfigService.GetBaseDir();
    }

    private static string CreateTitle(string goal)
    {
        var trimmed = string.IsNullOrWhiteSpace(goal) ? $"V3 Run {DateTime.Now:HHmmss}" : goal.Trim();
        return trimmed.Length <= 48 ? trimmed : trimmed[..48] + "…";
    }

    private static string DescribePhase(string phaseKey, string lane)
        => $"{lane}:{phaseKey}";

    private static void LogRunEvent(V3PairRun run, string type, string content)
    {
        RunCsvLogService.Append(run.WorkspaceRoot, run.RunId, type, content);
    }

    private static List<string> BuildCopilotArguments(V3PairRun run, AgentRoleDefinition role, string workspaceRoot, string model)
    {
        var args = new List<string> { "-p", "$prompt", "--no-alt-screen", "--yolo", "--model", model, "--add-dir", workspaceRoot };

        foreach (var directory in run.AdditionalAllowedDirectories.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--add-dir");
            args.Add(directory);
        }

        foreach (var tool in (role.DeniedTools ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--deny-tool");
            args.Add(tool);
        }

        return args;
    }

    private static string BuildMainlineKickoffPrompt(V3PairRun run, AgentRoleDefinition role, PendingInterjectionPayload? pendingInterjection, string? rejectionComment)
    {
        var template = ExpandRoleTemplate(role, run, new Dictionary<string, string?>
        {
            ["taskCard"] = null,
            ["reviewDirective"] = null,
            ["partnerName"] = run.SubRoleName,
            ["partnerSummary"] = null,
            ["roundNumber"] = "0",
            ["reviewFocus"] = null,
            ["stagePlanSummary"] = run.StagePlanSummary,
            ["currentStage"] = run.CurrentStageLabel,
            ["currentStageGoal"] = run.CurrentStageGoal,
            ["architectureGuardrails"] = run.ArchitectureGuardrails,
            ["changeDecision"] = run.LatestChangeDecision
        });

        var sb = new StringBuilder();
        sb.AppendLine(template);
        sb.AppendLine();
        sb.AppendLine("你现在处于 AI助手V3 的启动起案阶段。先判断目标是否已可收口；若不能，必须先给出完整整体计划，再给出当前阶段、架构红线与首轮任务卡。你像项目经理 / 产品负责人一样工作：主职责是定义用户、目标、范围、阶段、验收、边界与非目标，而不是过早替子线程做细技术设计。你不能亲自修改代码。\n");
        AppendRequiredContextReads(sb,
            $"{V3ContextRoot}/README.md",
            $"{V3ContextRoot}/routing.md",
            $"{V3ContextRoot}/roles/mainline.md",
            $"{V3ContextRoot}/phases/kickoff.md",
            $"{V3ContextRoot}/outputs/kickoff-plan.md",
            $"{V3ContextRoot}/constraints/hard-rules.md");
        if (!string.IsNullOrWhiteSpace(rejectionComment))
        {
            sb.AppendLine("上一版首轮方案没有通过人工确认。你现在不是进入执行，而是根据驳回意见重写首轮方案。请优先纠正目标理解、产品边界、阶段拆分和首轮任务卡，不要和用户抬杠，也不要偷换成另一种缩小版目标。\n");
            if (!string.IsNullOrWhiteSpace(run.InitialPlanSummary)
                || !string.IsNullOrWhiteSpace(run.InitialPlanTaskCard)
                || !string.IsNullOrWhiteSpace(run.StagePlanSummary))
            {
                sb.AppendLine("上一版首轮方案摘要：");
                sb.AppendLine("<<<LAST_INITIAL_PLAN");
                sb.AppendLine($"版本：v{Math.Max(1, run.InitialPlanVersion)}");
                if (!string.IsNullOrWhiteSpace(run.InitialPlanSummary))
                {
                    sb.AppendLine($"摘要：{run.InitialPlanSummary}");
                }
                if (!string.IsNullOrWhiteSpace(run.StagePlanSummary))
                {
                    sb.AppendLine("阶段总览：");
                    sb.AppendLine(run.StagePlanSummary);
                }
                if (!string.IsNullOrWhiteSpace(run.InitialPlanTaskCard))
                {
                    sb.AppendLine("首轮任务卡：");
                    sb.AppendLine(run.InitialPlanTaskCard);
                }
                sb.AppendLine("LAST_INITIAL_PLAN>>>");
                sb.AppendLine();
            }
            sb.AppendLine("用户打回意见：");
            sb.AppendLine("<<<REJECTION_COMMENT");
            sb.AppendLine(rejectionComment);
            sb.AppendLine("REJECTION_COMMENT>>>");
            sb.AppendLine();
        }
        if (pendingInterjection is not null)
        {
            sb.AppendLine("有一条用户插话在本次主线阶段生效。你必须区分【用户原话】和【助攻手增强稿】：用户原话权重更高，助攻手增强稿只是风险放大镜与补充提醒，不可把它误当成用户新增的硬性需求；你要判断如何稳妥吸收到任务卡或复核重点里，不要因此偏离总目标。\n");
            sb.AppendLine("用户原话：");
            sb.AppendLine("<<<USER_INTERJECTION_RAW");
            sb.AppendLine(pendingInterjection.RawText);
            sb.AppendLine("USER_INTERJECTION_RAW>>>");
            if (!string.IsNullOrWhiteSpace(pendingInterjection.WingmanText))
            {
                sb.AppendLine();
                sb.AppendLine("助攻手增强稿：");
                sb.AppendLine("<<<INTERJECTION_WINGMAN");
                sb.AppendLine(pendingInterjection.WingmanText);
                sb.AppendLine("INTERJECTION_WINGMAN>>>");
            }
            sb.AppendLine();
        }
        sb.AppendLine("请按 kickoff 输出协议返回 Markdown；字段顺序可保持一致，字段语义以文档为准：");
        sb.AppendLine("STATUS: PLAN|COMPLETE");
        sb.AppendLine("STAGE_PLAN:");
        sb.AppendLine("<<<STAGE_PLAN");
        sb.AppendLine("若 STATUS=PLAN，必须写完整整体计划，且阶段数必须在 1~4 之间，由你判断。不要只写一句摘要，也不要只写当前阶段。请逐阶段展开，建议每个阶段至少写清：阶段名称 / 阶段目标 / 阶段交付物 / 阶段完成判据。若 STATUS=COMPLETE 则写 无");
        sb.AppendLine("STAGE_PLAN>>>");
        sb.AppendLine("CURRENT_STAGE: 若 STATUS=PLAN，写当前阶段名；否则写 无");
        sb.AppendLine("STAGE_GOAL: 若 STATUS=PLAN，写当前阶段目标；否则写 无");
        sb.AppendLine("ARCH_GUARDRAILS:");
        sb.AppendLine("<<<ARCH_GUARDRAILS");
        sb.AppendLine("若 STATUS=PLAN，写后续尽量保持不变的产品/边界/验收/必要技术红线；除非存在明确硬约束，否则不要写死具体框架、库、分层或实现细节。否则写 无");
        sb.AppendLine("ARCH_GUARDRAILS>>>");
        sb.AppendLine("ROUND_GOAL: 一句话说明首轮目标");
        sb.AppendLine("TASK_CARD:");
        sb.AppendLine("<<<TASK_CARD");
        sb.AppendLine("给子线的首轮任务卡；重点写用户、能力、边界、验收和非目标，给子线程保留实现自主性。若 STATUS=COMPLETE 则写 无");
        sb.AppendLine("TASK_CARD>>>");
        sb.AppendLine("REVIEW_FOCUS:");
        sb.AppendLine("<<<REVIEW_FOCUS");
        sb.AppendLine("首轮结束时主线最该重点检查什么；若无写 无");
        sb.AppendLine("REVIEW_FOCUS>>>");
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("<<<SUMMARY");
        sb.AppendLine("你对当前起案判断的简短说明：为何可以直接结束，或者为何值得先跑首轮");
        sb.AppendLine("SUMMARY>>>");
        sb.AppendLine("若 STATUS=PLAN 但你没有给出完整整体计划、阶段数不在 1~4 之间、或当前阶段无法映射到整体计划中的某一阶段，则本次输出视为无效。不要为了省 token 把整体计划缩成一句话。子线程默认应保留实现与 UI/UX 落地自主性。");
        return sb.ToString();
    }

    private static string BuildMainlineContinuationPrompt(V3PairRun run, AgentRoleDefinition role, string? instruction, PendingInterjectionPayload? pendingInterjection)
    {
        var template = ExpandRoleTemplate(role, run, new Dictionary<string, string?>
        {
            ["taskCard"] = run.LatestTaskCard,
            ["reviewDirective"] = run.LatestMainDirective,
            ["partnerName"] = run.SubRoleName,
            ["partnerSummary"] = run.LatestSublineSummary,
            ["roundNumber"] = run.CurrentRound.ToString(),
            ["reviewFocus"] = run.LatestReviewFocus,
            ["stagePlanSummary"] = run.StagePlanSummary,
            ["currentStage"] = run.CurrentStageLabel,
            ["currentStageGoal"] = run.CurrentStageGoal,
            ["architectureGuardrails"] = run.ArchitectureGuardrails,
            ["changeDecision"] = run.LatestChangeDecision
        });

        var latestRound = run.Rounds.LastOrDefault();
        var sb = new StringBuilder();
        sb.AppendLine(template);
        sb.AppendLine();
        sb.AppendLine("你现在处于 AI助手V3 的继续推进阶段。该 run 已有历史事实，你需要基于现有轮次结果判断是否值得继续，以及下一轮任务卡如何收束。你像项目经理 / 产品负责人一样工作：优先维护完整整体计划、当前阶段、验收边界和非目标，不要轻易替子线程写死技术细节。你不能亲自修改代码。\n");
        AppendRequiredContextReads(sb,
            $"{V3ContextRoot}/README.md",
            $"{V3ContextRoot}/routing.md",
            $"{V3ContextRoot}/roles/mainline.md",
            $"{V3ContextRoot}/phases/continue.md",
            $"{V3ContextRoot}/outputs/continue-plan.md",
            $"{V3ContextRoot}/constraints/hard-rules.md");
        sb.AppendLine($"当前 run 状态：{run.Status}");
        sb.AppendLine($"已完成轮次：{run.CurrentRound}/{run.MaxRounds}");
        sb.AppendLine($"最新 verdict：{run.LatestVerdict ?? latestRound?.ReviewVerdict ?? "无"}");
        sb.AppendLine($"最新目标状态：{run.LatestGoalStatus ?? (run.GoalCompleted ? GoalStatusComplete : GoalStatusIncomplete)}");
        sb.AppendLine($"当前阶段：{run.CurrentStageLabel ?? "无"}");
        sb.AppendLine($"当前阶段目标：{run.CurrentStageGoal ?? "无"}");
        sb.AppendLine();
        sb.AppendLine("现有阶段总览：");
        sb.AppendLine("<<<STAGE_PLAN");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.StagePlanSummary) ? "无" : run.StagePlanSummary);
        sb.AppendLine("STAGE_PLAN>>>");
        sb.AppendLine();
        sb.AppendLine("现有架构红线：");
        sb.AppendLine("<<<ARCH_GUARDRAILS");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.ArchitectureGuardrails) ? "无" : run.ArchitectureGuardrails);
        sb.AppendLine("ARCH_GUARDRAILS>>>");
        sb.AppendLine();
        sb.AppendLine("最近任务卡：");
        sb.AppendLine("<<<LATEST_TASK_CARD");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.LatestTaskCard) ? "无" : run.LatestTaskCard);
        sb.AppendLine("LATEST_TASK_CARD>>>");
        sb.AppendLine();
        sb.AppendLine("最近主线判断：");
        sb.AppendLine("<<<LATEST_REVIEW");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.LatestMainReviewSummary) ? latestRound?.ReviewSummary ?? "无" : run.LatestMainReviewSummary);
        sb.AppendLine("LATEST_REVIEW>>>");
        sb.AppendLine();
        sb.AppendLine("最近子线摘要：");
        sb.AppendLine("<<<LATEST_SUBLINE");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.LatestSublineSummary) ? latestRound?.SublineSummary ?? "无" : run.LatestSublineSummary);
        sb.AppendLine("LATEST_SUBLINE>>>");
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            sb.AppendLine();
            sb.AppendLine("用户给出的继续推进说明：");
            sb.AppendLine("<<<CONTINUE_PUSH");
            sb.AppendLine(instruction);
            sb.AppendLine("CONTINUE_PUSH>>>");
        }
        if (pendingInterjection is not null)
        {
            sb.AppendLine();
            sb.AppendLine("同时有一条用户插话在这次主线继续推进阶段生效。你必须区分【用户原话】和【助攻手增强稿】：用户原话权重更高，助攻手增强稿只是风险放大镜与补充提醒，不是对总目标的直接改写。\n");
            sb.AppendLine("<<<USER_INTERJECTION_RAW");
            sb.AppendLine(pendingInterjection.RawText);
            sb.AppendLine("USER_INTERJECTION_RAW>>>");
            if (!string.IsNullOrWhiteSpace(pendingInterjection.WingmanText))
            {
                sb.AppendLine();
                sb.AppendLine("<<<INTERJECTION_WINGMAN");
                sb.AppendLine(pendingInterjection.WingmanText);
                sb.AppendLine("INTERJECTION_WINGMAN>>>");
            }
        }
        sb.AppendLine();
        sb.AppendLine("请按 continue 输出协议返回 Markdown；字段语义以文档为准：");
        sb.AppendLine("STATUS: PLAN|COMPLETE");
        sb.AppendLine("STAGE_PLAN:");
        sb.AppendLine("<<<STAGE_PLAN");
        sb.AppendLine("若需调整整体计划，则必须写完整整体计划，而不是只补一句摘要。阶段数保持在 1~4 之间；若沿用现有整体计划或 STATUS=COMPLETE 则写 无");
        sb.AppendLine("STAGE_PLAN>>>");
        sb.AppendLine("CURRENT_STAGE: 若 STATUS=PLAN，写下一轮所处阶段名；否则写 无");
        sb.AppendLine("STAGE_GOAL: 若 STATUS=PLAN，写下一轮所处阶段目标；否则写 无");
        sb.AppendLine("ARCH_GUARDRAILS:");
        sb.AppendLine("<<<ARCH_GUARDRAILS");
        sb.AppendLine("若需调整红线则重写；重点保持产品边界、验收与必要技术红线，避免代替子线程做细技术设计。若沿用现有红线或 STATUS=COMPLETE 则写 无");
        sb.AppendLine("ARCH_GUARDRAILS>>>");
        sb.AppendLine("ROUND_GOAL: 一句话说明下一轮继续推进目标");
        sb.AppendLine("TASK_CARD:");
        sb.AppendLine("<<<TASK_CARD");
        sb.AppendLine("给子线的下一轮任务卡；优先写需求、边界、验收、非目标与必要约束，保留实现自主性。若 STATUS=COMPLETE 则写 无。仅当终极目标已完成时才可写 COMPLETE。");
        sb.AppendLine("TASK_CARD>>>");
        sb.AppendLine("REVIEW_FOCUS:");
        sb.AppendLine("<<<REVIEW_FOCUS");
        sb.AppendLine("下一轮结束时主线最该重点检查什么；若无写 无");
        sb.AppendLine("REVIEW_FOCUS>>>");
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("<<<SUMMARY");
        sb.AppendLine("说明为什么值得继续推进，或为什么基于现有事实已经不需要继续");
        sb.AppendLine("SUMMARY>>>");
        sb.AppendLine("若 STATUS=PLAN 但你没有维护完整整体计划、没有明确当前阶段与整体计划的对应关系，或把任务卡写成过度具体的实现方案，本次输出视为不合格。\n");
        return sb.ToString();
    }

    private static string BuildSublinePrompt(V3PairRun run, V3PairRoundRecord round, AgentRoleDefinition role)
    {
        var template = ExpandRoleTemplate(role, run, new Dictionary<string, string?>
        {
            ["taskCard"] = round.TaskCard,
            ["reviewDirective"] = run.LatestMainDirective,
            ["partnerName"] = run.MainRoleName,
            ["partnerSummary"] = round.MainPlanSummary,
            ["roundNumber"] = round.RoundNumber.ToString(),
            ["reviewFocus"] = round.ReviewFocus,
            ["stagePlanSummary"] = run.StagePlanSummary,
            ["currentStage"] = round.StageLabel ?? run.CurrentStageLabel,
            ["currentStageGoal"] = round.StageGoal ?? run.CurrentStageGoal,
            ["architectureGuardrails"] = run.ArchitectureGuardrails,
            ["changeDecision"] = run.LatestChangeDecision
        });

        var sb = new StringBuilder();
        sb.AppendLine(template);
        sb.AppendLine();
        sb.AppendLine($"你现在处于 AI助手V3 的子线执行阶段（第 {round.RoundNumber} 轮）。");
        sb.AppendLine("你只围绕当前任务卡推进，不要重写总目标。主线意见权重更高；若发现事实冲突、代价异常高或风险过大，明确反馈给主线。\n");
        AppendRequiredContextReads(sb,
            $"{V3ContextRoot}/README.md",
            $"{V3ContextRoot}/routing.md",
            $"{V3ContextRoot}/roles/subline.md",
            $"{V3ContextRoot}/phases/subline.md",
            $"{V3ContextRoot}/outputs/subline-report.md",
            $"{V3ContextRoot}/constraints/hard-rules.md");
        sb.AppendLine("阶段总览：");
        sb.AppendLine("<<<STAGE_PLAN");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.StagePlanSummary) ? "无" : run.StagePlanSummary);
        sb.AppendLine("STAGE_PLAN>>>");
        sb.AppendLine();
        sb.AppendLine($"当前阶段：{round.StageLabel ?? run.CurrentStageLabel ?? "无"}");
        sb.AppendLine($"当前阶段目标：{round.StageGoal ?? run.CurrentStageGoal ?? "无"}");
        sb.AppendLine("架构红线：");
        sb.AppendLine("<<<ARCH_GUARDRAILS");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.ArchitectureGuardrails) ? "无" : run.ArchitectureGuardrails);
        sb.AppendLine("ARCH_GUARDRAILS>>>");
        if (!string.IsNullOrWhiteSpace(run.LatestChangeDecision))
        {
            sb.AppendLine();
            sb.AppendLine("主线最近一次保留/修正决定：");
            sb.AppendLine("<<<CHANGE_DECISION");
            sb.AppendLine(run.LatestChangeDecision);
            sb.AppendLine("CHANGE_DECISION>>>");
        }
        sb.AppendLine("当前任务卡：");
        sb.AppendLine("<<<TASK_CARD");
        sb.AppendLine(string.IsNullOrWhiteSpace(round.TaskCard) ? "无" : round.TaskCard);
        sb.AppendLine("TASK_CARD>>>");
        if (!string.IsNullOrWhiteSpace(round.ReviewFocus))
        {
            sb.AppendLine();
            sb.AppendLine("主线提醒本轮复核重点：");
            sb.AppendLine("<<<REVIEW_FOCUS");
            sb.AppendLine(round.ReviewFocus);
            sb.AppendLine("REVIEW_FOCUS>>>");
        }
        sb.AppendLine();
        sb.AppendLine("结束时请按 subline 输出协议返回：");
        sb.AppendLine("STATUS: DONE|PARTIAL|BLOCKED");
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("<<<SUMMARY");
        sb.AppendLine("本轮完成了什么，用结果导向的话说清楚");
        sb.AppendLine("SUMMARY>>>");
        sb.AppendLine("FACTS:");
        sb.AppendLine("<<<FACTS");
        sb.AppendLine("列出关键事实：改动文件、验证结果、证据、阻塞点。少说空话，多说可核查事实");
        sb.AppendLine("FACTS>>>");
        sb.AppendLine("ADJUSTMENTS:");
        sb.AppendLine("<<<ADJUSTMENTS");
        sb.AppendLine("列出你本轮主动做的细微调整、顺手补强或低成本修正；如果没有写 无。这里要区分事实与主张");
        sb.AppendLine("ADJUSTMENTS>>>");
        sb.AppendLine("QUESTIONS:");
        sb.AppendLine("<<<QUESTIONS");
        sb.AppendLine("如果你对主线任务卡有疑义、发现事实冲突、代价异常高、或需要主线改判，就写在这里；没有写 无");
        sb.AppendLine("QUESTIONS>>>");
        sb.AppendLine("NEXT:");
        sb.AppendLine("<<<NEXT");
        sb.AppendLine("如果主线继续推进，你建议下一步关注什么；没有就写 无");
        sb.AppendLine("NEXT>>>");
        return sb.ToString();
    }

    private static string BuildMainlineReviewPrompt(V3PairRun run, V3PairRoundRecord round, AgentRoleDefinition role, PendingInterjectionPayload? pendingInterjection)
    {
        var template = ExpandRoleTemplate(role, run, new Dictionary<string, string?>
        {
            ["taskCard"] = round.TaskCard,
            ["reviewDirective"] = run.LatestMainDirective,
            ["partnerName"] = run.SubRoleName,
            ["partnerSummary"] = round.SublineSummary,
            ["roundNumber"] = round.RoundNumber.ToString(),
            ["reviewFocus"] = round.ReviewFocus,
            ["stagePlanSummary"] = run.StagePlanSummary,
            ["currentStage"] = round.StageLabel ?? run.CurrentStageLabel,
            ["currentStageGoal"] = round.StageGoal ?? run.CurrentStageGoal,
            ["architectureGuardrails"] = run.ArchitectureGuardrails,
            ["changeDecision"] = run.LatestChangeDecision
        });

        var sb = new StringBuilder();
        sb.AppendLine(template);
        sb.AppendLine();
        sb.AppendLine($"你现在处于 AI助手V3 的主线复核阶段（第 {round.RoundNumber} 轮）。");
        sb.AppendLine("你必须认真检查子线汇报，但不能只信摘要；请结合代码、终端输出、验证结果和当前工作区事实做判断。你不能亲自改代码。复核目标是用尽量少的额外成本完成验真、验全、验值，并决定哪些修改保留、哪些顺手修正、以及是否进入下一轮。\n");
        AppendRequiredContextReads(sb,
            $"{V3ContextRoot}/README.md",
            $"{V3ContextRoot}/routing.md",
            $"{V3ContextRoot}/roles/reviewer.md",
            $"{V3ContextRoot}/phases/review.md",
            $"{V3ContextRoot}/outputs/review-report.md",
            $"{V3ContextRoot}/constraints/hard-rules.md");
        sb.AppendLine("当前阶段总览：");
        sb.AppendLine("<<<STAGE_PLAN");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.StagePlanSummary) ? "无" : run.StagePlanSummary);
        sb.AppendLine("STAGE_PLAN>>>");
        sb.AppendLine();
        sb.AppendLine($"当前阶段：{round.StageLabel ?? run.CurrentStageLabel ?? "无"}");
        sb.AppendLine($"当前阶段目标：{round.StageGoal ?? run.CurrentStageGoal ?? "无"}");
        sb.AppendLine("架构红线：");
        sb.AppendLine("<<<ARCH_GUARDRAILS");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.ArchitectureGuardrails) ? "无" : run.ArchitectureGuardrails);
        sb.AppendLine("ARCH_GUARDRAILS>>>");
        sb.AppendLine();
        sb.AppendLine("本轮任务卡：");
        sb.AppendLine("<<<TASK_CARD");
        sb.AppendLine(string.IsNullOrWhiteSpace(round.TaskCard) ? "无" : round.TaskCard);
        sb.AppendLine("TASK_CARD>>>");
        sb.AppendLine();
        sb.AppendLine("子线摘要：");
        sb.AppendLine("<<<SUMMARY");
        sb.AppendLine(string.IsNullOrWhiteSpace(round.SublineSummary) ? "无" : round.SublineSummary);
        sb.AppendLine("SUMMARY>>>");
        sb.AppendLine();
        sb.AppendLine("子线事实：");
        sb.AppendLine("<<<FACTS");
        sb.AppendLine(string.IsNullOrWhiteSpace(round.SublineFacts) ? "无" : round.SublineFacts);
        sb.AppendLine("FACTS>>>");
        sb.AppendLine();
        sb.AppendLine("子线主动调整：");
        sb.AppendLine("<<<ADJUSTMENTS");
        sb.AppendLine(string.IsNullOrWhiteSpace(round.SublineAdjustments) ? "无" : round.SublineAdjustments);
        sb.AppendLine("ADJUSTMENTS>>>");
        if (!string.IsNullOrWhiteSpace(round.ReviewFocus))
        {
            sb.AppendLine();
            sb.AppendLine("本轮主线应重点复核：");
            sb.AppendLine("<<<REVIEW_FOCUS");
            sb.AppendLine(round.ReviewFocus);
            sb.AppendLine("REVIEW_FOCUS>>>");
        }
        if (pendingInterjection is not null)
        {
            sb.AppendLine();
            sb.AppendLine("有一条用户插话在本次主线复核阶段生效。它不是直接命令子线改方向，而是要求你在保持总目标不偏的前提下，优先考虑是否把它吸收到下一轮任务卡或下一轮复核重点。你必须区分【用户原话】和【助攻手增强稿】：用户原话权重更高，助攻手增强稿只是帮助你补看风险。若不适合并入，也应在 SUMMARY 的判断中体现你为何没有采纳。\n");
            sb.AppendLine("用户原话：");
            sb.AppendLine("<<<USER_INTERJECTION_RAW");
            sb.AppendLine(pendingInterjection.RawText);
            sb.AppendLine("USER_INTERJECTION_RAW>>>");
            if (!string.IsNullOrWhiteSpace(pendingInterjection.WingmanText))
            {
                sb.AppendLine();
                sb.AppendLine("助攻手增强稿：");
                sb.AppendLine("<<<INTERJECTION_WINGMAN");
                sb.AppendLine(pendingInterjection.WingmanText);
                sb.AppendLine("INTERJECTION_WINGMAN>>>");
            }
        }
        sb.AppendLine();
        sb.AppendLine("复核要求（详细语义以文档为准）：");
        sb.AppendLine("1. 验真：子线声称做了的事，是否有代码/输出/验证证据支撑。");
        sb.AppendLine("2. 验全：任务卡里关键事项是否真的覆盖到，不要只看表面完成感。");
        sb.AppendLine("3. 验值：即使做了，这些改动是否真的解决了问题，还是只是做了表面动作。");
        sb.AppendLine("4. 判断子线本轮主动微调中，哪些应正式保留为后续基线，哪些应在下一轮顺手修正；若问题非阻断，优先把小修改意见和下一阶段推进合并到同一轮。");
        sb.AppendLine("注意区分“本阶段可接受”和“终极目标已完成”：VERDICT=ACCEPT 只表示这一阶段结果可接收，不等于整个 run 可以结束。只有终极目标真的完成时，才应写 GOAL_STATUS: COMPLETE 并允许 CONTINUE: NO；若只是阶段完成但终极目标未完成，必须写 GOAL_STATUS: INCOMPLETE，并给出下一轮任务卡。");
        sb.AppendLine();
        sb.AppendLine("请按 review 输出协议返回：");
        sb.AppendLine("VERDICT: ACCEPT|REVISE|REJECT");
        sb.AppendLine("GOAL_STATUS: COMPLETE|INCOMPLETE");
        sb.AppendLine("CONTINUE: YES|NO");
        sb.AppendLine("NEXT_STAGE: 如果继续，写下一轮所处阶段；否则写 无");
        sb.AppendLine("NEXT_STAGE_GOAL: 如果继续，写下一轮阶段目标；否则写 无");
        sb.AppendLine("NEXT_ROUND_GOAL: 如果继续，写下一轮目标；否则写 无");
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("<<<SUMMARY");
        sb.AppendLine("主线对当前结果的认真判断：哪些是真的完成了，哪些还缺，当前结果是否已经可接受");
        sb.AppendLine("SUMMARY>>>");
        sb.AppendLine("CHANGE_DECISION:");
        sb.AppendLine("<<<CHANGE_DECISION");
        sb.AppendLine("明确写哪些子线主动修改正式保留、哪些下一轮顺手修、哪些应冻结不再继续扩散；若无写 无");
        sb.AppendLine("CHANGE_DECISION>>>");
        sb.AppendLine("NEXT_TASK_CARD:");
        sb.AppendLine("<<<NEXT_TASK_CARD");
        sb.AppendLine("如果要继续，写给子线的下一轮任务卡；否则写 无。优先写成“阻断问题先修 / 非阻断问题并入下一阶段一起做”的可执行任务卡。要短、明确、能执行，不要写成长篇论文");
        sb.AppendLine("NEXT_TASK_CARD>>>");
        sb.AppendLine("NEXT_REVIEW_FOCUS:");
        sb.AppendLine("<<<NEXT_REVIEW_FOCUS");
        sb.AppendLine("如果要继续，写下一轮结束时最该重点检查什么；否则写 无");
        sb.AppendLine("NEXT_REVIEW_FOCUS>>>");
        return sb.ToString();
    }

    private static void AppendRequiredContextReads(StringBuilder sb, params string[] relativePaths)
    {
        if (relativePaths.Length == 0)
        {
            return;
        }

        sb.AppendLine("若当前工作区存在以下文档，先按顺序阅读；若个别文件缺失，再回退到当前 prompt 与代码事实继续：");
        foreach (var path in relativePaths)
        {
            sb.AppendLine($"- `{path}`");
        }

        sb.AppendLine();
    }

    private static string ExpandRoleTemplate(AgentRoleDefinition role, V3PairRun run, IReadOnlyDictionary<string, string?> variables)
    {
        var template = string.IsNullOrWhiteSpace(role.PromptTemplate)
            ? "项目目标：{{goal}}。你的角色：{{roleName}}。"
            : role.PromptTemplate;

        var replacements = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["goal"] = run.Goal,
            ["roleName"] = role.Name,
            ["roleDescription"] = role.Description,
            ["runTitle"] = run.Title,
            ["workspaceName"] = run.WorkspaceName,
            ["mainRoleName"] = run.MainRoleName,
            ["subRoleName"] = run.SubRoleName,
            ["stagePlanSummary"] = run.StagePlanSummary,
            ["currentStage"] = run.CurrentStageLabel,
            ["currentStageGoal"] = run.CurrentStageGoal,
            ["architectureGuardrails"] = run.ArchitectureGuardrails,
            ["changeDecision"] = run.LatestChangeDecision
        };

        foreach (var item in variables)
        {
            replacements[item.Key] = item.Value;
        }

        foreach (var item in replacements)
        {
            template = template.Replace($"{{{{{item.Key}}}}}", item.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return template;
    }

    private static string BuildWingmanAssistPrompt(V3PairRun run, AgentRoleDefinition role, string rawText)
    {
        var template = ExpandRoleTemplate(role, run, new Dictionary<string, string?>
        {
            ["taskCard"] = run.LatestTaskCard,
            ["reviewDirective"] = run.LatestMainDirective,
            ["partnerName"] = run.MainRoleName,
            ["partnerSummary"] = run.LatestMainReviewSummary,
            ["roundNumber"] = run.CurrentRound.ToString(),
            ["reviewFocus"] = run.LatestReviewFocus,
            ["stagePlanSummary"] = run.StagePlanSummary,
            ["currentStage"] = run.CurrentStageLabel,
            ["currentStageGoal"] = run.CurrentStageGoal,
            ["architectureGuardrails"] = run.ArchitectureGuardrails,
            ["changeDecision"] = run.LatestChangeDecision
        });

        var latestRound = run.Rounds.LastOrDefault();
        var sb = new StringBuilder();
        sb.AppendLine(template);
        sb.AppendLine();
        sb.AppendLine("你现在只做一件事：把用户这句插嘴中隐含的底线、担忧、风险与反例提醒强化出来，供主线参考。你不能改写项目总目标，也不能替用户凭空新增新需求；你只是把『这句话真正想防什么坑』讲清楚。允许尖锐，但必须专业、克制、基于事实，不要骂人，不要阴阳怪气，不要编造仓库里并不存在的事实。\n");
        sb.AppendLine($"当前 run 状态：{run.Status}");
        sb.AppendLine($"当前阶段：{run.CurrentStageLabel ?? latestRound?.StageLabel ?? "无"}");
        sb.AppendLine($"当前阶段目标：{run.CurrentStageGoal ?? latestRound?.StageGoal ?? "无"}");
        sb.AppendLine();
        sb.AppendLine("阶段总览：");
        sb.AppendLine("<<<STAGE_PLAN");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.StagePlanSummary) ? "无" : run.StagePlanSummary);
        sb.AppendLine("STAGE_PLAN>>>");
        sb.AppendLine();
        sb.AppendLine("最近主线判断：");
        sb.AppendLine("<<<LATEST_REVIEW");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.LatestMainReviewSummary) ? latestRound?.ReviewSummary ?? "无" : run.LatestMainReviewSummary);
        sb.AppendLine("LATEST_REVIEW>>>");
        sb.AppendLine();
        sb.AppendLine("最近任务卡：");
        sb.AppendLine("<<<LATEST_TASK_CARD");
        sb.AppendLine(string.IsNullOrWhiteSpace(run.LatestTaskCard) ? latestRound?.TaskCard ?? "无" : run.LatestTaskCard);
        sb.AppendLine("LATEST_TASK_CARD>>>");
        sb.AppendLine();
        sb.AppendLine("用户原始插嘴：");
        sb.AppendLine("<<<USER_INTERJECTION_RAW");
        sb.AppendLine(rawText);
        sb.AppendLine("USER_INTERJECTION_RAW>>>");
        sb.AppendLine();
        sb.AppendLine("请严格按下面模板输出：");
        sb.AppendLine("AMPLIFIED_ADVICE:");
        sb.AppendLine("<<<AMPLIFIED_ADVICE");
        sb.AppendLine("分成 2~4 个短段或短要点即可，重点写：1) 用户原话真正想守住的底线；2) 当前阶段最可能忽视的风险或错误方向；3) 若主线要采纳，应如何把它吸收到下一轮任务卡或复核重点。不要复述空话，不要写成长文。");
        sb.AppendLine("AMPLIFIED_ADVICE>>>");
        return sb.ToString();
    }

    private static MainlinePlanResult ParseMainlinePlan(string output)
    {
        var status = ExtractSingleValue(output, "STATUS") ?? "PLAN";
        var roundGoal = ExtractSingleValue(output, "ROUND_GOAL") ?? ExtractTail(output, 240);
        var stagePlan = ExtractBlock(output, "STAGE_PLAN");
        var currentStage = ExtractSingleValue(output, "CURRENT_STAGE");
        var stageGoal = ExtractSingleValue(output, "STAGE_GOAL");
        var architectureGuardrails = ExtractBlock(output, "ARCH_GUARDRAILS");
        var taskCard = ExtractBlock(output, "TASK_CARD");
        var reviewFocus = ExtractBlock(output, "REVIEW_FOCUS");
        var summary = ExtractBlock(output, "SUMMARY") ?? ExtractTail(output, 320);

        return new MainlinePlanResult
        {
            Status = status.ToUpperInvariant(),
            StagePlanSummary = string.IsNullOrWhiteSpace(stagePlan) ? null : stagePlan,
            CurrentStage = NormalizeOptionalSingleValue(currentStage),
            StageGoal = NormalizeOptionalSingleValue(stageGoal),
            ArchitectureGuardrails = string.IsNullOrWhiteSpace(architectureGuardrails) ? null : architectureGuardrails,
            RoundGoal = roundGoal,
            TaskCard = string.IsNullOrWhiteSpace(taskCard) ? null : taskCard,
            ReviewFocus = string.IsNullOrWhiteSpace(reviewFocus) ? null : reviewFocus,
            Summary = summary
        };
    }

    private static SublineResult ParseSublineResult(string output)
    {
        return new SublineResult
        {
            Status = (ExtractSingleValue(output, "STATUS") ?? "PARTIAL").ToUpperInvariant(),
            Summary = ExtractBlock(output, "SUMMARY") ?? ExtractTail(output, 320),
            Facts = ExtractBlock(output, "FACTS") ?? ExtractTail(output, 600),
            Adjustments = ExtractBlock(output, "ADJUSTMENTS") ?? "无",
            Questions = ExtractBlock(output, "QUESTIONS") ?? "无",
            Next = ExtractBlock(output, "NEXT") ?? "无"
        };
    }

    private static MainlineReviewResult ParseMainlineReview(string output)
    {
        var verdict = (ExtractSingleValue(output, "VERDICT") ?? "REVISE").ToUpperInvariant();
        var continueRaw = (ExtractSingleValue(output, "CONTINUE") ?? "YES").Trim();
        var continueRequested = continueRaw.Equals("YES", StringComparison.OrdinalIgnoreCase)
            || continueRaw.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || continueRaw.Equals("继续", StringComparison.OrdinalIgnoreCase);
        var goalStatus = NormalizeGoalStatus(ExtractSingleValue(output, "GOAL_STATUS"), continueRequested);
        var nextStage = ExtractSingleValue(output, "NEXT_STAGE");
        var nextStageGoal = ExtractSingleValue(output, "NEXT_STAGE_GOAL");
        var changeDecision = ExtractBlock(output, "CHANGE_DECISION");

        return new MainlineReviewResult
        {
            Verdict = verdict,
            ContinueRequested = continueRequested,
            GoalStatus = goalStatus,
            GoalCompleted = string.Equals(goalStatus, GoalStatusComplete, StringComparison.OrdinalIgnoreCase),
            NextStage = NormalizeOptionalSingleValue(nextStage),
            NextStageGoal = NormalizeOptionalSingleValue(nextStageGoal),
            NextRoundGoal = ExtractSingleValue(output, "NEXT_ROUND_GOAL"),
            Summary = ExtractBlock(output, "SUMMARY") ?? ExtractTail(output, 320),
            ChangeDecision = string.IsNullOrWhiteSpace(changeDecision) ? null : changeDecision,
            NextTaskCard = ExtractBlock(output, "NEXT_TASK_CARD"),
            NextReviewFocus = ExtractBlock(output, "NEXT_REVIEW_FOCUS"),
            Directive = ExtractBlock(output, "NEXT_TASK_CARD") ?? "无"
        };
    }

    private static string? ParseWingmanAssistOutput(string output)
    {
        var amplifiedAdvice = ExtractBlock(output, "AMPLIFIED_ADVICE") ?? ExtractTail(output, 600);
        return string.IsNullOrWhiteSpace(amplifiedAdvice) ? null : amplifiedAdvice;
    }

    private void CreateNextRoundFromPlan(V3PairRun run, MainlinePlanResult plan, string decisionKind)
    {
        run.CurrentRound++;
        run.StagePlanSummary = plan.StagePlanSummary ?? run.StagePlanSummary;
        run.CurrentStageLabel = plan.CurrentStage ?? run.CurrentStageLabel;
        run.CurrentStageGoal = plan.StageGoal ?? run.CurrentStageGoal;
        run.ArchitectureGuardrails = plan.ArchitectureGuardrails ?? run.ArchitectureGuardrails;
        var round = new V3PairRoundRecord
        {
            RoundNumber = run.CurrentRound,
            Status = "queued",
            Objective = plan.RoundGoal,
            StageLabel = run.CurrentStageLabel,
            StageGoal = run.CurrentStageGoal,
            TaskCard = plan.TaskCard,
            ReviewFocus = plan.ReviewFocus,
            MainPlanSummary = plan.Summary,
            MainPlanOutputPath = plan.OutputPath,
            StartedAt = DateTime.UtcNow
        };

        run.Rounds.Add(round);
        run.LatestTaskCard = plan.TaskCard;
        run.LatestReviewFocus = plan.ReviewFocus;
        run.LatestMainReviewSummary = plan.Summary;
        AddDecision(run, decisionKind, $"Round {round.RoundNumber} task card prepared: {plan.RoundGoal ?? plan.Summary}");
    }

    private void StageInitialPlanForApproval(V3PairRun run, MainlinePlanResult plan)
    {
        run.StagePlanSummary = plan.StagePlanSummary ?? run.StagePlanSummary;
        run.CurrentStageLabel = plan.CurrentStage ?? run.CurrentStageLabel;
        run.CurrentStageGoal = plan.StageGoal ?? run.CurrentStageGoal;
        run.ArchitectureGuardrails = plan.ArchitectureGuardrails ?? run.ArchitectureGuardrails;
        run.InitialPlanVersion = Math.Max(1, run.InitialPlanVersion + 1);
        run.AwaitingInitialApproval = true;
        run.InitialPlanStatus = "pending";
        run.InitialPlanRoundGoal = plan.RoundGoal;
        run.InitialPlanTaskCard = plan.TaskCard;
        run.InitialPlanReviewFocus = plan.ReviewFocus;
        run.InitialPlanSummary = plan.Summary;
        run.InitialPlanOutputPath = plan.OutputPath;
        run.LatestTaskCard = plan.TaskCard;
        run.LatestReviewFocus = plan.ReviewFocus;
        run.LatestMainReviewSummary = plan.Summary;
        run.LatestMainDirective = "等待你确认首轮方案；确认后才会启动子线。";
        run.MainThreadStatus = "completed";
        run.SubThreadStatus = "idle";
        run.Status = "awaiting-approval";
        AddDecision(run, "initial-plan-ready", $"首轮方案 v{run.InitialPlanVersion} 已生成，等待人工确认。{TrimForDecision(plan.Summary)}");
    }

    private void CreateNextRoundFromInitialPlan(V3PairRun run)
    {
        var plan = new MainlinePlanResult
        {
            StagePlanSummary = run.StagePlanSummary,
            CurrentStage = run.CurrentStageLabel,
            StageGoal = run.CurrentStageGoal,
            ArchitectureGuardrails = run.ArchitectureGuardrails,
            RoundGoal = run.InitialPlanRoundGoal,
            TaskCard = run.InitialPlanTaskCard,
            ReviewFocus = run.InitialPlanReviewFocus,
            Summary = run.InitialPlanSummary ?? string.Empty,
            OutputPath = run.InitialPlanOutputPath
        };

        CreateNextRoundFromPlan(run, plan, "round-prepared");
    }

    private void CreateNextRoundFromReview(V3PairRun run, MainlineReviewResult review)
    {
        var nextTaskCard = BuildFallbackNextTaskCard(review);
        run.CurrentRound++;
        run.CurrentStageLabel = review.NextStage ?? run.CurrentStageLabel;
        run.CurrentStageGoal = review.NextStageGoal ?? run.CurrentStageGoal;
        var round = new V3PairRoundRecord
        {
            RoundNumber = run.CurrentRound,
            Status = "queued",
            Objective = string.IsNullOrWhiteSpace(review.NextRoundGoal) ? review.Summary : review.NextRoundGoal,
            StageLabel = run.CurrentStageLabel,
            StageGoal = run.CurrentStageGoal,
            TaskCard = nextTaskCard,
            ReviewFocus = review.NextReviewFocus,
            MainPlanSummary = review.Summary,
            StartedAt = DateTime.UtcNow
        };

        run.Rounds.Add(round);
        run.LatestTaskCard = nextTaskCard;
        run.LatestMainDirective = nextTaskCard;
        run.LatestReviewFocus = review.NextReviewFocus;
        AddDecision(run, "round-prepared", $"Round {round.RoundNumber} prepared from previous review.");
    }

    private static string BuildFallbackNextTaskCard(MainlineReviewResult review)
    {
        return string.IsNullOrWhiteSpace(review.NextTaskCard)
            ? $"延续上一轮并围绕以下目标继续推进：{(string.IsNullOrWhiteSpace(review.NextRoundGoal) ? review.Summary : review.NextRoundGoal)}"
            : review.NextTaskCard;
    }

    private static string NormalizeGoalStatus(string? rawGoalStatus, bool continueRequested)
    {
        var normalized = rawGoalStatus?.Trim();
        if (string.Equals(normalized, GoalStatusComplete, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "DONE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "已完成", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "完成", StringComparison.OrdinalIgnoreCase))
        {
            return GoalStatusComplete;
        }

        if (string.Equals(normalized, GoalStatusIncomplete, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "PENDING", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "未完成", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "未收口", StringComparison.OrdinalIgnoreCase))
        {
            return GoalStatusIncomplete;
        }

        return continueRequested ? GoalStatusIncomplete : GoalStatusComplete;
    }

    private static string? ExtractSingleValue(string output, string key)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (Match match in SingleValueRegex.Matches(output))
        {
            if (string.Equals(match.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["value"].Value.Trim();
            }
        }

        return null;
    }

    private static string? ExtractBlock(string output, string key)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var pattern = $@"<<<{Regex.Escape(key)}\s*(?<body>[\s\S]*?)\s*{Regex.Escape(key)}>>>";
        var match = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var body = match.Groups["body"].Value.Trim();
        if (string.Equals(body, "无", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return body;
    }

    private static string ExtractTail(string? output, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        var trimmed = output.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[^maxLength..];
    }

    private static string? NormalizeOptionalSingleValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return string.Equals(trimmed, "无", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static void EnsureInterjectionEditable(V3PairRun run)
    {
        if (run.Status is "completed" or "failed" or "stopped")
        {
            throw new InvalidOperationException("当前 run 已结束，插话不会再生效。请新建或重启一个 V3 run。");
        }
    }

    private static void EnsureInitialPlanApprovalPending(V3PairRun run)
    {
        if (run.AwaitingInitialApproval
            && string.Equals(run.Status, "stopped", StringComparison.OrdinalIgnoreCase)
            && run.RecoveredFromStorage
            && !string.IsNullOrWhiteSpace(run.InitialPlanTaskCard))
        {
            run.Status = "awaiting-approval";
            run.InitialPlanStatus ??= "pending";
        }

        if (!run.AwaitingInitialApproval || !string.Equals(run.Status, "awaiting-approval", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前没有等待确认的首轮方案。请先让主线完成首轮起案。" );
        }

        if (string.IsNullOrWhiteSpace(run.InitialPlanTaskCard))
        {
            throw new InvalidOperationException("首轮方案内容不完整，无法确认或打回。" );
        }
    }

    private PendingInterjectionPayload? ConsumePendingInterjection(V3PairRun run, string phase, int roundNumber)
    {
        if (string.IsNullOrWhiteSpace(run.PendingInterjectionText))
        {
            return null;
        }

        var text = run.PendingInterjectionText.Trim();
	    var wingmanText = string.IsNullOrWhiteSpace(run.PendingInterjectionWingmanText) ? null : run.PendingInterjectionWingmanText.Trim();
        run.LastAppliedInterjectionText = text;
	    run.LastAppliedInterjectionWingmanText = wingmanText;
        run.LastAppliedInterjectionAt = DateTime.UtcNow;
        run.LastAppliedInterjectionRound = roundNumber;
        run.LastAppliedInterjectionPhase = phase;
        run.PendingInterjectionText = null;
        run.PendingInterjectionUpdatedAt = null;
	    run.PendingInterjectionUseWingman = false;
	    run.PendingInterjectionWingmanText = null;
	    run.PendingInterjectionWingmanUpdatedAt = null;
        LogRunEvent(run, "插话", RunCsvLogService.BuildDetails(
            ("事件", "插话已注入主线阶段"),
            ("阶段", phase),
            ("轮次", roundNumber),
            ("用户原话", text),
            ("助攻稿", wingmanText)));
	    AddDecision(run, "interjection-applied", string.IsNullOrWhiteSpace(wingmanText)
	        ? $"插话已注入主线阶段 {phase}：{TrimForDecision(text)}"
	        : $"插话与助攻增强稿已注入主线阶段 {phase}：{TrimForDecision(text)}");
	    return new PendingInterjectionPayload(text, wingmanText);
    }

    private static void EnsureContinuationAllowed(V3PairRun run)
    {
        if (!(run.RecoveredFromStorage
              || string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)
              || string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("继续推进只在软件重开恢复出的 run，或 run 已完成 / 失败时可用。");
        }

        if (run.Status is "planning" or "running" or "reviewing")
        {
            throw new InvalidOperationException("当前 run 仍在执行中，暂时不能使用继续推进。");
        }
    }

    private async Task BroadcastRunUpdatedAsync(V3PairRun run)
    {
        run.UpdatedAt = DateTime.UtcNow;
        WriteRunStateArtifacts(run);
        _runStore.Upsert(run);
        await _hubContext.Clients.All.SendAsync("V3RunUpdated", run);
    }

    private static void EnsureRunArtifactScaffold(V3PairRun run)
    {
        var root = GetArtifactRoot(run);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "rounds"));
        Directory.CreateDirectory(Path.Combine(root, "handoff"));
    }

    private static string GetArtifactRoot(V3PairRun run)
    {
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();
        var root = Path.Combine(workspaceRoot, ".repoops", "v3", "runs", run.RunId);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string GetRoundArtifactDirectory(V3PairRun run, int roundNumber)
    {
        var path = Path.Combine(GetArtifactRoot(run), "rounds", $"round-{Math.Max(1, roundNumber):D3}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreatePromptFile(V3PairRun run, string phaseKey, string lane, string prompt)
    {
        var root = Path.Combine(run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir(), ".repoops", "prompts", run.RunId, "v3", lane);
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{phaseKey}.md");
        File.WriteAllText(filePath, prompt, new UTF8Encoding(false));
        return filePath;
    }

    private static string CreateOutputFile(V3PairRun run, string phaseKey, string lane)
    {
        var roundDir = GetRoundArtifactDirectory(run, run.CurrentRound);
        var outDir = Path.Combine(roundDir, lane);
        Directory.CreateDirectory(outDir);
        return Path.Combine(outDir, $"{phaseKey}-output.md");
    }

    private static void WriteRunStateArtifacts(V3PairRun run)
    {
        EnsureRunArtifactScaffold(run);
        var root = GetArtifactRoot(run);
        var latestRound = run.Rounds.LastOrDefault();
        var payload = new
        {
            runId = run.RunId,
            title = run.Title,
            goal = run.Goal,
            initialPlanVersion = run.InitialPlanVersion,
            awaitingInitialApproval = run.AwaitingInitialApproval,
            initialPlanStatus = run.InitialPlanStatus,
            initialPlanRejectedCount = run.InitialPlanRejectedCount,
            lastInitialPlanReviewComment = run.LastInitialPlanReviewComment,
            initialPlanApprovedAt = run.InitialPlanApprovedAt,
            initialPlanRoundGoal = run.InitialPlanRoundGoal,
            initialPlanTaskCard = run.InitialPlanTaskCard,
            initialPlanReviewFocus = run.InitialPlanReviewFocus,
            initialPlanSummary = run.InitialPlanSummary,
            initialPlanOutputPath = run.InitialPlanOutputPath,
            status = run.Status,
            goalCompleted = run.GoalCompleted,
            latestGoalStatus = run.LatestGoalStatus,
            currentRound = run.CurrentRound,
            maxRounds = run.MaxRounds,
            workspaceRoot = run.WorkspaceRoot,
            executionRoot = run.ExecutionRoot,
            mainRole = new { run.MainRoleId, run.MainRoleName, run.MainRoleIcon, run.MainThreadStatus },
            subRole = new { run.SubRoleId, run.SubRoleName, run.SubRoleIcon, run.SubThreadStatus },
            latestTaskCard = run.LatestTaskCard,
            latestVerdict = run.LatestVerdict,
            latestDirective = run.LatestMainDirective,
            stagePlanSummary = run.StagePlanSummary,
            currentStageLabel = run.CurrentStageLabel,
            currentStageGoal = run.CurrentStageGoal,
            architectureGuardrails = run.ArchitectureGuardrails,
            latestChangeDecision = run.LatestChangeDecision,
            pendingInterjectionText = run.PendingInterjectionText,
            pendingInterjectionUpdatedAt = run.PendingInterjectionUpdatedAt,
            pendingInterjectionUseWingman = run.PendingInterjectionUseWingman,
            pendingInterjectionWingmanText = run.PendingInterjectionWingmanText,
            pendingInterjectionWingmanUpdatedAt = run.PendingInterjectionWingmanUpdatedAt,
            lastAppliedInterjectionText = run.LastAppliedInterjectionText,
            lastAppliedInterjectionWingmanText = run.LastAppliedInterjectionWingmanText,
            lastAppliedInterjectionAt = run.LastAppliedInterjectionAt,
            lastAppliedInterjectionRound = run.LastAppliedInterjectionRound,
            lastAppliedInterjectionPhase = run.LastAppliedInterjectionPhase,
            recoveredFromStorage = run.RecoveredFromStorage,
            lastContinueInstruction = run.LastContinueInstruction,
            lastContinueRoundIncrement = run.LastContinueRoundIncrement,
            latestRound,
            recentDecisions = run.Decisions.TakeLast(10),
            createdAtUtc = run.CreatedAt,
            updatedAtUtc = run.UpdatedAt
        };

        File.WriteAllText(Path.Combine(root, "run-state.json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "run.json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        var summary = new StringBuilder();
        summary.AppendLine($"# V3 Pair Run {run.RunId}");
        summary.AppendLine();
        summary.AppendLine($"- 标题：{run.Title}");
        summary.AppendLine($"- 目标：{run.Goal}");
        summary.AppendLine($"- 状态：{run.Status}");
        summary.AppendLine($"- 终极目标状态：{run.LatestGoalStatus ?? (run.GoalCompleted ? GoalStatusComplete : GoalStatusIncomplete)}");
        summary.AppendLine($"- 当前轮次：{run.CurrentRound}/{run.MaxRounds}");
        summary.AppendLine($"- 当前阶段：{run.CurrentStageLabel ?? "无"}");
        summary.AppendLine($"- 当前阶段目标：{run.CurrentStageGoal ?? "无"}");
        summary.AppendLine($"- 主线：{run.MainRoleName}");
        summary.AppendLine($"- 子线：{run.SubRoleName}");
        if (!string.IsNullOrWhiteSpace(run.StagePlanSummary))
        {
            summary.AppendLine();
            summary.AppendLine("## 阶段总览");
            summary.AppendLine();
            summary.AppendLine(run.StagePlanSummary);
        }
        if (!string.IsNullOrWhiteSpace(run.LatestChangeDecision))
        {
            summary.AppendLine();
            summary.AppendLine("## 最近一次保留/修正决定");
            summary.AppendLine();
            summary.AppendLine(run.LatestChangeDecision);
        }
        if (run.RecoveredFromStorage)
        {
            summary.AppendLine("- 恢复来源：来自重开软件后的落地 run");
        }
        if (!string.IsNullOrWhiteSpace(run.PendingInterjectionText))
        {
            summary.AppendLine($"- 待生效插话：{run.PendingInterjectionText}");
        }
	    if (!string.IsNullOrWhiteSpace(run.PendingInterjectionWingmanText))
	    {
	        summary.AppendLine($"- 待生效助攻增强：{run.PendingInterjectionWingmanText}");
	    }
        if (latestRound is not null)
        {
            summary.AppendLine($"- 最新 verdict：{latestRound.ReviewVerdict ?? run.LatestVerdict ?? "—"}");
            summary.AppendLine();
            summary.AppendLine("## 最新摘要");
            summary.AppendLine();
            summary.AppendLine(latestRound.ReviewSummary ?? latestRound.SublineSummary ?? latestRound.MainPlanSummary ?? "暂无");
        }

        File.WriteAllText(Path.Combine(root, "handoff", "stage-summary.md"), summary.ToString(), new UTF8Encoding(false));
    }

    private static ExecutionLaunchScript CreateExecutionLaunchScript(
        string workingDirectory,
        string promptFilePath,
        string outputFilePath,
        string executable,
        IReadOnlyList<string> arguments,
        string runId,
        string label)
    {
        var renderedArgs = arguments.Select(arg =>
            string.Equals(arg, "$prompt", StringComparison.Ordinal)
                ? "$prompt"
                : NeedsQuoting(arg) ? QuotePowerShellLiteral(arg) : arg);

        var copilotCommand = $"{executable} {string.Join(" ", renderedArgs)}";

        var scriptLines = new[]
        {
            "$utf8NoBom = [System.Text.UTF8Encoding]::new($false)",
            "$OutputEncoding = $utf8NoBom",
            "[Console]::InputEncoding = $utf8NoBom",
            "[Console]::OutputEncoding = $utf8NoBom",
            "$PSNativeCommandUseErrorActionPreference = $false",
            "chcp.com 65001 > $null",
            $"Set-Location -LiteralPath {QuotePowerShellLiteral(workingDirectory)}",
            $"$promptPath = {QuotePowerShellLiteral(promptFilePath)}",
            $"$outputPath = {QuotePowerShellLiteral(outputFilePath)}",
            "$prompt = Get-Content -LiteralPath $promptPath -Raw -Encoding utf8",
            $"{copilotCommand} 2>&1 | Tee-Object -FilePath $outputPath",
            "$exitCode = if ($LASTEXITCODE -eq $null) { 0 } else { $LASTEXITCODE }",
            "exit $exitCode"
        };

        var script = string.Join(Environment.NewLine, scriptLines);
        var scriptFolder = Path.Combine(workingDirectory, ".repoops", "prompts", runId, "v3", "scripts");
        Directory.CreateDirectory(scriptFolder);
        var scriptPath = Path.Combine(scriptFolder, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{SanitizeLabel(label)}.ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        var commandLine = "pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass";
        var inputScript = script.EndsWith("\r\n", StringComparison.Ordinal) ? script : script + "\r\n";
        return new ExecutionLaunchScript(commandLine, script, inputScript, scriptPath);
    }

    private static bool NeedsQuoting(string arg)
        => arg.Length == 0 || arg.Contains(' ') || arg.Contains('\'') || arg.Contains('(') || arg.Contains(')');

    private static string QuotePowerShellLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string SanitizeLabel(string label)
    {
        var sb = new StringBuilder();
        foreach (var ch in label)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-')
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        return sb.ToString().Trim('-');
    }

    private void AddDecision(V3PairRun run, string kind, string summary)
    {
        run.Decisions.Add(new V3PairDecision { Kind = kind, Summary = summary });
        if (run.Decisions.Count > 60)
        {
            run.Decisions = run.Decisions[^60..];
        }
        run.UpdatedAt = DateTime.UtcNow;
    }

    private static string TrimForDecision(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "（空）";
        }

        var trimmed = text.Trim();
        return trimmed.Length <= 72 ? trimmed : trimmed[..72] + "…";
    }

    private void SyncRunsFromStore()
    {
        var persistedRuns = _runStore.GetAll();
        var persistedIds = persistedRuns.Select(item => item.RunId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var persisted in persistedRuns)
        {
            var hadExisting = _runs.TryGetValue(persisted.RunId, out var existingBeforeUpdate);
            _runs.AddOrUpdate(persisted.RunId, persisted, (_, existing) =>
            {
                var hasActiveSession = !string.IsNullOrWhiteSpace(existing.MainThreadSessionId) || !string.IsNullOrWhiteSpace(existing.SubThreadSessionId);
                return hasActiveSession ? existing : persisted;
            });

            if (persisted.RecoveredFromStorage && (!hadExisting || existingBeforeUpdate?.RecoveredFromStorage != true))
            {
                LogRunEvent(persisted, "恢复", RunCsvLogService.BuildDetails(
                    ("事件", "运行已从持久化状态恢复"),
                    ("状态", persisted.Status),
                    ("当前轮次", $"{persisted.CurrentRound}/{persisted.MaxRounds}"),
                    ("工作区", persisted.WorkspaceRoot),
                    ("元数据", persisted.WorkspaceMetadataFile)));
            }
        }

        foreach (var key in _runs.Keys)
        {
            if (!persistedIds.Contains(key)
                && _runs.TryGetValue(key, out var run)
                && string.IsNullOrWhiteSpace(run.MainThreadSessionId)
                && string.IsNullOrWhiteSpace(run.SubThreadSessionId))
            {
                _runs.TryRemove(key, out _);
            }
        }
    }

    private static void DeleteRunArtifacts(V3PairRun run)
    {
        TryDeleteDirectory(GetArtifactRoot(run));

        var promptsRoot = Path.Combine(run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir(), ".repoops", "prompts", run.RunId);
        TryDeleteDirectory(promptsRoot);
    }

    private static void TryDeleteDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch
        {
            // Best effort only; stale files can still be manually removed from .repoops.
        }
    }

    private V3PairRun RequireRun(string runId)
        => _runs.TryGetValue(runId, out var run) ? run : throw new InvalidOperationException($"V3 Run '{runId}' not found.");

    private sealed class V3ExecutionResult
    {
        public V3ExecutionResult(string sessionId, int exitCode, string promptPath, string outputPath, string outputText, string commandPreview)
        {
            SessionId = sessionId;
            ExitCode = exitCode;
            PromptPath = promptPath;
            OutputPath = outputPath;
            OutputText = outputText;
            CommandPreview = commandPreview;
        }

        public string SessionId { get; }
        public int ExitCode { get; }
        public string PromptPath { get; }
        public string OutputPath { get; }
        public string OutputText { get; }
        public string CommandPreview { get; }
    }

    private sealed class PendingInterjectionPayload
    {
        public PendingInterjectionPayload(string rawText, string? wingmanText)
        {
            RawText = rawText;
            WingmanText = wingmanText;
        }

        public string RawText { get; }
        public string? WingmanText { get; }
    }

    private sealed class ExecutionLaunchScript
    {
        public ExecutionLaunchScript(string commandLine, string commandPreview, string inputScript, string scriptPath)
        {
            CommandLine = commandLine;
            CommandPreview = commandPreview;
            InputScript = inputScript;
            ScriptPath = scriptPath;
        }

        public string CommandLine { get; }
        public string CommandPreview { get; }
        public string InputScript { get; }
        public string ScriptPath { get; }
    }

    private sealed class MainlinePlanResult
    {
        public string Status { get; set; } = "PLAN";
        public string? StagePlanSummary { get; set; }
        public string? CurrentStage { get; set; }
        public string? StageGoal { get; set; }
        public string? ArchitectureGuardrails { get; set; }
        public string? RoundGoal { get; set; }
        public string? TaskCard { get; set; }
        public string? ReviewFocus { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string? OutputPath { get; set; }
    }

    private sealed class SublineResult
    {
        public string Status { get; set; } = "PARTIAL";
        public string Summary { get; set; } = string.Empty;
        public string Facts { get; set; } = string.Empty;
        public string Adjustments { get; set; } = "无";
        public string Questions { get; set; } = "无";
        public string Next { get; set; } = "无";
        public int ExitCode { get; set; }
        public string? OutputPath { get; set; }
    }

    private sealed class MainlineReviewResult
    {
        public string Verdict { get; set; } = "REVISE";
        public bool ContinueRequested { get; set; } = true;
        public string GoalStatus { get; set; } = GoalStatusIncomplete;
        public bool GoalCompleted { get; set; }
        public string? NextStage { get; set; }
        public string? NextStageGoal { get; set; }
        public string? NextRoundGoal { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string Directive { get; set; } = "无";
        public string? ChangeDecision { get; set; }
        public string? NextTaskCard { get; set; }
        public string? NextReviewFocus { get; set; }
        public string? OutputPath { get; set; }
    }
}
