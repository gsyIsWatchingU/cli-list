param(
    [string]$SourceDirectory = $PSScriptRoot,
    [switch]$ReplaceConfig
)

$ErrorActionPreference = 'Stop'

$installDirectory = Join-Path $env:USERPROFILE '.cli-list'
$binDirectory = Join-Path $env:USERPROFILE 'bin'
$desktopDirectory = [Environment]::GetFolderPath('Desktop')
$startupDirectory = [Environment]::GetFolderPath('Startup')
$executablePath = Join-Path $installDirectory 'CLIList.exe'
$iconPath = Join-Path $installDirectory 'cli-list.ico'

& (Join-Path $SourceDirectory 'test.ps1') -SkipInstalledSync

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $binDirectory -Force | Out-Null
Set-Content -LiteralPath (Join-Path $installDirectory '.source-repository') -Value ([IO.Path]::GetFullPath($SourceDirectory)) -Encoding UTF8

$installedConfigPath = Join-Path $installDirectory 'commands.json'
if ($ReplaceConfig) {
    $localConfigPath = Join-Path $installDirectory 'commands.local.json'
    Set-Content -LiteralPath $localConfigPath -Value '[]' -Encoding UTF8
}

& (Join-Path $SourceDirectory 'sync-installed.ps1') -SkipBuild -NoRestart

if (Test-Path -LiteralPath (Join-Path $SourceDirectory '.git')) {
    & git -C $SourceDirectory config --local core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) {
        throw 'Git Hook 配置失败。'
    }
}

function Install-ContextMenuEntry {
    param(
        [Parameter(Mandatory = $true)][string]$RegistryPath,
        [Parameter(Mandatory = $true)][string]$ContextPlaceholder
    )

    $commandPath = Join-Path $RegistryPath 'command'
    New-Item -Path $commandPath -Force | Out-Null
    Set-Item -LiteralPath $RegistryPath -Value 'CLI List'
    New-ItemProperty -LiteralPath $RegistryPath -Name 'Icon' -Value $iconPath -PropertyType String -Force | Out-Null
    Set-Item -LiteralPath $commandPath -Value ('"' + $executablePath + '" "' + $ContextPlaceholder + '"')
}

# 清理旧版本可能写入的 HKLM/HKCU 条目，避免重复菜单
$legacyPaths = @(
    'HKLM:\Software\Classes\Directory\Background\shell\CLIList',
    'HKLM:\Software\Classes\Directory\shell\CLIList',
    'HKLM:\Software\Classes\DesktopBackground\Shell\CLIList',
    'HKLM:\Software\Classes\*\shell\CLIList',
    'HKLM:\Software\Classes\Drive\shell\CLIList',
    'HKCU:\Software\Classes\Directory\Background\shell\CLIList',
    'HKCU:\Software\Classes\Directory\shell\CLIList',
    'HKCU:\Software\Classes\DesktopBackground\Shell\CLIList',
    'HKCU:\Software\Classes\*\shell\CLIList',
    'HKCU:\Software\Classes\Drive\shell\CLIList'
)
foreach ($legacyPath in $legacyPaths) {
    if (Test-Path -LiteralPath $legacyPath) {
        Remove-Item -LiteralPath $legacyPath -Recurse -Force
    }
}

# 写入当前用户右键菜单（HKCU，无需管理员权限）
Install-ContextMenuEntry -RegistryPath 'HKCU:\Software\Classes\Directory\Background\shell\CLIList' -ContextPlaceholder '%V'
Install-ContextMenuEntry -RegistryPath 'HKCU:\Software\Classes\Directory\shell\CLIList' -ContextPlaceholder '%1'
Install-ContextMenuEntry -RegistryPath 'HKCU:\Software\Classes\DesktopBackground\Shell\CLIList' -ContextPlaceholder '%V'
Install-ContextMenuEntry -RegistryPath 'HKCU:\Software\Classes\*\shell\CLIList' -ContextPlaceholder '%1'
Install-ContextMenuEntry -RegistryPath 'HKCU:\Software\Classes\Drive\shell\CLIList' -ContextPlaceholder '%1'

$shortcutPath = Join-Path $desktopDirectory 'CLI List.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executablePath
$shortcut.Arguments = '--dev-sync'
$shortcut.WorkingDirectory = $env:USERPROFILE
$shortcut.Description = '打开 CLI List 命令面板（源码开发模式，自动同步最新源码）'
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Save()

$residentShortcutPath = Join-Path $startupDirectory 'CLI List Resident.lnk'
$residentShortcut = $shell.CreateShortcut($residentShortcutPath)
$residentShortcut.TargetPath = $executablePath
$residentShortcut.Arguments = '--resident'
$residentShortcut.WorkingDirectory = $env:USERPROFILE
$residentShortcut.Description = '启动 CLI List 托盘与全局快捷键'
$residentShortcut.IconLocation = "$iconPath,0"
$residentShortcut.WindowStyle = 7
$residentShortcut.Save()

Start-Process -FilePath $executablePath -ArgumentList '--resident'

Write-Output "安装目录：$installDirectory"
Write-Output "桌面入口：$shortcutPath"
Write-Output "终端命令：cli-list"
Write-Output '全局快捷键：Ctrl + Alt + Space'
Write-Output '右键入口：CLI List（Windows 11 中可能位于“显示更多选项”）'
