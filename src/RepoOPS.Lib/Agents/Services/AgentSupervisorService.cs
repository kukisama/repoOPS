using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RepoOPS.Agents.Models;
using RepoOPS.Hubs;

namespace RepoOPS.Agents.Services;

public sealed class AgentSupervisorService(
    AgentRoleConfigService roleConfigService,
    SupervisorRunStore runStore,
    RunVerificationService verificationService,
    IHubContext<TaskHub> hubContext,
    ILogger<AgentSupervisorService> logger)
{
    private readonly AgentRoleConfigService _roleConfigService = roleConfigService;
    private readonly SupervisorRunStore _runStore = runStore;
    private readonly RunVerificationService _verificationService = verificationService;
    private readonly IHubContext<TaskHub> _hubContext = hubContext;
    private readonly ILogger<AgentSupervisorService> _logger = logger;
    private readonly ConcurrentDictionary<string, WorkerProcessHandle> _activeProcesses = new();
    private readonly ConcurrentDictionary<string, byte> _autoAdvancingRuns = new();

    public AgentRoleCatalog GetRoles() => _roleConfigService.Load();

    public AgentRoleCatalog SaveRoles(AgentRoleCatalog catalog)
    {
        var normalized = NormalizeAndValidateRoleCatalog(catalog);
        _roleConfigService.Save(normalized);
        return _roleConfigService.Load();
    }

    public IReadOnlyList<SupervisorRun> GetRuns() => _runStore.GetAll();

    public SupervisorRun? GetRun(string runId) => _runStore.Get(runId);

    public async Task<SupervisorRun> CreateRunAsync(CreateSupervisorRunRequest request)
    {
        var roleCatalog = _roleConfigService.Load();
        var roles = roleCatalog.Roles
            .Where(role => request.RoleIds.Contains(role.RoleId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var repositoryRoot = FindWorkspaceRoot();
        var runWorkspaceRoot = ResolveRunWorkspaceRoot(repositoryRoot, request.WorkspaceRoot);

        if (roles.Count == 0)
        {
            throw new InvalidOperationException("At least one role must be selected.");
        }

        var run = new SupervisorRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(request.Title) ? CreateTitleFromGoal(request.Goal) : request.Title.Trim(),
            Goal = request.Goal.Trim(),
            Status = request.AutoStart ? "running" : "draft",
            WorkspaceRoot = runWorkspaceRoot,
            AutoPilotEnabled = request.AutoPilotEnabled,
            MaxAutoSteps = Math.Max(1, request.MaxAutoSteps),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Workers = roles.Select(role => new AgentWorkerSession
            {
                WorkerId = Guid.NewGuid().ToString("N"),
                RoleId = role.RoleId,
                RoleName = role.Name,
                RoleDescription = role.Description,
                Icon = role.Icon,
                SessionId = Guid.NewGuid().ToString(),
                Status = request.AutoStart ? "queued" : "idle",
                WorkspacePath = ResolveWorkspacePath(runWorkspaceRoot, role.WorkspacePath),
                CurrentTask = role.Description,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }).ToList(),
            Decisions =
            [
                new SupervisorDecisionEntry
                {
                    Kind = "run-created",
                    Summary = $"Created run with {roles.Count} roles. Goal: {request.Goal.Trim()}"
                }
            ]
        };

        run = _runStore.Upsert(run);
        await BroadcastRunUpdatedAsync(run);

        if (request.AutoStart)
        {
            foreach (var worker in run.Workers)
            {
                await StartWorkerAsync(run.RunId, worker.WorkerId, null);
            }

            var updatedRun = _runStore.Get(run.RunId) ?? run;
            return updatedRun;
        }

        return run;
    }

    public async Task<SupervisorRun> StartWorkerAsync(string runId, string workerId, string? prompt)
    {
        var run = RequireRun(runId);
        var worker = RequireWorker(run, workerId);

        if (_activeProcesses.ContainsKey(worker.WorkerId))
        {
            throw new InvalidOperationException($"Worker '{worker.RoleName}' is already running.");
        }

        var role = RequireRole(worker.RoleId);
        var effectivePrompt = string.IsNullOrWhiteSpace(prompt)
            ? BuildInitialPrompt(run, worker, role)
            : prompt.Trim();
        var workspaceRoot = string.IsNullOrWhiteSpace(run.WorkspaceRoot) ? FindWorkspaceRoot() : run.WorkspaceRoot!;
        var workspacePath = ResolveWorkspacePath(workspaceRoot, role.WorkspacePath);
        var launchPlan = BuildWorkerLaunchPlan(run, worker, role, effectivePrompt, workspacePath);

        worker.Status = "running";
        worker.WorkspacePath = workspacePath;
        worker.LastPrompt = effectivePrompt;
        worker.EffectiveCommandPreview = launchPlan.CommandPreview;
        worker.StartedAt = DateTime.UtcNow;
        worker.UpdatedAt = DateTime.UtcNow;
        worker.ExitCode = null;
        worker.HasStructuredReport = false;
        worker.LastReportedStatus = null;
        worker.LastNextStep = null;
        worker.LastOutputPreview = string.Empty;
        run.Status = "running";
        run = AddDecision(run, "worker-started", $"Started {worker.RoleName} session.");
        PersistRun(run);

        var process = launchPlan.CreateProcess();
        var handle = new WorkerProcessHandle(process, new StringBuilder());
        _activeProcesses[worker.WorkerId] = handle;

        await _hubContext.Clients.All.SendAsync("AgentWorkerStatusChanged", run.RunId, worker.WorkerId, worker.Status);
        await _hubContext.Clients.All.SendAsync("AgentWorkerStarted", run.RunId, worker.WorkerId, worker.RoleName);

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

        return await StartWorkerAsync(runId, workerId, followUpPrompt);
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
        run = AddDecision(run, "worker-stopped", $"Stopped {worker.RoleName}.");
        PersistRun(run);
        await _hubContext.Clients.All.SendAsync("AgentWorkerStatusChanged", run.RunId, worker.WorkerId, worker.Status);
        return run;
    }

    public async Task<SupervisorRun> AskSupervisorAsync(string runId, string? extraInstruction)
    {
        var run = RequireRun(runId);
        var summaryPrompt = BuildSupervisorPrompt(run, extraInstruction);
        var recommendationResult = await ExecuteOneShotAsync(summaryPrompt, run);
        var recommendation = recommendationResult.Output;

        run.LatestSummary = recommendation;
        run.LastSupervisorCommandPreview = recommendationResult.CommandPreview;
        run = AddDecision(run, "supervisor", recommendation);
        PersistRun(run);
        await _hubContext.Clients.All.SendAsync("SupervisorDecisionMade", run.RunId, recommendation);
        return run;
    }

    public async Task<SupervisorRun> VerifyRunAsync(string runId, string? command)
    {
        var run = RequireRun(runId);
        run.Status = "verifying";
        run = AddDecision(run, "verification-started", "Started workspace verification.");
        PersistRun(run);
        await BroadcastRunUpdatedAsync(run);

        var verification = await _verificationService.ExecuteAsync(command, run.WorkspaceRoot);
        run = RequireRun(runId);
        run.LastVerification = verification;
        run.Status = verification.Passed ? "review" : "needs-attention";
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
            return run;
        }

        var queuedInstruction = run.PendingAutoStepInstruction;
        var effectiveInstruction = string.IsNullOrWhiteSpace(extraInstruction) ? queuedInstruction : extraInstruction;
        var effectiveRunVerificationFirst = run.PendingAutoStepRequested ? run.PendingAutoStepRunVerification : runVerificationFirst;

        run.PendingAutoStepRequested = false;
        run.PendingAutoStepInstruction = null;
        run.PendingAutoStepRunVerification = true;
        PersistRun(run);

        if (effectiveRunVerificationFirst)
        {
            run = await VerifyRunAsync(runId, null);
        }

        run = RequireRun(runId);
        run.AutoStepCount += 1;
        run.Status = "orchestrating";
        PersistRun(run);

        var planPrompt = BuildStructuredSupervisorPrompt(run, effectiveInstruction);
        var rawPlanResult = await ExecuteOneShotAsync(planPrompt, run);
        var rawPlan = rawPlanResult.Output;
        var plan = TryParsePlan(rawPlan);

        run = RequireRun(runId);
        run.LatestSummary = plan?.Summary ?? rawPlan;
        run.LastSupervisorCommandPreview = rawPlanResult.CommandPreview;
        run = AddDecision(run, "auto-step", plan?.Summary ?? "Supervisor returned a plan.");
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
        return run;
    }

    private async Task ExecuteWorkerProcessAsync(string runId, string workerId, Process process, WorkerProcessHandle handle)
    {
        var run = RequireRun(runId);
        var worker = RequireWorker(run, workerId);

        try
        {
            process.Start();
            var stdoutTask = PumpStreamAsync(runId, workerId, process.StandardOutput, handle.OutputBuffer, false);
            var stderrTask = PumpStreamAsync(runId, workerId, process.StandardError, handle.OutputBuffer, true);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            worker.ExitCode = process.ExitCode;
            worker.Status = process.ExitCode == 0 ? "completed" : "failed";
            worker.UpdatedAt = DateTime.UtcNow;
            var outputText = handle.OutputBuffer.ToString();
            var report = ParseWorkerReport(outputText);
            worker.HasStructuredReport = report.HasStructuredReport;
            worker.LastReportedStatus = report.Status;
            worker.LastSummary = report.Summary;
            worker.LastNextStep = report.Next;
            worker.LastOutputPreview = TrimOutput(outputText);

            var decisionSummary = worker.HasStructuredReport
                ? $"{worker.RoleName} exited with code {process.ExitCode}. STATUS={worker.LastReportedStatus}; SUMMARY={worker.LastSummary}"
                : $"{worker.RoleName} exited with code {process.ExitCode}.";
            run = AddDecision(run, "worker-finished", decisionSummary);
            if (run.Workers.All(w => w.Status is "completed" or "failed" or "stopped" or "idle"))
            {
                run.Status = "review";
            }
            PersistRun(run);

            await _hubContext.Clients.All.SendAsync("AgentWorkerCompleted", run.RunId, worker.WorkerId, process.ExitCode, worker.LastSummary);
            await _hubContext.Clients.All.SendAsync("AgentWorkerStatusChanged", run.RunId, worker.WorkerId, worker.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent worker {WorkerId} failed.", workerId);
            worker.Status = "failed";
            worker.UpdatedAt = DateTime.UtcNow;
            worker.LastSummary = ex.Message;
            run = AddDecision(run, "worker-error", $"{worker.RoleName} failed: {ex.Message}");
            PersistRun(run);
            await _hubContext.Clients.All.SendAsync("AgentWorkerCompleted", run.RunId, worker.WorkerId, -1, ex.Message);
            await _hubContext.Clients.All.SendAsync("AgentWorkerStatusChanged", run.RunId, worker.WorkerId, worker.Status);
        }
        finally
        {
            _activeProcesses.TryRemove(worker.WorkerId, out _);
            process.Dispose();
            var updatedRun = RequireRun(runId);
            await BroadcastRunUpdatedAsync(updatedRun);
            await TryAutoAdvanceRunAsync(updatedRun.RunId);
        }
    }

    private async Task TryAutoAdvanceRunAsync(string runId)
    {
        var run = RequireRun(runId);
        if ((!run.AutoPilotEnabled && !run.PendingAutoStepRequested) || HasActiveWorkers(run) || run.Status == "completed")
        {
            return;
        }

        if (!_autoAdvancingRuns.TryAdd(runId, 0))
        {
            return;
        }

        try
        {
            await Task.Delay(1000);
            run = RequireRun(runId);
            if ((!run.AutoPilotEnabled && !run.PendingAutoStepRequested) || HasActiveWorkers(run) || run.AutoStepCount >= run.MaxAutoSteps)
            {
                return;
            }

            await AutoStepAsync(runId, run.PendingAutoStepInstruction, run.PendingAutoStepRequested
                ? run.PendingAutoStepRunVerification
                : run.LastVerification is null || !run.LastVerification.Passed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-advance failed for run {RunId}.", runId);
            run = RequireRun(runId);
            run.Status = "needs-human";
            run = AddDecision(run, "auto-step-error", ex.Message);
            PersistRun(run);
            await BroadcastRunUpdatedAsync(run);
        }
        finally
        {
            _autoAdvancingRuns.TryRemove(runId, out _);
        }
    }

    private async Task PumpStreamAsync(string runId, string workerId, StreamReader reader, StringBuilder buffer, bool isError)
    {
        var chars = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(chars.AsMemory())) > 0)
        {
            var chunk = new string(chars, 0, count).Replace("\r\n", "\n").Replace("\n", "\r\n");
            lock (buffer)
            {
                buffer.Append(chunk);
                if (buffer.Length > 12000)
                {
                    buffer.Remove(0, buffer.Length - 12000);
                }
            }

            await _hubContext.Clients.All.SendAsync("AgentWorkerOutput", runId, workerId, isError ? $"[stderr] {chunk}" : chunk);
        }
    }

    private static CopilotLaunchPlan BuildWorkerLaunchPlan(SupervisorRun run, AgentWorkerSession worker, AgentRoleDefinition role, string prompt, string workspacePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            WorkingDirectory = workspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("copilot");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("--no-ask-user");
        startInfo.ArgumentList.Add($"--resume={worker.SessionId}");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(role.Model) ? "gpt-5.4" : role.Model);

        if (role.AllowAllTools)
        {
            startInfo.ArgumentList.Add("--allow-all-tools");
        }
        else
        {
            foreach (var tool in NormalizeList(role.AllowedTools))
            {
                startInfo.ArgumentList.Add("--allow-tool");
                startInfo.ArgumentList.Add(tool);
            }
        }

        if (role.AllowAllPaths)
        {
            startInfo.ArgumentList.Add("--allow-all-paths");
        }
        else
        {
            startInfo.ArgumentList.Add("--add-dir");
            startInfo.ArgumentList.Add(workspacePath);
        }

        if (role.AllowAllUrls)
        {
            startInfo.ArgumentList.Add("--allow-all-urls");
        }
        else
        {
            foreach (var url in NormalizeList(role.AllowedUrls))
            {
                startInfo.ArgumentList.Add("--allow-url");
                startInfo.ArgumentList.Add(url);
            }
        }

        foreach (var tool in NormalizeList(role.DeniedTools))
        {
            startInfo.ArgumentList.Add("--deny-tool");
            startInfo.ArgumentList.Add(tool);
        }

        return new CopilotLaunchPlan(startInfo, BuildCommandPreview(startInfo));
    }

    private async Task ExecuteActionAsync(string runId, SupervisorWorkerAction action)
    {
        if (string.IsNullOrWhiteSpace(action.WorkerId) || string.IsNullOrWhiteSpace(action.Mode))
        {
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

    private async Task<OneShotResult> ExecuteOneShotAsync(string prompt, SupervisorRun run)
    {
        var workspaceRoot = string.IsNullOrWhiteSpace(run.WorkspaceRoot) ? FindWorkspaceRoot() : run.WorkspaceRoot!;
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            WorkingDirectory = workspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("copilot");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("--no-ask-user");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("gpt-5.4");

        var commandPreview = BuildCommandPreview(startInfo);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogWarning("Supervisor prompt stderr for run {RunId}: {Error}", run.RunId, error);
        }

        return new OneShotResult(
            string.IsNullOrWhiteSpace(output)
                ? "Supervisor did not return a recommendation."
                : output.Trim(),
            commandPreview);
    }

    private string BuildInitialPrompt(SupervisorRun run, AgentWorkerSession worker, AgentRoleDefinition role)
    {
        var peerRoles = string.Join(", ", run.Workers.Where(w => w.WorkerId != worker.WorkerId).Select(w => w.RoleName));
        var template = string.IsNullOrWhiteSpace(role.PromptTemplate)
            ? "项目目标：{{goal}}。你的角色：{{roleName}}。请立即开始工作。"
            : role.PromptTemplate;

        return template
            .Replace("{{goal}}", run.Goal, StringComparison.OrdinalIgnoreCase)
            .Replace("{{roleName}}", worker.RoleName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{roleDescription}}", worker.RoleDescription ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{runTitle}}", run.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{{peerRoles}}", peerRoles, StringComparison.OrdinalIgnoreCase);
    }

    private string BuildSupervisorPrompt(SupervisorRun run, string? extraInstruction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是本地多角色 coding agent 控制台里的总调度员。请根据各角色当前汇报，给出下一轮最合理的调度建议。");
        sb.AppendLine($"总目标：{run.Goal}");
        sb.AppendLine($"Run 标题：{run.Title}");
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
        sb.AppendLine("请输出：");
        sb.AppendLine("1. 总进度判断");
        sb.AppendLine("2. 推荐下一步（按角色列出）");
        sb.AppendLine("3. 哪些角色应继续旧会话，哪些应新开会话");
        sb.AppendLine("4. 是否应立刻触发验证");
        return sb.ToString();
    }

    private string BuildStructuredSupervisorPrompt(SupervisorRun run, string? extraInstruction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是本地多角色 coding agent 控制台里的总调度员。请根据当前 run 的状态，给出下一轮自动调度计划。你必须只输出 JSON，不要输出 Markdown、解释或代码块。");
        sb.AppendLine($"总目标：{run.Goal}");
        sb.AppendLine($"Run 标题：{run.Title}");
        sb.AppendLine($"自动推进次数：{run.AutoStepCount}/{run.MaxAutoSteps}");
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

        sb.AppendLine("JSON schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"summary\": \"一句话总结当前总进度\",");
        sb.AppendLine("  \"runVerification\": true,");
        sb.AppendLine("  \"markCompleted\": false,");
        sb.AppendLine("  \"actions\": [");
        sb.AppendLine("    { \"workerId\": \"某个 workerId\", \"mode\": \"continue|restart|start|stop\", \"prompt\": \"给该角色的下一条具体指令\" }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("要求：如果已经可以收尾且最近验证通过，就 markCompleted=true 且 actions 为空；如果需要继续推进，请给出最少必要动作；prompt 要具体，不要空泛。只输出 JSON。 ");
        return sb.ToString();
    }

    private SupervisorRun RequireRun(string runId)
    {
        return _runStore.Get(runId) ?? throw new InvalidOperationException($"Run '{runId}' not found.");
    }

    private bool HasActiveWorkers(SupervisorRun run)
    {
        return run.Workers.Any(worker => _activeProcesses.ContainsKey(worker.WorkerId));
    }

    private AgentWorkerSession RequireWorker(SupervisorRun run, string workerId)
    {
        return run.Workers.FirstOrDefault(w => string.Equals(w.WorkerId, workerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Worker '{workerId}' not found.");
    }

    private AgentRoleDefinition RequireRole(string roleId)
    {
        return _roleConfigService.Load().Roles.FirstOrDefault(r => string.Equals(r.RoleId, roleId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Role '{roleId}' not found.");
    }

    private static AgentRoleCatalog NormalizeAndValidateRoleCatalog(AgentRoleCatalog catalog)
    {
        var roles = catalog?.Roles ?? [];
        if (roles.Count == 0)
        {
            throw new InvalidOperationException("At least one role must be defined.");
        }

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
                Model = string.IsNullOrWhiteSpace(role.Model) ? "gpt-5.4" : role.Model.Trim(),
                AllowAllTools = role.AllowAllTools,
                AllowAllPaths = role.AllowAllPaths,
                AllowAllUrls = role.AllowAllUrls,
                WorkspacePath = string.IsNullOrWhiteSpace(role.WorkspacePath) ? "." : role.WorkspacePath.Trim(),
                AllowedUrls = NormalizeList(role.AllowedUrls),
                AllowedTools = NormalizeList(role.AllowedTools),
                DeniedTools = NormalizeList(role.DeniedTools)
            });
        }

        return new AgentRoleCatalog
        {
            Roles = normalizedRoles
        };
    }

    private SupervisorRun AddDecision(SupervisorRun run, string kind, string summary)
    {
        run.Decisions.Add(new SupervisorDecisionEntry
        {
            Kind = kind,
            Summary = summary,
            CreatedAt = DateTime.UtcNow
        });

        if (run.Decisions.Count > 40)
        {
            run.Decisions = run.Decisions[^40..];
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

    private static string CreateTitleFromGoal(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return $"Run {DateTime.Now:HHmmss}";
        }

        var trimmed = goal.Trim();
        return trimmed.Length <= 48 ? trimmed : trimmed[..48] + "…";
    }

    private static SupervisorPlan? TryParsePlan(string rawOutput)
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
            return JsonSerializer.Deserialize<SupervisorPlan>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
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

    private static string FindWorkspaceRoot()
    {
        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        foreach (var candidate in candidates)
        {
            var current = new DirectoryInfo(candidate);
            while (current is not null)
            {
                if (current.GetFiles("*.sln").Length > 0)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        return candidates.FirstOrDefault() ?? Directory.GetCurrentDirectory();
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

    private static string BuildCommandPreview(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Select(QuoteArgument);
        return string.Join(" ", new[] { startInfo.FileName }.Concat(arguments));
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

    private sealed record WorkerProcessHandle(Process Process, StringBuilder OutputBuffer);

    private sealed record CopilotLaunchPlan(ProcessStartInfo StartInfo, string CommandPreview)
    {
        public Process CreateProcess() => new() { StartInfo = StartInfo };
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
}
