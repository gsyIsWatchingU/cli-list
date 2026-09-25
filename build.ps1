param(
    [switch]$SkipInstalledSync,
    [switch]$Release,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'CLIList.cs'
$targetPath = if ($OutputPath) {
    [IO.Path]::GetFullPath($OutputPath)
}
else {
    Join-Path $PSScriptRoot 'CLIList.exe'
}
$iconPath = Join-Path $PSScriptRoot 'cli-list.ico'
$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

$iconSourcePaths = @(
    (Join-Path $PSScriptRoot 'cli-list.svg'),
    (Join-Path $PSScriptRoot 'render-icon.html'),
    (Join-Path $PSScriptRoot 'generate-icon.ps1')
)
$shouldGenerateIcon = -not (Test-Path -LiteralPath $iconPath)
if (-not $shouldGenerateIcon) {
    $iconWriteTime = (Get-Item -LiteralPath $iconPath).LastWriteTimeUtc
    $newerIconSource = $iconSourcePaths |
        Where-Object { (Test-Path -LiteralPath $_) -and (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $iconWriteTime } |
        Select-Object -First 1
    $shouldGenerateIcon = $null -ne $newerIconSource
}

if ($shouldGenerateIcon) {
    & (Join-Path $PSScriptRoot 'generate-icon.ps1')
}
else {
    Write-Output '图标资源未变化，复用现有 cli-list.ico。'
}

if (Test-Path -LiteralPath $targetPath) {
    Remove-Item -LiteralPath $targetPath -Force
}

if (-not $compilerPath) {
    throw '未找到 .NET Framework 4.x C# 编译器。'
}

$compilerArgs = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/codepage:65001',
    "/win32icon:$iconPath",
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.IO.Compression.dll',
    '/reference:System.Web.Extensions.dll',
    "/out:$targetPath"
)
if ($Release) {
    # 正式安装包构建标记：区分“用户安装版”与本地开发版。
    $compilerArgs += '/define:CLI_LIST_RELEASE'
}
$compilerArgs += $sourcePath

& $compilerPath @compilerArgs

if ($LASTEXITCODE -ne 0) {
    throw "C# 编译失败，退出码：$LASTEXITCODE"
}

Write-Output "已生成：$targetPath"

if ($OutputPath) {
    Write-Output '已跳过安装版同步（指定了独立输出路径）。'
    return
}

$installDirectory = Join-Path $env:USERPROFILE '.cli-list'
$installedExecutablePath = Join-Path $installDirectory 'CLIList.exe'
$installedSourcePath = Join-Path $installDirectory 'CLIList.cs'

if (-not $SkipInstalledSync -and (Test-Path -LiteralPath $installDirectory)) {
    $runningProcesses = Get-Process -Name 'CLIList' -ErrorAction SilentlyContinue
    $shouldRestartResident = $null -ne $runningProcesses
    if ($runningProcesses) {
        $runningProcesses | Stop-Process -Force
        $runningProcesses | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    }

    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Copy-Item -LiteralPath $targetPath -Destination $installedExecutablePath -Force
            break
        }
        catch {
            if ($attempt -eq 10) {
                throw
            }
            Start-Sleep -Milliseconds 300
        }
    }
    Copy-Item -LiteralPath $sourcePath -Destination $installedSourcePath -Force
    if ($shouldRestartResident) {
        Start-Process -FilePath $installedExecutablePath -ArgumentList '--resident'
    }
    Write-Output "已同步安装版：$installedExecutablePath"
}
elseif ($SkipInstalledSync) {
    Write-Output '已跳过安装版同步。'
}
else {
    Write-Output '未检测到安装目录，跳过安装版同步。首次使用请运行 install.ps1。'
}
