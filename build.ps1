$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'CLIList.cs'
$outputPath = Join-Path $PSScriptRoot 'CLIList.exe'
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

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}

if (-not $compilerPath) {
    throw '未找到 .NET Framework 4.x C# 编译器。'
}

& $compilerPath `
    /nologo `
    /target:winexe `
    /optimize+ `
    /codepage:65001 `
    "/win32icon:$iconPath" `
    "/reference:System.Windows.Forms.dll" `
    "/reference:System.Drawing.dll" `
    "/reference:System.Web.Extensions.dll" `
    "/out:$outputPath" `
    $sourcePath

if ($LASTEXITCODE -ne 0) {
    throw "C# 编译失败，退出码：$LASTEXITCODE"
}

Write-Output "已生成：$outputPath"

$installDirectory = Join-Path $env:USERPROFILE '.cli-list'
$installedExecutablePath = Join-Path $installDirectory 'CLIList.exe'
$installedSourcePath = Join-Path $installDirectory 'CLIList.cs'

if (Test-Path -LiteralPath $installDirectory) {
    $runningProcesses = Get-Process -Name 'CLIList' -ErrorAction SilentlyContinue
    if ($runningProcesses) {
        $runningProcesses | Stop-Process -Force
        $runningProcesses | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    }

    Copy-Item -LiteralPath $outputPath -Destination $installedExecutablePath -Force
    Copy-Item -LiteralPath $sourcePath -Destination $installedSourcePath -Force
    Write-Output "已同步安装版：$installedExecutablePath"
}
else {
    Write-Output '未检测到安装目录，跳过安装版同步。首次使用请运行 install.ps1。'
}
