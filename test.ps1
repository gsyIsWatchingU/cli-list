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

$sourceText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CLIList.cs') -Raw
if ($sourceText -notmatch 'BeginSilentCheck\(tagName\s*=>') {
    throw 'CLI List 启动流程缺少静默更新检查。'
}
if ($sourceText -notmatch 'new ToolStripMenuItem\("检查更新…"\)' -or
    $sourceText -notmatch 'CreateFooterButton\("检查更新"\)') {
    throw 'CLI List 的托盘或主窗口缺少常驻“检查更新”入口。'
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

    $sharedConfigHash = (Get-FileHash -LiteralPath (Join-Path $temporaryDirectory 'commands.json') -Algorithm SHA256).Hash
    $patchPath = Join-Path $temporaryDirectory 'ai-patch.json'
    @'
{
  "version": 1,
  "ops": [
    {"op":"update","id":"open-powershell","fields":{"Description":"AI 增量修改测试"}},
    {"op":"delete","id":"open-vscode"},
    {"op":"add","fields":{"Name":"测试命令","Description":"仅用于自动化验证","Tags":["测试"],"Executable":"cmd.exe","Arguments":"/c echo ok","WorkingDirectory":"{context}","CloseAfterLaunch":true}}
  ]
}
'@ | Set-Content -LiteralPath $patchPath -Encoding UTF8
    $patchProcess = Start-Process -FilePath (Join-Path $temporaryDirectory 'CLIList.exe') -ArgumentList @('--apply-ai-patch', $patchPath) -Wait -PassThru
    if ($patchProcess.ExitCode -ne 0) {
        throw "AI 增量命令应用失败，退出码：$($patchProcess.ExitCode)"
    }

    $localCommands = @(Get-Content -LiteralPath (Join-Path $temporaryDirectory 'commands.local.json') -Raw | ConvertFrom-Json)
    $updatedCommand = $localCommands | Where-Object Id -eq 'open-powershell'
    $hiddenCommand = $localCommands | Where-Object Id -eq 'open-vscode'
    $addedCommand = $localCommands | Where-Object Name -eq '测试命令'
    if ($updatedCommand.Description -ne 'AI 增量修改测试' -or -not $hiddenCommand.Disabled -or
        -not $addedCommand.Id.StartsWith('local-')) {
        throw 'AI 增量命令没有正确执行新增、修改和隐藏。'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $temporaryDirectory 'commands.json') -Algorithm SHA256).Hash -ne $sharedConfigHash) {
        throw 'AI 增量命令不应修改共享 commands.json。'
    }

    $previewNamePatchPath = Join-Path $temporaryDirectory 'ai-preview-name-patch.json'
    '{"version":1,"ops":[{"op":"add","fields":{"Name":"原始名称","Executable":"cmd.exe"}}]}' |
        Set-Content -LiteralPath $previewNamePatchPath -Encoding UTF8
    [string]$sharedConfigPath = Join-Path $temporaryDirectory 'commands.json'
    [string]$localConfigPath = Join-Path $temporaryDirectory 'commands.local.json'
    $previewNameTestScriptPath = Join-Path $temporaryDirectory 'test-preview-name.ps1'
    @'
param(
    [string]$ExecutablePath,
    [string]$PatchPath,
    [string]$SharedConfigPath,
    [string]$LocalConfigPath
)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ExecutablePath))
$serviceType = $assembly.GetType('CliListApp.AiCommandPatchService', $true)
[string]$fingerprint = $serviceType.GetMethod('ComputeFingerprint').Invoke($null, [object[]]@($SharedConfigPath, $LocalConfigPath))
[string]$patch = Get-Content -LiteralPath $PatchPath -Raw
$plan = $serviceType.GetMethod('Prepare').Invoke($null, [object[]]@(
    $patch,
    $SharedConfigPath,
    $LocalConfigPath,
    $fingerprint
))
$editedNames = New-Object 'System.Collections.Generic.Dictionary[int,string]'
$editedNames.Add(0, '确认页重命名')
$serviceType.GetMethod('UpdatePreviewNames').Invoke(
    $null,
    [object[]]@($plan.PSObject.BaseObject, $editedNames.PSObject.BaseObject)
) | Out-Null
$serviceType.GetMethod('Apply').Invoke(
    $null,
    [object[]]@($plan.PSObject.BaseObject, $SharedConfigPath, $LocalConfigPath)
) | Out-Null
'@ | Set-Content -LiteralPath $previewNameTestScriptPath -Encoding Unicode
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $previewNameTestScriptPath `
        -ExecutablePath (Join-Path $temporaryDirectory 'CLIList.exe') `
        -PatchPath $previewNamePatchPath `
        -SharedConfigPath $sharedConfigPath `
        -LocalConfigPath $localConfigPath
    if ($LASTEXITCODE -ne 0) {
        throw "确认页名称编辑测试失败，退出码：$LASTEXITCODE"
    }

    $localCommands = @(Get-Content -LiteralPath $localConfigPath -Raw | ConvertFrom-Json)
    $renamedCommand = $localCommands | Where-Object Name -eq '确认页重命名'
    if (-not $renamedCommand -or $renamedCommand.Executable -ne 'cmd.exe') {
        throw '确认页名称编辑没有保留其他命令配置。'
    }

    $localHashBeforeInvalidPatch = (Get-FileHash -LiteralPath (Join-Path $temporaryDirectory 'commands.local.json') -Algorithm SHA256).Hash
    $invalidPatchPath = Join-Path $temporaryDirectory 'ai-patch-invalid.json'
    '{"version":1,"ops":[{"op":"update","id":"open-powershell","fields":{"Id":"forbidden"}}]}' |
        Set-Content -LiteralPath $invalidPatchPath -Encoding UTF8
    $invalidPatchProcess = Start-Process -FilePath (Join-Path $temporaryDirectory 'CLIList.exe') -ArgumentList @('--apply-ai-patch', $invalidPatchPath) -Wait -PassThru
    if ($invalidPatchProcess.ExitCode -eq 0) {
        throw 'AI 增量命令应拒绝非白名单字段。'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $temporaryDirectory 'commands.local.json') -Algorithm SHA256).Hash -ne $localHashBeforeInvalidPatch) {
        throw '非法 AI 增量命令不应改写本机配置。'
    }

    $fullConfigPatchPath = Join-Path $temporaryDirectory 'ai-patch-full-config.json'
    '[{"Id":"replace-all","Name":"不允许整份替换","Executable":"cmd.exe"}]' |
        Set-Content -LiteralPath $fullConfigPatchPath -Encoding UTF8
    $fullConfigPatchProcess = Start-Process -FilePath (Join-Path $temporaryDirectory 'CLIList.exe') -ArgumentList @('--apply-ai-patch', $fullConfigPatchPath) -Wait -PassThru
    if ($fullConfigPatchProcess.ExitCode -eq 0) {
        throw 'AI 增量命令应拒绝整份配置数组。'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $temporaryDirectory 'commands.local.json') -Algorithm SHA256).Hash -ne $localHashBeforeInvalidPatch) {
        throw '整份配置数组被拒绝后不应改写本机配置。'
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
