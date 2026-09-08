$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$edgeCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe')
)
$edgePath = $edgeCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
$htmlPath = Join-Path $PSScriptRoot 'render-icon.html'
$svgPath = Join-Path $PSScriptRoot 'cli-list.svg'
$previewPath = Join-Path $PSScriptRoot 'cli-list-icon.png'
$iconPath = Join-Path $PSScriptRoot 'cli-list.ico'
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$edgeProfilePath = Join-Path $temporaryRoot ("cli-list-edge-" + [System.Guid]::NewGuid().ToString('N'))

if (-not $edgePath) {
    throw '未找到 Microsoft Edge，无法从 SVG 生成 Windows 图标。'
}

foreach ($requiredPath in @($htmlPath, $svgPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "缺少生成图标所需文件：$requiredPath"
    }
}

$htmlUri = ([System.Uri]::new($htmlPath)).AbsoluteUri
if (Test-Path -LiteralPath $previewPath) {
    Remove-Item -LiteralPath $previewPath -Force
}
$edgeArguments = @(
    '--headless=new'
    '--disable-gpu'
    '--hide-scrollbars'
    '--allow-file-access-from-files'
    '--run-all-compositor-stages-before-draw'
    '--default-background-color=00000000'
    '--window-size=512,512'
    "--user-data-dir=$edgeProfilePath"
    "--screenshot=$previewPath"
    $htmlUri
)

$edgeProcess = $null
try {
    $edgeProcess = Start-Process -FilePath $edgePath -ArgumentList $edgeArguments -WindowStyle Hidden -Wait -PassThru
    if ($edgeProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $previewPath)) {
        throw "SVG 渲染失败，Edge 退出代码：$($edgeProcess.ExitCode)"
    }
}
finally {
    $resolvedProfilePath = [System.IO.Path]::GetFullPath($edgeProfilePath)
    if ($resolvedProfilePath.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedProfilePath).StartsWith('cli-list-edge-', [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedProfilePath)) {
        Remove-Item -LiteralPath $resolvedProfilePath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$sourceImage = [System.Drawing.Image]::FromFile($previewPath)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = New-Object System.Collections.Generic.List[byte[]]

try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($sourceImage, 0, 0, $size, $size)

            $memoryStream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($memoryStream, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames.Add($memoryStream.ToArray())
            }
            finally {
                $memoryStream.Dispose()
            }
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}
finally {
    $sourceImage.Dispose()
}

$fileStream = [System.IO.File]::Open($iconPath, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($fileStream)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $frame = $frames[$index]
        $dimension = if ($size -eq 256) { [byte]0 } else { [byte]$size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frame.Length)
        $writer.Write([UInt32]$offset)
        $offset += $frame.Length
    }

    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame)
    }
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

Write-Output "已从 SVG 生成图标：$iconPath"
