param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory = (Join-Path $env:USERPROFILE '.cli-list'),
    [int]$CallerPid = 0
)

<#
源码开发模式同步（任务栏 --dev-sync 专用入口）：
- 以「源码文件、构建脚本、共享配置、运行资源」的内容指纹判断安装版是否需要更新，
  不依赖文件时间戳；全程只检查本机源码目录，不联网、不拉取 GitHub。
- 指纹与源码路径保存在安装目录的 .dev-sync-state.json，只保存源码路径与指纹，
  不读取、不保存任何个人数据。
- 需要更新时先执行完整 test.ps1，测试通过后才停止旧进程、暂存并切换新程序；
  任何一步失败都自动恢复原程序（回滚），安装版始终保持可用。
- 始终保留 commands.local.json、usage.json、历史版本等个人文件。

退出码约定（供调用方 CLIList.exe DeveloperSync 使用）：
- 0：无需同步（安装版已是最新），调用方直接启动现有版本。
- 2：同步完成（安装版已更新），调用方应重启新版本。
- 1：同步失败（已自动回滚），调用方询问用户是否启动上次成功版本。
#>

$ErrorActionPreference = 'Stop'

$sourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$installDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$sourceCodePath = Join-Path $sourceDirectory 'CLIList.cs'
$installedExecutablePath = Join-Path $installDirectory 'CLIList.exe'
$statePath = Join-Path $installDirectory '.dev-sync-state.json'
$lockPath = Join-Path $installDirectory '.dev-sync.lock'
$stagedPath = Join-Path $installDirectory 'CLIList.exe.staged'
$previousPath = Join-Path $installDirectory 'CLIList.exe.previous'

if (-not (Test-Path -LiteralPath $sourceCodePath)) {
    exit 0
}
if (-not (Test-Path -LiteralPath $installDirectory)) {
    exit 0
}

# 参与指纹判断的文件：源码、构建脚本、共享配置与运行资源（全部位于源码目录）。
# 本机个人文件（commands.local.json、usage.json、历史版本等）不参与、不读取。
$fingerprintFiles = @(
    'CLIList.cs',
    'build.ps1',
    'test.ps1',
    'cli-list.ico',
    'cli-list.svg',
    'commands.json',
    'cli-list.cmd',
    'gpu-trae.vbs',
    'minimize-all.vbs',
    'skill-atlas-desktop.cmd',
    'skill-atlas-desktop.vbs',
    'skill-atlas-dev.cmd',
    'skill-atlas-dev.vbs',
    'tool-desk-start.cmd',
    'travel-test-start.cmd',
    'travel-test-start.vbs',
    'update-helper.ps1',
    'dev-sync.ps1',
    'sync-installed.ps1',
    'uninstall.ps1'
)

# 与 sync-installed.ps1 保持一致：同步到安装目录的运行资源清单。
$runtimeFiles = @(
    'CLIList.exe',
    'CLIList.cs',
    'cli-list.ico',
    'cli-list.svg',
    'commands.json',
    'gpu-trae.vbs',
    'minimize-all.vbs',
    'skill-atlas-desktop.cmd',
    'skill-atlas-desktop.vbs',
    'skill-atlas-dev.cmd',
    'skill-atlas-dev.vbs',
    'tool-desk-start.cmd',
    'travel-test-start.cmd',
    'travel-test-start.vbs',
    'update-helper.ps1',
    'dev-sync.ps1',
    'sync-installed.ps1',
    'uninstall.ps1'
)

function Get-SyncFingerprint {
    $entries = New-Object System.Collections.Generic.List[string]
    foreach ($name in $fingerprintFiles) {
        $path = Join-Path $sourceDirectory $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $entries.Add($name + "`t" + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
        }
    }
    $hasher = [Security.Cryptography.SHA256]::Create()
    $bytes = [Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))
    return ([BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-', '')
}

function Read-SyncState {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        return $null
    }
    try {
        return (Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
    catch {
        return $null
    }
}

function Test-NeedsSync {
    if (-not (Test-Path -LiteralPath $installedExecutablePath -PathType Leaf)) {
        return $true
    }
    $state = Read-SyncState
    if ($null -eq $state) {
        return $true
    }
    [string]$recordedSource = [string]$state.SourceDirectory
    [string]$recordedFingerprint = [string]$state.Fingerprint
    if ([string]::IsNullOrWhiteSpace($recordedSource) -or [string]::IsNullOrWhiteSpace($recordedFingerprint)) {
        return $true
    }
    if (-not [string]::Equals($recordedSource.Trim().TrimStart([char]0xFEFF), $sourceDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    if (-not [string]::Equals($recordedFingerprint.Trim(), (Get-SyncFingerprint), [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    return $false
}

function Copy-FileWithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force
            return
        }
        catch {
            if ($attempt -eq 10) {
                throw
            }
            Start-Sleep -Milliseconds 300
        }
    }
}

function Copy-RuntimeFile {
    param(
        [Parameter(Mandatory = $true)][string]$FileName
    )

    $from = Join-Path $sourceDirectory $FileName
    if (Test-Path -LiteralPath $from -PathType Leaf) {
        Copy-FileWithRetry -Source $from -Destination (Join-Path $installDirectory $FileName)
    }
}

# 并发保护：多个入口同时启动时只有一个负责同步，其余等待其完成后直接使用新版本。
# 等待上限 5 分钟；若超时仍未拿到锁，则不阻塞启动，直接使用现有安装版。
$lockStream = $null
$hasLock = $false
for ($attempt = 0; $attempt -lt 300; $attempt++) {
    try {
        $lockStream = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $hasLock = $true
        break
    }
    catch {
        Start-Sleep -Seconds 1
    }
}
if (-not $hasLock) {
    exit 0
}

$exitCode = 0
try {
    if (Test-NeedsSync) {
        # 完整测试（含构建与配置验证），测试通过前不动安装目录。
        & (Join-Path $sourceDirectory 'test.ps1') -SkipInstalledSync
        if ($LASTEXITCODE -ne 0) {
            throw "源码测试失败，退出码：$LASTEXITCODE"
        }

        # 测试通过后停止正在运行的实例（保留调用方进程，由调用方负责重启新版本），
        # 避免文件占用导致切换失败。只停运行「被替换安装路径」的实例，
        # 不误伤从其他路径启动的副本。
        $running = Get-Process -Name 'CLIList' -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Id -ne $CallerPid -and
                $null -ne $_.Path -and
                [string]::Equals($_.Path, $installedExecutablePath, [StringComparison]::OrdinalIgnoreCase)
            }
        if ($running) {
            $running | Stop-Process -Force
            $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
        }

        # 暂存并切换新程序：调用方进程仍占用 CLIList.exe，无法原地覆盖，
        # 因此先把旧版重命名让出原路径（运行中的进程可被重命名），再写入新版。
        Remove-Item -LiteralPath $stagedPath, $previousPath -Force -ErrorAction SilentlyContinue
        Copy-FileWithRetry -Source (Join-Path $sourceDirectory 'CLIList.exe') -Destination $stagedPath
        if (Test-Path -LiteralPath $installedExecutablePath -PathType Leaf) {
            Rename-Item -LiteralPath $installedExecutablePath -NewName 'CLIList.exe.previous' -Force
        }
        Copy-FileWithRetry -Source $stagedPath -Destination $installedExecutablePath
        Remove-Item -LiteralPath $stagedPath -Force -ErrorAction SilentlyContinue

        foreach ($fileName in $runtimeFiles) {
            Copy-RuntimeFile -FileName $fileName
        }
        # Desk X 开发模式必须显示控制台；移除旧版静默 VBS 入口。
        Remove-Item -LiteralPath (Join-Path $installDirectory 'tool-desk-start.vbs') -Force -ErrorAction SilentlyContinue

        $binDirectory = Join-Path $env:USERPROFILE 'bin'
        if (-not (Test-Path -LiteralPath $binDirectory)) {
            New-Item -ItemType Directory -Path $binDirectory -Force | Out-Null
        }
        $cliCmd = Join-Path $sourceDirectory 'cli-list.cmd'
        if (Test-Path -LiteralPath $cliCmd -PathType Leaf) {
            Copy-FileWithRetry -Source $cliCmd -Destination (Join-Path $binDirectory 'cli-list.cmd')
        }

        # 校验切换后的安装版可正常加载配置。
        $validationProcess = Start-Process -FilePath $installedExecutablePath -ArgumentList '--validate' -Wait -PassThru
        if ($validationProcess.ExitCode -ne 0) {
            throw "同步后的安装版验证失败，退出码：$($validationProcess.ExitCode)"
        }

        # 同步成功：记录源码路径与指纹（不包含任何个人数据）。
        $fingerprint = Get-SyncFingerprint
        $stateJson = @{ SourceDirectory = $sourceDirectory; Fingerprint = $fingerprint } | ConvertTo-Json -Compress
        Set-Content -LiteralPath $statePath -Value $stateJson -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $installDirectory '.source-repository') -Value $sourceDirectory -Encoding UTF8

        # 清理旧版备份。
        Remove-Item -LiteralPath $previousPath -Force -ErrorAction SilentlyContinue

        $exitCode = 2
    }
}
catch {
    # 同步失败：自动恢复原程序（回滚）。旧版可能已被重命名到 previous，
    # 新版可能已写入 installed：移除新版后把旧版改回原位。
    $failure = $_
    try {
        if (Test-Path -LiteralPath $previousPath -PathType Leaf) {
            if (Test-Path -LiteralPath $installedExecutablePath -PathType Leaf) {
                Remove-Item -LiteralPath $installedExecutablePath -Force -ErrorAction SilentlyContinue
            }
            Rename-Item -LiteralPath $previousPath -NewName 'CLIList.exe' -Force
        }
        Remove-Item -LiteralPath $stagedPath -Force -ErrorAction SilentlyContinue
    }
    catch {
        Write-Output ("回滚异常：" + $_.Exception.Message)
    }
    Write-Output ('源码同步失败：' + $failure.Exception.Message)
    $exitCode = 1
}
finally {
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
}

exit $exitCode
