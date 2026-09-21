param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Release', 'Source')]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$CurrentDirectory,

    [Parameter(Mandatory = $true)]
    [int]$CurrentProcessId,

    [Parameter(Mandatory = $true)]
    [string]$WorkDirectory,

    [string]$StageDirectory,
    [string]$BackupDirectory,
    [string]$SourceDirectory,
    [switch]$SkipRestart,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$currentPath = [IO.Path]::GetFullPath($CurrentDirectory).TrimEnd('\', '/')
$currentExecutable = Join-Path $currentPath 'CLIList.exe'
$currentMoved = $false
$newVersionActivated = $false

function Show-UpdateError {
    param([Parameter(Mandatory = $true)][string]$Message)

    if ($Quiet) {
        return
    }

    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        "CLI List 更新失败，已保留或恢复原版本。`r`n`r`n$Message",
        'CLI List 更新',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    ) | Out-Null
}

function Start-InstalledApp {
    if (-not $SkipRestart -and (Test-Path -LiteralPath $currentExecutable)) {
        Start-Process -FilePath $currentExecutable -ArgumentList '--resident' -WorkingDirectory $currentPath
    }
}

function Wait-ForCurrentProcess {
    if ($CurrentProcessId -le 0) {
        return
    }

    $currentProcess = Get-Process -Id $CurrentProcessId -ErrorAction SilentlyContinue
    if ($null -ne $currentProcess) {
        Wait-Process -Id $CurrentProcessId -Timeout 20
    }
}

function Assert-Success {
    param(
        [Parameter(Mandatory = $true)][string]$Action,
        [Parameter(Mandatory = $true)][int]$ExitCode
    )

    if ($ExitCode -ne 0) {
        throw "$Action 失败，退出码：$ExitCode"
    }
}

function Invoke-SourceUpdate {
    if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
        throw '缺少源码仓库路径。'
    }

    $sourcePath = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath (Join-Path $sourcePath '.git'))) {
        throw "源码仓库不存在：$sourcePath"
    }

    $branch = (& git -C $sourcePath branch --show-current | Out-String).Trim()
    Assert-Success -Action '读取 Git 分支' -ExitCode $LASTEXITCODE
    if ($branch -ne 'main') {
        throw "当前分支是 $branch；一键更新只允许更新 main 分支。"
    }

    $changes = @(& git -C $sourcePath status --porcelain --untracked-files=normal)
    Assert-Success -Action '检查工作区状态' -ExitCode $LASTEXITCODE
    if ($changes.Count -gt 0) {
        throw '源码仓库存在未提交修改，已停止更新。请先提交或处理这些修改。'
    }

    & git -C $sourcePath fetch origin main
    Assert-Success -Action '拉取 GitHub 更新' -ExitCode $LASTEXITCODE
    & git -C $sourcePath merge --ff-only origin/main
    Assert-Success -Action '快进 main 分支' -ExitCode $LASTEXITCODE

    & (Join-Path $sourcePath 'test.ps1') -SkipInstalledSync
    & (Join-Path $sourcePath 'sync-installed.ps1') -SkipBuild -NoRestart
}

function Invoke-ReleaseUpdate {
    if ([string]::IsNullOrWhiteSpace($StageDirectory) -or [string]::IsNullOrWhiteSpace($BackupDirectory)) {
        throw '缺少 Release 更新目录。'
    }

    $stagePath = [IO.Path]::GetFullPath($StageDirectory).TrimEnd('\', '/')
    $backupPath = [IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\', '/')
    $parentPath = [IO.Path]::GetDirectoryName($currentPath)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($stagePath), $parentPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetDirectoryName($backupPath), $parentPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not $stagePath.StartsWith($currentPath + '.__stage.', [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($backupPath, $currentPath + '.__backup', [StringComparison]::OrdinalIgnoreCase)) {
        throw '更新目录校验失败。'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $stagePath 'CLIList.exe'))) {
        throw '暂存目录缺少 CLIList.exe。'
    }

    if (Test-Path -LiteralPath $backupPath) {
        Remove-Item -LiteralPath $backupPath -Recurse -Force
    }

    Move-Item -LiteralPath $currentPath -Destination $backupPath
    $script:currentMoved = $true
    Move-Item -LiteralPath $stagePath -Destination $currentPath
    $script:newVersionActivated = $true

    $validationProcess = Start-Process -FilePath $currentExecutable -ArgumentList '--validate' -WorkingDirectory $currentPath -Wait -PassThru
    Assert-Success -Action '验证新版本' -ExitCode $validationProcess.ExitCode
}

try {
    if (-not (Test-Path -LiteralPath $currentExecutable)) {
        throw "当前安装目录无效：$currentPath"
    }

    Wait-ForCurrentProcess

    if ($Mode -eq 'Source') {
        Invoke-SourceUpdate
    }
    else {
        Invoke-ReleaseUpdate
    }

    Start-InstalledApp
    exit 0
}
catch {
    $failureMessage = $_.Exception.Message

    if ($Mode -eq 'Release' -and $currentMoved) {
        try {
            $backupPath = [IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\', '/')
            if ($newVersionActivated -and (Test-Path -LiteralPath $currentPath)) {
                $failedPath = [IO.Path]::GetFullPath($StageDirectory).TrimEnd('\', '/') + '.__failed'
                if (Test-Path -LiteralPath $failedPath) {
                    Remove-Item -LiteralPath $failedPath -Recurse -Force
                }
                Move-Item -LiteralPath $currentPath -Destination $failedPath
            }
            if (Test-Path -LiteralPath $backupPath) {
                Move-Item -LiteralPath $backupPath -Destination $currentPath
            }
        }
        catch {
            $failureMessage += "`r`n自动回滚失败：$($_.Exception.Message)"
        }
    }

    Show-UpdateError -Message $failureMessage
    Start-InstalledApp
    exit 1
}
