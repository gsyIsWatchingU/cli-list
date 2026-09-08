$ErrorActionPreference = 'Stop'

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '移除系统右键菜单需要管理员权限，请以管理员身份运行 PowerShell。'
}

$registryPaths = @(
    'HKCU:\Software\Classes\Directory\Background\shell\CLIList',
    'HKCU:\Software\Classes\Directory\shell\CLIList',
    'HKCU:\Software\Classes\DesktopBackground\Shell\CLIList',
    'HKCU:\Software\Classes\*\shell\CLIList',
    'HKCU:\Software\Classes\Drive\shell\CLIList',
    'HKLM:\Software\Classes\Directory\Background\shell\CLIList',
    'HKLM:\Software\Classes\Directory\shell\CLIList',
    'HKLM:\Software\Classes\DesktopBackground\Shell\CLIList',
    'HKLM:\Software\Classes\*\shell\CLIList',
    'HKLM:\Software\Classes\Drive\shell\CLIList'
)

foreach ($registryPath in $registryPaths) {
    if (Test-Path -LiteralPath $registryPath) {
        Remove-Item -LiteralPath $registryPath -Recurse -Force
    }
}

$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CLI List.lnk'
$commandPath = Join-Path $env:USERPROFILE 'bin\cli-list.cmd'

if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}
if (Test-Path -LiteralPath $commandPath) {
    Remove-Item -LiteralPath $commandPath -Force
}

Write-Output 'CLI List 右键菜单、桌面入口和终端命令已移除。'
Write-Output "程序配置仍保留在：$(Join-Path $env:USERPROFILE '.cli-list')"
