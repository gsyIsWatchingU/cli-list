param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallDirectory = (Join-Path $env:USERPROFILE '.cli-list')
)

$ErrorActionPreference = 'Stop'

# 源码开发模式同步：源码比安装目录新时，重新编译并同步。
# 退出码 0 表示可以启动（已同步或无需同步）；非 0 表示同步失败，调用方应回落到现有安装版。
# 注意：本脚本自带同步逻辑，不依赖 .source-repository 标记（该标记只由 install.ps1 写入）。

$sourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$installDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$sourceCodePath = Join-Path $sourceDirectory 'CLIList.cs'
$installedExecutablePath = Join-Path $installDirectory 'CLIList.exe'

if (-not (Test-Path -LiteralPath $sourceCodePath)) {
    exit 0
}

if (-not (Test-Path -LiteralPath $installDirectory)) {
    exit 0
}

$syncCandidates = @(
    (Join-Path $sourceDirectory 'CLIList.cs'),
    (Join-Path $sourceDirectory 'cli-list.ico'),
    (Join-Path $sourceDirectory 'build.ps1')
)

function Test-NeedsSync {
    if (-not (Test-Path -LiteralPath $installedExecutablePath)) {
        return $true
    }
    $installedWriteTime = (Get-Item -LiteralPath $installedExecutablePath).LastWriteTimeUtc
    foreach ($candidate in $syncCandidates) {
        if ((Test-Path -LiteralPath $candidate) -and
            (Get-Item -LiteralPath $candidate).LastWriteTimeUtc -gt $installedWriteTime) {
            return $true
        }
    }
    return $false
}
if (-not (Test-NeedsSync)) {
    exit 0
}

# 并发保护：两个入口同时启动时只有一个负责编译，其余直接回落启动现有版本。
$lockPath = Join-Path $installDirectory '.dev-sync.lock'
$lockStream = $null
$hasLock = $false
try {
    $lockStream = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $hasLock = $true
}
catch {
    $hasLock = $false
}

if (-not $hasLock) {
    exit 0
}

function Copy-RuntimeFile {
    param(
        [Parameter(Mandatory = $true)][string]$FileName
    )
    $from = Join-Path $sourceDirectory $FileName
    if (Test-Path -LiteralPath $from) {
        for ($attempt = 1; $attempt -le 10; $attempt++) {
            try {
                Copy-Item -LiteralPath $from -Destination (Join-Path $installDirectory $FileName) -Force
                return
            }
            catch {
                if ($attempt -eq 10) { throw }
                Start-Sleep -Milliseconds 300
            }
        }
    }
}

$exitCode = 0
try {
    # 抢到锁后重新判断一次：可能另一个进程刚刚已经同步完成。
    if (Test-NeedsSync) {
        # 停掉正在运行的实例，避免文件占用导致复制失败。
        $running = Get-Process -Name 'CLIList' -ErrorAction SilentlyContinue
        if ($running) {
            $running | Stop-Process -Force
            $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
        }

        & (Join-Path $sourceDirectory 'build.ps1') -SkipInstalledSync
        if ($LASTEXITCODE -ne 0) {
            $exitCode = $LASTEXITCODE
        }
        else {
            foreach ($fileName in @(
                'CLIList.exe', 'CLIList.cs', 'cli-list.ico', 'cli-list.svg', 'commands.json',
                'gpu-trae.vbs', 'minimize-all.vbs',
                'skill-atlas-desktop.cmd', 'skill-atlas-desktop.vbs',
                'skill-atlas-dev.cmd', 'skill-atlas-dev.vbs',
                'tool-desk-start.cmd', 'tool-desk-start.vbs',
                'travel-test-start.cmd', 'travel-test-start.vbs',
                'update-helper.ps1', 'dev-sync.ps1', 'sync-installed.ps1', 'uninstall.ps1'
            )) {
                Copy-RuntimeFile -FileName $fileName
            }

            $binDirectory = Join-Path $env:USERPROFILE 'bin'
            if (-not (Test-Path -LiteralPath $binDirectory)) {
                New-Item -ItemType Directory -Path $binDirectory -Force | Out-Null
            }
            $cliCmd = Join-Path $sourceDirectory 'cli-list.cmd'
            if (Test-Path -LiteralPath $cliCmd) {
                Copy-Item -LiteralPath $cliCmd -Destination (Join-Path $binDirectory 'cli-list.cmd') -Force
            }

            # 同步完成后写回源码仓库标记，让「检查更新」也走源码模式。
            Set-Content -LiteralPath (Join-Path $installDirectory '.source-repository') -Value $sourceDirectory -Encoding UTF8

            # 对齐时间戳：让安装版 exe 的时间不早于参与判断的源码文件，
            # 否则源码时间戳持续领先会导致每次启动都重复编译。
            $latestSourceWriteTime = (Get-Item -LiteralPath $installedExecutablePath).LastWriteTimeUtc
            foreach ($candidate in $syncCandidates) {
                if ((Test-Path -LiteralPath $candidate) -and
                    (Get-Item -LiteralPath $candidate).LastWriteTimeUtc -gt $latestSourceWriteTime) {
                    $latestSourceWriteTime = (Get-Item -LiteralPath $candidate).LastWriteTimeUtc
                }
            }
            (Get-Item -LiteralPath $installedExecutablePath).LastWriteTimeUtc = $latestSourceWriteTime
        }
    }
}
catch {
    Write-Error $_
    $exitCode = 1
}
finally {
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
}

exit $exitCode
