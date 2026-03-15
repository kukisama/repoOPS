using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RepoOPS.Agents.Models;
using RepoOPS.Hubs;
using RepoOPS.Services;

namespace RepoOPS.Agents.Services;

/// <summary>
/// V2 Orchestrator — self-driving, template-based multi-agent scheduler.
/// All execution happens in visible PTY terminals via PtyService.
/// The frontend creates xterm.js terminals to display each session.
/// </summary>
public sealed class V2OrchestratorService : IDisposable
{
    private static readonly Regex WorkspaceNameRegex = new("^[a-z]{5,}$", RegexOptions.Compiled);
    private static readonly Regex StageAsciiTokenRegex = new("[a-z0-9]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StageCjkSegmentRegex = new("[\u4e00-\u9fff]{2,}", RegexOptions.Compiled);
    private const string NamingModel = "gpt-5-mini";
    private const string StandardExecutionModel = "gpt-5.4";
    private readonly AgentRoleConfigService _roleConfigService;
    private readonly V2WorkspaceBootstrapService _workspaceBootstrapService;
    private readonly PtyService _ptyService;
    private readonly IHubContext<TaskHub> _hubContext;
    private readonly ILogger<V2OrchestratorService> _logger;

    private readonly ConcurrentDictionary<string, V2Run> _runs = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<int>> _sessionCompletions = new();
    private readonly ConcurrentDictionary<string, int> _completedSessionExitCodes = new();
    private readonly V2PromptTemplateEngine _templates = new();

    public V2OrchestratorService(
        AgentRoleConfigService roleConfigService,
        V2WorkspaceBootstrapService workspaceBootstrapService,
        PtyService ptyService,
        IHubContext<TaskHub> hubContext,
        ILogger<V2OrchestratorService> logger)
    {
        _roleConfigService = roleConfigService;
        _workspaceBootstrapService = workspaceBootstrapService;
        _ptyService = ptyService;
        _hubContext = hubContext;
        _logger = logger;

        // Subscribe to PTY session completions
        _ptyService.SessionCompleted += OnPtySessionCompleted;
    }

    public void Dispose()
    {
        _ptyService.SessionCompleted -= OnPtySessionCompleted;
    }

    private void OnPtySessionCompleted(string sessionId, int exitCode)
    {
        if (_sessionCompletions.TryRemove(sessionId, out var tcs))
            tcs.TrySetResult(exitCode);
        else
            _completedSessionExitCodes[sessionId] = exitCode;
    }

    // ── Public API ──

    public IReadOnlyList<V2Run> GetRuns() => [.. _runs.Values.OrderByDescending(r => r.UpdatedAt)];

    public V2Run? GetRun(string runId) => _runs.TryGetValue(runId, out var r) ? r : null;

    public V2RunSnapshot GetRunSnapshot(string runId)
    {
        var run = RequireRun(runId);
        return new V2RunSnapshot
        {
            Run = run,
            Workers = run.Workers,
            Rounds = run.Rounds,
            Decisions = run.Decisions
        };
    }

    public async Task<V2Run> CreateRunAsync(CreateV2RunRequest request)
    {
        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var executionRoot = ResolveWorkspace(settings, request.WorkspaceRoot);
        var deferAiNamingBootstrap = string.IsNullOrWhiteSpace(request.WorkspaceRoot) && string.IsNullOrWhiteSpace(request.WorkspaceName);

        V2WorkspaceBootstrapResult? bootstrap = null;
        if (!deferAiNamingBootstrap)
        {
            bootstrap = _workspaceBootstrapService.Bootstrap(
                executionRoot,
                request.Goal.Trim(),
                request.WorkspaceRoot,
                request.WorkspaceName,
                catalog.Roles);
        }

        var run = new V2Run
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? CreateTitle(request.Goal) : request.Title.Trim(),
            Goal = request.Goal.Trim(),
            Status = "draft",
            MaxRounds = request.MaxRounds > 0 ? request.MaxRounds : 6,
            WorkspaceRoot = bootstrap?.WorkspaceRoot ?? executionRoot,
            ExecutionRoot = bootstrap?.ExecutionRoot ?? executionRoot,
            WorkspaceMetadataFile = bootstrap?.WorkspaceMetadataFile,
            AllowedPathsFile = bootstrap?.AllowedPathsFile,
            AllowedToolsFile = bootstrap?.AllowedToolsFile,
            AllowedUrlsFile = bootstrap?.AllowedUrlsFile
        };

        EnsureRunArtifactScaffold(run);
        TryAttachRelatedPreviousStage(run);
        AddDecision(run, "run-created", $"V2 run created: {run.Title}");
        if (!string.IsNullOrWhiteSpace(run.RelatedPreviousRunId))
        {
            AddDecision(run, "previous-stage-linked", $"Auto-linked previous stage run {run.RelatedPreviousRunId} (score={run.RelatedPreviousRunScore:0.00}).");
        }

        WriteRunStateArtifacts(run);
        _runs[run.RunId] = run;
        await BroadcastV2RunUpdatedAsync(run);

        if (request.AutoStart)
        {
            _ = Task.Run(() => RunMainLoopAsync(run.RunId));
        }

        return run;
    }

    public async Task<V2Run> StopRunAsync(string runId)
    {
        var run = RequireRun(runId);
        run.Status = "failed";
        run.UpdatedAt = DateTime.UtcNow;
        AddDecision(run, "run-stopped", "Run stopped by user.");

        // Stop all running PTY sessions
        foreach (var worker in run.Workers.Where(w => w.Status == "running"))
        {
            if (!string.IsNullOrEmpty(worker.PtySessionId))
                _ptyService.StopSession(worker.PtySessionId);
            worker.Status = "stopped";
            worker.UpdatedAt = DateTime.UtcNow;
        }

        // Complete any pending session waits
        foreach (var kvp in _sessionCompletions)
            kvp.Value.TrySetResult(-1);

        await BroadcastV2RunUpdatedAsync(run);
        return run;
    }

    public IReadOnlyDictionary<string, string> GetPromptTemplates() => _templates.GetAllTemplates();

    // ── Main self-driving loop ──

    private async Task RunMainLoopAsync(string runId)
    {
        var run = RequireRun(runId);
        try
        {
            if (NeedsAiWorkspaceBootstrap(run))
            {
                var ok = await TryBootstrapWorkspaceFromMainThreadAsync(run);
                if (!ok)
                {
                    run.Status = "failed";
                    var stopReason = "主线程命名阶段未通过校验，已中止后续动作。";
                    AddDecision(run, "workspace-name-invalid", stopReason);
                    await _hubContext.Clients.All.SendAsync("V2MainThreadActivity", run.RunId, "V2 Workspace Naming", $"failed: {stopReason}");
                    await BroadcastV2RunUpdatedAsync(run);
                    return;
                }
            }

            run.Status = "planning";
            run.MainThreadStatus = "running";
            await BroadcastV2RunUpdatedAsync(run);

            // Notify frontend to create the main thread terminal panel
            await _hubContext.Clients.All.SendAsync("V2MainThreadStarted", run.RunId, "(main-thread)");

            while (run.CurrentRound < run.MaxRounds && run.Status != "completed" && run.Status != "failed")
            {
                run.CurrentRound++;
                run.Status = "running";
                _logger.LogInformation("V2 Run {RunId}: Starting round {Round}", runId, run.CurrentRound);

                // Phase 1: Plan — launch copilot in a visible PTY to decide role assignments
                var planResult = await PlanRoundInPtyAsync(run);
                if (planResult is null || planResult.Assignments.Count == 0)
                {
                    AddDecision(run, "round-completed", $"Round {run.CurrentRound}: No assignments generated, completing.");
                    run.Status = "completed";
                    break;
                }

                var roundRecord = new V2RoundRecord
                {
                    RoundNumber = run.CurrentRound,
                    Phase = "dispatch",
                    MainThreadSummary = planResult.Summary,
                    StartedAt = DateTime.UtcNow
                };
                run.Rounds.Add(roundRecord);
                AddDecision(run, "round-started", $"Round {run.CurrentRound}: {planResult.Summary}");
                await BroadcastV2RunUpdatedAsync(run);

                // Phase 2: Dispatch workers — each gets a visible PTY terminal
                var roundWorkers = await DispatchWorkersInPtyAsync(run, planResult);
                roundRecord.Phase = "waiting";
                await BroadcastV2RunUpdatedAsync(run);

                // Phase 3: Wait for all worker PTY sessions to complete
                await WaitForWorkerPtysAsync(run, roundWorkers);

                // Phase 4: Collect results
                CollectWorkerResults(roundRecord, roundWorkers);
                WriteWorkerRoundArtifacts(run, roundWorkers);
                WriteUsageLog(run, roundWorkers);
                roundRecord.Phase = "review";
                await BroadcastV2RunUpdatedAsync(run);

                // Phase 5: Main thread reviews results (another PTY call)
                var reviewResult = await ReviewRoundInPtyAsync(run, roundRecord);
                roundRecord.CompletedAt = DateTime.UtcNow;

                if (reviewResult.OverallStatus == "completed")
                {
                    // Check: all reported done → force a review round
                    if (!run.ReviewForced)
                    {
                        _logger.LogInformation("V2 Run {RunId}: All workers done, forcing review round", runId);
                        run.ReviewForced = true;
                        AddDecision(run, "review-triggered", "All workers reported done. Triggering mandatory independent review.");
                        roundRecord.AllReportedDone = true;
                        roundRecord.Phase = "complete";
                        await BroadcastV2RunUpdatedAsync(run);

                        // Execute forced review
                        var reviewPassed = await ExecuteForcedReviewAsync(run);
                        if (reviewPassed)
                        {
                            run.Status = "completed";
                            AddDecision(run, "run-completed", "Independent review passed. Project completed.");
                        }
                        else
                        {
                            AddDecision(run, "review-triggered", "Independent review found issues. Continuing to next round.");
                            // Loop continues to next round
                        }
                    }
                    else
                    {
                        run.Status = "completed";
                        AddDecision(run, "run-completed", "Project completed (review already done).");
                    }
                }
                else if (reviewResult.OverallStatus == "needs-review")
                {
                    AddDecision(run, "review-triggered", "Main thread flagged for review.");
                    var reviewPassed = await ExecuteForcedReviewAsync(run);
                    if (reviewPassed)
                    {
                        run.Status = "completed";
                        AddDecision(run, "run-completed", "Review passed after main thread flagged.");
                    }
                }
                else
                {
                    // "continue" — next round
                    if (reviewResult.Issues.Count > 0)
                    {
                        AddDecision(run, "round-completed",
                            $"Round {run.CurrentRound} issues: {string.Join("; ", reviewResult.Issues)}");
                    }
                }

                roundRecord.Phase = "complete";
                await BroadcastV2RunUpdatedAsync(run);
            }

            if (run.Status != "completed" && run.Status != "failed")
            {
                run.Status = "completed";
                AddDecision(run, "run-completed", $"Max rounds ({run.MaxRounds}) reached. Run finished.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "V2 Run {RunId} main loop failed", runId);
            run.Status = "failed";
            AddDecision(run, "run-failed", $"Main loop error: {ex.Message}");
        }
        finally
        {
            run.MainThreadStatus = "completed";
            run.UpdatedAt = DateTime.UtcNow;
            await BroadcastV2RunUpdatedAsync(run);
        }
    }

    // ── Round planning — runs copilot in a visible PTY ──

    private async Task<RoundPlan?> PlanRoundInPtyAsync(V2Run run)
    {
        var catalog = _roleConfigService.Load();
        var roles = catalog.Roles;
        var roleListText = string.Join("\n", roles.Select(r => $"- {r.RoleId}: {r.Name} ({r.Description})"));

        var prompt = _templates.Render("main-plan-roles", new Dictionary<string, string>
        {
            ["goal"] = run.Goal,
            ["workspaceRoot"] = run.WorkspaceRoot ?? ".",
            ["roleList"] = roleListText,
            ["roundContext"] = BuildRoundProgressContext(run),
            ["previousStageContext"] = BuildPreviousStageReferenceContext(run)
        });

        var execution = await ExecuteCopilotInPtyAsync(run, prompt, $"V2 R{run.CurrentRound} Planning");
        WriteMainThreadUsageLog(run, "planner (main-thread)", execution);
        if (execution.ExitCode != 0)
        {
            return null;
        }

        var parsedPlan = TryParseRoundPlan(execution.OutputText, roles);
        RoundPlan plan;
        if (parsedPlan is not null && parsedPlan.Assignments.Count > 0)
        {
            plan = parsedPlan;
        }
        else
        {
            AddDecision(run, "plan-parse-fallback", $"Round {run.CurrentRound}: failed to parse planning JSON, fallback to default assignments.");
            plan = new RoundPlan { Summary = $"Round {run.CurrentRound} planning completed (fallback)." };
            var plannerRole = roles.FirstOrDefault(r => string.Equals(r.RoleId, "planner", StringComparison.OrdinalIgnoreCase));
            if (plannerRole is not null)
            {
                plan.Assignments.Add(new RoundAssignment
                {
                    RoleId = plannerRole.RoleId,
                    Task = "首轮请先完成方案规划与执行路线判断：明确是 plan-first 还是 direct-exec，并给出下一轮最小任务集。"
                });
            }
            else if (roles.Count > 0)
            {
                plan.Assignments.Add(new RoundAssignment { RoleId = roles[0].RoleId, Task = run.Goal });
            }
        }

        plan = ApplySingleActiveRolePolicy(run, plan, roles);

        WriteRoundPlanArtifact(run, execution, plan);
        WriteRoundPlanMarkdownArtifacts(run, plan);
        return plan;
    }

    private static RoundPlan ApplySingleActiveRolePolicy(V2Run run, RoundPlan plan, IReadOnlyList<AgentRoleDefinition> roles)
    {
        if (plan.Assignments.Count <= 1)
        {
            return plan;
        }

        RoundAssignment? selected = null;
        if (run.CurrentRound == 1)
        {
            var summary = plan.Summary ?? string.Empty;
            var indicatesDirectExec = summary.Contains("direct", StringComparison.OrdinalIgnoreCase)
                                      || summary.Contains("直接", StringComparison.OrdinalIgnoreCase)
                                      || summary.Contains("编码", StringComparison.OrdinalIgnoreCase)
                                      || summary.Contains("写代码", StringComparison.OrdinalIgnoreCase);
            if (indicatesDirectExec)
            {
                selected = plan.Assignments.FirstOrDefault(a => string.Equals(a.RoleId, "builder", StringComparison.OrdinalIgnoreCase));
            }

            selected ??= plan.Assignments.FirstOrDefault(a => string.Equals(a.RoleId, "planner", StringComparison.OrdinalIgnoreCase));
        }

        selected ??= plan.Assignments.First();

        plan.Assignments = [selected];

        var selectedRoleName = roles.FirstOrDefault(r => string.Equals(r.RoleId, selected.RoleId, StringComparison.OrdinalIgnoreCase))?.Name
                               ?? selected.RoleId;
        var currentSummary = plan.Summary ?? string.Empty;
        if (!currentSummary.Contains("单执行槽", StringComparison.OrdinalIgnoreCase))
        {
            plan.Summary = $"{currentSummary} 本轮收敛为单执行槽：{selectedRoleName}。".Trim();
        }

        return plan;
    }

    private async Task<List<V2Worker>> DispatchWorkersInPtyAsync(V2Run run, RoundPlan plan)
    {
        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var roundWorkers = new List<V2Worker>();
        var currentRoundDirectory = GetRoundArtifactDirectory(run);

        foreach (var assignment in plan.Assignments)
        {
            var role = catalog.Roles.FirstOrDefault(r => string.Equals(r.RoleId, assignment.RoleId, StringComparison.OrdinalIgnoreCase));
            if (role is null)
            {
                _logger.LogWarning("V2 Run {RunId}: Role {RoleId} not found, skipping.", run.RunId, assignment.RoleId);
                continue;
            }

            // Reuse existing worker for same role if available and not running 
            var existingWorker = run.Workers.FirstOrDefault(w =>
                w.RoleId == role.RoleId && w.Status != "running");

            V2Worker worker;
            if (existingWorker is not null)
            {
                worker = existingWorker;
                worker.Status = "running";
                worker.AssignedRound = run.CurrentRound;
                worker.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                worker = new V2Worker
                {
                    RoleId = role.RoleId,
                    RoleName = role.Name,
                    Icon = role.Icon,
                    Description = role.Description,
                    Status = "running",
                    AssignedRound = run.CurrentRound,
                };
                run.Workers.Add(worker);
            }

            var peerRoles = string.Join(", ", plan.Assignments
                .Where(a => a.RoleId != assignment.RoleId)
                .Select(a => a.RoleId));
            var ownedArtifactPath = GetRoleOwnedArtifactPath(run, role.RoleId);
            var roleWritePolicy = BuildRoleWritePolicy(role.RoleId, run.WorkspaceRoot ?? ".", currentRoundDirectory, ownedArtifactPath);

            var workerPrompt = _templates.Render("worker-dispatch", new Dictionary<string, string>
            {
                ["roleName"] = role.Name,
                ["roleId"] = role.RoleId,
                ["roleDescription"] = role.Description ?? "",
                ["goal"] = run.Goal,
                ["task"] = assignment.Task,
                ["workspaceRoot"] = run.WorkspaceRoot ?? ".",
                ["peerRoles"] = peerRoles,
                ["planIndexPath"] = plan.PlanIndexMarkdownPath ?? "(not generated)",
                ["currentRoundDirectory"] = currentRoundDirectory,
                ["roleOwnedOutputPath"] = ownedArtifactPath,
                ["roleWritePolicy"] = roleWritePolicy,
                ["taskMarkdownPath"] = plan.AssignmentMarkdownPaths.TryGetValue(role.RoleId, out var taskPath)
                    ? taskPath
                    : "(not generated)",
                ["previousStageContext"] = BuildPreviousStageReferenceContext(run)
            });

            worker.LastPrompt = workerPrompt;

            // Launch as a visible PTY terminal
            var sessionId = await LaunchWorkerPtyAsync(run, worker, role, workerPrompt, settings);
            worker.PtySessionId = sessionId;

            AddDecision(run, "worker-dispatched", $"Dispatched {role.Name} for round {run.CurrentRound}");

            // Tell frontend to create an xterm.js terminal for this worker (include command preview)
            await _hubContext.Clients.All.SendAsync("V2WorkerStarted", run.RunId, worker.WorkerId,
                sessionId, worker.RoleName, worker.Icon, worker.Status, worker.CommandPreview ?? "");

            roundWorkers.Add(worker);
        }

        await BroadcastV2RunUpdatedAsync(run);
        return roundWorkers;
    }

    /// <summary>Build the copilot command for a worker and launch it as a visible PTY session.</summary>
    private async Task<string> LaunchWorkerPtyAsync(V2Run run, V2Worker worker, AgentRoleDefinition role, string prompt, SupervisorSettings settings)
    {
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();

        // Write prompt to file
        var promptPath = CreatePromptFile(run.RunId, $"worker-{worker.RoleId}", workspaceRoot, prompt);

        var effectiveModel = StandardExecutionModel;

        // Build copilot argument list — mirrors V1 BuildWorkerLaunchPlan exactly
        var copilotArguments = new List<string> { "-p", "$prompt", "--no-alt-screen", "--yolo" };

        copilotArguments.AddRange(["--model", effectiveModel]);

        // Denied tools
        foreach (var tool in role.DeniedTools.Where(t => !string.IsNullOrWhiteSpace(t)))
            copilotArguments.AddRange(["--deny-tool", tool]);

        var outputPath = CreateWorkerOutputFile(run, worker);
        var (commandLine, commandPreview, inputScript) = BuildPtyLaunchScript(
            workspaceRoot, promptPath, outputPath, "copilot", copilotArguments, run.RunId, $"worker-{worker.RoleId}");
        worker.CommandPreview = commandPreview;
        worker.OutputFilePath = outputPath;

        var sessionId = _ptyService.StartRawSession(commandLine, workspaceRoot, 120, 30);
        // Inject commands as keyboard input into the interactive pwsh session
        await _ptyService.SendInputAsync(sessionId, inputScript);
        return sessionId;
    }

    // ── PTY-based worker wait ──

    private async Task WaitForWorkerPtysAsync(V2Run run, List<V2Worker> roundWorkers)
    {
        var waitTasks = new List<Task>();
        foreach (var worker in roundWorkers)
        {
            if (string.IsNullOrEmpty(worker.PtySessionId)) continue;
            var w = worker;
            waitTasks.Add(Task.Run(async () =>
            {
                var exitCode = await WaitForPtySessionAsync(w.PtySessionId!, TimeSpan.FromMinutes(30));
                w.ExitCode = exitCode;
                w.Status = exitCode == 0 ? "completed" : "failed";
                w.UpdatedAt = DateTime.UtcNow;

                await _hubContext.Clients.All.SendAsync("V2WorkerStatusChanged",
                    run.RunId, w.WorkerId, w.Status, w.RoleName, w.Icon);
            }));
        }
        await Task.WhenAll(waitTasks);
        await BroadcastV2RunUpdatedAsync(run);
    }

    private async Task<int> WaitForPtySessionAsync(string sessionId, TimeSpan timeout)
    {
        if (_completedSessionExitCodes.TryRemove(sessionId, out var completedExitCode))
            return completedExitCode;

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionCompletions[sessionId] = tcs;

        if (_completedSessionExitCodes.TryRemove(sessionId, out completedExitCode))
        {
            _sessionCompletions.TryRemove(sessionId, out _);
            return completedExitCode;
        }

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetResult(-1));
        return await tcs.Task;
    }

    // ── Round review ──

    private static void CollectWorkerResults(V2RoundRecord roundRecord, List<V2Worker> roundWorkers)
    {
        foreach (var worker in roundWorkers)
        {
            var outputSummary = string.Empty;
            string? rawFullOutput = null;
            if (!string.IsNullOrWhiteSpace(worker.OutputFilePath) && File.Exists(worker.OutputFilePath))
            {
                rawFullOutput = File.ReadAllText(worker.OutputFilePath);
                outputSummary = TrimOutput(rawFullOutput);
                worker.LastOutputPreview = outputSummary;
                worker.UsageStats = ParseUsageStats(rawFullOutput);
            }

            roundRecord.WorkerResults.Add(new V2WorkerResult
            {
                WorkerId = worker.WorkerId,
                RoleName = worker.RoleName,
                Status = worker.Status,
                Summary = string.IsNullOrWhiteSpace(outputSummary)
                    ? $"{worker.RoleName}: exit code {worker.ExitCode}"
                    : outputSummary,
                ResultMarkdown = string.IsNullOrWhiteSpace(worker.OutputFilePath) || !File.Exists(worker.OutputFilePath)
                    ? null
                    : rawFullOutput ?? File.ReadAllText(worker.OutputFilePath),
                ExitCode = worker.ExitCode
            });
        }
    }

    private async Task<ReviewResult> ReviewRoundInPtyAsync(V2Run run, V2RoundRecord roundRecord)
    {
        var workerReports = new StringBuilder();
        foreach (var wr in roundRecord.WorkerResults)
        {
            workerReports.AppendLine($"### {wr.RoleName} (status: {wr.Status}, exit: {wr.ExitCode})");
            workerReports.AppendLine(string.IsNullOrWhiteSpace(wr.Summary) ? "(no output)" : wr.Summary);
            workerReports.AppendLine();
        }

        var decisionHistory = string.Join("\n", run.Decisions.TakeLast(20).Select(d => $"[{d.Kind}] {d.Summary}"));

        var prompt = _templates.Render("main-review-round", new Dictionary<string, string>
        {
            ["goal"] = run.Goal,
            ["roundNumber"] = run.CurrentRound.ToString(),
            ["workerReports"] = workerReports.ToString(),
            ["decisionHistory"] = decisionHistory,
            ["roundArtifactDirectory"] = GetRoundArtifactDirectory(run),
            ["currentPlanIndexPath"] = Path.Combine(GetRoundArtifactDirectory(run), "plans", "plan-index.md"),
            ["initialPlanContext"] = BuildInitialPlanContext(run),
            ["previousStageContext"] = BuildPreviousStageReferenceContext(run)
        });

        var execution = await ExecuteCopilotInPtyAsync(run, prompt, $"V2 R{run.CurrentRound} Review");
        WriteMainThreadUsageLog(run, "reviewer (main-thread)", execution);
        var parsed = TryParseReviewResult(execution.OutputText);
        if (parsed is not null)
        {
            WriteRoundReviewArtifact(run, execution, parsed);
            return parsed;
        }

        // When review JSON cannot be parsed, never auto-complete — always continue.
        // Only an explicitly parsed "completed" status should end the loop.
        var fallback = new ReviewResult { OverallStatus = "continue", Summary = "Review output could not be parsed. Continuing to next round." };

        AddDecision(run, "review-parse-fallback", $"Round {run.CurrentRound}: failed to parse review JSON, defaulting to continue.");
        WriteRoundReviewArtifact(run, execution, fallback);
        return fallback;
    }

    private static bool NeedsAiWorkspaceBootstrap(V2Run run)
    {
        return string.IsNullOrWhiteSpace(run.WorkspaceMetadataFile)
               && !string.IsNullOrWhiteSpace(run.ExecutionRoot)
               && string.Equals(run.WorkspaceRoot, run.ExecutionRoot, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryBootstrapWorkspaceFromMainThreadAsync(V2Run run)
    {
        var prompt = string.Join("\n", new[]
        {
            "You generate one project directory name from a user goal.",
            "Rules:",
            "- Output exactly one name and nothing else.",
            "- Use lowercase English letters only.",
            "- Use 2-3 words but concatenate them directly (no '-', no '_', no spaces).",
            "- Minimum length is 5 letters. No maximum length limit.",
            "- Do not call tools. Do not access files. Do not write any extra text.",
            $"Goal: {run.Goal}"
        });

        var execution = await ExecuteCopilotInPtyAsync(
            run,
            prompt,
            "V2 Workspace Naming",
            modelOverride: NamingModel);

        var candidate = ExtractWorkspaceNameCandidate(execution.OutputText);
        if (string.IsNullOrWhiteSpace(candidate) || !WorkspaceNameRegex.IsMatch(candidate))
        {
            run.Status = "failed";
            var rejectReason = $"命名被阻止：模型输出不符合规则（仅小写字母连写且至少5个字母）。candidate='{(candidate ?? "<empty>")}' raw='{TrimOutput(execution.OutputText)}'";
            AddDecision(run, "workspace-name-rejected", rejectReason);
            await _hubContext.Clients.All.SendAsync("V2MainThreadActivity", run.RunId, "V2 Workspace Naming", $"failed: {rejectReason}");
            await BroadcastV2RunUpdatedAsync(run);
            return false;
        }

        var catalog = _roleConfigService.Load();
        var bootstrap = _workspaceBootstrapService.Bootstrap(
            run.ExecutionRoot ?? AgentRoleConfigService.GetBaseDir(),
            run.Goal,
            null,
            candidate,
            catalog.Roles);

        run.WorkspaceRoot = bootstrap.WorkspaceRoot;
        run.ExecutionRoot = bootstrap.ExecutionRoot;
        run.WorkspaceMetadataFile = bootstrap.WorkspaceMetadataFile;
        run.AllowedPathsFile = bootstrap.AllowedPathsFile;
        run.AllowedToolsFile = bootstrap.AllowedToolsFile;
        run.AllowedUrlsFile = bootstrap.AllowedUrlsFile;

        AddDecision(run, "workspace-name-accepted", $"主线程命名完成：{candidate}");
        await BroadcastV2RunUpdatedAsync(run);
        return true;
    }

    private static string? ExtractWorkspaceNameCandidate(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return null;
        }

        var contentOnly = rawOutput;
        var usageIndex = contentOnly.IndexOf("Total usage est:", StringComparison.OrdinalIgnoreCase);
        if (usageIndex >= 0)
        {
            contentOnly = contentOnly[..usageIndex];
        }

        var lines = contentOnly
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        // 仅接受“单行候选名”格式，避免把脚本回显、命令行、路径文本误识别成目录名。
        foreach (var line in lines)
        {
            if (line.Contains("\\", StringComparison.Ordinal) || line.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            var trimmed = line.Trim('`', '\'', '"');
            if (!Regex.IsMatch(trimmed, "^[a-zA-Z][a-zA-Z_-]{4,}$", RegexOptions.CultureInvariant))
            {
                continue;
            }

            var normalized = V2WorkspaceBootstrapService.TryNormalizeWorkspaceNameCandidate(trimmed);
            if (!string.IsNullOrWhiteSpace(normalized) && WorkspaceNameRegex.IsMatch(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private async Task<bool> ExecuteForcedReviewAsync(V2Run run)
    {
        var workerReports = new StringBuilder();
        foreach (var worker in run.Workers)
        {
            workerReports.AppendLine($"### {worker.RoleName} (status: {worker.Status})");
            workerReports.AppendLine($"Exit code: {worker.ExitCode}");
            workerReports.AppendLine();
        }

        var reviewPrompt = _templates.Render("forced-review", new Dictionary<string, string>
        {
            ["goal"] = run.Goal,
            ["workerReports"] = workerReports.ToString(),
            ["workspaceRoot"] = run.WorkspaceRoot ?? "."
        });

        // Find or create a reviewer role
        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var reviewerRole = catalog.Roles.FirstOrDefault(r =>
            r.RoleId.Contains("review", StringComparison.OrdinalIgnoreCase) ||
            r.Name.Contains("review", StringComparison.OrdinalIgnoreCase))
            ?? new AgentRoleDefinition
            {
                RoleId = "v2-reviewer",
                Name = "独立审查员",
                Description = "独立审查、验证项目交付质量",
                Icon = "🔍",
                Model = StandardExecutionModel,
                AllowAllTools = true
            };

        var reviewWorker = new V2Worker
        {
            RoleId = reviewerRole.RoleId,
            RoleName = reviewerRole.Name,
            Icon = reviewerRole.Icon,
            Description = reviewerRole.Description,
            Status = "running",
            AssignedRound = run.CurrentRound,
            LastPrompt = reviewPrompt
        };
        run.Workers.Add(reviewWorker);
        await BroadcastV2RunUpdatedAsync(run);

        // Launch reviewer as a visible PTY terminal
        var sessionId = await LaunchWorkerPtyAsync(run, reviewWorker, reviewerRole, reviewPrompt, settings);
        reviewWorker.PtySessionId = sessionId;

        await _hubContext.Clients.All.SendAsync("V2WorkerStarted", run.RunId, reviewWorker.WorkerId,
            sessionId, reviewWorker.RoleName, reviewWorker.Icon, reviewWorker.Status, reviewWorker.CommandPreview ?? "");
        await BroadcastV2RunUpdatedAsync(run);

        // Wait for reviewer PTY to complete
        var exitCode = await WaitForPtySessionAsync(sessionId, TimeSpan.FromMinutes(30));
        reviewWorker.ExitCode = exitCode;
        reviewWorker.Status = exitCode == 0 ? "completed" : "failed";
        reviewWorker.UpdatedAt = DateTime.UtcNow;

        // Parse reviewer usage stats
        if (!string.IsNullOrWhiteSpace(reviewWorker.OutputFilePath) && File.Exists(reviewWorker.OutputFilePath))
        {
            reviewWorker.UsageStats = ParseUsageStats(File.ReadAllText(reviewWorker.OutputFilePath));
        }
        WriteUsageLog(run, [reviewWorker]);

        await _hubContext.Clients.All.SendAsync("V2WorkerStatusChanged", run.RunId,
            reviewWorker.WorkerId, reviewWorker.Status, reviewWorker.RoleName, reviewWorker.Icon);
        await BroadcastV2RunUpdatedAsync(run);

        // Also run a final verdict via main thread PTY
        var verdictPrompt = _templates.Render("main-final-verdict", new Dictionary<string, string>
        {
            ["goal"] = run.Goal,
            ["reviewReport"] = $"Review worker exit code: {exitCode}",
            ["decisionHistory"] = string.Join("\n", run.Decisions.TakeLast(20).Select(d => $"[{d.Kind}] {d.Summary}"))
        });

        var verdictExecution = await ExecuteCopilotInPtyAsync(run, verdictPrompt, "V2 Final Verdict");
        WriteMainThreadUsageLog(run, "verdict (main-thread)", verdictExecution);

        // Parse verdict JSON; only pass when explicit "completed" verdict is found.
        // If parsing fails (empty transcript etc.), treat as not-passed so the loop continues.
        if (exitCode != 0 || verdictExecution.ExitCode != 0)
            return false;

        var verdictJson = ExtractJsonObject(verdictExecution.OutputText);
        if (verdictJson is null)
        {
            AddDecision(run, "verdict-parse-fallback", "Final verdict JSON could not be parsed. Treating as not-passed.");
            return false;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(verdictJson);
            var verdict = doc.RootElement.TryGetProperty("verdict", out var v) ? v.GetString() : null;
            return string.Equals(verdict, "completed", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            AddDecision(run, "verdict-parse-fallback", "Final verdict JSON deserialization failed. Treating as not-passed.");
            return false;
        }
    }

    // ── One-shot copilot execution via visible PTY ──

    /// <summary>
    /// Run a one-shot copilot call in a visible PTY session.
    /// Returns execution details including output captured to file while still streaming to PTY terminal.
    /// </summary>
    private async Task<V2ExecutionResult> ExecuteCopilotInPtyAsync(V2Run run, string prompt, string label, string? modelOverride = null)
    {
        var catalog = _roleConfigService.Load();
        var model = string.IsNullOrWhiteSpace(modelOverride)
            ? StandardExecutionModel
            : modelOverride;
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();

        var promptPath = CreatePromptFile(run.RunId, "main-thread", workspaceRoot, prompt);

        var copilotArguments = new List<string> { "-p", "$prompt", "--no-alt-screen", "--yolo" };

        copilotArguments.AddRange(["--model", model]);

        var outputPath = CreateMainThreadOutputFile(run, label);
        var (commandLine, commandPreview, inputScript) = BuildPtyLaunchScript(
            workspaceRoot, promptPath, outputPath, "copilot", copilotArguments, run.RunId, "main-thread");

        // Notify frontend about main thread activity
        await _hubContext.Clients.All.SendAsync("V2MainThreadActivity", run.RunId, label, "running");

        var sessionId = _ptyService.StartRawSession(commandLine, workspaceRoot, 120, 30);
        // Inject commands as keyboard input into the interactive pwsh session
        await _ptyService.SendInputAsync(sessionId, inputScript);
        run.MainThreadSessionId = sessionId;
        await BroadcastV2RunUpdatedAsync(run);

        // Tell frontend to attach xterm.js terminal to this session (include command preview)
        await _hubContext.Clients.All.SendAsync("V2MainThreadPtyStarted", run.RunId, sessionId, label, commandPreview);

        // Wait for the PTY session to complete
        var exitCode = await WaitForPtySessionAsync(sessionId, TimeSpan.FromMinutes(15));
        var outputText = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath) : string.Empty;

        await _hubContext.Clients.All.SendAsync("V2MainThreadActivity", run.RunId, label,
            exitCode == 0 ? "completed" : "failed");

        return new V2ExecutionResult(sessionId, exitCode, promptPath, outputPath, outputText, commandPreview, label);
    }

    // ── Command line building ──

    /// <summary>
    /// Write a .ps1 launch script and return (commandLine for ConPTY, commandPreview for UI).
    /// Uses direct invocation (no &amp; operator / no per-arg quoting) because
    /// copilot inside ConPTY does not parse individually-quoted arguments correctly.
    /// Only paths that contain spaces are quoted.
    /// </summary>
    private static (string commandLine, string commandPreview, string inputScript) BuildPtyLaunchScript(
        string workingDirectory, string promptFilePath, string outputFilePath, string executable,
        IReadOnlyList<string> arguments, string runId, string label)
    {
        // Render arguments: $prompt stays as-is, paths with spaces get single-quoted, rest bare
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

        // Write script file for audit/preview
        var scriptFolder = Path.Combine(workingDirectory, ".repoops", "prompts", runId, "v2", "scripts");
        Directory.CreateDirectory(scriptFolder);
        var scriptFileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{SanitizeLabel(label)}-{Guid.NewGuid().ToString("N")[..6]}.ps1";
        var scriptPath = Path.Combine(scriptFolder, scriptFileName);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        // Launch interactive pwsh — child processes see a real TTY,
        // enabling copilot's interactive permission prompts.
        var commandLine = "pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass";
        // Send full script content directly for better terminal readability/highlight,
        // while still writing the .ps1 file for audit and troubleshooting.
        var runScriptCommand = script.EndsWith("\r\n", StringComparison.Ordinal)
            ? script
            : script + "\r\n";
        return (commandLine, script, runScriptCommand);
    }

    /// <summary>Does this argument need single-quoting in a PowerShell invocation?</summary>
    private static bool NeedsQuoting(string arg) =>
        arg.Length == 0 || arg.Contains(' ') || arg.Contains('\'') || arg.Contains('(') || arg.Contains(')');

    private static List<string> LoadEffectiveAllowedPaths(V2Run run, string workspaceRoot)
    {
        var merged = new List<string>();

        foreach (var item in LoadPolicyEntries(run.AllowedPathsFile))
        {
            if (!merged.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(item);
            }
        }

        foreach (var item in LoadCopilotSettingsAllowedPaths(workspaceRoot))
        {
            if (!merged.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    private static List<string> LoadCopilotSettingsAllowedPaths(string workspaceRoot)
    {
        var result = new List<string>();
        var copilotDir = Path.Combine(workspaceRoot, ".github", "copilot");
        var settingsFiles = new[]
        {
            Path.Combine(copilotDir, "settings.local.json"),
            Path.Combine(copilotDir, "settings.json")
        };

        foreach (var file in settingsFiles)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                if (root.TryGetProperty("trusted_folders", out var trustedFolders)
                    && trustedFolders.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in trustedFolders.EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var normalized = NormalizeAllowedPath(workspaceRoot, element.GetString());
                        if (!string.IsNullOrWhiteSpace(normalized) && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                        {
                            result.Add(normalized);
                        }
                    }
                }

                if (root.TryGetProperty("repoops", out var repoops)
                    && repoops.ValueKind == JsonValueKind.Object)
                {
                    if (repoops.TryGetProperty("allowed_paths", out var allowedPaths)
                        && allowedPaths.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in allowedPaths.EnumerateArray())
                        {
                            if (element.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            var normalized = NormalizeAllowedPath(workspaceRoot, element.GetString());
                            if (!string.IsNullOrWhiteSpace(normalized) && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                            {
                                result.Add(normalized);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed settings files and fall back to txt policy.
            }
        }

        return result;
    }

    private static string? NormalizeAllowedPath(string workspaceRoot, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var raw = value.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var fullPath = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(workspaceRoot, raw));

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

    private static List<string> LoadPolicyEntries(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return [];
        }

        return File.ReadAllLines(filePath)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CreateMainThreadOutputFile(V2Run run, string label)
    {
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();
        var dir = Path.Combine(workspaceRoot, ".repoops", "prompts", run.RunId, "v2", "outputs", "main-thread");
        Directory.CreateDirectory(dir);
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{SanitizeLabel(label)}-{Guid.NewGuid().ToString("N")[..6]}.txt";
        return Path.Combine(dir, fileName);
    }

    private static string CreateWorkerOutputFile(V2Run run, V2Worker worker)
    {
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();
        var dir = Path.Combine(workspaceRoot, ".repoops", "prompts", run.RunId, "v2", "outputs", "workers", worker.RoleId);
        Directory.CreateDirectory(dir);
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{worker.WorkerId}-{Guid.NewGuid().ToString("N")[..6]}.txt";
        return Path.Combine(dir, fileName);
    }

    private static string GetV2ArtifactRoot(V2Run run)
    {
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();
        var dir = Path.Combine(workspaceRoot, ".repoops", "v2", "runs", run.RunId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetRoundArtifactDirectory(V2Run run)
    {
        var dir = Path.Combine(GetV2ArtifactRoot(run), "rounds", $"round-{Math.Max(1, run.CurrentRound):D3}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void EnsureRunArtifactScaffold(V2Run run)
    {
        var root = GetV2ArtifactRoot(run);
        Directory.CreateDirectory(Path.Combine(root, "rounds"));
        Directory.CreateDirectory(Path.Combine(root, "handoff"));
    }

    private static string BuildInitialPlanContext(V2Run run)
    {
        var roundOneDir = Path.Combine(GetV2ArtifactRoot(run), "rounds", "round-001");
        if (!Directory.Exists(roundOneDir))
        {
            return "首轮计划尚不存在，请优先依赖当前轮次工件和已落库决策。";
        }

        var lines = new List<string>
        {
            $"首轮目录：{roundOneDir}"
        };

        var planIndex = Path.Combine(roundOneDir, "plans", "plan-index.md");
        if (File.Exists(planIndex))
        {
            lines.Add($"首轮计划索引：{planIndex}");
        }

        var plansDir = Path.Combine(roundOneDir, "plans");
        if (Directory.Exists(plansDir))
        {
            foreach (var file in Directory.GetFiles(plansDir, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                         .Take(8))
            {
                lines.Add($"首轮计划文件：{file}");
            }
        }

        return string.Join("\n", lines);
    }

    private static string GetRoleOwnedArtifactPath(V2Run run, string roleId)
    {
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();
        var roundDir = GetRoundArtifactDirectory(run);

        if (string.Equals(roleId, "planner", StringComparison.OrdinalIgnoreCase))
        {
            var plansDir = Path.Combine(roundDir, "plans");
            Directory.CreateDirectory(plansDir);
            return Path.Combine(plansDir, "planner-output.md");
        }

        if (string.Equals(roleId, "reviewer", StringComparison.OrdinalIgnoreCase))
        {
            var reviewsDir = Path.Combine(roundDir, "reviews");
            Directory.CreateDirectory(reviewsDir);
            return Path.Combine(reviewsDir, "reviewer-review.md");
        }

        if (string.Equals(roleId, "builder", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(workspaceRoot, "builder-delivery.md");
        }

        var feedbackDir = Path.Combine(roundDir, "feedback");
        Directory.CreateDirectory(feedbackDir);
        return Path.Combine(feedbackDir, $"{SanitizeLabel(roleId)}-feedback.md");
    }

    private static string BuildRoleWritePolicy(string roleId, string workspaceRoot, string roundDirectory, string ownedArtifactPath)
    {
        if (string.Equals(roleId, "planner", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join(Environment.NewLine, new[]
            {
                "- 你是规划者，本轮只能修改计划工件，不能修改业务代码、业务配置、review 文件或 builder 交付说明。",
                $"- 你唯一允许落笔的主文件：{ownedArtifactPath}",
                $"- 允许写入目录仅限当前轮次 plans：{Path.Combine(roundDirectory, "plans")}",
                "- 你的计划必须明确：初级目标、中级目标、最终目标；若项目过大，必须拆阶段，禁止一次性要求编码者做完整项目。",
                "- 若当前阶段已具备收口条件，需在计划末尾明确建议主线程结束本轮并进入下一轮，并给出强理由。"
            });
        }

        if (string.Equals(roleId, "reviewer", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join(Environment.NewLine, new[]
            {
                "- 你是审查者，本轮只能写 review 文件，不能修改业务代码、planner 计划文件或 builder 交付说明。",
                $"- 你唯一允许落笔的主文件：{ownedArtifactPath}",
                $"- 允许写入目录仅限当前轮次 reviews：{Path.Combine(roundDirectory, "reviews")}",
                "- 你可以提出进入下一轮的建议，但必须给出足够强的证据和理由；若理由不足，主线程不应仅因你的建议而切轮。"
            });
        }

        if (string.Equals(roleId, "builder", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join(Environment.NewLine, new[]
            {
                "- 你是执行者，可以修改业务代码、业务配置和自己的交付说明；禁止修改 planner/reviewer 的专属文件。",
                $"- 你的交付说明文件：{ownedArtifactPath}",
                $"- 业务代码必须写在工作区业务目录中：{workspaceRoot}",
                "- 你不得改写当前轮次 plans/reviews 目录里的主文件；若需要异议，只能在自己的交付说明中记录。"
            });
        }

        return string.Join(Environment.NewLine, new[]
        {
            "- 你不是执行型角色，只能提供 comment / suggestion / feedback。",
            $"- 你唯一允许落笔的文件：{ownedArtifactPath}",
            $"- 允许写入目录仅限当前轮次 feedback：{Path.Combine(roundDirectory, "feedback")}",
            "- 禁止修改业务代码、计划文件、review 文件和 builder 交付说明。"
        });
    }

    private static string BuildRoundProgressContext(V2Run run)
    {
        var lines = new List<string>();
        var previousStageContext = BuildPreviousStageReferenceContext(run);

        if (run.CurrentRound <= 1)
        {
            lines.Add("首轮执行：当前 run 暂无历史轮次产物。");
            if (!string.IsNullOrWhiteSpace(previousStageContext))
            {
                lines.Add(previousStageContext);
            }

            return string.Join("\n", lines);
        }

        var prevRound = Math.Max(1, run.CurrentRound - 1);
        var prevRoundDir = Path.Combine(GetV2ArtifactRoot(run), "rounds", $"round-{prevRound:D3}");
        if (!Directory.Exists(prevRoundDir))
        {
            lines.Add($"第 {prevRound} 轮目录不存在：{prevRoundDir}");
            if (!string.IsNullOrWhiteSpace(previousStageContext))
            {
                lines.Add(previousStageContext);
            }

            return string.Join("\n", lines);
        }

        lines.Add($"上一轮目录：{prevRoundDir}");

        var planIndex = Path.Combine(prevRoundDir, "plans", "plan-index.md");
        if (File.Exists(planIndex))
        {
            lines.Add($"上一轮计划索引：{planIndex}");
        }

        var workerDir = Path.Combine(prevRoundDir, "workers");
        if (Directory.Exists(workerDir))
        {
            foreach (var file in Directory.GetFiles(workerDir, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                         .Take(12))
            {
                lines.Add($"上一轮 worker 报告：{file}");
            }
        }

        if (!string.IsNullOrWhiteSpace(previousStageContext))
        {
            lines.Add(previousStageContext);
        }

        return string.Join("\n", lines);
    }

    private static string BuildPreviousStageReferenceContext(V2Run run)
    {
        if (string.IsNullOrWhiteSpace(run.RelatedPreviousRunId)
            || string.IsNullOrWhiteSpace(run.RelatedPreviousRunSummaryPath)
            || !File.Exists(run.RelatedPreviousRunSummaryPath))
        {
            return "未自动关联上一阶段记录。";
        }

        try
        {
            var summary = File.ReadAllText(run.RelatedPreviousRunSummaryPath);
            summary = TrimForPrompt(summary, 2200);
            return string.Join("\n", new[]
            {
                "## 自动关联的上一阶段摘要（仅在目标明显相关时参考）",
                $"- 来源 runId：{run.RelatedPreviousRunId}",
                $"- 摘要文件：{run.RelatedPreviousRunSummaryPath}",
                $"- 相关度分数：{run.RelatedPreviousRunScore:0.00}",
                "- 这份记录只是交接摘要，不等于当前轮事实；你仍然必须以当前工作区现有文件与本 run 工件为准。",
                summary
            });
        }
        catch
        {
            return "上一阶段摘要存在但读取失败，请仅依赖当前 run 工件。";
        }
    }

    private static void TryAttachRelatedPreviousStage(V2Run run)
    {
        if (string.IsNullOrWhiteSpace(run.WorkspaceRoot))
        {
            return;
        }

        var runsRoot = Path.Combine(run.WorkspaceRoot, ".repoops", "v2", "runs");
        if (!Directory.Exists(runsRoot))
        {
            return;
        }

        var currentNormalized = NormalizeStageReferenceText($"{run.Title}\n{run.Goal}");
        var currentTokens = ExtractStageReferenceTokens(currentNormalized);
        if (currentTokens.Count == 0 && string.IsNullOrWhiteSpace(currentNormalized))
        {
            return;
        }

        PreviousStageCandidate? best = null;
        foreach (var candidateDir in Directory.GetDirectories(runsRoot))
        {
            var candidateRunId = Path.GetFileName(candidateDir);
            if (string.Equals(candidateRunId, run.RunId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var summaryJsonPath = Path.Combine(candidateDir, "handoff", "stage-summary.json");
            var summaryMarkdownPath = Path.Combine(candidateDir, "handoff", "stage-summary.md");
            if (!File.Exists(summaryJsonPath) || !File.Exists(summaryMarkdownPath))
            {
                continue;
            }

            var candidate = TryReadPreviousStageCandidate(summaryJsonPath, summaryMarkdownPath);
            if (candidate is null)
            {
                continue;
            }

            var score = ComputeStageReferenceScore(currentNormalized, currentTokens, candidate.Value.MatchText);
            if (score < 0.22d)
            {
                continue;
            }

            var current = new PreviousStageCandidate(candidate.Value.RunId, summaryMarkdownPath, candidate.Value.UpdatedAtUtc, score);
            if (best is null
                || current.Score > best.Value.Score
                || (Math.Abs(current.Score - best.Value.Score) < 0.0001d && current.UpdatedAtUtc > best.Value.UpdatedAtUtc))
            {
                best = current;
            }
        }

        if (best is null)
        {
            return;
        }

        run.RelatedPreviousRunId = best.Value.RunId;
        run.RelatedPreviousRunSummaryPath = best.Value.SummaryPath;
        run.RelatedPreviousRunScore = Math.Round(best.Value.Score, 4);
    }

    private static (string RunId, string MatchText, DateTime UpdatedAtUtc)? TryReadPreviousStageCandidate(string summaryJsonPath, string summaryMarkdownPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(summaryJsonPath));
            var root = doc.RootElement;
            var runId = root.TryGetProperty("runId", out var runIdElement) ? runIdElement.GetString() : null;
            var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
            var goal = root.TryGetProperty("goal", out var goalElement) ? goalElement.GetString() : null;
            var latestSummary = root.TryGetProperty("latestSummary", out var latestSummaryElement) ? latestSummaryElement.GetString() : null;
            var updatedAtUtc = root.TryGetProperty("updatedAtUtc", out var updatedAtElement) && updatedAtElement.ValueKind == JsonValueKind.String
                && DateTime.TryParse(updatedAtElement.GetString(), out var parsedUpdatedAt)
                ? parsedUpdatedAt
                : File.GetLastWriteTimeUtc(summaryMarkdownPath);

            if (string.IsNullOrWhiteSpace(runId))
            {
                return null;
            }

            var matchText = string.Join("\n", new[]
            {
                title,
                goal,
                latestSummary
            }.Where(item => !string.IsNullOrWhiteSpace(item)));

            return (runId!, matchText, updatedAtUtc);
        }
        catch
        {
            return null;
        }
    }

    private static double ComputeStageReferenceScore(string currentNormalized, HashSet<string> currentTokens, string candidateText)
    {
        var candidateNormalized = NormalizeStageReferenceText(candidateText);
        var candidateTokens = ExtractStageReferenceTokens(candidateNormalized);

        var unionCount = currentTokens.Union(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
        var intersectionCount = currentTokens.Intersect(candidateTokens, StringComparer.OrdinalIgnoreCase).Count();
        var tokenScore = unionCount == 0 ? 0d : (double)intersectionCount / unionCount;

        var containsBonus = 0d;
        if (!string.IsNullOrWhiteSpace(currentNormalized)
            && !string.IsNullOrWhiteSpace(candidateNormalized)
            && (candidateNormalized.Contains(currentNormalized, StringComparison.OrdinalIgnoreCase)
                || currentNormalized.Contains(candidateNormalized, StringComparison.OrdinalIgnoreCase)))
        {
            containsBonus = 0.25d;
        }

        return Math.Min(1d, tokenScore + containsBonus);
    }

    private static string NormalizeStageReferenceText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || (ch >= '\u4e00' && ch <= '\u9fff'))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().Trim();
    }

    private static HashSet<string> ExtractStageReferenceTokens(string normalized)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return tokens;
        }

        foreach (Match match in StageAsciiTokenRegex.Matches(normalized))
        {
            tokens.Add(match.Value);
        }

        foreach (Match match in StageCjkSegmentRegex.Matches(normalized))
        {
            var segment = match.Value;
            if (segment.Length == 2)
            {
                tokens.Add(segment);
                continue;
            }

            for (var i = 0; i < segment.Length - 1; i++)
            {
                tokens.Add(segment.Substring(i, 2));
            }
        }

        return tokens;
    }

    private static void WriteRunStateArtifacts(V2Run run)
    {
        EnsureRunArtifactScaffold(run);
        var runRoot = GetV2ArtifactRoot(run);
        var handoffDir = Path.Combine(runRoot, "handoff");
        Directory.CreateDirectory(handoffDir);

        var latestRound = run.Rounds.OrderByDescending(item => item.RoundNumber).FirstOrDefault();
        var latestSummary = latestRound?.MainThreadSummary
                            ?? run.Decisions.LastOrDefault()?.Summary
                            ?? string.Empty;
        var recentDecisions = run.Decisions
            .TakeLast(8)
            .Select(item => new
            {
                item.Kind,
                item.Summary,
                item.CreatedAt
            })
            .ToList();
        var workerStates = run.Workers
            .Select(worker => new
            {
                worker.WorkerId,
                worker.RoleId,
                worker.RoleName,
                worker.Status,
                worker.ExitCode,
                worker.AssignedRound,
                worker.LastOutputPreview
            })
            .ToList();

        var summaryPayload = new
        {
            runId = run.RunId,
            title = run.Title,
            goal = run.Goal,
            status = run.Status,
            currentRound = run.CurrentRound,
            maxRounds = run.MaxRounds,
            workspaceRoot = run.WorkspaceRoot,
            executionRoot = run.ExecutionRoot,
            relatedPreviousRunId = run.RelatedPreviousRunId,
            relatedPreviousRunSummaryPath = run.RelatedPreviousRunSummaryPath,
            relatedPreviousRunScore = run.RelatedPreviousRunScore,
            latestSummary,
            latestRound = latestRound is null ? null : new
            {
                latestRound.RoundNumber,
                latestRound.Phase,
                latestRound.MainThreadSummary,
                latestRound.ReviewerVerdict,
                latestRound.AllReportedDone,
                latestRound.ReviewPassed,
                latestRound.StartedAt,
                latestRound.CompletedAt
            },
            recentDecisions,
            workerStates,
            createdAtUtc = run.CreatedAt,
            updatedAtUtc = run.UpdatedAt
        };

        File.WriteAllText(
            Path.Combine(runRoot, "run-state.json"),
            JsonSerializer.Serialize(summaryPayload, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        File.WriteAllText(
            Path.Combine(handoffDir, "stage-summary.json"),
            JsonSerializer.Serialize(summaryPayload, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        var md = new StringBuilder();
        md.AppendLine($"# Run {run.RunId} Stage Summary");
        md.AppendLine();
        md.AppendLine($"- 标题：{run.Title}");
        md.AppendLine($"- 目标：{run.Goal}");
        md.AppendLine($"- 状态：{run.Status}");
        md.AppendLine($"- 当前轮次：{run.CurrentRound}/{run.MaxRounds}");
        md.AppendLine($"- 工作区：{run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir()}");
        if (!string.IsNullOrWhiteSpace(run.RelatedPreviousRunId))
        {
            md.AppendLine($"- 自动继承来源：{run.RelatedPreviousRunId}（score={run.RelatedPreviousRunScore:0.00}）");
        }

        md.AppendLine();
        md.AppendLine("## 最新阶段结论");
        md.AppendLine();
        md.AppendLine(string.IsNullOrWhiteSpace(latestSummary) ? "(empty)" : latestSummary.Trim());
        md.AppendLine();
        md.AppendLine("## 最近决策");
        md.AppendLine();
        foreach (var item in recentDecisions)
        {
            md.AppendLine($"- [{item.Kind}] {item.Summary}");
        }

        md.AppendLine();
        md.AppendLine("## 当前角色状态");
        md.AppendLine();
        foreach (var worker in run.Workers.OrderBy(item => item.RoleName, StringComparer.OrdinalIgnoreCase))
        {
            md.AppendLine($"- {worker.RoleName} ({worker.RoleId}) · status={worker.Status} · round={worker.AssignedRound} · exit={worker.ExitCode}");
            if (!string.IsNullOrWhiteSpace(worker.LastOutputPreview))
            {
                md.AppendLine($"  - 摘录：{TrimForPrompt(worker.LastOutputPreview, 240)}");
            }
        }

        File.WriteAllText(Path.Combine(handoffDir, "stage-summary.md"), md.ToString(), new UTF8Encoding(false));
    }

    private static string TrimForPrompt(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "…";
    }

    private static void WriteRoundPlanArtifact(V2Run run, V2ExecutionResult execution, RoundPlan plan)
    {
        var roundDir = GetRoundArtifactDirectory(run);
        var path = Path.Combine(roundDir, "main-plan.json");
        var payload = JsonSerializer.Serialize(new
        {
            runId = run.RunId,
            round = run.CurrentRound,
            label = execution.Label,
            execution.ExitCode,
            execution.CommandPreview,
            execution.PromptPath,
            execution.OutputPath,
            rawOutput = execution.OutputText,
            plan
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, payload, new UTF8Encoding(false));
    }

    private static void WriteRoundPlanMarkdownArtifacts(V2Run run, RoundPlan plan)
    {
        var roundDir = GetRoundArtifactDirectory(run);
        var plansDir = Path.Combine(roundDir, "plans");
        Directory.CreateDirectory(plansDir);

        var indexPath = Path.Combine(plansDir, "plan-index.md");
        var indexBuilder = new StringBuilder();
        indexBuilder.AppendLine($"# Round {Math.Max(1, run.CurrentRound)} Plan");
        indexBuilder.AppendLine();
        indexBuilder.AppendLine($"- RunId: {run.RunId}");
        indexBuilder.AppendLine($"- Goal: {run.Goal}");
        indexBuilder.AppendLine($"- GeneratedAt: {DateTime.UtcNow:O}");
        indexBuilder.AppendLine();
        indexBuilder.AppendLine("## Summary");
        indexBuilder.AppendLine();
        indexBuilder.AppendLine(string.IsNullOrWhiteSpace(plan.Summary) ? "(empty)" : plan.Summary.Trim());
        indexBuilder.AppendLine();
        indexBuilder.AppendLine("## Assignments");
        indexBuilder.AppendLine();

        plan.AssignmentMarkdownPaths.Clear();
        for (var i = 0; i < plan.Assignments.Count; i++)
        {
            var assignment = plan.Assignments[i];
            var roleId = string.IsNullOrWhiteSpace(assignment.RoleId) ? $"role-{i + 1}" : assignment.RoleId.Trim();
            var safeRoleId = SanitizeLabel(roleId);
            if (string.IsNullOrWhiteSpace(safeRoleId))
            {
                safeRoleId = $"role-{i + 1}";
            }

            var taskFileName = $"task-{i + 1:D2}-{safeRoleId}.md";
            var taskPath = Path.Combine(plansDir, taskFileName);

            var taskBuilder = new StringBuilder();
            taskBuilder.AppendLine($"# Assignment {i + 1}: {assignment.RoleId}");
            taskBuilder.AppendLine();
            taskBuilder.AppendLine($"- Round: {Math.Max(1, run.CurrentRound)}");
            taskBuilder.AppendLine($"- RoleId: {assignment.RoleId}");
            taskBuilder.AppendLine($"- Goal: {run.Goal}");
            taskBuilder.AppendLine();
            taskBuilder.AppendLine("## Task");
            taskBuilder.AppendLine();
            taskBuilder.AppendLine(string.IsNullOrWhiteSpace(assignment.Task) ? "(empty)" : assignment.Task.Trim());
            File.WriteAllText(taskPath, taskBuilder.ToString(), new UTF8Encoding(false));

            plan.AssignmentMarkdownPaths[roleId] = taskPath;
            indexBuilder.AppendLine($"- `{assignment.RoleId}` → `{taskPath}`");
        }

        File.WriteAllText(indexPath, indexBuilder.ToString(), new UTF8Encoding(false));
        plan.PlanIndexMarkdownPath = indexPath;
    }

    private static void WriteRoundReviewArtifact(V2Run run, V2ExecutionResult execution, ReviewResult review)
    {
        var roundDir = GetRoundArtifactDirectory(run);
        var path = Path.Combine(roundDir, "review.json");
        var payload = JsonSerializer.Serialize(new
        {
            runId = run.RunId,
            round = run.CurrentRound,
            label = execution.Label,
            execution.ExitCode,
            execution.CommandPreview,
            execution.PromptPath,
            execution.OutputPath,
            rawOutput = execution.OutputText,
            review
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, payload, new UTF8Encoding(false));
    }

    private static void WriteWorkerRoundArtifacts(V2Run run, IEnumerable<V2Worker> workers)
    {
        var roundDir = GetRoundArtifactDirectory(run);
        var workersDir = Path.Combine(roundDir, "workers");
        Directory.CreateDirectory(workersDir);

        foreach (var worker in workers)
        {
            var rawOutput = !string.IsNullOrWhiteSpace(worker.OutputFilePath) && File.Exists(worker.OutputFilePath)
                ? File.ReadAllText(worker.OutputFilePath)
                : string.Empty;
            var artifact = new StringBuilder();
            artifact.AppendLine($"# Worker {worker.RoleName}");
            artifact.AppendLine();
            artifact.AppendLine($"- WorkerId: {worker.WorkerId}");
            artifact.AppendLine($"- RoleId: {worker.RoleId}");
            artifact.AppendLine($"- Status: {worker.Status}");
            artifact.AppendLine($"- ExitCode: {worker.ExitCode}");
            artifact.AppendLine($"- Prompt: {worker.LastPrompt}");
            artifact.AppendLine($"- CommandPreview: {worker.CommandPreview}");
            artifact.AppendLine($"- OutputFile: {worker.OutputFilePath}");
            artifact.AppendLine();
            artifact.AppendLine("## Raw Output");
            artifact.AppendLine();
            artifact.AppendLine("```text");
            artifact.AppendLine(string.IsNullOrWhiteSpace(rawOutput) ? "<empty>" : rawOutput.TrimEnd());
            artifact.AppendLine("```");

            var fileName = $"{worker.RoleId}-{worker.WorkerId}.md";
            var path = Path.Combine(workersDir, fileName);
            File.WriteAllText(path, artifact.ToString(), new UTF8Encoding(false));
        }
    }

    private static RoundPlan? TryParseRoundPlan(string rawOutput, IReadOnlyCollection<AgentRoleDefinition> roles)
    {
        var json = ExtractJsonObject(rawOutput);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RoundPlanJson>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed is null)
            {
                return null;
            }

            var roleSet = roles.Select(role => role.RoleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var plan = new RoundPlan
            {
                Summary = string.IsNullOrWhiteSpace(parsed.Summary)
                    ? "Planning parsed with empty summary."
                    : parsed.Summary.Trim()
            };

            foreach (var assignment in parsed.Assignments ?? [])
            {
                if (string.IsNullOrWhiteSpace(assignment.RoleId) || string.IsNullOrWhiteSpace(assignment.Task))
                {
                    continue;
                }

                if (!roleSet.Contains(assignment.RoleId.Trim()))
                {
                    continue;
                }

                plan.Assignments.Add(new RoundAssignment
                {
                    RoleId = assignment.RoleId.Trim(),
                    Task = assignment.Task.Trim()
                });
            }

            return plan.Assignments.Count == 0 ? null : plan;
        }
        catch
        {
            return null;
        }
    }

    private static ReviewResult? TryParseReviewResult(string rawOutput)
    {
        var json = ExtractJsonObject(rawOutput);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ReviewResultJson>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (parsed is null)
            {
                return null;
            }

            return new ReviewResult
            {
                OverallStatus = string.IsNullOrWhiteSpace(parsed.OverallStatus) ? "continue" : parsed.OverallStatus.Trim().ToLowerInvariant(),
                Summary = parsed.Summary?.Trim() ?? string.Empty,
                Issues = (parsed.Issues ?? [])
                    .Where(issue => !string.IsNullOrWhiteSpace(issue))
                    .Select(issue => issue.Trim())
                    .ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }


    private static string CreatePromptFile(string runId, string label, string workspaceRoot, string prompt)
    {
        var folderPath = Path.Combine(workspaceRoot, ".repoops", "prompts", runId, "v2", label);
        Directory.CreateDirectory(folderPath);
        var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}.prompt.md";
        var promptPath = Path.Combine(folderPath, fileName);
        File.WriteAllText(promptPath, prompt, new UTF8Encoding(false));
        return promptPath;
    }

    private static string QuotePowerShellLiteral(string? value) =>
        string.IsNullOrEmpty(value) ? "''" : $"'{value.Replace("'", "''")}'";

    private static string SanitizeLabel(string label)
    {
        var sb = new StringBuilder();
        foreach (var ch in label)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    // ── Helpers ──

    private V2Run RequireRun(string runId) =>
        _runs.TryGetValue(runId, out var r) ? r : throw new InvalidOperationException($"V2 Run '{runId}' not found.");

    private void AddDecision(V2Run run, string kind, string summary)
    {
        run.Decisions.Add(new V2Decision { Kind = kind, Summary = summary });
        if (run.Decisions.Count > 40)
            run.Decisions = run.Decisions[^40..];
        run.UpdatedAt = DateTime.UtcNow;
    }

    private async Task BroadcastV2RunUpdatedAsync(V2Run run)
    {
        WriteRunStateArtifacts(run);
        await _hubContext.Clients.All.SendAsync("V2RunUpdated", run);
    }

    private static string ResolveWorkspace(SupervisorSettings settings, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var p = Path.GetFullPath(configuredPath.Trim());
            if (Directory.Exists(p)) return p;
            throw new InvalidOperationException($"Workspace '{p}' does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultWorkspaceRoot))
        {
            var p = Path.GetFullPath(settings.DefaultWorkspaceRoot);
            if (Directory.Exists(p)) return p;
        }

        return AgentRoleConfigService.GetBaseDir();
    }

    private static string CreateTitle(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return $"V2 Run {DateTime.Now:HHmmss}";
        var t = goal.Trim();
        return t.Length <= 48 ? t : t[..48] + "…";
    }

    private static string TrimOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;
        var t = output.Trim();
        return t.Length <= 6000 ? t : t[^6000..];
    }

    // ── Usage stats parsing ──

    private static readonly Regex UsageBlockRegex = new(
        @"Total usage est:\s*(?<usage>.+?)\nAPI time spent:\s*(?<api>.+?)\nTotal session time:\s*(?<session>.+?)\nTotal code changes:\s*(?<changes>.+?)\nBreakdown by AI model:\s*\n(?<breakdown>(?:\s+\S.+\n?)+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex ModelLineRegex = new(
        @"^\s+(?<model>\S+)\s+(?<detail>.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static V2UsageStats? ParseUsageStats(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var match = UsageBlockRegex.Match(output);
        if (!match.Success) return null;

        var stats = new V2UsageStats
        {
            TotalUsage = match.Groups["usage"].Value.Trim(),
            ApiTime = match.Groups["api"].Value.Trim(),
            SessionTime = match.Groups["session"].Value.Trim(),
            CodeChanges = match.Groups["changes"].Value.Trim(),
            RawBlock = match.Value.Trim()
        };

        var breakdownText = match.Groups["breakdown"].Value;
        foreach (Match ml in ModelLineRegex.Matches(breakdownText))
        {
            stats.ModelBreakdown.Add(new V2ModelUsage
            {
                Model = ml.Groups["model"].Value.Trim(),
                Detail = ml.Groups["detail"].Value.Trim()
            });
        }

        return stats;
    }

    private static void WriteUsageLog(V2Run run, IEnumerable<V2Worker> workers)
    {
        var logPath = Path.Combine(GetV2ArtifactRoot(run), "usage-log.md");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var sb = new StringBuilder();
        foreach (var worker in workers)
        {
            var usage = worker.UsageStats;
            sb.AppendLine($"## Round {run.CurrentRound} - {worker.RoleName} ({worker.RoleId})");
            sb.AppendLine();
            sb.AppendLine($"| 字段 | 值 |");
            sb.AppendLine($"| --- | --- |");
            sb.AppendLine($"| 轮次 | {run.CurrentRound} |");
            sb.AppendLine($"| 角色 | {worker.RoleName} ({worker.RoleId}) |");
            sb.AppendLine($"| WorkerId | {worker.WorkerId} |");
            sb.AppendLine($"| 退出码 | {worker.ExitCode} |");
            sb.AppendLine($"| 状态 | {worker.Status} |");

            if (usage is not null)
            {
                sb.AppendLine($"| Premium Requests | {usage.TotalUsage} |");
                sb.AppendLine($"| API 时间 | {usage.ApiTime} |");
                sb.AppendLine($"| 会话时间 | {usage.SessionTime} |");
                sb.AppendLine($"| 代码变更 | {usage.CodeChanges} |");
                foreach (var m in usage.ModelBreakdown)
                {
                    sb.AppendLine($"| 模型: {m.Model} | {m.Detail} |");
                }
            }
            else
            {
                sb.AppendLine($"| 资源统计 | (未解析到) |");
            }

            sb.AppendLine();
        }

        File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteMainThreadUsageLog(V2Run run, string label, V2ExecutionResult execution)
    {
        var usage = ParseUsageStats(execution.OutputText);
        if (usage is null) return;

        var logPath = Path.Combine(GetV2ArtifactRoot(run), "usage-log.md");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var sb = new StringBuilder();
        sb.AppendLine($"## Round {run.CurrentRound} - {label}");
        sb.AppendLine();
        sb.AppendLine($"| 字段 | 值 |");
        sb.AppendLine($"| --- | --- |");
        sb.AppendLine($"| 轮次 | {run.CurrentRound} |");
        sb.AppendLine($"| 角色 | {label} |");
        sb.AppendLine($"| 退出码 | {execution.ExitCode} |");
        sb.AppendLine($"| Premium Requests | {usage.TotalUsage} |");
        sb.AppendLine($"| API 时间 | {usage.ApiTime} |");
        sb.AppendLine($"| 会话时间 | {usage.SessionTime} |");
        sb.AppendLine($"| 代码变更 | {usage.CodeChanges} |");
        foreach (var m in usage.ModelBreakdown)
        {
            sb.AppendLine($"| 模型: {m.Model} | {m.Detail} |");
        }
        sb.AppendLine();

        File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(false));
    }

    // ── Internal DTOs ──

    private sealed class RoundPlan
    {
        public string Summary { get; set; } = string.Empty;
        public List<RoundAssignment> Assignments { get; set; } = [];
        public string? PlanIndexMarkdownPath { get; set; }
        public Dictionary<string, string> AssignmentMarkdownPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RoundAssignment
    {
        public string RoleId { get; set; } = string.Empty;
        public string Task { get; set; } = string.Empty;
    }

    private sealed class RoundPlanJson
    {
        public string? Summary { get; set; }
        public List<RoundAssignmentJson>? Assignments { get; set; }
    }

    private sealed class RoundAssignmentJson
    {
        public string? RoleId { get; set; }
        public string? Task { get; set; }
    }

    private sealed class ReviewResult
    {
        public string OverallStatus { get; set; } = "continue";
        public string Summary { get; set; } = string.Empty;
        public List<string> Issues { get; set; } = [];
    }

    private sealed class ReviewResultJson
    {
        public string? OverallStatus { get; set; }
        public string? Summary { get; set; }
        public List<string>? Issues { get; set; }
    }

    private sealed record V2ExecutionResult(
        string SessionId,
        int ExitCode,
        string PromptPath,
        string OutputPath,
        string OutputText,
        string CommandPreview,
        string Label);

    private readonly record struct PreviousStageCandidate(
        string RunId,
        string SummaryPath,
        DateTime UpdatedAtUtc,
        double Score);
}
