param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1')
}

$executablePath = Join-Path $PSScriptRoot 'CLIList.exe'
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "未找到构建产物：$executablePath"
}

$process = Start-Process -FilePath $executablePath -ArgumentList '--validate' -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "CLI List 验证失败，退出码：$($process.ExitCode)"
}

Write-Output 'CLI List 构建与配置验证通过。'
