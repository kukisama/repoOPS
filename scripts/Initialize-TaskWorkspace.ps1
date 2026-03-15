param(
    [Parameter(Mandatory = $true)]
    [string]$TaskRoot,

    [Parameter(Mandatory = $true)]
    [string]$Goal,

    [string]$WorkspaceName,
    [string]$RunId,
    [string]$AllowedPathsJson = '[]',
    [string]$AllowedToolsJson = '[]',
    [string]$AllowedUrlsJson = '[]'
)

$ErrorActionPreference = 'Stop'

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        return
    }

    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        $targetPath = Join-Path $Destination $_.Name
        if ($_.PSIsContainer) {
            New-Item -ItemType Directory -Path $targetPath -Force | Out-Null
            Copy-DirectoryContents -Source $_.FullName -Destination $targetPath
            return
        }

        $parent = Split-Path -Parent $targetPath
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }

        if (-not (Test-Path -LiteralPath $targetPath)) {
            Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Force
        }
    }
}

function Initialize-GitRepositoryIfNeeded {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkspaceRoot
    )

    if (Test-Path -LiteralPath (Join-Path $WorkspaceRoot '.git')) {
        return [ordered]@{
            gitRepositoryPresent = $true
            gitInitializedByRepoOps = $false
        }
    }

    try {
        $process = Start-Process -FilePath 'git' -ArgumentList 'init' -WorkingDirectory $WorkspaceRoot -NoNewWindow -PassThru -Wait -ErrorAction Stop
        $initialized = $process.ExitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $WorkspaceRoot '.git'))
        return [ordered]@{
            gitRepositoryPresent = $initialized
            gitInitializedByRepoOps = $initialized
        }
    }
    catch {
        return [ordered]@{
            gitRepositoryPresent = Test-Path -LiteralPath (Join-Path $WorkspaceRoot '.git')
            gitInitializedByRepoOps = $false
        }
    }
}

function Convert-JsonArray([string]$jsonText) {
    if ([string]::IsNullOrWhiteSpace($jsonText)) { return @() }
    try {
        $parsed = $jsonText | ConvertFrom-Json
        if ($parsed -is [System.Array]) {
            return @($parsed | ForEach-Object { "$_" })
        }

        if ($null -eq $parsed) { return @() }
        return @("$parsed")
    }
    catch {
        return @()
    }
}

function Escape-CsvField([string]$value) {
    if ($null -eq $value) {
        $value = ''
    }

    '"{0}"' -f ($value -replace '"', '""')
}

function Convert-ToLogDetail {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [object[]]$Pairs
    )

    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($pair in $Pairs) {
        if ($null -eq $pair) {
            continue
        }

        $key = "$($pair[0])"
        $value = if ($pair.Count -gt 1) { "$($pair[1])" } else { '' }
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        $normalized = $value.Replace("`r`n", ' | ').Replace("`n", ' | ').Replace("`r", ' | ').Trim()
        if ([string]::IsNullOrWhiteSpace($normalized)) {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($key)) {
            $parts.Add($normalized)
        }
        else {
            $parts.Add(('{0}={1}' -f $key, $normalized))
        }
    }

    return [string]::Join('；', $parts)
}

function Write-RunCsvLog {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkspaceRoot,

        [Parameter(Mandatory = $true)]
        [string]$Type,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    if ([string]::IsNullOrWhiteSpace($RunId)) {
        return
    }

    $logDir = Join-Path $WorkspaceRoot '.repoops\logs\runs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $logPath = Join-Path $logDir ("{0}.log" -f $RunId)

    $utf8Bom = [System.Text.UTF8Encoding]::new($true)
    if (-not (Test-Path -LiteralPath $logPath)) {
        [System.IO.File]::WriteAllText($logPath, "时间戳,类型,事件内容`r`n", $utf8Bom)
    }

    $timestamp = [DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff zzz')
    $line = [string]::Join(',', @(
            (Escape-CsvField $timestamp),
            (Escape-CsvField $Type),
            (Escape-CsvField $Content)
        ))
    [System.IO.File]::AppendAllText($logPath, $line + "`r`n", $utf8Bom)
}

function Normalize-Name([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return '' }
    $lower = $value.Trim().ToLowerInvariant()
    $chars = New-Object System.Collections.Generic.List[char]
    $lastDash = $true
    foreach ($c in $lower.ToCharArray()) {
        $code = [int][char]$c
        $isAsciiDigit = ($code -ge 48 -and $code -le 57)
        $isAsciiLower = ($code -ge 97 -and $code -le 122)
        if ($isAsciiDigit -or $isAsciiLower) {
            $chars.Add($c)
            $lastDash = $false
            continue
        }

        if (-not $lastDash) {
            $chars.Add('-')
            $lastDash = $true
        }
    }

    $name = (-join $chars).Trim('-')
    if ($name.Length -gt 24) { $name = $name.Substring(0, 24).Trim('-') }
    return $name
}

function Get-StableSuffix([string]$value) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($value ?? '')
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    $hex = [Convert]::ToHexString($hash).ToLowerInvariant()
    return $hex.Substring(0, 6)
}

function Ensure-UniqueName([string]$root, [string]$preferred) {
    $candidate = $preferred
    $index = 1
    while (Test-Path -LiteralPath (Join-Path $root $candidate)) {
        $candidate = "{0}-{1}" -f $preferred, $index
        if ($candidate.Length -gt 24) {
            $head = $preferred.Substring(0, [Math]::Min($preferred.Length, 20)).Trim('-')
            $candidate = "{0}-{1}" -f $head, $index
        }
        $index++
    }

    return $candidate
}

$resolvedTaskRoot = [System.IO.Path]::GetFullPath($TaskRoot)
New-Item -ItemType Directory -Path $resolvedTaskRoot -Force | Out-Null

$AllowedPaths = Convert-JsonArray $AllowedPathsJson
$AllowedTools = Convert-JsonArray $AllowedToolsJson
$AllowedUrls = Convert-JsonArray $AllowedUrlsJson

$workspace = Normalize-Name $WorkspaceName
if ([string]::IsNullOrWhiteSpace($workspace)) {
    $workspace = Normalize-Name $Goal
}
if ([string]::IsNullOrWhiteSpace($workspace) -or $workspace.Length -lt 4) {
    $workspace = "task-{0}" -f (Get-StableSuffix $Goal)
}

$workspace = Ensure-UniqueName $resolvedTaskRoot $workspace

$workspaceRoot = Join-Path $resolvedTaskRoot $workspace
New-Item -ItemType Directory -Path $workspaceRoot -Force | Out-Null

Write-RunCsvLog -WorkspaceRoot $workspaceRoot -Type '初始化' -Content (Convert-ToLogDetail @(
        '事件', '初始化脚本已启动'
    ) @(
        '脚本', $PSCommandPath
    ) @(
        '任务根目录', $resolvedTaskRoot
    ) @(
        '工作区', $workspaceRoot
    ) @(
        '目标', $Goal
    ))

$scriptRoot = if ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { Join-Path $resolvedTaskRoot 'scripts' }
$bootstrapTemplateRoot = Join-Path $scriptRoot 'bootstrap-template'
Copy-DirectoryContents -Source $bootstrapTemplateRoot -Destination $workspaceRoot

Write-RunCsvLog -WorkspaceRoot $workspaceRoot -Type '初始化' -Content (Convert-ToLogDetail @(
        '事件', '稳定模板复制完成'
    ) @(
        '模板源', $bootstrapTemplateRoot
    ) @(
        '工作区', $workspaceRoot
    ))

$gitState = Initialize-GitRepositoryIfNeeded -WorkspaceRoot $workspaceRoot

Write-RunCsvLog -WorkspaceRoot $workspaceRoot -Type '初始化' -Content (Convert-ToLogDetail @(
        '事件', 'Git检查完成'
    ) @(
        '工作区', $workspaceRoot
    ) @(
        'Git仓库存在', $gitState.gitRepositoryPresent
    ) @(
        'RepoOPS执行Git初始化', $gitState.gitInitializedByRepoOps
    ))

$githubDir = Join-Path $workspaceRoot '.github\copilot'
New-Item -ItemType Directory -Path $githubDir -Force | Out-Null

$instructionsPath = Join-Path $githubDir 'copilot-instructions.md'
if (-not (Test-Path $instructionsPath)) {
    @"
# Workspace Rules

## Scope
- Work only inside the current workspace root unless the user explicitly expands it.
- Prefer the files under ``.repoops/v2`` for run metadata and policy snapshots.

## 交付目录规则（严格执行）
- 所有业务交付物（文档、代码、配置、方案等）必须写到工作目录根 ``/`` 或其下的业务子目录（如 ``docs/``、``src/`` 等）。
- **绝对禁止**将业务交付物写入 ``.repoops/``、``.github/`` 或任何以 ``.`` 开头的隐藏目录。
- ``.repoops/`` 目录仅供系统内部元数据使用，你只能从中读取任务单，不能往里写业务产出。
- 如果不确定往哪写，就写到工作目录根。

## 变更日志规范（每个 Agent 必须遵守）

每个 Agent 在每轮执行后**必须**记录变更日志。

### 日志文件规则
- 文件名格式：``<角色ID>-变更日志.md``（例如 ``planner-变更日志.md``、``builder-a-变更日志.md``），放在工作目录根。
- 仅追加，不得删改历史内容。新日志追加到文末。

### 日志格式（固定）

``````markdown
## 第 N 轮操作

### 实现目标
- （变更 / 修复 / 优化目标）

### 变更内容
- （改动点）

### 验证结果（可选）

### 后续计划（可选）
``````

### 产出交付要求
- 每轮执行必须产出可见文件（md 文档、代码文件、配置等），不允许"只做规划不落文件"。
- Planner 轮次的最低交付：一份结构化方案文档（放在工作目录根或 ``docs/`` 下）。
- 所有业务产出应放在工作目录根或专用子目录，不要写入 ``.repoops`` 或 ``.github`` 内部。
"@ | Set-Content -Path $instructionsPath -Encoding UTF8
}

$gitIgnorePath = Join-Path $workspaceRoot '.gitignore'
if (-not (Test-Path $gitIgnorePath)) {
    @"
*.user
*.tmp
bin/
obj/
.github/copilot/settings.local.json
"@ | Set-Content -Path $gitIgnorePath -Encoding UTF8
}

$repoopsV2Dir = Join-Path $workspaceRoot '.repoops\v2'
$policyDir = Join-Path $repoopsV2Dir 'policy'
New-Item -ItemType Directory -Path $policyDir -Force | Out-Null

$allowedPathsFile = Join-Path $policyDir 'allowed-paths.txt'
$allowedToolsFile = Join-Path $policyDir 'allowed-tools.txt'
$allowedUrlsFile = Join-Path $policyDir 'allowed-urls.txt'

@($AllowedPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) | Set-Content -Path $allowedPathsFile -Encoding UTF8
@($AllowedTools | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) | Set-Content -Path $allowedToolsFile -Encoding UTF8
@($AllowedUrls | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) | Set-Content -Path $allowedUrlsFile -Encoding UTF8

$pathLines = @($workspaceRoot) + @($AllowedPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$pathLines | Select-Object -Unique | Set-Content -Path $allowedPathsFile -Encoding UTF8

$settingsLocalPath = Join-Path $githubDir 'settings.local.json'
$trustedFolders = @($pathLines | Select-Object -Unique)
$settingsPayload = [ordered]@{
    trusted_folders = $trustedFolders
    repoops = [ordered]@{
        allowed_paths = $trustedFolders
        allowed_paths_file = $allowedPathsFile
        generated_by = 'RepoOPS.Initialize-TaskWorkspace.ps1'
        updated_at_utc = [DateTime]::UtcNow.ToString('o')
    }
}
$settingsPayload | ConvertTo-Json -Depth 8 | Set-Content -Path $settingsLocalPath -Encoding UTF8

$metadataPath = Join-Path $repoopsV2Dir 'workspace-metadata.json'
$metadata = [ordered]@{
    workspaceRoot = $workspaceRoot
    workspaceName = $workspace
    executionRoot = $resolvedTaskRoot
    goal = $Goal
    gitRepositoryPresent = $gitState.gitRepositoryPresent
    gitInitializedByRepoOps = $gitState.gitInitializedByRepoOps
    policy = [ordered]@{
        allowedPathsFile = $allowedPathsFile
        allowedToolsFile = $allowedToolsFile
        allowedUrlsFile = $allowedUrlsFile
    }
    createdAtUtc = [DateTime]::UtcNow.ToString('o')
}
$metadata | ConvertTo-Json -Depth 8 | Set-Content -Path $metadataPath -Encoding UTF8

Write-RunCsvLog -WorkspaceRoot $workspaceRoot -Type '初始化' -Content (Convert-ToLogDetail @(
        '事件', '初始化脚本执行完成'
    ) @(
        '工作区', $workspaceRoot
    ) @(
        '元数据', $metadataPath
    ) @(
        '路径策略', $allowedPathsFile
    ) @(
        '工具策略', $allowedToolsFile
    ) @(
        'URL策略', $allowedUrlsFile
    ))

$result = [ordered]@{
    workspaceRoot = $workspaceRoot
    workspaceName = $workspace
    executionRoot = $resolvedTaskRoot
    gitRepositoryPresent = $gitState.gitRepositoryPresent
    gitInitializedByRepoOps = $gitState.gitInitializedByRepoOps
    allowedPathsFile = $allowedPathsFile
    allowedToolsFile = $allowedToolsFile
    allowedUrlsFile = $allowedUrlsFile
    metadataFile = $metadataPath
}
$result | ConvertTo-Json -Depth 8 -Compress
