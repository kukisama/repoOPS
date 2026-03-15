using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoOPS.Agents.Models;

namespace RepoOPS.Agents.Services;

public sealed class V2WorkspaceBootstrapService(ILogger<V2WorkspaceBootstrapService> logger)
{
    private const string BootstrapTemplateDirectoryName = "bootstrap-template";
    private static readonly Regex AsciiTokenRegex = new("[a-z]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameCandidateRegex = new("^[a-z]{5,}$", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "the", "to", "for", "in", "on", "at", "by", "of", "and", "or", "is", "are", "be", "with",
        "this", "that", "it", "from", "as", "into", "your", "my", "our", "you", "me", "we", "i"
    ];
    private static readonly IReadOnlyDictionary<string, string> GoalKeywordMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["简单"] = "simple",
        ["任务"] = "task",
        ["子进程"] = "workers",
        ["并行"] = "parallel",
        ["免费模型"] = "freemodel",
        ["模型"] = "model",
        ["角色"] = "role",
        ["名字"] = "name",
        ["目录"] = "dir",
        ["文件"] = "file",
        ["文档"] = "docs",
        ["配置"] = "config",
        ["测试"] = "test",
        ["构建"] = "build",
        ["修复"] = "fix",
        ["优化"] = "optimize",
        ["部署"] = "deploy"
    };

    private readonly ILogger<V2WorkspaceBootstrapService> _logger = logger;

    public V2WorkspaceBootstrapResult Bootstrap(string executionRoot, string goal, string? requestedWorkspaceRoot, string? requestedWorkspaceName, IReadOnlyCollection<AgentRoleDefinition> roles, string? runId = null)
    {
        var normalizedExecutionRoot = Path.GetFullPath(executionRoot);
        Directory.CreateDirectory(normalizedExecutionRoot);

        var allowedTools = roles
            .SelectMany(role => role.AllowedTools ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allowedUrls = roles
            .SelectMany(role => role.AllowedUrls ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(requestedWorkspaceRoot))
        {
            var manualWorkspace = Path.GetFullPath(requestedWorkspaceRoot.Trim());
            Directory.CreateDirectory(manualWorkspace);
            EnsureBootstrapTemplateContent(manualWorkspace);
            var manualGitState = TryInitializeGitRepository(manualWorkspace);
            var paths = new[] { manualWorkspace };
            var policy = EnsurePolicyFiles(manualWorkspace, normalizedExecutionRoot, goal, paths, allowedTools, allowedUrls, null);
            RunCsvLogService.Append(manualWorkspace, runId, "初始化", RunCsvLogService.BuildDetails(
                ("事件", "使用现有工作区"),
                ("工作区", manualWorkspace),
                ("元数据", policy.MetadataFile),
                ("路径策略", policy.AllowedPathsFile),
                ("工具策略", policy.AllowedToolsFile),
                ("URL策略", policy.AllowedUrlsFile),
                ("Git仓库存在", manualGitState.GitRepositoryPresent),
                ("RepoOPS执行Git初始化", manualGitState.GitInitializedByRepoOps)));
            return new V2WorkspaceBootstrapResult(normalizedExecutionRoot, manualWorkspace, Path.GetFileName(manualWorkspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), policy.MetadataFile, policy.AllowedPathsFile, policy.AllowedToolsFile, policy.AllowedUrlsFile, manualGitState.GitRepositoryPresent, manualGitState.GitInitializedByRepoOps);
        }

        var scriptPath = ResolveBootstrapScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            _logger.LogWarning("V2 bootstrap script not found, using in-process fallback.");
            var fallbackWorkspace = Path.Combine(normalizedExecutionRoot, ResolveWorkspaceName(goal, requestedWorkspaceName));
            Directory.CreateDirectory(fallbackWorkspace);
            EnsureBootstrapTemplateContent(fallbackWorkspace);
            var missingScriptGitState = TryInitializeGitRepository(fallbackWorkspace);
            var paths = new[] { fallbackWorkspace };
            var policy = EnsurePolicyFiles(fallbackWorkspace, normalizedExecutionRoot, goal, paths, allowedTools, allowedUrls, null);
            RunCsvLogService.Append(fallbackWorkspace, runId, "初始化", RunCsvLogService.BuildDetails(
                ("事件", "初始化脚本缺失，已走进程内回退"),
                ("工作区", fallbackWorkspace),
                ("元数据", policy.MetadataFile),
                ("路径策略", policy.AllowedPathsFile),
                ("工具策略", policy.AllowedToolsFile),
                ("URL策略", policy.AllowedUrlsFile),
                ("Git仓库存在", missingScriptGitState.GitRepositoryPresent),
                ("RepoOPS执行Git初始化", missingScriptGitState.GitInitializedByRepoOps)));
            return new V2WorkspaceBootstrapResult(normalizedExecutionRoot, fallbackWorkspace, Path.GetFileName(fallbackWorkspace), policy.MetadataFile, policy.AllowedPathsFile, policy.AllowedToolsFile, policy.AllowedUrlsFile, missingScriptGitState.GitRepositoryPresent, missingScriptGitState.GitInitializedByRepoOps);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = normalizedExecutionRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-TaskRoot");
        startInfo.ArgumentList.Add(normalizedExecutionRoot);
        startInfo.ArgumentList.Add("-Goal");
        startInfo.ArgumentList.Add(goal);
        startInfo.ArgumentList.Add("-WorkspaceName");
        startInfo.ArgumentList.Add(ResolveWorkspaceName(goal, requestedWorkspaceName));
        startInfo.ArgumentList.Add("-AllowedPathsJson");
        startInfo.ArgumentList.Add(JsonSerializer.Serialize(new[] { normalizedExecutionRoot }));
        startInfo.ArgumentList.Add("-AllowedToolsJson");
        startInfo.ArgumentList.Add(JsonSerializer.Serialize(allowedTools));
        startInfo.ArgumentList.Add("-AllowedUrlsJson");
        startInfo.ArgumentList.Add(JsonSerializer.Serialize(allowedUrls));
        if (!string.IsNullOrWhiteSpace(runId))
        {
            startInfo.ArgumentList.Add("-RunId");
            startInfo.ArgumentList.Add(runId);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("V2 bootstrap script failed with code {Code}: {Error}", process.ExitCode, error);
            var fallbackWorkspace = Path.Combine(normalizedExecutionRoot, ResolveWorkspaceName(goal, requestedWorkspaceName));
            Directory.CreateDirectory(fallbackWorkspace);
            EnsureBootstrapTemplateContent(fallbackWorkspace);
            var failedScriptGitState = TryInitializeGitRepository(fallbackWorkspace);
            var paths = new[] { fallbackWorkspace };
            var policy = EnsurePolicyFiles(fallbackWorkspace, normalizedExecutionRoot, goal, paths, allowedTools, allowedUrls, null);
            RunCsvLogService.Append(fallbackWorkspace, runId, "初始化", RunCsvLogService.BuildDetails(
                ("事件", "初始化脚本失败，已走进程内回退"),
                ("退出码", process.ExitCode),
                ("错误", error),
                ("工作区", fallbackWorkspace),
                ("元数据", policy.MetadataFile),
                ("路径策略", policy.AllowedPathsFile),
                ("工具策略", policy.AllowedToolsFile),
                ("URL策略", policy.AllowedUrlsFile),
                ("Git仓库存在", failedScriptGitState.GitRepositoryPresent),
                ("RepoOPS执行Git初始化", failedScriptGitState.GitInitializedByRepoOps)));
            return new V2WorkspaceBootstrapResult(normalizedExecutionRoot, fallbackWorkspace, Path.GetFileName(fallbackWorkspace), policy.MetadataFile, policy.AllowedPathsFile, policy.AllowedToolsFile, policy.AllowedUrlsFile, failedScriptGitState.GitRepositoryPresent, failedScriptGitState.GitInitializedByRepoOps);
        }

        var json = ExtractJsonObject(output);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Bootstrap script did not return valid JSON output.");
        }

        var response = JsonSerializer.Deserialize<V2BootstrapScriptResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Unable to parse bootstrap response.");

        var resolvedWorkspaceRoot = response.WorkspaceRoot ?? normalizedExecutionRoot;
        EnsureBootstrapTemplateContent(resolvedWorkspaceRoot);
        var gitState = TryInitializeGitRepository(resolvedWorkspaceRoot);

        RunCsvLogService.Append(resolvedWorkspaceRoot, runId, "初始化", RunCsvLogService.BuildDetails(
            ("事件", "初始化脚本执行完成"),
            ("脚本", scriptPath),
            ("工作区", resolvedWorkspaceRoot),
            ("元数据", response.MetadataFile),
            ("路径策略", response.AllowedPathsFile),
            ("工具策略", response.AllowedToolsFile),
            ("URL策略", response.AllowedUrlsFile),
            ("脚本声明Git仓库存在", response.GitRepositoryPresent),
            ("脚本声明RepoOPS执行Git初始化", response.GitInitializedByRepoOps),
            ("最终Git仓库存在", gitState.GitRepositoryPresent),
            ("最终RepoOPS执行Git初始化", gitState.GitInitializedByRepoOps)));

        return new V2WorkspaceBootstrapResult(
            response.ExecutionRoot ?? normalizedExecutionRoot,
            resolvedWorkspaceRoot,
            response.WorkspaceName ?? Path.GetFileName(resolvedWorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            response.MetadataFile,
            response.AllowedPathsFile,
            response.AllowedToolsFile,
            response.AllowedUrlsFile,
            gitState.GitRepositoryPresent,
            response.GitInitializedByRepoOps || gitState.GitInitializedByRepoOps);
    }

    private static string ResolveWorkspaceName(string goal, string? requestedWorkspaceName)
    {
        var source = string.IsNullOrWhiteSpace(requestedWorkspaceName)
            ? goal
            : requestedWorkspaceName!;

        if (!string.IsNullOrWhiteSpace(requestedWorkspaceName))
        {
            var explicitName = TryNormalizeWorkspaceNameCandidate(source);
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                return explicitName;
            }
        }

        return BuildAiStyleWorkspaceName(source, goal);
    }

    public static string? TryNormalizeWorkspaceNameCandidate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var candidate = raw.Trim().ToLowerInvariant();
        candidate = candidate
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        var filtered = new string(candidate.Where(char.IsAsciiLetter).ToArray());

        if (filtered.Length < 5)
        {
            return null;
        }

        return NameCandidateRegex.IsMatch(filtered) ? filtered : null;
    }

    private static string BuildAiStyleWorkspaceName(string source, string goal)
    {
        var tokens = new List<string>();
        var lowered = (source ?? string.Empty).Trim().ToLowerInvariant();
        var fallbackContext = string.IsNullOrWhiteSpace(goal) ? source : goal;

        foreach (var kvp in GoalKeywordMap)
        {
            if (lowered.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(kvp.Value);
            }
        }

        foreach (Match match in AsciiTokenRegex.Matches(lowered))
        {
            var token = match.Value.Trim();
            if (token.Length >= 2 && !StopWords.Contains(token))
            {
                tokens.Add(token);
            }

            if (tokens.Count >= 12)
            {
                break;
            }
        }

        var normalizedTokens = tokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        var suffix = CreateStableSuffix(fallbackContext);
        if (normalizedTokens.Count == 0)
        {
            return $"task{suffix}";
        }

        var compact = string.Concat(normalizedTokens.Select(token => ShrinkToken(token, 12)));
        if (compact.Length < 5)
        {
            compact += suffix;
        }

        if (compact.Length < 5)
        {
            compact = $"task{suffix}";
        }

        return compact;
    }

    private static string ShrinkToken(string token, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var cleaned = new string(token.ToLowerInvariant().Where(char.IsAsciiLetter).ToArray());
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static string CreateStableSuffix(string? value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        var chars = new char[6];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)('a' + (bytes[i] % 26));
        }

        return new string(chars);
    }

    private static string? ResolveBootstrapScriptPath()
    {
        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && current is not null; i++)
        {
            candidates.Add(Path.Combine(current.FullName, "scripts", "Initialize-TaskWorkspace.ps1"));
            current = current.Parent;
        }

        var cwd = Directory.GetCurrentDirectory();
        candidates.Add(Path.Combine(cwd, "scripts", "Initialize-TaskWorkspace.ps1"));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveBootstrapTemplateRootPath()
    {
        var scriptPath = ResolveBootstrapScriptPath();
        if (!string.IsNullOrWhiteSpace(scriptPath))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(scriptPath) ?? string.Empty, BootstrapTemplateDirectoryName);
            if (Directory.Exists(sibling))
            {
                return sibling;
            }
        }

        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && current is not null; i++)
        {
            candidates.Add(Path.Combine(current.FullName, "scripts", BootstrapTemplateDirectoryName));
            current = current.Parent;
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "scripts", BootstrapTemplateDirectoryName));
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static void EnsureBootstrapTemplateContent(string workspaceRoot)
    {
        var templateRoot = ResolveBootstrapTemplateRootPath();
        if (string.IsNullOrWhiteSpace(templateRoot) || !Directory.Exists(templateRoot))
        {
            return;
        }

        CopyDirectoryContents(templateRoot, workspaceRoot);
    }

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destinationFile = Path.Combine(destinationDirectory, relative);
            var parent = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (!File.Exists(destinationFile))
            {
                File.Copy(file, destinationFile, overwrite: false);
            }
        }
    }

    private static GitInitState TryInitializeGitRepository(string workspaceRoot)
    {
        if (Directory.Exists(Path.Combine(workspaceRoot, ".git")))
        {
            return new GitInitState(true, false);
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workspaceRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("init");
            process.Start();
            process.WaitForExit(5000);
            var initialized = process.ExitCode == 0 && Directory.Exists(Path.Combine(workspaceRoot, ".git"));
            return new GitInitState(initialized, initialized);
        }
        catch
        {
            // Ignore bootstrap git failures; the workspace should still be usable.
            return new GitInitState(Directory.Exists(Path.Combine(workspaceRoot, ".git")), false);
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

    private static V2PolicyFiles EnsurePolicyFiles(
        string workspaceRoot,
        string executionRoot,
        string goal,
        IReadOnlyCollection<string> allowedPaths,
        IReadOnlyCollection<string> allowedTools,
        IReadOnlyCollection<string> allowedUrls,
        string? workspaceName)
    {
        var policyDir = Path.Combine(workspaceRoot, ".repoops", "v2", "policy");
        Directory.CreateDirectory(policyDir);

        var allowedPathsFile = Path.Combine(policyDir, "allowed-paths.txt");
        var allowedToolsFile = Path.Combine(policyDir, "allowed-tools.txt");
        var allowedUrlsFile = Path.Combine(policyDir, "allowed-urls.txt");

        File.WriteAllLines(allowedPathsFile, allowedPaths);
        File.WriteAllLines(allowedToolsFile, allowedTools);
        File.WriteAllLines(allowedUrlsFile, allowedUrls);

        EnsureCopilotPathSettings(workspaceRoot, allowedPathsFile, allowedPaths);

        var metadataDir = Path.Combine(workspaceRoot, ".repoops", "v2");
        Directory.CreateDirectory(metadataDir);
        var metadataPath = Path.Combine(metadataDir, "workspace-metadata.json");
        var payload = JsonSerializer.Serialize(new
        {
            workspaceRoot,
            workspaceName = workspaceName ?? Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            executionRoot,
            goal,
            policy = new
            {
                allowedPathsFile,
                allowedToolsFile,
                allowedUrlsFile
            },
            createdAtUtc = DateTime.UtcNow
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(metadataPath, payload);

        return new V2PolicyFiles(metadataPath, allowedPathsFile, allowedToolsFile, allowedUrlsFile);
    }

    private static void EnsureCopilotPathSettings(string workspaceRoot, string allowedPathsFile, IReadOnlyCollection<string> allowedPaths)
    {
        var copilotDir = Path.Combine(workspaceRoot, ".github", "copilot");
        Directory.CreateDirectory(copilotDir);

        var trustedFolders = allowedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!trustedFolders.Contains(workspaceRoot, StringComparer.OrdinalIgnoreCase))
        {
            trustedFolders.Insert(0, workspaceRoot);
        }

        var settingsLocalPath = Path.Combine(copilotDir, "settings.local.json");
        var payload = JsonSerializer.Serialize(new
        {
            trusted_folders = trustedFolders,
            repoops = new
            {
                allowed_paths = trustedFolders,
                allowed_paths_file = allowedPathsFile,
                generated_by = "RepoOPS.V2WorkspaceBootstrapService",
                updated_at_utc = DateTime.UtcNow
            }
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(settingsLocalPath, payload, new UTF8Encoding(false));
    }

    private sealed class V2BootstrapScriptResponse
    {
        public string? WorkspaceRoot { get; set; }
        public string? WorkspaceName { get; set; }
        public string? ExecutionRoot { get; set; }
        public bool GitRepositoryPresent { get; set; }
        public bool GitInitializedByRepoOps { get; set; }
        public string? AllowedPathsFile { get; set; }
        public string? AllowedToolsFile { get; set; }
        public string? AllowedUrlsFile { get; set; }
        public string? MetadataFile { get; set; }
    }

    private sealed record V2PolicyFiles(string MetadataFile, string AllowedPathsFile, string AllowedToolsFile, string AllowedUrlsFile);
    private sealed record GitInitState(bool GitRepositoryPresent, bool GitInitializedByRepoOps);
}

public sealed record V2WorkspaceBootstrapResult(
    string ExecutionRoot,
    string WorkspaceRoot,
    string WorkspaceName,
    string? WorkspaceMetadataFile,
    string? AllowedPathsFile,
    string? AllowedToolsFile,
    string? AllowedUrlsFile,
    bool GitRepositoryPresent = false,
    bool GitInitializedByRepoOps = false);
