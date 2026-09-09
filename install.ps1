param(
    [string]$SourceDirectory = $PSScriptRoot,
    [switch]$ReplaceConfig
)

$ErrorActionPreference = 'Stop'

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '安装右键菜单需要管理员权限，请以管理员身份运行 PowerShell。'
}

$installDirectory = Join-Path $env:USERPROFILE '.cli-list'
$binDirectory = Join-Path $env:USERPROFILE 'bin'
$desktopDirectory = [Environment]::GetFolderPath('Desktop')
$executablePath = Join-Path $installDirectory 'CLIList.exe'
$iconPath = Join-Path $installDirectory 'cli-list.ico'

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $binDirectory -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $SourceDirectory 'CLIList.exe') -Destination $executablePath -Force
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'CLIList.cs') -Destination (Join-Path $installDirectory 'CLIList.cs') -Force
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'cli-list.ico') -Destination $iconPath -Force
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'cli-list.svg') -Destination (Join-Path $installDirectory 'cli-list.svg') -Force
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'gpu-trae.vbs') -Destination (Join-Path $installDirectory 'gpu-trae.vbs') -Force
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'skill-atlas-dev.cmd') -Destination (Join-Path $installDirectory 'skill-atlas-dev.cmd') -Force
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'skill-atlas-dev.vbs') -Destination (Join-Path $installDirectory 'skill-atlas-dev.vbs') -Force
$installedConfigPath = Join-Path $installDirectory 'commands.json'
if ($ReplaceConfig -or -not (Test-Path -LiteralPath $installedConfigPath)) {
    Copy-Item -LiteralPath (Join-Path $SourceDirectory 'commands.json') -Destination $installedConfigPath -Force
}
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'uninstall.ps1') -Destination (Join-Path $installDirectory 'uninstall.ps1') -Force
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'cli-list.cmd') -Destination (Join-Path $binDirectory 'cli-list.cmd') -Force

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

$legacyUserPaths = @(
    'HKCU:\Software\Classes\Directory\Background\shell\CLIList',
    'HKCU:\Software\Classes\Directory\shell\CLIList',
    'HKCU:\Software\Classes\DesktopBackground\Shell\CLIList',
    'HKCU:\Software\Classes\*\shell\CLIList',
    'HKCU:\Software\Classes\Drive\shell\CLIList'
)
foreach ($legacyUserPath in $legacyUserPaths) {
    if (Test-Path -LiteralPath $legacyUserPath) {
        Remove-Item -LiteralPath $legacyUserPath -Recurse -Force
    }
}

Install-ContextMenuEntry -RegistryPath 'HKLM:\Software\Classes\Directory\Background\shell\CLIList' -ContextPlaceholder '%V'
Install-ContextMenuEntry -RegistryPath 'HKLM:\Software\Classes\Directory\shell\CLIList' -ContextPlaceholder '%1'
Install-ContextMenuEntry -RegistryPath 'HKLM:\Software\Classes\DesktopBackground\Shell\CLIList' -ContextPlaceholder '%V'
Install-ContextMenuEntry -RegistryPath 'HKLM:\Software\Classes\*\shell\CLIList' -ContextPlaceholder '%1'
Install-ContextMenuEntry -RegistryPath 'HKLM:\Software\Classes\Drive\shell\CLIList' -ContextPlaceholder '%1'

$shortcutPath = Join-Path $desktopDirectory 'CLI List.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executablePath
$shortcut.WorkingDirectory = $env:USERPROFILE
$shortcut.Description = '打开 CLI List 命令面板'
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Save()

Write-Output "安装目录：$installDirectory"
Write-Output "桌面入口：$shortcutPath"
Write-Output "终端命令：cli-list"
Write-Output '右键入口：CLI List（Windows 11 中可能位于“显示更多选项”）'
