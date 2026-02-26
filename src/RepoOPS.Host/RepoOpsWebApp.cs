using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        return app;
    }
}
