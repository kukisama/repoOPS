using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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
    private readonly AgentRoleConfigService _roleConfigService;
    private readonly PtyService _ptyService;
    private readonly IHubContext<TaskHub> _hubContext;
    private readonly ILogger<V2OrchestratorService> _logger;

    private readonly ConcurrentDictionary<string, V2Run> _runs = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<int>> _sessionCompletions = new();
    private readonly V2PromptTemplateEngine _templates = new();

    public V2OrchestratorService(
        AgentRoleConfigService roleConfigService,
        PtyService ptyService,
        IHubContext<TaskHub> hubContext,
        ILogger<V2OrchestratorService> logger)
    {
        _roleConfigService = roleConfigService;
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
        var workspaceRoot = ResolveWorkspace(settings, request.WorkspaceRoot);

        var run = new V2Run
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? CreateTitle(request.Goal) : request.Title.Trim(),
            Goal = request.Goal.Trim(),
            Status = "draft",
            MaxRounds = request.MaxRounds > 0 ? request.MaxRounds : 6,
            WorkspaceRoot = workspaceRoot,
            ExecutionRoot = workspaceRoot
        };

        AddDecision(run, "run-created", $"V2 run created: {run.Title}");
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

                // Phase 1: Plan — launch gh copilot in a visible PTY to decide role assignments
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

    // ── Round planning — runs gh copilot in a visible PTY ──

    private async Task<RoundPlan?> PlanRoundInPtyAsync(V2Run run)
    {
        var catalog = _roleConfigService.Load();
        var roles = catalog.Roles;
        var roleListText = string.Join("\n", roles.Select(r => $"- {r.RoleId}: {r.Name} ({r.Description})"));

        var prompt = _templates.Render("main-plan-roles", new Dictionary<string, string>
        {
            ["goal"] = run.Goal,
            ["workspaceRoot"] = run.WorkspaceRoot ?? ".",
            ["roleList"] = roleListText
        });

        var (_, exitCode) = await ExecuteGhCopilotInPtyAsync(run, prompt, $"V2 R{run.CurrentRound} Planning");
        // Since PTY output goes directly to xterm, we can't capture text server-side.
        // Return a synthetic plan — the real coordination happens client-side via terminal.
        // For the first iteration, auto-assign all matching roles.
        if (exitCode != 0) return null;

        // Build a basic plan from all registered roles
        var plan = new RoundPlan { Summary = $"Round {run.CurrentRound} planning completed." };
        foreach (var role in roles.Take(4))
        {
            plan.Assignments.Add(new RoundAssignment { RoleId = role.RoleId, Task = run.Goal });
        }
        return plan;
    }

    private async Task<List<V2Worker>> DispatchWorkersInPtyAsync(V2Run run, RoundPlan plan)
    {
        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var roundWorkers = new List<V2Worker>();

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

            var workerPrompt = _templates.Render("worker-dispatch", new Dictionary<string, string>
            {
                ["roleName"] = role.Name,
                ["roleDescription"] = role.Description ?? "",
                ["goal"] = run.Goal,
                ["task"] = assignment.Task,
                ["workspaceRoot"] = run.WorkspaceRoot ?? ".",
                ["peerRoles"] = peerRoles
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

    /// <summary>Build the gh copilot command for a worker and launch it as a visible PTY session.</summary>
    private async Task<string> LaunchWorkerPtyAsync(V2Run run, V2Worker worker, AgentRoleDefinition role, string prompt, SupervisorSettings settings)
    {
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();

        // Write prompt to file
        var promptPath = CreatePromptFile(run.RunId, $"worker-{worker.RoleId}", workspaceRoot, prompt);

        var effectiveModel = string.IsNullOrWhiteSpace(role.Model) ? settings.DefaultModel : role.Model;
        if (string.IsNullOrWhiteSpace(effectiveModel)) effectiveModel = "gpt-5.4";

        // Build gh copilot argument list — mirrors V1 BuildWorkerLaunchPlan exactly
        var ghArguments = new List<string> { "copilot", "--", "-p", "$prompt", "-s", "--no-alt-screen" };

        // YOLO mode: --yolo bypasses all permission prompts.
        // With interactive PTY, gh copilot can now prompt for permission normally,
        // so we only add --yolo when the user explicitly enables it.
        if (settings.EnableYoloMode)
            ghArguments.Add("--yolo");

        ghArguments.AddRange(["--model", effectiveModel]);

        // Tools
        if (role.AllowAllTools)
        {
            ghArguments.Add("--allow-all-tools");
        }
        else
        {
            foreach (var tool in role.AllowedTools.Where(t => !string.IsNullOrWhiteSpace(t)))
                ghArguments.AddRange(["--allow-tool", tool]);
        }

        ghArguments.Add("--disable-parallel-tools-execution");

        // Paths
        if (role.AllowAllPaths)
        {
            ghArguments.Add("--allow-all-paths");
        }
        else
        {
            ghArguments.AddRange(["--add-dir", workspaceRoot]);

            foreach (var dir in run.AdditionalAllowedDirectories)
                ghArguments.AddRange(["--add-dir", dir]);

            foreach (var path in role.AllowedPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(workspaceRoot, path));
                ghArguments.AddRange(["--add-dir", resolved]);
            }
        }

        // URLs
        if (role.AllowAllUrls)
        {
            ghArguments.Add("--allow-all-urls");
        }
        else
        {
            foreach (var url in role.AllowedUrls.Where(u => !string.IsNullOrWhiteSpace(u)))
                ghArguments.AddRange(["--allow-url", url]);
        }

        // Denied tools
        foreach (var tool in role.DeniedTools.Where(t => !string.IsNullOrWhiteSpace(t)))
            ghArguments.AddRange(["--deny-tool", tool]);

        var (commandLine, commandPreview, inputScript) = BuildPtyLaunchScript(
            workspaceRoot, promptPath, "gh", ghArguments, run.RunId, $"worker-{worker.RoleId}");
        worker.CommandPreview = commandPreview;

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
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionCompletions[sessionId] = tcs;
        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetResult(-1));
        return await tcs.Task;
    }

    // ── Round review ──

    private static void CollectWorkerResults(V2RoundRecord roundRecord, List<V2Worker> roundWorkers)
    {
        foreach (var worker in roundWorkers)
        {
            roundRecord.WorkerResults.Add(new V2WorkerResult
            {
                WorkerId = worker.WorkerId,
                RoleName = worker.RoleName,
                Status = worker.Status,
                Summary = $"{worker.RoleName}: exit code {worker.ExitCode}",
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
            ["decisionHistory"] = decisionHistory
        });

        var (_, exitCode) = await ExecuteGhCopilotInPtyAsync(run, prompt, $"V2 R{run.CurrentRound} Review");

        // With PTY-based execution, we can't capture stdout. Determine status from exit codes.
        var allSucceeded = roundRecord.WorkerResults.All(w => w.ExitCode == 0);
        if (allSucceeded)
            return new ReviewResult { OverallStatus = "completed", Summary = "All workers succeeded." };

        return exitCode == 0
            ? new ReviewResult { OverallStatus = "continue", Summary = "Review completed, continuing." }
            : new ReviewResult { OverallStatus = "continue", Summary = "Review had issues, continuing." };
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
                Model = settings.SupervisorModel,
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

        var (_, verdictExit) = await ExecuteGhCopilotInPtyAsync(run, verdictPrompt, "V2 Final Verdict");
        return exitCode == 0 && verdictExit == 0;
    }

    // ── One-shot gh copilot execution via visible PTY ──

    /// <summary>
    /// Run a one-shot gh copilot call in a visible PTY session.
    /// Returns (sessionId, exitCode). Output goes directly to xterm via SignalR.
    /// </summary>
    private async Task<(string sessionId, int exitCode)> ExecuteGhCopilotInPtyAsync(V2Run run, string prompt, string label)
    {
        var catalog = _roleConfigService.Load();
        var settings = catalog.Settings ?? new SupervisorSettings();
        var model = string.IsNullOrWhiteSpace(settings.SupervisorModel) ? "gpt-5.4" : settings.SupervisorModel;
        var workspaceRoot = run.WorkspaceRoot ?? AgentRoleConfigService.GetBaseDir();

        var promptPath = CreatePromptFile(run.RunId, "main-thread", workspaceRoot, prompt);

        var ghArguments = new List<string> { "copilot", "--", "-p", "$prompt", "-s", "--no-alt-screen" };

        // Main thread YOLO mode: when enabled, bypasses all permission prompts.
        // When disabled, gh copilot can show interactive permission confirmations
        // in the PTY terminal.
        if (settings.EnableYoloMode)
            ghArguments.Add("--yolo");

        // Main thread gets full tool/path access for scheduling
        ghArguments.AddRange(["--allow-all-tools", "--disable-parallel-tools-execution"]);
        ghArguments.AddRange(["--add-dir", workspaceRoot]);

        foreach (var dir in run.AdditionalAllowedDirectories)
            ghArguments.AddRange(["--add-dir", dir]);

        ghArguments.AddRange(["--model", model]);

        var (commandLine, commandPreview, inputScript) = BuildPtyLaunchScript(
            workspaceRoot, promptPath, "gh", ghArguments, run.RunId, "main-thread");

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

        await _hubContext.Clients.All.SendAsync("V2MainThreadActivity", run.RunId, label,
            exitCode == 0 ? "completed" : "failed");

        return (sessionId, exitCode);
    }

    // ── Command line building ──

    /// <summary>
    /// Write a .ps1 launch script and return (commandLine for ConPTY, commandPreview for UI).
    /// Uses direct invocation (no &amp; operator / no per-arg quoting) because
    /// gh copilot inside ConPTY does not parse individually-quoted arguments correctly.
    /// Only paths that contain spaces are quoted.
    /// </summary>
    private static (string commandLine, string commandPreview, string inputScript) BuildPtyLaunchScript(
        string workingDirectory, string promptFilePath, string executable,
        IReadOnlyList<string> arguments, string runId, string label)
    {
        // Render arguments: $prompt stays as-is, paths with spaces get single-quoted, rest bare
        var renderedArgs = arguments.Select(arg =>
            string.Equals(arg, "$prompt", StringComparison.Ordinal)
                ? "$prompt"
                : NeedsQuoting(arg) ? QuotePowerShellLiteral(arg) : arg);

        var ghCommand = $"{executable} {string.Join(" ", renderedArgs)}";

        var scriptLines = new[]
        {
            "$utf8NoBom = [System.Text.UTF8Encoding]::new($false)",
            "$OutputEncoding = $utf8NoBom",
            "[Console]::InputEncoding = $utf8NoBom",
            "[Console]::OutputEncoding = $utf8NoBom",
            $"Set-Location -LiteralPath {QuotePowerShellLiteral(workingDirectory)}",
            $"$promptPath = {QuotePowerShellLiteral(promptFilePath)}",
            "$prompt = Get-Content -LiteralPath $promptPath -Raw -Encoding utf8",
            $"{ghCommand}; exit"
        };

        var script = string.Join(Environment.NewLine, scriptLines);
        // Input uses \r\n terminators — each line is a "keypress Enter" in ConPTY
        var inputScript = string.Join("\r\n", scriptLines) + "\r\n";

        // Write script file for audit/preview
        var scriptFolder = Path.Combine(workingDirectory, ".repoops", "prompts", runId, "v2", "scripts");
        Directory.CreateDirectory(scriptFolder);
        var scriptFileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{SanitizeLabel(label)}-{Guid.NewGuid().ToString("N")[..6]}.ps1";
        var scriptPath = Path.Combine(scriptFolder, scriptFileName);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        // Launch interactive pwsh — child processes see a real TTY,
        // enabling gh copilot's interactive permission prompts.
        var commandLine = "pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass";
        return (commandLine, script, inputScript);
    }

    /// <summary>Does this argument need single-quoting in a PowerShell invocation?</summary>
    private static bool NeedsQuoting(string arg) =>
        arg.Length == 0 || arg.Contains(' ') || arg.Contains('\'') || arg.Contains('(') || arg.Contains(')');


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

    // ── Internal DTOs ──

    private sealed class RoundPlan
    {
        public string Summary { get; set; } = string.Empty;
        public List<RoundAssignment> Assignments { get; set; } = [];
    }

    private sealed class RoundAssignment
    {
        public string RoleId { get; set; } = string.Empty;
        public string Task { get; set; } = string.Empty;
    }

    private sealed class ReviewResult
    {
        public string OverallStatus { get; set; } = "continue";
        public string Summary { get; set; } = string.Empty;
        public List<string> Issues { get; set; } = [];
    }
}
