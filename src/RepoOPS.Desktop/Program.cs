using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using RepoOPS.Host;

namespace RepoOPS.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var logFile = DesktopLog.GetLogFilePath();
        DesktopLog.Initialize(logFile);

        var port = PortPicker.GetAvailableTcpPort();
        var url = $"http://127.0.0.1:{port}";

        WebApplication? app = null;
        try
        {
            // Use the EXE's directory for content root (wwwroot, scripts, tasks.json all here)
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            app = RepoOpsWebApp.Create(args, exeDir);
            app.Urls.Clear();
            app.Urls.Add(url);

            var serverReady = new TaskCompletionSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await app.StartAsync();
                    DesktopLog.Info($"RepoOPS web host started at {url}");
                    serverReady.TrySetResult();
                }
                catch (Exception ex)
                {
                    DesktopLog.Error(ex, "Failed to start web host");
                    serverReady.TrySetException(ex);
                    MessageBox.Show(
                        $"RepoOPS 启动失败。\n\n原因：{ex.Message}\n\n日志：{logFile}",
                        "RepoOPS",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    Environment.Exit(1);
                }
            });

            // Wait for the web server to be ready before showing the UI
            if (!serverReady.Task.Wait(TimeSpan.FromSeconds(15)))
            {
                DesktopLog.Error(new TimeoutException("Web host did not start within 15 seconds"), "Startup timeout");
            }

            using var mainForm = new MainForm(url, logFile);
            Application.Run(mainForm);
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    app.StopAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // ignore shutdown errors
                }
            }
        }
    }
}

internal static class PortPicker
{
    public static int GetAvailableTcpPort()
    {
        // Bind to port 0 to let OS choose an available port.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
