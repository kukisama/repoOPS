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
        builder.Services.AddSingleton<AgentRoleConfigService>();
        builder.Services.AddSingleton<SupervisorRunStore>();
        builder.Services.AddSingleton<RunVerificationService>();
        builder.Services.AddSingleton<AgentSupervisorService>();

        var app = builder.Build();

        app.UseDefaultFiles(new DefaultFilesOptions
        {
            DefaultFileNames = { "index.html" }
        });
        app.UseStaticFiles();

        app.MapHub<TaskHub>("/hub/tasks");

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

            var saved = supervisorService.SaveSettings(settings);
            return Results.Ok(saved);
        });

        app.MapGet("/api/agent/runs", (AgentSupervisorService supervisorService) => Results.Ok(supervisorService.GetRuns()));

        app.MapGet("/api/agent/runs/{runId}", (string runId, AgentSupervisorService supervisorService) =>
        {
            var run = supervisorService.GetRun(runId);
            return run is null ? Results.NotFound() : Results.Ok(run);
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

        return app;
    }
}
