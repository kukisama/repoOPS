using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RepoOPS.Agents.Models;

namespace RepoOPS.Agents.Services;

public sealed partial class AgentSupervisorService
{
    public async Task<SupervisorRun> CreateRunAsync(CreateSupervisorRunRequest request)
    {
        var roleCatalog = _roleConfigService.Load();
        var settings = roleCatalog.Settings ?? new SupervisorSettings();
        var selectedRoleIds = (request.RoleIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roles = roleCatalog.Roles
            .Where(role => selectedRoleIds.Count == 0 || selectedRoleIds.Contains(role.RoleId))
            .ToList();

        if (roles.Count == 0)
        {
            throw new InvalidOperationException("At least one valid role must be selected.");
        }

        var goal = request.Goal.Trim();
        var executionRoot = ResolveExecutionRoot(settings, request.WorkspaceRoot);
        var workspaceBootstrap = string.IsNullOrWhiteSpace(request.WorkspaceRoot)
            ? InitializeTaskWorkspace(executionRoot, goal, request.WorkspaceName)
            : UseManualWorkspace(executionRoot);
        var additionalDirectories = ExtractReferencedDirectories([goal], workspaceBootstrap.ExecutionRoot, workspaceBootstrap.WorkspacePath);
        var run = new SupervisorRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(request.Title) ? CreateTitleFromGoal(goal) : request.Title.Trim(),
            Goal = goal,
            Status = "draft",
            RoundNumber = 0,
            ExecutionRoot = workspaceBootstrap.ExecutionRoot,
            WorkspaceRoot = workspaceBootstrap.WorkspacePath,
            WorkspaceName = workspaceBootstrap.WorkspaceName,
            AdditionalAllowedDirectories = additionalDirectories,
            RoundHistoryDocumentPath = Path.Combine(workspaceBootstrap.WorkspacePath, "repoops-round-history.md"),
            UsesManualWorkspaceRoot = workspaceBootstrap.UsesManualWorkspaceRoot,
            AutoPilotEnabled = request.AutoPilotEnabled,
            MaxAutoSteps = request.MaxAutoSteps > 0 ? request.MaxAutoSteps : settings.DefaultMaxAutoSteps,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Workers = roles.Select((role, index) => new AgentWorkerSession
            {
                WorkerId = Guid.NewGuid().ToString("N"),
                RoleId = role.RoleId,
                RoleName = role.Name,
                RoleDescription = role.Description,
                Icon = role.Icon,
                WorkspacePath = ResolveWorkspacePath(workspaceBootstrap.WorkspacePath, role.WorkspacePath),
                CurrentTask = role.Description,
                Status = "idle",
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }).ToList()
        };

        run = AddDecision(run, "run-created", $"Created run with {roles.Count} roles in workspace '{run.WorkspaceRoot}'.");
        EnsureRoundHistoryDocument(run);
        EnsureRunLayout(run, settings);
        RecalculateAttentionAggregates(run, settings);
        PersistRun(run);
        await BroadcastRunUpdatedAsync(run);

        if (request.AutoStart)
        {
            foreach (var worker in run.Workers)
            {
                await StartWorkerAsync(run.RunId, worker.WorkerId, null);
            }

            return RequireRun(run.RunId);
        }

        return run;
    }

    public async Task<RoleProposalResponse> ProposeRolesAsync(RoleProposalRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Goal))
        {
            throw new InvalidOperationException("Goal is required for role proposal.");
        }

        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var executionRoot = ResolveExecutionRoot(settings, request.WorkspaceRoot);
        var tempRun = new SupervisorRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            Title = CreateTitleFromGoal(request.Goal),
            Goal = request.Goal.Trim(),
            ExecutionRoot = executionRoot,
            WorkspaceRoot = executionRoot,
            WorkspaceName = string.Empty,
            AdditionalAllowedDirectories = ExtractReferencedDirectories([request.Goal], executionRoot, executionRoot),
            Workers = catalog.Roles.Select(role => new AgentWorkerSession
            {
                WorkerId = role.RoleId,
                RoleId = role.RoleId,
                RoleName = role.Name,
                RoleDescription = role.Description,
                CurrentTask = role.Description,
                Status = "idle"
            }).ToList()
        };

        RoleProposalResponse? proposal = null;
        try
        {
            var prompt = BuildRoleProposalPrompt(tempRun, catalog.Roles, settings);
            var result = await ExecuteOneShotAsync(prompt, tempRun, "AI 正在生成角色分工建议");
            proposal = TryParseRoleProposal(result.Output, catalog.Roles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Role proposal generation failed; using fallback roles.");
        }

        proposal ??= BuildFallbackRoleProposal(tempRun.Goal, catalog.Roles);
        proposal.RecommendedWorkspaceName = BuildWorkspaceName(tempRun.Goal, proposal.RecommendedWorkspaceName);
        proposal.ExistingRoles = proposal.ExistingRoles
            .Where(item => catalog.Roles.Any(role => string.Equals(role.RoleId, item.RoleId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        proposal.NewRoles = NormalizeDraftRoles(proposal.NewRoles, settings.DefaultModel);
        return proposal;
    }

    public async Task<SupervisorRun> StartWorkerAsync(string runId, string workerId, string? prompt)
    {
        var run = RequireRun(runId);
        var worker = RequireWorker(run, workerId);
        var roleCatalog = _roleConfigService.Load();
        var settings = roleCatalog.Settings ?? new SupervisorSettings();
        SyncAssistantExecutionState(run);

        if (_activeProcesses.ContainsKey(worker.WorkerId))
        {
            throw new InvalidOperationException($"Worker '{worker.RoleName}' is already running.");
        }

        if (settings.MaxConcurrentWorkers > 0 && _activeProcesses.Count >= settings.MaxConcurrentWorkers)
        {
            throw new InvalidOperationException($"Maximum concurrent workers limit ({settings.MaxConcurrentWorkers}) reached. Stop a running worker first.");
        }

        if (!TryValidateAssistantExecutionEligibility(run, worker, out var assistantBlockReason))
        {
            RaiseAttention(run, settings, GetWorkerSurfaceId(worker.WorkerId), worker.WorkerId, "assistant-round-blocked", "warning", $"{worker.RoleName} 不在当前轮次", assistantBlockReason ?? "当前轮次未安排该角色执行。");
            run = AddDecision(run, "assistant-round-blocked", assistantBlockReason ?? $"Blocked worker '{worker.RoleName}' because it is not scheduled in the current assistant round.");
            PersistRun(run);
            await BroadcastRunUpdatedAsync(run);
            throw new InvalidOperationException(assistantBlockReason);
        }

        var role = RequireRole(roleCatalog.Roles, worker.RoleId);
        var workspaceRoot = string.IsNullOrWhiteSpace(run.WorkspaceRoot) ? FindWorkspaceRoot(settings) : run.WorkspaceRoot!;
        var workspacePath = string.IsNullOrWhiteSpace(worker.WorkspacePath)
            ? ResolveWorkspacePath(workspaceRoot, role.WorkspacePath)
            : worker.WorkspacePath!;
        var effectivePrompt = string.IsNullOrWhiteSpace(prompt)
            ? BuildInitialPrompt(run, worker, role)
            : prompt.Trim();
        effectivePrompt = ApplyAssistantExecutionPromptGuardrails(run, worker, effectivePrompt);
        MergeAdditionalAllowedDirectories(run, workspaceRoot, effectivePrompt);
        var promptArtifact = CreateWorkerPromptArtifact(run, worker, workspaceRoot, effectivePrompt);
        var launchPlan = BuildWorkerLaunchPlan(run, worker, role, promptArtifact, workspaceRoot, settings);

        worker.Status = "running";
        worker.WorkspacePath = workspacePath;
        worker.CurrentTask = string.IsNullOrWhiteSpace(worker.AssistantAssignedRoundTitle)
            ? worker.CurrentTask
            : $"第 {worker.AssistantAssignedRoundNumber} 轮 · {worker.AssistantAssignedRoundTitle} · {(worker.AssistantRoleMode ?? "participant")}";
        worker.LastPrompt = effectivePrompt;
        worker.EffectiveCommandPreview = launchPlan.CommandPreview;
        worker.StartedAt = DateTime.UtcNow;
        worker.UpdatedAt = DateTime.UtcNow;
        worker.ExitCode = null;
        worker.HasStructuredReport = false;
        worker.LastReportedStatus = null;
        worker.LastNextStep = null;
        worker.LastOutputPreview = string.Empty;
        worker.LastSummary = null;
        worker.NeedsAttention = false;
        worker.UnreadCount = 0;
        worker.AttentionLevel = null;
        worker.LastAttentionMessage = null;
        worker.LastSurfaceActivityAt = DateTime.UtcNow;
        run.Status = "running";
        run = AddDecision(run, "worker-started", $"Started {worker.RoleName} session.");
        EnsureRunLayout(run, settings);
        RecalculateAttentionAggregates(run, settings);
        PersistRun(run);

        var process = launchPlan.CreateProcess();
        var handle = new WorkerProcessHandle(process, new StringBuilder());
        _activeProcesses[worker.WorkerId] = handle;

        await _hubContext.Clients.All.SendAsync("AgentWorkerStatusChanged", run.RunId, worker.WorkerId, worker.Status);
        await _hubContext.Clients.All.SendAsync("AgentWorkerStarted", run.RunId, worker.WorkerId, worker.RoleName);
        await BroadcastRunUpdatedAsync(run);

        _ = Task.Run(() => ExecuteWorkerProcessAsync(run.RunId, worker.WorkerId, process, handle));
        return RequireRun(runId);
    }

    public async Task<SupervisorRun> ContinueWorkerAsync(string runId, string workerId, string? prompt)
    {
        var run = RequireRun(runId);
        var worker = RequireWorker(run, workerId);
        var followUpPrompt = string.IsNullOrWhiteSpace(prompt)
            ? $"继续推进当前目标：{run.Goal}。请基于你已有会话上下文继续工作，必要时自行检查代码和命令结果。回答最后请用 STATUS / SUMMARY / NEXT 三行收尾。"
            : prompt.Trim();

        return await StartWorkerAsync(runId, worker.WorkerId, followUpPrompt);
    }

    public async Task<SupervisorRun> StopWorkerAsync(string runId, string workerId)
    {
        var run = RequireRun(runId);
        var worker = RequireWorker(run, workerId);

        if (_activeProcesses.TryRemove(worker.WorkerId, out var handle))
        {
            try
            {
                if (!handle.Process.HasExited)
                {
                    handle.Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop worker {WorkerId}.", workerId);
            }
        }

        worker.Status = "stopped";
        worker.UpdatedAt = DateTime.UtcNow;
        worker.LastSurfaceActivityAt = DateTime.UtcNow;
        run = AddDecision(run, "worker-stopped", $"Stopped {worker.RoleName}.");
        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();
        RecalculateAttentionAggregates(run, settings);
        PersistRun(run);
        await _hubContext.Clients.All.SendAsync("AgentWorkerStatusChanged", run.RunId, worker.WorkerId, worker.Status);
        await BroadcastRunUpdatedAsync(run);
        return run;
    }

    public async Task StopAllWorkersAsync()
    {
        var activeWorkerIds = _activeProcesses.Keys.ToList();
        if (activeWorkerIds.Count == 0)
        {
            return;
        }

        foreach (var workerId in activeWorkerIds)
        {
            var run = _runStore.GetAll().FirstOrDefault(item => item.Workers.Any(worker => worker.WorkerId == workerId));
            if (run is null)
            {
                if (_activeProcesses.TryRemove(workerId, out var orphanedHandle))
                {
                    try
                    {
                        if (!orphanedHandle.Process.HasExited)
                        {
                            orphanedHandle.Process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to stop orphaned worker {WorkerId} during shutdown.", workerId);
                    }
                }

                continue;
            }

            try
            {
                await StopWorkerAsync(run.RunId, workerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop worker {WorkerId} during shutdown.", workerId);
            }
        }
    }

    public async Task<SupervisorRun> AskSupervisorAsync(string runId, string? extraInstruction)
    {
        var run = RequireRun(runId);
        if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Status, "archived", StringComparison.OrdinalIgnoreCase))
        {
            run.Status = "review";
            run = AddDecision(run, "run-reopened", "User explicitly asked the supervisor for another round; the run was reopened for follow-up work.");
        }

        run.RoundNumber += 1;
    SyncAssistantExecutionState(run);
        EnsureRoundHistoryDocument(run);
        PersistRun(run);
        await BroadcastRunUpdatedAsync(run);

        var summaryPrompt = BuildSupervisorPrompt(run, extraInstruction);
        var recommendationResult = await ExecuteOneShotAsync(summaryPrompt, run, $"问调度器：正在生成这轮建议");
        var recommendation = recommendationResult.Output;

        run.LatestSummary = recommendation;
        run.LastSupervisorCommandPreview = recommendationResult.CommandPreview;
        run = AddDecision(run, "supervisor", recommendation);
        TryAppendRoundHistoryEntry(run, run.RoundNumber, "ask-supervisor", extraInstruction, recommendation, null);
        TryWriteAssistantRoundArtifacts(run, run.RoundNumber, "ask-supervisor", extraInstruction, recommendation, false);
        PersistRun(run);
        await _hubContext.Clients.All.SendAsync("SupervisorDecisionMade", run.RunId, recommendation);
        await BroadcastRunUpdatedAsync(run);
        return run;
    }

    public async Task<SupervisorRun> VerifyRunAsync(string runId, string? command)
    {
        var run = RequireRun(runId);
        run.Status = "verifying";
        run = AddDecision(run, "verification-started", "Started workspace verification.");
        PersistRun(run);
        await BroadcastRunUpdatedAsync(run);

        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();
        var effectiveCommand = string.IsNullOrWhiteSpace(command) ? settings.DefaultVerificationCommand : command;
        var verification = await _verificationService.ExecuteAsync(effectiveCommand, run.WorkspaceRoot);

        run = RequireRun(runId);
        run.LastVerification = verification;
        run.VerificationHistory.Insert(0, verification);
        run.VerificationHistory = run.VerificationHistory.Take(20).ToList();
        run.Status = verification.Passed ? "review" : "needs-attention";
        if (!verification.Passed)
        {
            RaiseAttention(run, settings, VerificationSurfaceId, null, "verification-failed", "error", "Verification failed", verification.Summary ?? "Verification failed.");
        }
            else
            {
                foreach (var item in run.Attention.Where(item =>
                             !item.IsResolved
                             && string.Equals(item.SurfaceId, VerificationSurfaceId, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(item.Kind, "verification-failed", StringComparison.OrdinalIgnoreCase)))
                {
                    item.IsRead = true;
                    item.IsResolved = true;
                    item.AcknowledgedAt ??= DateTime.UtcNow;
                    item.ResolvedAt = DateTime.UtcNow;
                }
            }

        RecalculateAttentionAggregates(run, settings);
        run = AddDecision(run, "verification-finished", verification.Summary ?? "Verification finished.");
        PersistRun(run);
        await _hubContext.Clients.All.SendAsync("VerificationCompleted", run.RunId, verification);
        await BroadcastRunUpdatedAsync(run);
        return run;
    }

    public async Task<SupervisorRun> AutoStepAsync(string runId, string? extraInstruction, bool runVerificationFirst)
    {
        var run = RequireRun(runId);
        if (HasActiveWorkers(run))
        {
            run.PendingAutoStepRequested = true;
            run.PendingAutoStepInstruction = string.IsNullOrWhiteSpace(extraInstruction) ? null : extraInstruction.Trim();
            run.PendingAutoStepRunVerification = runVerificationFirst;
            run = AddDecision(run, "auto-step-queued", "Workers are still running; auto-step will start after they become idle.");
            PersistRun(run);
            await BroadcastRunUpdatedAsync(run);
            return run;
        }

        if (run.AutoStepCount >= run.MaxAutoSteps)
        {
            run.Status = "needs-human";
            run = AddDecision(run, "auto-step-blocked", $"Auto step limit reached ({run.MaxAutoSteps}).");
            PersistRun(run);
            await BroadcastRunUpdatedAsync(run);
            return run;
        }

        var queuedInstruction = run.PendingAutoStepInstruction;
        var effectiveInstruction = string.IsNullOrWhiteSpace(extraInstruction) ? queuedInstruction : extraInstruction;
        var effectiveRunVerificationFirst = run.PendingAutoStepRequested ? run.PendingAutoStepRunVerification : runVerificationFirst;

        run.PendingAutoStepRequested = false;
        run.PendingAutoStepInstruction = null;
        run.PendingAutoStepRunVerification = true;
        run.AutoStepCount += 1;
        run.RoundNumber += 1;
        SyncAssistantExecutionState(run);
        run.Status = "orchestrating";
        EnsureRoundHistoryDocument(run);
        PersistRun(run);
        await BroadcastRunUpdatedAsync(run);

        if (effectiveRunVerificationFirst)
        {
            run = await VerifyRunAsync(runId, null);
        }

        run = RequireRun(runId);
        var planPrompt = BuildStructuredSupervisorPrompt(run, effectiveInstruction);
        var rawPlanResult = await ExecuteOneShotAsync(planPrompt, run, $"自动推进第 {run.AutoStepCount} 轮：正在生成调度计划");
        var rawPlan = rawPlanResult.Output;
        var plan = TryParsePlan(rawPlan, run);

        run = RequireRun(runId);
        run.LatestSummary = plan?.Summary ?? rawPlan;
        run.LastSupervisorCommandPreview = rawPlanResult.CommandPreview;
        run = AddDecision(run, "auto-step", plan?.Summary ?? "Supervisor returned a plan.");
        TryAppendRoundHistoryEntry(run, run.RoundNumber, "auto-step", effectiveInstruction, rawPlan, plan);
        TryWriteAssistantRoundArtifacts(run, run.RoundNumber, "auto-step", effectiveInstruction, rawPlan, false);
        PersistRun(run);
        await _hubContext.Clients.All.SendAsync("SupervisorDecisionMade", run.RunId, run.LatestSummary);

        if (plan?.RunVerification == true && !effectiveRunVerificationFirst)
        {
            run = await VerifyRunAsync(runId, null);
        }

        run = RequireRun(runId);
        if (plan?.MarkCompleted == true && run.LastVerification?.Passed == true)
        {
            run.Status = "completed";
            run = AddDecision(run, "run-completed", "Supervisor marked the run as completed after successful verification.");
            PersistRun(run);
            await BroadcastRunUpdatedAsync(run);
            return run;
        }

        if (plan?.Actions is { Count: > 0 })
        {
            foreach (var action in plan.Actions)
            {
                await ExecuteActionAsync(runId, action);
            }

            return RequireRun(runId);
        }

        run.Status = run.LastVerification?.Passed == true ? "review" : "needs-human";
        run = AddDecision(run, "auto-step-noop", "No executable worker actions were produced.");
        PersistRun(run);
        await BroadcastRunUpdatedAsync(run);
        return run;
    }

    public SupervisorRun SetAutopilot(string runId, bool enabled)
    {
        var run = RequireRun(runId);
        run.AutoPilotEnabled = enabled;
        run = AddDecision(run, "autopilot", enabled ? "Enabled autopilot." : "Disabled autopilot.");
        PersistRun(run);
        _ = BroadcastRunUpdatedAsync(run);
        return run;
    }

    public SupervisorRun PauseRun(string runId)
    {
        var run = RequireRun(runId);
        run.AutoPilotEnabled = false;
        run.Status = "paused";
        run = AddDecision(run, "run-paused", "Paused orchestration.");
        PersistRun(run);
        _ = BroadcastRunUpdatedAsync(run);
        return run;
    }

    public async Task<SupervisorRun> ResumeRun(string runId)
    {
        var run = RequireRun(runId);
        run.AutoPilotEnabled = true;
        var hasActiveWorkers = HasActiveWorkers(run);
        run.Status = hasActiveWorkers ? "running" : "orchestrating";
        run = AddDecision(run, "run-resumed", "Resumed orchestration.");
        PersistRun(run);
        await BroadcastRunUpdatedAsync(run);

        if (!hasActiveWorkers)
        {
            await TryAutoAdvanceRunAsync(runId);
            return RequireRun(runId);
        }

        return run;
    }

    public SupervisorRun CompleteRun(string runId)
    {
        var run = RequireRun(runId);
        run.Status = "completed";
        run = AddDecision(run, "run-completed", "Run marked as completed.");
        PersistRun(run);
        _ = BroadcastRunUpdatedAsync(run);
        return run;
    }

    public SupervisorRun ArchiveRun(string runId)
    {
        var run = RequireRun(runId);
        run.Status = "archived";
        run = AddDecision(run, "run-archived", "Run archived.");
        PersistRun(run);
        _ = BroadcastRunUpdatedAsync(run);
        return run;
    }

    private async Task ExecuteActionAsync(string runId, SupervisorWorkerAction action)
    {
        if (string.IsNullOrWhiteSpace(action.WorkerId) || string.IsNullOrWhiteSpace(action.Mode))
        {
            return;
        }

        var run = RequireRun(runId);
        var worker = RequireWorker(run, action.WorkerId);
        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();
        SyncAssistantExecutionState(run);

        if (!TryValidateAssistantExecutionEligibility(run, worker, out var assistantBlockReason))
        {
            RaiseAttention(run, settings, GetWorkerSurfaceId(worker.WorkerId), worker.WorkerId, "assistant-action-blocked", "warning", $"{worker.RoleName} 未被本轮调度", assistantBlockReason ?? "当前轮次未安排该角色执行。已阻止启动动作。");
            run.Status = "needs-human";
            run = AddDecision(run, "assistant-action-blocked", assistantBlockReason ?? $"Blocked action '{action.Mode}' for worker '{worker.RoleName}' because it is outside the current assistant round.");
            PersistRun(run);
            await BroadcastRunUpdatedAsync(run);
            return;
        }

        switch (action.Mode.Trim().ToLowerInvariant())
        {
            case "start":
                await StartWorkerAsync(runId, action.WorkerId, action.Prompt);
                break;
            case "continue":
                await ContinueWorkerAsync(runId, action.WorkerId, action.Prompt);
                break;
            case "restart":
                await RestartWorkerAsync(runId, action.WorkerId, action.Prompt);
                break;
            case "stop":
                await StopWorkerAsync(runId, action.WorkerId);
                break;
        }
    }

    private async Task<SupervisorRun> RestartWorkerAsync(string runId, string workerId, string? prompt)
    {
        var run = RequireRun(runId);
        var worker = RequireWorker(run, workerId);
        worker.SessionId = Guid.NewGuid().ToString();
        worker.Status = "queued";
        worker.UpdatedAt = DateTime.UtcNow;
        run = AddDecision(run, "worker-restart", $"Restarted {worker.RoleName} with a new session.");
        PersistRun(run);
        return await StartWorkerAsync(runId, workerId, prompt);
    }

    private async Task ExecuteWorkerProcessAsync(string runId, string workerId, Process process, WorkerProcessHandle handle)
    {
        var settings = _roleConfigService.Load().Settings ?? new SupervisorSettings();
        var bufferMaxChars = Math.Max(1000, settings.OutputBufferMaxChars);
        var timedOut = false;
        var processStarted = false;
            var processExited = false;
            var exitCode = -1;
        Exception? executionError = null;

        try
        {
            process.Start();
            processStarted = true;
            var stdoutTask = PumpStreamAsync(runId, workerId, process.StandardOutput, handle.OutputBuffer, false, bufferMaxChars);
            var stderrTask = PumpStreamAsync(runId, workerId, process.StandardError, handle.OutputBuffer, true, bufferMaxChars);

            var timeoutMinutes = settings.WorkerTimeoutMinutes > 0 ? settings.WorkerTimeoutMinutes : 30;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                    processExited = process.HasExited;
                    if (processExited)
                    {
                        exitCode = process.ExitCode;
                    }
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
            }

            if (timedOut && !process.HasExited)
            {
                _logger.LogWarning("Worker {WorkerId} timed out after {Minutes} minutes, killing process.", workerId, timeoutMinutes);
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception killEx)
                {
                    _logger.LogWarning(killEx, "Failed to kill timed-out worker {WorkerId}.", workerId);
                }

                await process.WaitForExitAsync();
                processExited = process.HasExited;
                if (processExited)
                {
                    exitCode = process.ExitCode;
                }
            }
        }
        catch (Exception ex)
        {
            executionError = ex;
            _logger.LogError(ex, "Agent worker {WorkerId} failed.", workerId);
        }
        finally
        {
            _activeProcesses.TryRemove(workerId, out _);
            if (processStarted)
            {
                process.Dispose();
            }
        }

        var run = RequireRun(runId);
        var worker = RequireWorker(run, workerId);
        var outputText = handle.OutputBuffer.ToString();
        var report = ParseWorkerReport(outputText);
            worker.ExitCode = processStarted && processExited ? exitCode : -1;
        worker.Status = executionError is not null || timedOut ? "failed" : worker.ExitCode == 0 ? "completed" : "failed";
        worker.UpdatedAt = DateTime.UtcNow;
        worker.LastSurfaceActivityAt = DateTime.UtcNow;
        worker.HasStructuredReport = report.HasStructuredReport;
        worker.LastReportedStatus = report.Status;
        worker.LastSummary = timedOut
            ? "Worker execution timed out."
            : executionError is not null
                ? executionError.Message
                : report.Summary;
        worker.LastNextStep = report.Next;
        worker.LastOutputPreview = TrimOutput(outputText);
        ApplyWorkerAttentionRules(run, worker, timedOut, settings);
        RecalculateAttentionAggregates(run, settings);

        var decisionSummary = worker.HasStructuredReport
            ? $"{worker.RoleName} exited with code {worker.ExitCode}. STATUS={worker.LastReportedStatus}; SUMMARY={worker.LastSummary}"
            : $"{worker.RoleName} exited with code {worker.ExitCode}.";
        run = AddDecision(run, "worker-finished", decisionSummary);
        if (run.Workers.All(w => w.Status is "completed" or "failed" or "stopped" or "idle"))
        {
            run.Status = "review";
        }

        PersistRun(run);
        await _hubContext.Clients.All.SendAsync("AgentWorkerCompleted", run.RunId, worker.WorkerId, worker.ExitCode, worker.LastSummary);
        await _hubContext.Clients.All.SendAsync("AgentWorkerStatusChanged", run.RunId, worker.WorkerId, worker.Status);
        await BroadcastRunUpdatedAsync(run);
        await TryAutoAdvanceRunAsync(run.RunId);
    }

    private async Task TryAutoAdvanceRunAsync(string runId)
    {
        var run = RequireRun(runId);
        if (!run.AutoPilotEnabled && !run.PendingAutoStepRequested)
        {
            return;
        }

        if (HasActiveWorkers(run))
        {
            return;
        }

        if (!_autoAdvancingRuns.TryAdd(runId, 0))
        {
            return;
        }

        try
        {
            await AutoStepAsync(runId, run.PendingAutoStepInstruction, run.PendingAutoStepRunVerification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto advance failed for run {RunId}", runId);
        }
        finally
        {
            _autoAdvancingRuns.TryRemove(runId, out _);
        }
    }

    private async Task PumpStreamAsync(string runId, string workerId, StreamReader reader, StringBuilder buffer, bool isError, int bufferMaxChars)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            lock (buffer)
            {
                if (buffer.Length > 0)
                {
                    buffer.AppendLine();
                }

                buffer.Append(isError ? $"[stderr] {line}" : line);
                if (buffer.Length > bufferMaxChars)
                {
                    buffer.Remove(0, buffer.Length - bufferMaxChars);
                }
            }

            try
            {
                var run = RequireRun(runId);
                var worker = RequireWorker(run, workerId);
                worker.LastOutputPreview = TrimOutput(buffer.ToString());
                worker.UpdatedAt = DateTime.UtcNow;
                worker.LastSurfaceActivityAt = DateTime.UtcNow;
                PersistRun(run);
            }
            catch
            {
                // Ignore transient update failures while streaming.
            }
        }
    }

    private static CopilotLaunchPlan BuildWorkerLaunchPlan(SupervisorRun run, AgentWorkerSession worker, AgentRoleDefinition role, PromptArtifact promptArtifact, string workspaceRoot, SupervisorSettings settings)
    {
        var effectiveModel = string.IsNullOrWhiteSpace(role.Model) ? settings.DefaultModel : role.Model;
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            effectiveModel = "gpt-5.4";
        }

        var ghArguments = new List<string>
        {
            "copilot",
            "--",
            "-p",
            PromptArgumentToken,
            "-s"
        };

        if (!settings.AllowWorkerPermissionRequests)
        {
            ghArguments.Add("--no-ask-user");
        }

        ghArguments.Add($"--resume={worker.SessionId}");
        ghArguments.Add("--model");
        ghArguments.Add(effectiveModel);

        if (role.AllowAllTools)
        {
            ghArguments.Add("--allow-all-tools");
        }
        else
        {
            foreach (var tool in NormalizeList(role.AllowedTools))
            {
                ghArguments.Add("--allow-tool");
                ghArguments.Add(tool);
            }
        }

        if (role.AllowAllPaths)
        {
            ghArguments.Add("--allow-all-paths");
        }
        else
        {
            ghArguments.Add("--add-dir");
            ghArguments.Add(workspaceRoot);

            foreach (var directory in NormalizeList(run.AdditionalAllowedDirectories))
            {
                ghArguments.Add("--add-dir");
                ghArguments.Add(directory);
            }

            foreach (var path in NormalizeList(role.AllowedPaths))
            {
                ghArguments.Add("--add-dir");
                ghArguments.Add(ResolveAllowedPath(run.WorkspaceRoot ?? workspaceRoot, path));
            }
        }

        if (role.AllowAllUrls)
        {
            ghArguments.Add("--allow-all-urls");
        }
        else
        {
            foreach (var url in NormalizeList(role.AllowedUrls))
            {
                ghArguments.Add("--allow-url");
                ghArguments.Add(url);
            }
        }

        foreach (var tool in NormalizeList(role.DeniedTools))
        {
            ghArguments.Add("--deny-tool");
            ghArguments.Add(tool);
        }

        var commandPreview = BuildPromptFileReplayCommand(workspaceRoot, promptArtifact.PromptPath, "gh", ghArguments);
        var startInfo = CreatePowerShellStartInfo(workspaceRoot, commandPreview);

        foreach (var kvp in settings.EnvironmentVariables ?? [])
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key))
            {
                startInfo.Environment[kvp.Key] = kvp.Value;
            }
        }

        foreach (var kvp in role.EnvironmentVariables ?? [])
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key))
            {
                startInfo.Environment[kvp.Key] = kvp.Value;
            }
        }

        return new CopilotLaunchPlan(startInfo, commandPreview);
    }

    private async Task<OneShotResult> ExecuteOneShotAsync(string prompt, SupervisorRun run, string liveTitle)
    {
        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var supervisorModel = string.IsNullOrWhiteSpace(settings.SupervisorModel) ? "gpt-5.4" : settings.SupervisorModel;
        var workspaceRoot = string.IsNullOrWhiteSpace(run.WorkspaceRoot) ? FindWorkspaceRoot(settings) : run.WorkspaceRoot!;
        MergeAdditionalAllowedDirectories(run, workspaceRoot, prompt);
        var promptArtifact = CreateSupervisorPromptArtifact(run, workspaceRoot, liveTitle, prompt);
        var ghArguments = new List<string>
        {
            "copilot",
            "--",
            "-p",
            PromptArgumentToken,
            "-s"
        };

        if (!settings.AllowWorkerPermissionRequests)
        {
            ghArguments.Add("--no-ask-user");
        }

        ghArguments.Add("--allow-all-tools");
        ghArguments.Add("--disable-parallel-tools-execution");
        ghArguments.Add("--add-dir");
        ghArguments.Add(workspaceRoot);
        foreach (var directory in NormalizeList(run.AdditionalAllowedDirectories))
        {
            ghArguments.Add("--add-dir");
            ghArguments.Add(directory);
        }
        ghArguments.Add("--model");
        ghArguments.Add(supervisorModel);

        var commandPreview = BuildPromptFileReplayCommand(workspaceRoot, promptArtifact.PromptPath, "gh", ghArguments);
        var startInfo = CreatePowerShellStartInfo(workspaceRoot, commandPreview);

        foreach (var kvp in settings.EnvironmentVariables ?? [])
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key))
            {
                startInfo.Environment[kvp.Key] = kvp.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        Exception? executionError = null;

        await BroadcastSupervisorStreamStartedAsync(run.RunId, liveTitle, commandPreview);

        try
        {
            process.Start();

            var stdoutTask = PumpOneShotStreamAsync(process.StandardOutput, stdout, chunk => BroadcastSupervisorStreamChunkAsync(run.RunId, chunk, false));
            var stderrTask = PumpOneShotStreamAsync(process.StandardError, stderr, chunk => BroadcastSupervisorStreamChunkAsync(run.RunId, chunk, true));

            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());
        }
        catch (Exception ex)
        {
            executionError = ex;
            _logger.LogWarning(ex, "Supervisor prompt execution failed for run {RunId}", run.RunId);
        }

        var output = stdout.ToString().Trim();
        var error = stderr.ToString().Trim();

        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogWarning("Supervisor prompt stderr for run {RunId}: {Error}", run.RunId, error);
        }

        await BroadcastSupervisorStreamCompletedAsync(run.RunId, executionError is not null, output, string.IsNullOrWhiteSpace(error) ? executionError?.Message : error);

        if (executionError is not null)
        {
            throw new InvalidOperationException($"Supervisor execution failed: {executionError.Message}", executionError);
        }

        return new OneShotResult(
            string.IsNullOrWhiteSpace(output)
                ? "Supervisor did not return a recommendation."
                : output,
            commandPreview);
    }

    private static async Task PumpOneShotStreamAsync(StreamReader reader, StringBuilder buffer, Func<string, Task> onChunk)
    {
        var chunkBuffer = new char[256];

        while (true)
        {
            var read = await reader.ReadAsync(chunkBuffer, 0, chunkBuffer.Length);
            if (read <= 0)
            {
                break;
            }

            var chunk = new string(chunkBuffer, 0, read);
            buffer.Append(chunk);
            await onChunk(chunk);
        }
    }

    private string BuildInitialPrompt(SupervisorRun run, AgentWorkerSession worker, AgentRoleDefinition role)
    {
        var peerRoles = string.Join(", ", run.Workers.Where(w => w.WorkerId != worker.WorkerId).Select(w => w.RoleName));
        var template = string.IsNullOrWhiteSpace(role.PromptTemplate)
            ? "项目目标：{{goal}}。你的角色：{{roleName}}。请立即开始工作。"
            : role.PromptTemplate;

        var prompt = template
            .Replace("{{goal}}", run.Goal, StringComparison.OrdinalIgnoreCase)
            .Replace("{{roleName}}", worker.RoleName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{roleDescription}}", worker.RoleDescription ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{runTitle}}", run.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{{peerRoles}}", peerRoles, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(run.AssistantPlanSummary))
        {
            prompt += $"\n\nAI 助手计划摘要：{run.AssistantPlanSummary}";
        }

        if (!string.IsNullOrWhiteSpace(run.AssistantSkillSummary))
        {
            prompt += $"\nAI 助手 skill 摘要：{run.AssistantSkillSummary}";
        }

        if (!string.IsNullOrWhiteSpace(run.AssistantSkillFilePath))
        {
            prompt += $"\n如需查看完整轮次策略与共享规则，可优先读取：{run.AssistantSkillFilePath}";
        }

        if (run.AssistantPlanningBatchSize is > 0 && run.AssistantMaxRounds is > 0)
        {
            prompt += $"\n当前 run 使用 AI 助手轮次策略：每批 {run.AssistantPlanningBatchSize} 轮，最多 {run.AssistantMaxRounds} 轮；请优先生成可交接的 Markdown 摘要，减少无效上下文堆叠。";
        }

        return prompt;
    }

    private string BuildSupervisorPrompt(SupervisorRun run, string? extraInstruction)
    {
        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(settings.SupervisorPromptPrefix))
        {
            sb.AppendLine(settings.SupervisorPromptPrefix);
            sb.AppendLine();
        }

        sb.AppendLine("你是本地多角色 coding agent 控制台里的总调度员。请根据各角色当前汇报，给出下一轮最合理的调度建议。");
        sb.AppendLine($"当前轮次：第 {Math.Max(1, run.RoundNumber)} 轮");
        sb.AppendLine($"总目标：{run.Goal}");
        sb.AppendLine($"Run 标题：{run.Title}");
        if (!string.IsNullOrWhiteSpace(run.LatestSummary))
        {
            sb.AppendLine($"上一轮调度结论：{run.LatestSummary}");
        }
        if (!string.IsNullOrWhiteSpace(run.AssistantPlanSummary))
        {
            sb.AppendLine($"AI 助手计划摘要：{run.AssistantPlanSummary}");
        }
        if (!string.IsNullOrWhiteSpace(run.AssistantSkillSummary))
        {
            sb.AppendLine($"AI 助手 skill 摘要：{run.AssistantSkillSummary}");
        }
        if (run.LastVerification is not null)
        {
            sb.AppendLine($"最近验证：status={run.LastVerification.Status}, passed={run.LastVerification.Passed}, summary={run.LastVerification.Summary}");
        }
        sb.AppendLine();
        sb.AppendLine("当前角色状态：");

        foreach (var worker in run.Workers)
        {
            sb.AppendLine($"- {worker.RoleName} ({worker.Status})");
            if (!string.IsNullOrWhiteSpace(worker.LastSummary))
            {
                sb.AppendLine($"  Summary: {worker.LastSummary}");
            }
            if (!string.IsNullOrWhiteSpace(worker.LastOutputPreview))
            {
                sb.AppendLine($"  Recent output: {worker.LastOutputPreview}");
            }
        }

        if (!string.IsNullOrWhiteSpace(extraInstruction))
        {
            sb.AppendLine();
            sb.AppendLine($"额外要求：{extraInstruction}");
        }

        sb.AppendLine();
        sb.AppendLine("你必须先总结上一轮已经完成了什么、还缺什么，再判断是否进入下一轮，以及下一轮怎么拆。这个判断由你自己做。");
        sb.AppendLine("约束：优先基于当前 run 已记录的 worker 汇报、最近验证结果、工作区现有文件与日志做判断。");
        sb.AppendLine("约束：如果用户问题里显式提到某个现存目录或文件路径，RepoOPS 可能会把对应目录作为 `--add-dir` 传给 CLI；除此之外，不要假装已经读到未明确授权的外部路径。");
        sb.AppendLine("除非用户明确要求你亲自复跑命令，否则不要主动调用 shell / powershell 去重新 build、run、test；需要验证时，优先建议由外层 RepoOPS 验证链路执行。");
        if (run.AssistantPlanningBatchSize is > 0 && run.AssistantMaxRounds is > 0)
        {
            sb.AppendLine($"当前 run 绑定了 AI 助手轮次策略：每批 {run.AssistantPlanningBatchSize} 轮，最多 {run.AssistantMaxRounds} 轮；若当前批次末尾仍未完成，应先总结本批结果，再决定下一批如何推进。每轮都应强调交付物、未完成项和下一棒读取建议。");
        }
        sb.AppendLine("这次是用户主动来问调度器，说明用户对当前结果仍有疑问；即使之前出现过“完成”结论，也不要把它当作终局，而要基于新的问题继续拆分、继续推进。");
        sb.AppendLine("请输出：");
        sb.AppendLine("1. 总进度判断");
        sb.AppendLine("2. 先总结上一轮结果，再解释为何需要 / 不需要下一轮");
        sb.AppendLine("3. 推荐下一步（按角色列出）");
        sb.AppendLine("4. 哪些角色应继续旧会话，哪些应新开会话");
        sb.AppendLine("5. 是否应立刻触发验证");
        return sb.ToString();
    }

    private string BuildStructuredSupervisorPrompt(SupervisorRun run, string? extraInstruction)
    {
        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(settings.SupervisorPromptPrefix))
        {
            sb.AppendLine(settings.SupervisorPromptPrefix);
            sb.AppendLine();
        }

        sb.AppendLine("你是本地多角色 coding agent 控制台里的总调度员。请根据当前 run 的状态，给出下一轮自动调度计划。你必须只输出 JSON，不要输出 Markdown、解释或代码块。");
        sb.AppendLine($"当前轮次：第 {Math.Max(1, run.RoundNumber)} 轮");
        sb.AppendLine($"总目标：{run.Goal}");
        sb.AppendLine($"Run 标题：{run.Title}");
        sb.AppendLine($"自动推进次数：{run.AutoStepCount}/{run.MaxAutoSteps}");
        if (!string.IsNullOrWhiteSpace(run.LatestSummary))
        {
            sb.AppendLine($"上一轮调度结论：{run.LatestSummary}");
        }
        if (!string.IsNullOrWhiteSpace(run.AssistantPlanSummary))
        {
            sb.AppendLine($"AI 助手计划摘要：{run.AssistantPlanSummary}");
        }
        if (!string.IsNullOrWhiteSpace(run.AssistantSkillSummary))
        {
            sb.AppendLine($"AI 助手 skill 摘要：{run.AssistantSkillSummary}");
        }
        if (run.LastVerification is not null)
        {
            sb.AppendLine($"最近验证：status={run.LastVerification.Status}, passed={run.LastVerification.Passed}, summary={run.LastVerification.Summary}");
        }

        sb.AppendLine("角色列表：");
        foreach (var worker in run.Workers)
        {
            sb.AppendLine($"- workerId={worker.WorkerId}; role={worker.RoleName}; status={worker.Status}; reportedStatus={worker.LastReportedStatus}; summary={worker.LastSummary}; next={worker.LastNextStep}; task={worker.CurrentTask}");
        }

        if (!string.IsNullOrWhiteSpace(extraInstruction))
        {
            sb.AppendLine($"额外要求：{extraInstruction}");
        }

        sb.AppendLine("约束：优先基于当前 run 已记录的 worker 汇报、最近验证结果、工作区现有文件与日志做判断。");
        sb.AppendLine("约束：如果用户问题里显式提到某个现存目录或文件路径，RepoOPS 可能会把对应目录作为 `--add-dir` 传给 CLI；除此之外，不要假装已经读到未明确授权的外部路径。");
        sb.AppendLine("除非用户明确要求你亲自复跑命令，否则不要主动调用 shell / powershell 去重新 build、run、test；这里的 runVerification=true 表示建议由外层 RepoOPS 验证链路执行，而不是你自己在本轮里发命令。");
        if (run.AssistantPlanningBatchSize is > 0 && run.AssistantMaxRounds is > 0)
        {
            sb.AppendLine($"你必须遵守 AI 助手轮次策略：每批 {run.AssistantPlanningBatchSize} 轮，最多 {run.AssistantMaxRounds} 轮；设计动作时默认只允许 1 个写代码角色，除非你能明确证明不同角色写入的模块/文件范围互不重叠。若当前轮结束后已满足目标，应直接建议结束；否则只滚动设计下一轮，不要把很远的轮次写死。");
        }
        sb.AppendLine("你必须先在 summary 里概括上一轮结果，再给出本轮是否继续推进的判断；不要跳过这个复盘步骤。");
        sb.AppendLine("JSON schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"summary\": \"一句话总结当前总进度\",");
        sb.AppendLine("  \"runVerification\": true,");
        sb.AppendLine("  \"markCompleted\": false,");
        sb.AppendLine("  \"actions\": [");
        sb.AppendLine("    { \"workerId\": \"某个 workerId\", \"mode\": \"continue|restart|start|stop\", \"prompt\": \"给该角色的下一条具体指令\" }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("要求：如果已经可以收尾且最近验证通过，就 markCompleted=true 且 actions 为空；如果需要继续推进，请给出最少必要动作；prompt 要具体，不要空泛；不要把‘自己再跑一遍 shell 验证’当成默认动作。只输出 JSON。");
        return sb.ToString();
    }

    private string BuildRoleProposalPrompt(SupervisorRun run, IReadOnlyCollection<AgentRoleDefinition> roles, SupervisorSettings settings)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.RoleProposalPromptPrefix))
        {
            sb.AppendLine(settings.RoleProposalPromptPrefix);
            sb.AppendLine();
        }

        sb.AppendLine("你正在为一个多角色编码任务规划分工。只能输出 JSON，不要输出 Markdown、解释或代码块。");
        sb.AppendLine($"任务目标：{run.Goal}");
        sb.AppendLine($"执行根目录：{run.WorkspaceRoot}");
        sb.AppendLine("现有角色：");
        foreach (var role in roles)
        {
            sb.AppendLine($"- roleId={role.RoleId}; name={role.Name}; description={role.Description}; workspacePath={role.WorkspacePath}");
        }

        sb.AppendLine("要求：优先复用现有角色；优先让执行角色在各自流程里自行构建、运行、测试；只有在项目确实需要统一兜底时，才建议新增专门的构建/检查角色；不要为了“验证”而机械新增一个空泛角色。新角色最多 3 个。summary 和 reason 可以用中文；roleId 必须是简短英文 kebab-case。recommendedWorkspaceName 保持 24 个字符以内。\n");
        sb.AppendLine("要求：如果用户问题里显式提到某个现存目录或文件路径，RepoOPS 可能会把对应目录作为 `--add-dir` 传给 CLI；除此之外，请仍然把未明确授权的外部路径视为不可访问。\n");
        sb.AppendLine("JSON schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"summary\": \"简短说明为什么这么分工\",");
        sb.AppendLine("  \"recommendedWorkspaceName\": \"short-english-name\",");
        sb.AppendLine("  \"existingRoles\": [");
        sb.AppendLine("    { \"roleId\": \"planner\", \"reason\": \"为什么复用它\", \"selected\": true }");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"newRoles\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"reason\": \"为什么值得新增这个角色\",");
        sb.AppendLine("      \"selected\": true,");
        sb.AppendLine("      \"role\": {");
        sb.AppendLine("        \"roleId\": \"short-english-id\",");
        sb.AppendLine("        \"name\": \"可读的角色名称\",");
        sb.AppendLine("        \"description\": \"简洁职责说明，最好包含是否需要自行构建/测试\",");
        sb.AppendLine("        \"icon\": \"🧩\",");
        sb.AppendLine("        \"promptTemplate\": \"包含 {{goal}} 和 {{roleName}} 的具体提示词模板，并鼓励角色自己验证改动\",");
        sb.AppendLine("        \"model\": \"gpt-5.4\",");
        sb.AppendLine("        \"workspacePath\": \".\",");
        sb.AppendLine("        \"allowAllTools\": true,");
        sb.AppendLine("        \"allowAllPaths\": false,");
        sb.AppendLine("        \"allowAllUrls\": false,");
        sb.AppendLine("        \"allowedUrls\": [],");
        sb.AppendLine("        \"allowedTools\": [],");
        sb.AppendLine("        \"deniedTools\": [],");
        sb.AppendLine("        \"allowedPaths\": [],");
        sb.AppendLine("        \"environmentVariables\": {} }");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("再次提醒：不要默认创建一个抽象的 verification-only 角色；只有当仓库确实需要独立的统一构建/测试职责时才这样做。只输出 JSON。\n");
        return sb.ToString();
    }

    private static RoleProposalResponse? TryParseRoleProposal(string rawOutput, IReadOnlyCollection<AgentRoleDefinition> roles)
    {
        var json = ExtractJsonObject(rawOutput);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<StructuredRoleProposal>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed is null)
            {
                return null;
            }

            var roleMap = roles.ToDictionary(role => role.RoleId, StringComparer.OrdinalIgnoreCase);
            return new RoleProposalResponse
            {
                Summary = parsed.Summary ?? string.Empty,
                RecommendedWorkspaceName = parsed.RecommendedWorkspaceName ?? string.Empty,
                ExistingRoles = (parsed.ExistingRoles ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.RoleId) && roleMap.ContainsKey(item.RoleId))
                    .Select(item => new RoleProposalExistingRole
                    {
                        RoleId = item.RoleId!.Trim(),
                        Name = roleMap[item.RoleId!].Name,
                        Description = roleMap[item.RoleId!].Description,
                        Reason = item.Reason,
                        Selected = item.Selected ?? true
                    })
                    .ToList(),
                NewRoles = (parsed.NewRoles ?? [])
                    .Where(item => item.Role is not null)
                    .Select(item => new RoleProposalDraftRole
                    {
                        Role = item.Role!,
                        Reason = item.Reason,
                        Selected = item.Selected ?? true
                    })
                    .ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    private static RoleProposalResponse BuildFallbackRoleProposal(string goal, IReadOnlyCollection<AgentRoleDefinition> roles)
    {
        var preferredIds = new[] { "planner", "builder-a", "reviewer" };
        var existingRoles = preferredIds
            .Select(roleId => roles.FirstOrDefault(role => string.Equals(role.RoleId, roleId, StringComparison.OrdinalIgnoreCase)))
            .Where(role => role is not null)
            .Select(role => new RoleProposalExistingRole
            {
                RoleId = role!.RoleId,
                Name = role.Name,
                Description = role.Description,
                Reason = "按默认交付流程做的兜底选择。",
                Selected = true
            })
            .ToList();

        if (existingRoles.Count == 0)
        {
            existingRoles = roles.Take(2).Select(role => new RoleProposalExistingRole
            {
                RoleId = role.RoleId,
                Name = role.Name,
                Description = role.Description,
                Reason = "兜底选择。",
                Selected = true
            }).ToList();
        }

        return new RoleProposalResponse
        {
            Summary = "AI 角色建议生成失败，已使用本地兜底分工。",
            RecommendedWorkspaceName = BuildWorkspaceName(goal, null),
            ExistingRoles = existingRoles,
            NewRoles = []
        };
    }

    private static List<RoleProposalDraftRole> NormalizeDraftRoles(IEnumerable<RoleProposalDraftRole>? draftRoles, string defaultModel)
    {
        return (draftRoles ?? [])
            .Where(item => item.Role is not null)
            .Select(item =>
            {
                var role = item.Role;
                role.RoleId = NormalizeWorkspaceName(role.RoleId);
                role.Name = string.IsNullOrWhiteSpace(role.Name) ? role.RoleId : role.Name.Trim();
                role.Description = string.IsNullOrWhiteSpace(role.Description) ? null : role.Description.Trim();
                role.PromptTemplate = string.IsNullOrWhiteSpace(role.PromptTemplate)
                    ? "Project goal: {{goal}}. Role: {{roleName}}. Work inside the assigned workspace only. If the user mentions reading or checking an external path/resource, treat it as inaccessible unless it is already inside the current workspace, then finish with STATUS / SUMMARY / NEXT."
                    : role.PromptTemplate.Trim();
                role.Model = string.IsNullOrWhiteSpace(role.Model) ? defaultModel : role.Model.Trim();
                role.WorkspacePath = string.IsNullOrWhiteSpace(role.WorkspacePath) ? "." : role.WorkspacePath.Trim();
                role.AllowedPaths = NormalizeList(role.AllowedPaths);
                role.AllowedTools = NormalizeList(role.AllowedTools);
                role.DeniedTools = NormalizeList(role.DeniedTools);
                role.AllowedUrls = NormalizeList(role.AllowedUrls);
                role.EnvironmentVariables = NormalizeDictionary(role.EnvironmentVariables);
                return new RoleProposalDraftRole
                {
                    Role = role,
                    Reason = string.IsNullOrWhiteSpace(item.Reason) ? null : item.Reason.Trim(),
                    Selected = item.Selected
                };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Role.RoleId))
            .Take(3)
            .ToList();
    }

    private static bool HasActiveWorkers(SupervisorRun run)
        => run.Workers.Any(worker => worker.Status is "running" or "queued");

    private SupervisorRun RequireRun(string runId)
    {
        var run = _runStore.Get(runId);
        return run ?? throw new KeyNotFoundException($"Unknown run '{runId}'.");
    }

    private static AgentWorkerSession RequireWorker(SupervisorRun run, string workerId)
    {
        var worker = run.Workers.FirstOrDefault(item => item.WorkerId == workerId);
        return worker ?? throw new KeyNotFoundException($"Unknown worker '{workerId}'.");
    }

    private static AgentRoleDefinition RequireRole(IReadOnlyCollection<AgentRoleDefinition> roles, string roleId)
    {
        var role = roles.FirstOrDefault(item => item.RoleId == roleId);
        return role ?? throw new KeyNotFoundException($"Unknown role '{roleId}'.");
    }

    private sealed class StructuredRoleProposal
    {
        public string? Summary { get; set; }
        public string? RecommendedWorkspaceName { get; set; }
        public List<StructuredExistingRoleProposal>? ExistingRoles { get; set; }
        public List<StructuredDraftRoleProposal>? NewRoles { get; set; }
    }

    private sealed class StructuredExistingRoleProposal
    {
        public string? RoleId { get; set; }
        public string? Reason { get; set; }
        public bool? Selected { get; set; }
    }

    private sealed class StructuredDraftRoleProposal
    {
        public AgentRoleDefinition? Role { get; set; }
        public string? Reason { get; set; }
        public bool? Selected { get; set; }
    }
}
