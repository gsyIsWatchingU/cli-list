param(
    [switch]$SkipBuild,
    [switch]$SkipInstalledSync
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

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipInstalledSync:$SkipInstalledSync
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
}
finally {
    $resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
    $resolvedSystemTemporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporaryDirectory.StartsWith($resolvedSystemTemporaryDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output 'CLI List 构建与配置验证通过。'
