using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RepoOPS.Agents.Models;
using RepoOPS.Agents.Services;
using RepoOPS.Hubs;
using RepoOPS.Services;

namespace RepoOPS.Host;

public static class RepoOpsWebApp
{
    public static WebApplication Create(string[] args, string? contentRoot = null)
    {
        var options = contentRoot is not null
            ? new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
                WebRootPath = Path.Combine(contentRoot, "wwwroot")
            }
            : new WebApplicationOptions { Args = args };

        var builder = WebApplication.CreateBuilder(options);

        builder.Services.AddSignalR();
        builder.Services.AddSingleton<ConfigService>();
        builder.Services.AddSingleton<ScriptTaskService>();
        builder.Services.AddSingleton<PtyService>();
        builder.Services.AddSingleton<AgentRoleConfigService>();
        builder.Services.AddSingleton<AssistantPlanStore>();
        builder.Services.AddSingleton<SupervisorRunStore>();
        builder.Services.AddSingleton<RunVerificationService>();
        builder.Services.AddSingleton<AgentSupervisorService>();
        builder.Services.AddSingleton<V2OrchestratorService>();

        var app = builder.Build();

        app.UseDefaultFiles(new DefaultFilesOptions
        {
            DefaultFileNames = { "index.html" }
        });
        app.UseStaticFiles();

        app.MapHub<TaskHub>("/hub/tasks");
        app.MapHub<PtyHub>("/hub/pty");

        app.MapGet("/api/config", (ConfigService configService) => Results.Ok(configService.LoadConfig()));

        app.MapGet("/api/tasks/running", (ScriptTaskService taskService) => Results.Ok(taskService.GetRunningTasks()));

        app.MapGet("/api/config/path", (ConfigService configService) => Results.Ok(new { path = configService.GetConfigPath() }));

        app.MapPut("/api/config", async (HttpRequest request, ConfigService configService) =>
        {
            try
            {
                var config = await request.ReadFromJsonAsync<RepoOPS.Models.TaskConfig>();
                if (config == null)
                {
                    return Results.BadRequest(new { error = "Invalid configuration" });
                }

                configService.SaveConfig(config);
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/api/agent/roles", (AgentSupervisorService supervisorService) => Results.Ok(supervisorService.GetRoles()));

        app.MapPost("/api/agent/roles/propose", async (HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var payload = await request.ReadFromJsonAsync<RoleProposalRequest>();
            if (payload == null || string.IsNullOrWhiteSpace(payload.Goal))
            {
                return Results.BadRequest(new { error = "Goal is required" });
            }

            try
            {
                var proposal = await supervisorService.ProposeRolesAsync(payload);
                return Results.Ok(proposal);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPut("/api/agent/roles", async (HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var catalog = await request.ReadFromJsonAsync<AgentRoleCatalog>();
            if (catalog == null)
            {
                return Results.BadRequest(new { error = "Invalid role catalog" });
            }

            var saved = supervisorService.SaveRoles(catalog);
            return Results.Ok(saved);
        });

        app.MapGet("/api/agent/settings", (AgentSupervisorService supervisorService) =>
        {
            var catalog = supervisorService.GetRoles();
            return Results.Ok(catalog.Settings ?? new SupervisorSettings());
        });

        app.MapPut("/api/agent/settings", async (HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var settings = await request.ReadFromJsonAsync<SupervisorSettings>();
            if (settings == null)
            {
                return Results.BadRequest(new { error = "Invalid settings" });
            }

            try
            {
                var saved = supervisorService.SaveSettings(settings);
                return Results.Ok(saved);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/api/agent/runs", (AgentSupervisorService supervisorService) => Results.Ok(supervisorService.GetRuns()));

        app.MapGet("/api/ai-assistant/plans", (AgentSupervisorService supervisorService) => Results.Ok(supervisorService.GetAssistantPlans()));

        app.MapGet("/api/ai-assistant/plans/{planId}", (string planId, AgentSupervisorService supervisorService) =>
        {
            var plan = supervisorService.GetAssistantPlan(planId);
            return plan is null ? Results.NotFound() : Results.Ok(plan);
        });

        app.MapPost("/api/ai-assistant/plans/generate", async (HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var payload = await request.ReadFromJsonAsync<GenerateAssistantPlanRequest>();
            if (payload == null || string.IsNullOrWhiteSpace(payload.Goal))
            {
                return Results.BadRequest(new { error = "Goal is required" });
            }

            try
            {
                var plan = await supervisorService.GenerateAssistantPlanAsync(payload);
                return Results.Ok(plan);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPut("/api/ai-assistant/plans/{planId}", async (string planId, HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var payload = await request.ReadFromJsonAsync<AssistantPlan>();
            if (payload == null)
            {
                return Results.BadRequest(new { error = "Invalid assistant plan" });
            }

            try
            {
                payload.PlanId = planId;
                var saved = supervisorService.SaveAssistantPlan(payload);
                return Results.Ok(saved);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/ai-assistant/plans/{planId}/create-run", async (string planId, HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var payload = await request.ReadFromJsonAsync<CreateRunFromAssistantPlanRequest>() ?? new CreateRunFromAssistantPlanRequest();

            try
            {
                var run = await supervisorService.CreateRunFromAssistantPlanAsync(planId, payload.AutoStart);
                return Results.Ok(run);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/api/agent/runs/{runId}", (string runId, AgentSupervisorService supervisorService) =>
        {
            var run = supervisorService.GetRun(runId);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        app.MapGet("/api/orchestration/runs/{runId}/snapshot", (string runId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                return Results.Ok(supervisorService.GetRunSnapshot(runId));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/api/orchestration/runs/{runId}/surfaces", (string runId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                return Results.Ok(supervisorService.GetSurfaces(runId));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/api/orchestration/runs/{runId}/lanes", (string runId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                return Results.Ok(supervisorService.GetLanes(runId));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/api/orchestration/runs/{runId}/attention", (string runId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                return Results.Ok(supervisorService.GetAttention(runId));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/orchestration/runs/{runId}/surfaces/{surfaceId}/focus-intent", async (string runId, string surfaceId, HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var payload = await request.ReadFromJsonAsync<SurfaceFocusIntentRequest>() ?? new SurfaceFocusIntentRequest();

            try
            {
                var snapshot = await supervisorService.FocusSurfaceIntentAsync(runId, surfaceId, payload.AcknowledgeRelatedAttention);
                return Results.Ok(snapshot);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/orchestration/runs/{runId}/attention/{eventId}/ack", async (string runId, string eventId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                var snapshot = await supervisorService.AcknowledgeAttentionAsync(runId, eventId);
                return Results.Ok(snapshot);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/orchestration/runs/{runId}/attention/ack-all", async (string runId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                var snapshot = await supervisorService.AcknowledgeAllAttentionAsync(runId);
                return Results.Ok(snapshot);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/orchestration/runs/{runId}/attention/{eventId}/resolve", async (string runId, string eventId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                var snapshot = await supervisorService.ResolveAttentionAsync(runId, eventId);
                return Results.Ok(snapshot);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/agent/runs", async (HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var runRequest = await request.ReadFromJsonAsync<CreateSupervisorRunRequest>();
            if (runRequest == null || string.IsNullOrWhiteSpace(runRequest.Goal))
            {
                return Results.BadRequest(new { error = "Goal is required" });
            }

            try
            {
                var run = await supervisorService.CreateRunAsync(runRequest);
                return Results.Ok(run);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/agent/runs/{runId}/workers/{workerId}/start", async (string runId, string workerId, HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var payload = await request.ReadFromJsonAsync<ContinueWorkerRequest>();

            try
            {
                var run = await supervisorService.StartWorkerAsync(runId, workerId, payload?.Prompt);
                return Results.Ok(run);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/agent/runs/{runId}/workers/{workerId}/continue", async (string runId, string workerId, HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var payload = await request.ReadFromJsonAsync<ContinueWorkerRequest>();

            try
            {
                var run = await supervisorService.ContinueWorkerAsync(runId, workerId, payload?.Prompt);
                return Results.Ok(run);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/agent/runs/{runId}/workers/{workerId}/stop", async (string runId, string workerId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                var run = await supervisorService.StopWorkerAsync(runId, workerId);
                return Results.Ok(run);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/agent/runs/{runId}/supervisor", async (string runId, HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var payload = await request.ReadFromJsonAsync<AskSupervisorRequest>();

            try
            {
                var run = await supervisorService.AskSupervisorAsync(runId, payload?.ExtraInstruction);
                return Results.Ok(run);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/agent/runs/{runId}/auto-step", async (string runId, HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var payload = await request.ReadFromJsonAsync<AutoStepRequest>() ?? new AutoStepRequest();

            try
            {
                var run = await supervisorService.AutoStepAsync(runId, payload.ExtraInstruction, payload.RunVerificationFirst);
                return Results.Ok(run);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/agent/runs/{runId}/verify", async (string runId, HttpRequest request, AgentSupervisorService supervisorService) =>
        {
            var payload = await request.ReadFromJsonAsync<RunVerificationRequest>();

            try
            {
                var run = await supervisorService.VerifyRunAsync(runId, payload?.Command);
                return Results.Ok(run);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/agent/runs/{runId}/open-folder", (string runId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                var run = supervisorService.GetRun(runId);
                if (run is null)
                {
                    return Results.NotFound(new { error = "Run not found" });
                }

                var folderPath = string.IsNullOrWhiteSpace(run.WorkspaceRoot)
                    ? run.ExecutionRoot
                    : run.WorkspaceRoot;

                if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                {
                    return Results.BadRequest(new { error = "Workspace folder does not exist" });
                }

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{folderPath}\"",
                        UseShellExecute = true
                    });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "open",
                        Arguments = $"\"{folderPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = $"\"{folderPath}\"",
                        UseShellExecute = true
                    });
                }

                return Results.Ok(new { success = true, path = folderPath });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/agent/runs/{runId}/autopilot/{enabled}", (string runId, bool enabled, AgentSupervisorService supervisorService) =>
        {
            try
            {
                var run = supervisorService.SetAutopilot(runId, enabled);
                return Results.Ok(run);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/orchestration/runs/{runId}/pause", (string runId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                return Results.Ok(supervisorService.PauseRun(runId));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/orchestration/runs/{runId}/resume", async (string runId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                return Results.Ok(await supervisorService.ResumeRun(runId));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/orchestration/runs/{runId}/complete", (string runId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                return Results.Ok(supervisorService.CompleteRun(runId));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/orchestration/runs/{runId}/archive", (string runId, AgentSupervisorService supervisorService) =>
        {
            try
            {
                return Results.Ok(supervisorService.ArchiveRun(runId));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // ── V2 AI 助手 V2 endpoints ──

        app.MapGet("/api/v2/runs", (V2OrchestratorService v2) => Results.Ok(v2.GetRuns()));

        app.MapGet("/api/v2/runs/{runId}", (string runId, V2OrchestratorService v2) =>
        {
            var run = v2.GetRun(runId);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        app.MapGet("/api/v2/runs/{runId}/snapshot", (string runId, V2OrchestratorService v2) =>
        {
            try { return Results.Ok(v2.GetRunSnapshot(runId)); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        app.MapPost("/api/v2/runs", async (HttpRequest request, V2OrchestratorService v2) =>
        {
            var payload = await request.ReadFromJsonAsync<CreateV2RunRequest>();
            if (payload == null || string.IsNullOrWhiteSpace(payload.Goal))
                return Results.BadRequest(new { error = "Goal is required" });
            try
            {
                var run = await v2.CreateRunAsync(payload);
                return Results.Ok(run);
            }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        app.MapPost("/api/v2/runs/{runId}/stop", async (string runId, V2OrchestratorService v2) =>
        {
            try { return Results.Ok(await v2.StopRunAsync(runId)); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        app.MapGet("/api/v2/templates", (V2OrchestratorService v2) => Results.Ok(v2.GetPromptTemplates()));

        return app;
    }
}
