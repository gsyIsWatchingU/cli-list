param(
    [switch]$SkipBuild,
    [switch]$SkipInstalledSync,
    [switch]$Release
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

$sourceText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'CLIList.cs') -Raw -Encoding UTF8
if ($sourceText -notmatch 'public static bool IsInstalledRelease') {
    throw 'CLI List 缺少统一的用户安装版环境判断 IsInstalledRelease。'
}
if ($sourceText -notmatch 'if \(!AppInfo\.IsInstalledRelease\)\s*\{\s*// 本地开发版不启动静默更新检查') {
    throw 'CLI List 的静默更新检查必须经过 IsInstalledRelease 守卫。'
}
if ($sourceText -notmatch '本地开发版不提供更新功能') {
    throw 'CLI List 的 CheckForUpdate 必须拒绝本地开发版的更新请求。'
}
if ($sourceText -notmatch 'if \(AppInfo\.IsInstalledRelease\)\s*\{\s*updateItem = new ToolStripMenuItem\("检查更新…"\)') {
    throw 'CLI List 托盘“检查更新”入口必须由 IsInstalledRelease 守卫。'
}
if ($sourceText -notmatch 'if \(AppInfo\.IsInstalledRelease\)\s*\{\s*updateButton = CreateFooterButton\("检查更新"\)') {
    throw 'CLI List 主窗口“检查更新”入口必须由 IsInstalledRelease 守卫。'
}
if ($sourceText -notmatch 'restartButton = CreateFooterButton\("立即重启"\)') {
    throw 'CLI List 开发版应提供“立即重启”入口。'
}
if ($sourceText -notmatch 'private void RestartForDevelopment') {
    throw 'CLI List 缺少开发版“立即重启”实现。'
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipInstalledSync:$SkipInstalledSync -Release:$Release
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

    # ---- 开发版/安装版环境判断与更新入口自动化测试 ----
    $releaseExePath = Join-Path $temporaryDirectory 'CLIList-Release.exe'
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipInstalledSync -Release -OutputPath $releaseExePath
    if (-not (Test-Path -LiteralPath $releaseExePath)) {
        throw '未生成正式安装包构建产物。'
    }

    # “本地开发版”环境判断需要一份非正式版构建。仅当 -Release 时主目录 CLIList.exe
    # 已是正式版，才额外构建独立开发版；否则直接复用主目录构建产物。
    $devCheckExecutablePath = $executablePath
    if ($Release) {
        $devCheckExecutablePath = Join-Path $temporaryDirectory 'CLIList-Dev.exe'
        & (Join-Path $PSScriptRoot 'build.ps1') -SkipInstalledSync -OutputPath $devCheckExecutablePath
        if (-not (Test-Path -LiteralPath $devCheckExecutablePath)) {
            throw '未生成本地开发版构建产物。'
        }
    }

    $envCheckScriptPath = Join-Path $temporaryDirectory 'check-env.ps1'
    @'
param(
    [string]$ExecutablePath,
    [switch]$ExpectRelease
)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ExecutablePath))
$appInfo = $assembly.GetType('CliListApp.AppInfo', $true)
$isReleaseBuild = $appInfo.GetProperty('IsReleaseBuild').GetValue($null, $null)
$isInstalledRelease = $appInfo.GetProperty('IsInstalledRelease').GetValue($null, $null)
if ($ExpectRelease) {
    if (-not $isReleaseBuild) { throw '正式安装包构建应标记 IsReleaseBuild=true。' }
}
else {
    if ($isReleaseBuild) { throw '本地开发版不应标记 IsReleaseBuild=true。' }
}
if ($isInstalledRelease) { throw '从临时目录加载不应判定为用户安装版。' }
$installer = $assembly.GetType('CliListApp.Installer', $true)
$method = $installer.GetMethod('IsProductInstallDirectory', [Reflection.BindingFlags]'Static,Public,NonPublic')
$canonical = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.cli-list'))
if (-not $method.Invoke($null, [object[]]@($canonical))) {
    throw "规范安装目录应被识别为产品安装目录：$canonical"
}
$other = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE 'cli-list-temp-not-installed'))
if ($method.Invoke($null, [object[]]@($other))) {
    throw "非规范安装目录不应被识别为产品安装目录：$other"
}
'@ | Set-Content -LiteralPath $envCheckScriptPath -Encoding Unicode

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $envCheckScriptPath -ExecutablePath $devCheckExecutablePath
    if ($LASTEXITCODE -ne 0) {
        throw "本地开发版环境判断测试失败，退出码：$LASTEXITCODE"
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $envCheckScriptPath -ExecutablePath $releaseExePath -ExpectRelease
    if ($LASTEXITCODE -ne 0) {
        throw "正式安装包构建环境判断测试失败，退出码：$LASTEXITCODE"
    }

    if (-not $env:CI) {
        $ideCliPickerScreenshotPath = Join-Path $temporaryDirectory 'ide-cli-picker.png'
        $ideCliPickerProcess = Start-Process -FilePath (Join-Path $temporaryDirectory 'CLIList.exe') `
            -ArgumentList @('--screenshot-ide-cli-picker', $ideCliPickerScreenshotPath) -Wait -PassThru
        if ($ideCliPickerProcess.ExitCode -ne 0 -or
            -not (Test-Path -LiteralPath $ideCliPickerScreenshotPath) -or
            (Get-Item -LiteralPath $ideCliPickerScreenshotPath).Length -lt 1000) {
            throw 'IDE/CLI 选择器没有正确渲染。'
        }
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

    $localCommands = Get-Content -LiteralPath (Join-Path $temporaryDirectory 'commands.local.json') -Raw -Encoding UTF8 | ConvertFrom-Json
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
[string]$patch = Get-Content -LiteralPath $PatchPath -Raw -Encoding UTF8
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

    $localCommands = Get-Content -LiteralPath $localConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $renamedCommand = $localCommands | Where-Object Name -eq '确认页重命名'
    if (-not $renamedCommand -or $renamedCommand.Executable -ne 'cmd.exe') {
        throw '确认页名称编辑没有保留其他命令配置。'
    }

    $presetPatchPath = Join-Path $temporaryDirectory 'ai-preset-patch.json'
    '{"version":1,"ops":[{"op":"add","preset":"ide-cli-picker","fields":{"Name":"开发工具选择器"}}]}' |
        Set-Content -LiteralPath $presetPatchPath -Encoding UTF8
    $presetProcess = Start-Process -FilePath (Join-Path $temporaryDirectory 'CLIList.exe') `
        -ArgumentList @('--apply-ai-patch', $presetPatchPath) -Wait -PassThru
    if ($presetProcess.ExitCode -ne 0) {
        throw "AI 内置选择器 preset 应用失败，退出码：$($presetProcess.ExitCode)"
    }
    $localCommands = Get-Content -LiteralPath $localConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $presetCommand = $localCommands | Where-Object Name -eq '开发工具选择器'
    if (-not $presetCommand -or $presetCommand.Action -ne 'ChooseIdeOrCli' -or $presetCommand.Executable) {
        throw 'AI preset 没有映射为受控的内置 IDE/CLI 选择器。'
    }

    $versionsDirectory = Join-Path $temporaryDirectory 'ai-versions'
    $versionsBefore = @(Get-ChildItem -LiteralPath $versionsDirectory -Filter 'ai-*.json' -ErrorAction SilentlyContinue)
    if ($versionsBefore.Count -lt 1) {
        throw 'AI 修改后没有生成版本快照。'
    }

    for ($i = 0; $i -lt 6; $i++) {
        $loopPatchPath = Join-Path $temporaryDirectory ("ai-loop-" + $i + ".json")
        "{`"version`":1,`"ops`":[{`"op`":`"update`",`"id`":`"open-powershell`",`"fields`":{`"Description`":`"循环测试 $i`"}}]}" |
            Set-Content -LiteralPath $loopPatchPath -Encoding UTF8
        $loopProcess = Start-Process -FilePath (Join-Path $temporaryDirectory 'CLIList.exe') -ArgumentList @('--apply-ai-patch', $loopPatchPath) -Wait -PassThru
        if ($loopProcess.ExitCode -ne 0) {
            throw "版本历史循环应用第 $i 次失败。"
        }
    }
    $versionsAfter = @(Get-ChildItem -LiteralPath $versionsDirectory -Filter 'ai-*.json')
    if ($versionsAfter.Count -gt 5) {
        throw "版本历史超过 5 个上限，当前 $($versionsAfter.Count) 个。"
    }

    $versionRestoreScriptPath = Join-Path $temporaryDirectory 'test-version-restore.ps1'
    @'
param(
    [string]$ExecutablePath,
    [string]$SharedConfigPath,
    [string]$LocalConfigPath
)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ExecutablePath))
$serviceType = $assembly.GetType('CliListApp.AiCommandPatchService', $true)
$versions = $serviceType.GetMethod('ListVersions').Invoke($null, [object[]]@($LocalConfigPath))
if (-not $versions -or $versions.Count -eq 0) { throw 'ListVersions 没有返回任何版本。' }
$oldest = $versions[$versions.Count - 1]
$serviceType.GetMethod('RestoreVersion').Invoke($null, [object[]]@(
    $SharedConfigPath, $LocalConfigPath, $oldest.FilePath.PSObject.BaseObject
)) | Out-Null
'@ | Set-Content -LiteralPath $versionRestoreScriptPath -Encoding Unicode
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $versionRestoreScriptPath `
    -ExecutablePath (Join-Path $temporaryDirectory 'CLIList.exe') `
    -SharedConfigPath $sharedConfigPath -LocalConfigPath $localConfigPath
if ($LASTEXITCODE -ne 0) {
    throw "版本恢复测试失败，退出码：$LASTEXITCODE"
}

    $missingScriptHash = (Get-FileHash -LiteralPath $localConfigPath -Algorithm SHA256).Hash
    $missingScriptPatchPath = Join-Path $temporaryDirectory 'ai-missing-script-patch.json'
    '{"version":1,"ops":[{"op":"add","fields":{"Name":"无效脚本命令","Executable":"wscript.exe","Arguments":"//B \"%USERPROFILE%\\.cli-list\\missing-ai-test.vbs\""}}]}' |
        Set-Content -LiteralPath $missingScriptPatchPath -Encoding UTF8
    $missingScriptProcess = Start-Process -FilePath (Join-Path $temporaryDirectory 'CLIList.exe') `
        -ArgumentList @('--apply-ai-patch', $missingScriptPatchPath) -Wait -PassThru
    if ($missingScriptProcess.ExitCode -eq 0) {
        throw 'AI 增量命令应拒绝引用不存在的本机脚本。'
    }
    if ((Get-FileHash -LiteralPath $localConfigPath -Algorithm SHA256).Hash -ne $missingScriptHash) {
        throw '拒绝不存在的脚本后不应改写本机配置。'
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

    $migrationDirectory = Join-Path $temporaryDirectory 'legacy-migration'
    New-Item -ItemType Directory -Path $migrationDirectory | Out-Null
    Copy-Item -LiteralPath $executablePath -Destination (Join-Path $migrationDirectory 'CLIList.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'commands.json') -Destination (Join-Path $migrationDirectory 'commands.json')
    @'
[{"Id":"legacy-picker","Name":"选择 IDE 或 CLI","Description":"旧配置","Tags":["开发"],"Executable":"%SystemRoot%\\System32\\wscript.exe","Arguments":"//B \"%USERPROFILE%\\.cli-list\\ide-cli-launcher.vbs\" \"{context}\"","WorkingDirectory":"{context}","CloseAfterLaunch":true}]
'@ | Set-Content -LiteralPath (Join-Path $migrationDirectory 'commands.local.json') -Encoding UTF8
    $migrationProcess = Start-Process -FilePath (Join-Path $migrationDirectory 'CLIList.exe') -ArgumentList '--validate' -Wait -PassThru
    if ($migrationProcess.ExitCode -ne 0) {
        throw "旧 IDE/CLI 命令迁移失败，退出码：$($migrationProcess.ExitCode)"
    }
    $migratedCommands = Get-Content -LiteralPath (Join-Path $migrationDirectory 'commands.local.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $migratedCommand = $migratedCommands | Where-Object Id -eq 'legacy-picker'
    if (-not $migratedCommand -or $migratedCommand.Action -ne 'ChooseIdeOrCli' -or
        $migratedCommand.Name -ne '选择 IDE 或 CLI' -or $migratedCommand.Executable -or
        -not (Test-Path -LiteralPath (Join-Path $migrationDirectory 'commands.local.json.pre-ide-cli-picker.bak'))) {
        throw '旧命令迁移没有保留身份与文案，或没有生成迁移备份。'
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

# ---- 源码开发模式（任务栏 --dev-sync）自动化测试 ----
# 使用临时源码工作区与临时安装目录，复用真实 dev-sync.ps1 / build.ps1 / CLIList.cs，
# 用测试桩 test.ps1 只做构建、不递归运行真实测试。
$devSyncTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('cli-list-dev-sync-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $devSyncTestRoot | Out-Null
try {
    $sourceDir = Join-Path $devSyncTestRoot 'src'
    $installDir = Join-Path $devSyncTestRoot 'inst'
    New-Item -ItemType Directory -Path $sourceDir, $installDir | Out-Null

    foreach ($fileName in @(
        'CLIList.cs', 'build.ps1', 'cli-list.ico', 'cli-list.svg', 'commands.json',
        'cli-list.cmd', 'dev-sync.ps1', 'update-helper.ps1',
        'gpu-trae.vbs', 'minimize-all.vbs',
        'skill-atlas-desktop.cmd', 'skill-atlas-desktop.vbs',
        'skill-atlas-dev.cmd', 'skill-atlas-dev.vbs',
        'tool-desk-start.cmd', 'tool-desk-start.vbs',
        'travel-test-start.cmd', 'travel-test-start.vbs',
        'sync-installed.ps1', 'uninstall.ps1'
    )) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $fileName) -Destination (Join-Path $sourceDir $fileName)
    }

    $stubRunLog = Join-Path $devSyncTestRoot 'stub-runs.log'
    $env:CLILIST_DEV_SYNC_STUB_LOG = $stubRunLog
    @'
param([switch]$SkipBuild, [switch]$SkipInstalledSync, [switch]$Release)
$ErrorActionPreference = 'Stop'
$log = [Environment]::GetEnvironmentVariable('CLILIST_DEV_SYNC_STUB_LOG')
if (-not $log) { throw '缺少桩日志路径 CLILIST_DEV_SYNC_STUB_LOG。' }
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipInstalledSync
    if ($LASTEXITCODE -ne 0) { throw "桩构建失败，退出码：$LASTEXITCODE" }
}
Add-Content -LiteralPath $log -Value (Get-Date -Format 'o') -Encoding UTF8
'@ | Set-Content -LiteralPath (Join-Path $sourceDir 'test.ps1') -Encoding UTF8

    $installExePath = Join-Path $installDir 'CLIList.exe'
    $repoExePath = Join-Path $PSScriptRoot 'CLIList.exe'
    Copy-Item -LiteralPath $repoExePath -Destination $installExePath
    Set-Content -LiteralPath (Join-Path $installDir 'commands.local.json') -Value '[{"Id":"personal","Name":"个人命令","Executable":"cmd.exe"}]' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $installDir 'usage.json') -Value '{"open-powershell":3}' -Encoding UTF8
    $localHashBefore = (Get-FileHash -LiteralPath (Join-Path $installDir 'commands.local.json') -Algorithm SHA256).Hash
    $usageHashBefore = (Get-FileHash -LiteralPath (Join-Path $installDir 'usage.json') -Algorithm SHA256).Hash

    $devSyncScriptPath = Join-Path $sourceDir 'dev-sync.ps1'
    $devSyncArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $devSyncScriptPath, '-SourceDirectory', $sourceDir, '-InstallDirectory', $installDir)

    # 场景 A：首次同步（安装目录无状态文件）→ 构建一次、同步成功（退出码 2）。
    & powershell.exe @devSyncArgs
    if ($LASTEXITCODE -ne 2) { throw "首次同步应返回 2，实际 $LASTEXITCODE" }
    $stateAfterA = Get-Content -LiteralPath (Join-Path $installDir '.dev-sync-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($stateAfterA.SourceDirectory -ne $sourceDir -or [string]::IsNullOrWhiteSpace($stateAfterA.Fingerprint)) {
        throw '同步后状态文件未记录源码路径与指纹。'
    }
    if ((Get-FileHash -LiteralPath $installExePath -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $sourceDir 'CLIList.exe') -Algorithm SHA256).Hash) {
        throw '同步后安装版应等于源码构建产物。'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $installDir 'commands.local.json') -Algorithm SHA256).Hash -ne $localHashBefore) {
        throw '同步后本机 commands.local.json 被改写。'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $installDir 'usage.json') -Algorithm SHA256).Hash -ne $usageHashBefore) {
        throw '同步后 usage.json 被改写。'
    }
    $runsA = @(Get-Content -LiteralPath $stubRunLog -ErrorAction SilentlyContinue).Count
    if ($runsA -ne 1) { throw "首次同步应恰好构建一次，实际 $runsA 次。" }

    # 场景 B：源码未变化 → 直接启动（退出码 0），不重复构建、安装版不变。
    $exeHashBeforeB = (Get-FileHash -LiteralPath $installExePath -Algorithm SHA256).Hash
    & powershell.exe @devSyncArgs
    if ($LASTEXITCODE -ne 0) { throw "源码未变化应返回 0，实际 $LASTEXITCODE" }
    $runsB = @(Get-Content -LiteralPath $stubRunLog -ErrorAction SilentlyContinue).Count
    if ($runsB -ne 1) { throw "源码未变化不应重复构建，实际 $runsB 次。" }
    if ((Get-FileHash -LiteralPath $installExePath -Algorithm SHA256).Hash -ne $exeHashBeforeB) {
        throw '源码未变化时安装版不应被替换。'
    }

    # 场景 C：构建失败 → 退出码 1，安装版未被破坏、状态文件未被污染。
    $failSourceDir = Join-Path $devSyncTestRoot 'src-fail'
    New-Item -ItemType Directory -Path $failSourceDir | Out-Null
    Get-ChildItem -LiteralPath $sourceDir -File | Copy-Item -Destination $failSourceDir -Force
    @'
param([switch]$SkipBuild, [switch]$SkipInstalledSync, [switch]$Release)
$ErrorActionPreference = 'Stop'
throw '模拟源码测试失败'
'@ | Set-Content -LiteralPath (Join-Path $failSourceDir 'test.ps1') -Encoding UTF8
    $exeHashBeforeC = (Get-FileHash -LiteralPath $installExePath -Algorithm SHA256).Hash
    $stateTextBeforeC = Get-Content -LiteralPath (Join-Path $installDir '.dev-sync-state.json') -Raw -Encoding UTF8
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $failSourceDir 'dev-sync.ps1') `
        -SourceDirectory $failSourceDir -InstallDirectory $installDir
    if ($LASTEXITCODE -ne 1) { throw "构建失败应返回 1，实际 $LASTEXITCODE" }
    $global:LASTEXITCODE = 0
    if ((Get-FileHash -LiteralPath $installExePath -Algorithm SHA256).Hash -ne $exeHashBeforeC) {
        throw '构建失败后安装版不应被替换。'
    }
    if ((Get-Content -LiteralPath (Join-Path $installDir '.dev-sync-state.json') -Raw -Encoding UTF8) -ne $stateTextBeforeC) {
        throw '构建失败后状态文件不应被改写。'
    }

    # 场景 D：切换后写入状态失败 → 自动回滚，安装版恢复为旧版、无残留。
    $installDir2 = Join-Path $devSyncTestRoot 'inst2'
    New-Item -ItemType Directory -Path $installDir2 | Out-Null
    Copy-Item -LiteralPath $repoExePath -Destination (Join-Path $installDir2 'CLIList.exe')
    New-Item -ItemType Directory -Path (Join-Path $installDir2 '.dev-sync-state.json') | Out-Null
    $oldExeHashD = (Get-FileHash -LiteralPath (Join-Path $installDir2 'CLIList.exe') -Algorithm SHA256).Hash
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $devSyncScriptPath -SourceDirectory $sourceDir -InstallDirectory $installDir2
    if ($LASTEXITCODE -ne 1) { throw "状态写入失败应返回 1，实际 $LASTEXITCODE" }
    $global:LASTEXITCODE = 0
    if ((Get-FileHash -LiteralPath (Join-Path $installDir2 'CLIList.exe') -Algorithm SHA256).Hash -ne $oldExeHashD) {
        throw '切换失败后未回滚安装版。'
    }
    if (Test-Path -LiteralPath (Join-Path $installDir2 'CLIList.exe.staged')) { throw '切换失败后残留暂存文件。' }
    if (Test-Path -LiteralPath (Join-Path $installDir2 'CLIList.exe.previous')) { throw '切换失败后残留备份文件。' }

    # 场景 E：并发点击 → 只构建一次，退出码恰好为一个 0、一个 2，安装版最终一致。
    $installDir3 = Join-Path $devSyncTestRoot 'inst3'
    New-Item -ItemType Directory -Path $installDir3 | Out-Null
    Copy-Item -LiteralPath $repoExePath -Destination (Join-Path $installDir3 'CLIList.exe')
    Add-Content -LiteralPath (Join-Path $sourceDir 'gpu-trae.vbs') -Value "`r`n' concurrency-change-20260927" -Encoding ASCII
    $stubRunLog2 = Join-Path $devSyncTestRoot 'stub-runs2.log'
    $env:CLILIST_DEV_SYNC_STUB_LOG = $stubRunLog2
    $concurrentArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $devSyncScriptPath, '-SourceDirectory', $sourceDir, '-InstallDirectory', $installDir3)
    $concurrentP1 = Start-Process -FilePath 'powershell.exe' -ArgumentList $concurrentArgs -PassThru
    $concurrentP2 = Start-Process -FilePath 'powershell.exe' -ArgumentList $concurrentArgs -PassThru
    Wait-Process -Id $concurrentP1.Id, $concurrentP2.Id -Timeout 420 -ErrorAction Stop
    $concurrentCodes = @($concurrentP1.ExitCode, $concurrentP2.ExitCode) | Sort-Object
    if (-not ($concurrentCodes[0] -eq 0 -and $concurrentCodes[1] -eq 2)) {
        throw "并发同步应恰好一次成功，退出码：$($concurrentP1.ExitCode) / $($concurrentP2.ExitCode)"
    }
    $runsE = @(Get-Content -LiteralPath $stubRunLog2 -ErrorAction SilentlyContinue).Count
    if ($runsE -ne 1) { throw "并发点击应只构建一次，实际 $runsE 次。" }
    $stateAfterE = Get-Content -LiteralPath (Join-Path $installDir3 '.dev-sync-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($stateAfterE.SourceDirectory -ne $sourceDir -or [string]::IsNullOrWhiteSpace($stateAfterE.Fingerprint)) {
        throw '并发同步后状态文件未正确记录。'
    }
}
finally {
    Remove-Item Env:CLILIST_DEV_SYNC_STUB_LOG -ErrorAction SilentlyContinue
    $resolvedDevSyncTestRoot = [IO.Path]::GetFullPath($devSyncTestRoot)
    $resolvedSystemTemporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedDevSyncTestRoot.StartsWith($resolvedSystemTemporaryDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedDevSyncTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output 'CLI List 构建与配置验证通过。'
exit 0
