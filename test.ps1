param(
    [switch]$SkipBuild,
    [switch]$SkipInstalledSync
)

$ErrorActionPreference = 'Stop'

foreach ($scriptPath in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($scriptPath.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) {
        throw "PowerShell 脚本语法检查失败：$($scriptPath.Name) - $($parseErrors[0].Message)"
    }
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipInstalledSync:$SkipInstalledSync
}

$executablePath = Join-Path $PSScriptRoot 'CLIList.exe'
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "未找到构建产物：$executablePath"
}

$process = Start-Process -FilePath $executablePath -ArgumentList '--validate' -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "CLI List 验证失败，退出码：$($process.ExitCode)"
}

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('cli-list-config-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    Copy-Item -LiteralPath $executablePath -Destination (Join-Path $temporaryDirectory 'CLIList.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'commands.json') -Destination (Join-Path $temporaryDirectory 'commands.json')
    Set-Content -LiteralPath (Join-Path $temporaryDirectory 'commands.local.json') -Value '[{"Id":"open-explorer","Disabled":true}]' -Encoding UTF8
    $localConfigProcess = Start-Process -FilePath (Join-Path $temporaryDirectory 'CLIList.exe') -ArgumentList '--validate' -Wait -PassThru
    if ($localConfigProcess.ExitCode -ne 0) {
        throw "本机命令覆盖配置验证失败，退出码：$($localConfigProcess.ExitCode)"
    }
}
finally {
    $resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
    $resolvedSystemTemporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporaryDirectory.StartsWith($resolvedSystemTemporaryDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$updateHelperPath = Join-Path $PSScriptRoot 'update-helper.ps1'
$updateTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('cli-list-update-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $updateTestRoot | Out-Null
try {
    $currentDirectory = Join-Path $updateTestRoot 'CLIList'
    $stageDirectory = $currentDirectory + '.__stage.success'
    $backupDirectory = $currentDirectory + '.__backup'
    $workDirectory = Join-Path $updateTestRoot 'work-success'
    New-Item -ItemType Directory -Path $currentDirectory, $stageDirectory, $workDirectory | Out-Null

    foreach ($directory in @($currentDirectory, $stageDirectory)) {
        Copy-Item -LiteralPath $executablePath -Destination (Join-Path $directory 'CLIList.exe')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'commands.json') -Destination (Join-Path $directory 'commands.json')
    }
    Set-Content -LiteralPath (Join-Path $currentDirectory 'version-marker.txt') -Value 'old' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $stageDirectory 'version-marker.txt') -Value 'new' -Encoding UTF8

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $updateHelperPath `
        -Mode Release `
        -CurrentDirectory $currentDirectory `
        -CurrentProcessId 0 `
        -WorkDirectory $workDirectory `
        -StageDirectory $stageDirectory `
        -BackupDirectory $backupDirectory `
        -SkipRestart `
        -Quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Release 更新切换测试失败，退出码：$LASTEXITCODE"
    }
    if ((Get-Content -LiteralPath (Join-Path $currentDirectory 'version-marker.txt') -Raw).Trim() -ne 'new' -or
        (Get-Content -LiteralPath (Join-Path $backupDirectory 'version-marker.txt') -Raw).Trim() -ne 'old') {
        throw 'Release 更新没有正确切换新旧目录。'
    }

    $failureCurrentDirectory = Join-Path $updateTestRoot 'CLIListFail'
    $failureStageDirectory = $failureCurrentDirectory + '.__stage.failure'
    $failureBackupDirectory = $failureCurrentDirectory + '.__backup'
    $failureWorkDirectory = Join-Path $updateTestRoot 'work-failure'
    New-Item -ItemType Directory -Path $failureCurrentDirectory, $failureStageDirectory, $failureWorkDirectory | Out-Null
    Copy-Item -LiteralPath $executablePath -Destination (Join-Path $failureCurrentDirectory 'CLIList.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'commands.json') -Destination (Join-Path $failureCurrentDirectory 'commands.json')
    Set-Content -LiteralPath (Join-Path $failureCurrentDirectory 'version-marker.txt') -Value 'old' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $failureStageDirectory 'CLIList.exe') -Value 'invalid executable' -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'commands.json') -Destination (Join-Path $failureStageDirectory 'commands.json')
    Set-Content -LiteralPath (Join-Path $failureStageDirectory 'version-marker.txt') -Value 'broken' -Encoding UTF8

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $updateHelperPath `
        -Mode Release `
        -CurrentDirectory $failureCurrentDirectory `
        -CurrentProcessId 0 `
        -WorkDirectory $failureWorkDirectory `
        -StageDirectory $failureStageDirectory `
        -BackupDirectory $failureBackupDirectory `
        -SkipRestart `
        -Quiet
    $rollbackTestExitCode = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($rollbackTestExitCode -eq 0) {
        throw '损坏更新包应触发失败与回滚。'
    }
    if ((Get-Content -LiteralPath (Join-Path $failureCurrentDirectory 'version-marker.txt') -Raw).Trim() -ne 'old') {
        throw '更新失败后未恢复旧版本目录。'
    }
}
finally {
    $resolvedUpdateTestRoot = [IO.Path]::GetFullPath($updateTestRoot)
    $resolvedSystemTemporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedUpdateTestRoot.StartsWith($resolvedSystemTemporaryDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedUpdateTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output 'CLI List 构建与配置验证通过。'
