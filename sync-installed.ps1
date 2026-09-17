param(
    [switch]$SkipBuild,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'

$sourceDirectory = $PSScriptRoot
$installDirectory = Join-Path $env:USERPROFILE '.cli-list'
$installedExecutablePath = Join-Path $installDirectory 'CLIList.exe'
$sharedConfigPath = Join-Path $sourceDirectory 'commands.json'
$installedConfigPath = Join-Path $installDirectory 'commands.json'
$localConfigPath = Join-Path $installDirectory 'commands.local.json'
$legacyBackupPath = Join-Path $installDirectory 'commands.pre-shared-migration.json'
$sourceMarkerPath = Join-Path $installDirectory '.source-repository'

if (-not (Test-Path -LiteralPath $installDirectory) -or -not (Test-Path -LiteralPath $sourceMarkerPath)) {
    Write-Output 'CLI List 尚未安装，跳过自动同步。首次使用请运行 install.ps1。'
    return
}

function Get-CommandKey {
    param([Parameter(Mandatory = $true)]$Command)

    if (-not [string]::IsNullOrWhiteSpace([string]$Command.Id)) {
        return ([string]$Command.Id).Trim().ToLowerInvariant()
    }
    return ([string]$Command.Name).Trim().ToLowerInvariant()
}

function ConvertTo-NormalizedJson {
    param([Parameter(Mandatory = $true)]$Value)

    return ConvertTo-Json -InputObject $Value -Depth 8 -Compress
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

function Initialize-LocalConfig {
    if (Test-Path -LiteralPath $localConfigPath) {
        return
    }

    if (-not (Test-Path -LiteralPath $installedConfigPath)) {
        Set-Content -LiteralPath $localConfigPath -Value '[]' -Encoding UTF8
        return
    }

    if (-not (Test-Path -LiteralPath $legacyBackupPath)) {
        Copy-Item -LiteralPath $installedConfigPath -Destination $legacyBackupPath
    }

    $sharedCommands = @(Get-Content -LiteralPath $sharedConfigPath -Raw | ConvertFrom-Json)
    $installedCommands = @(Get-Content -LiteralPath $installedConfigPath -Raw | ConvertFrom-Json)
    $sharedByKey = @{}
    $installedByKey = @{}
    $localOverrides = New-Object System.Collections.Generic.List[object]

    foreach ($command in $sharedCommands) {
        $sharedByKey[(Get-CommandKey $command)] = $command
    }
    foreach ($command in $installedCommands) {
        $key = Get-CommandKey $command
        $installedByKey[$key] = $command
        if (-not $sharedByKey.ContainsKey($key) -or
            (ConvertTo-NormalizedJson $sharedByKey[$key]) -ne (ConvertTo-NormalizedJson $command)) {
            $localOverrides.Add($command)
        }
    }
    foreach ($command in $sharedCommands) {
        $key = Get-CommandKey $command
        if (-not $installedByKey.ContainsKey($key)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$command.Id)) {
                $localOverrides.Add([pscustomobject]@{ Id = $command.Id; Disabled = $true })
            }
            else {
                $localOverrides.Add([pscustomobject]@{ Name = $command.Name; Disabled = $true })
            }
        }
    }

    $localJson = if ($localOverrides.Count -eq 0) {
        '[]'
    }
    else {
        ConvertTo-Json -InputObject $localOverrides.ToArray() -Depth 8
    }
    Set-Content -LiteralPath $localConfigPath -Value $localJson -Encoding UTF8
    Write-Output "已迁移本机命令差异：$localConfigPath"
}

$runningProcesses = Get-Process -Name 'CLIList' -ErrorAction SilentlyContinue
$shouldRestartResident = -not $NoRestart -and $null -ne $runningProcesses

try {
    if ($runningProcesses) {
        $runningProcesses | Stop-Process -Force
        $runningProcesses | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    }

    if (-not $SkipBuild) {
        & (Join-Path $sourceDirectory 'test.ps1') -SkipInstalledSync
    }

    Initialize-LocalConfig

    $runtimeFiles = @(
        'CLIList.exe',
        'CLIList.cs',
        'cli-list.ico',
        'cli-list.svg',
        'commands.json',
        'gpu-trae.vbs',
        'minimize-all.vbs',
        'skill-atlas-dev.cmd',
        'skill-atlas-dev.vbs',
        'tool-desk-start.cmd',
        'tool-desk-start.vbs',
        'sync-installed.ps1',
        'uninstall.ps1'
    )
    foreach ($fileName in $runtimeFiles) {
        $sourcePath = Join-Path $sourceDirectory $fileName
        if (Test-Path -LiteralPath $sourcePath) {
            Copy-FileWithRetry -Source $sourcePath -Destination (Join-Path $installDirectory $fileName)
        }
    }

    $binDirectory = Join-Path $env:USERPROFILE 'bin'
    New-Item -ItemType Directory -Path $binDirectory -Force | Out-Null
    Copy-FileWithRetry -Source (Join-Path $sourceDirectory 'cli-list.cmd') -Destination (Join-Path $binDirectory 'cli-list.cmd')

    $validationProcess = Start-Process -FilePath $installedExecutablePath -ArgumentList '--validate' -Wait -PassThru
    if ($validationProcess.ExitCode -ne 0) {
        throw "安装版配置验证失败，退出码：$($validationProcess.ExitCode)"
    }

    Write-Output "CLI List 已同步：$installedExecutablePath"
}
finally {
    if ($shouldRestartResident -and (Test-Path -LiteralPath $installedExecutablePath)) {
        Start-Process -FilePath $installedExecutablePath -ArgumentList '--resident'
    }
}
