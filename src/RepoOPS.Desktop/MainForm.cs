using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RepoOPS.Desktop;

public sealed class MainForm : Form
{
    private readonly string _url;
    private readonly string _logFile;
    private readonly WebView2 _webView;

    public MainForm(string url, string logFile)
    {
        _url = url;
        _logFile = logFile;

        Text = "RepoOPS";
        Width = 1200;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;

        var menu = BuildMenu();
        MainMenuStrip = menu;
        Controls.Add(menu);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };

        Controls.Add(_webView);
        _webView.BringToFront();

        Shown += async (_, _) =>
        {
            try
            {
                await _webView.EnsureCoreWebView2Async();

                _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

                _webView.CoreWebView2.NavigationStarting += (_, e) => DesktopLog.Info($"Navigating: {e.Uri}");
                _webView.CoreWebView2.NavigationCompleted += (_, e) => DesktopLog.Info($"NavigationCompleted: success={e.IsSuccess} status={e.HttpStatusCode}");

                _webView.CoreWebView2.Navigate(_url);
            }
            catch (Exception ex)
            {
                DesktopLog.Error(ex, "Failed to initialize WebView2");
                MessageBox.Show(
                    $"WebView2 初始化失败。\n\n原因：{ex.Message}\n\n日志：{_logFile}",
                    "RepoOPS",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        };

        FormClosing += (_, _) =>
        {
            try
            {
                _webView?.Dispose();
            }
            catch
            {
                // ignore
            }
        };
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var app = new ToolStripMenuItem("RepoOPS");

        var openLogs = new ToolStripMenuItem("打开日志")
        {
            ToolTipText = _logFile
        };
        openLogs.Click += (_, _) =>
        {
            try
            {
                var dir = Path.GetDirectoryName(_logFile) ?? Environment.CurrentDirectory;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                DesktopLog.Error(ex, "Failed to open log directory");
            }
        };

        var reload = new ToolStripMenuItem("刷新")
        {
            ShortcutKeys = Keys.F5
        };
        reload.Click += (_, _) =>
        {
            try
            {
                _webView?.CoreWebView2?.Reload();
            }
            catch { }
        };

        var devTools = new ToolStripMenuItem("开发者工具")
        {
            ShortcutKeys = Keys.F12
        };
        devTools.Click += (_, _) =>
        {
            try
            {
                _webView?.CoreWebView2?.OpenDevToolsWindow();
            }
            catch { }
        };

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => Close();

        app.DropDownItems.Add(reload);
        app.DropDownItems.Add(devTools);
        app.DropDownItems.Add(openLogs);
        app.DropDownItems.Add(new ToolStripSeparator());
        app.DropDownItems.Add(exit);

        menu.Items.Add(app);
        return menu;
    }
}
