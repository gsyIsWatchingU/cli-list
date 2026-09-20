$ErrorActionPreference = 'Stop'

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
$residentShortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'CLI List Resident.lnk'
$commandPath = Join-Path $env:USERPROFILE 'bin\cli-list.cmd'
$installDirectory = Join-Path $env:USERPROFILE '.cli-list'
$appDataInstallDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CLIList'
$sourceMarkerPath = Join-Path $installDirectory '.source-repository'

Get-Process -Name 'CLIList' -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}
if (Test-Path -LiteralPath $commandPath) {
    Remove-Item -LiteralPath $commandPath -Force
}
if (Test-Path -LiteralPath $residentShortcutPath) {
    Remove-Item -LiteralPath $residentShortcutPath -Force
}

# 清理生产化安装（%LOCALAPPDATA%\CLIList）的注册表标记与安装目录
Remove-Item -LiteralPath 'HKCU:\Software\CliListApp' -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $appDataInstallDirectory) {
    Remove-Item -LiteralPath $appDataInstallDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $sourceMarkerPath) {
    $sourceRepository = (Get-Content -LiteralPath $sourceMarkerPath -Raw).Trim()
    if (-not [string]::IsNullOrWhiteSpace($sourceRepository) -and (Test-Path -LiteralPath (Join-Path $sourceRepository '.git'))) {
        $configuredHooksPath = (& git -C $sourceRepository config --local --get core.hooksPath 2>$null)
        if ($configuredHooksPath -eq '.githooks') {
            & git -C $sourceRepository config --local --unset-all core.hooksPath
        }
    }
    Remove-Item -LiteralPath $sourceMarkerPath -Force
}

Write-Output 'CLI List 右键菜单、桌面入口和终端命令已移除。'
Write-Output "程序配置仍保留在：$installDirectory"
