$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'CLIList.cs'
$outputPath = Join-Path $PSScriptRoot 'CLIList.exe'
$iconPath = Join-Path $PSScriptRoot 'cli-list.ico'
$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}

& (Join-Path $PSScriptRoot 'generate-icon.ps1')

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
