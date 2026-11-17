[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$SourceRoot,
    [string]$ArchivePath,
    [switch]$Overwrite
)

$ErrorActionPreference = 'Stop'

function Resolve-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

$project = Resolve-FullPath $ProjectRoot
if (!(Test-Path -LiteralPath $project -PathType Container)) {
    throw "工程目录不存在：$project"
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path $project 'dist/release'
} else {
    $SourceRoot = Resolve-FullPath $SourceRoot
}
$source = Resolve-FullPath $SourceRoot
if (!(Test-Path -LiteralPath $source -PathType Container)) {
    throw "发布目录不存在：$source"
}

if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $ArchivePath = Join-Path $project 'dist/ClaudeZhInput-windows-x64.zip'
} else {
    $ArchivePath = Resolve-FullPath $ArchivePath
}
$archive = Resolve-FullPath $ArchivePath
$archiveParent = Split-Path -Parent $archive
if (!(Test-Path -LiteralPath $archiveParent -PathType Container)) {
    New-Item -ItemType Directory -Path $archiveParent -Force | Out-Null
}

$required = @(
    '使用说明.txt',
    '验证范围.txt',
    '安装输入法.cmd',
    '打开设置.cmd',
    '卸载输入法.cmd',
    'app/ClaudeZhInput.exe',
    'app/ClaudeZhInput.Rime.dll',
    'app/runtime/rime.dll',
    'app/runtime/data/build/claude_pinyin.schema.yaml',
    'app/runtime/data/build/pinyin_simp.table.bin',
    'worker.exe',
    'native/install.ps1',
    'native/uninstall.ps1',
    'licenses/README.md',
    'licenses/librime-BSD-3-Clause.txt',
    'licenses/rime-pinyin-simp-Apache-2.0.txt',
    'licenses/THIRD-PARTY-NOTICES.md',
    'RELEASE-MANIFEST.sha256'
)
foreach ($relative in $required) {
    $file = Join-Path $source ($relative -replace '/', '\')
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "发布目录缺少归档必需文件：$relative"
    }
}

if (Test-Path -LiteralPath $archive -PathType Leaf) {
    if (!$Overwrite) {
        throw "归档已存在；如需替换请显式使用 -Overwrite：$archive"
    }
    if (!$PSCmdlet.ShouldProcess($archive, '删除旧归档')) {
        return
    }
    Remove-Item -LiteralPath $archive -Force
}

if (!$PSCmdlet.ShouldProcess($archive, "从 $source 生成 ZIP")) {
    [pscustomobject]@{
        archive = $archive
        source = $source
        requiredFiles = $required.Count
        created = $false
        whatIf = $true
    } | ConvertTo-Json -Compress
    return
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $source,
    $archive,
    [IO.Compression.CompressionLevel]::Optimal,
    $false
)

$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        [void]$entryNames.Add($entry.FullName.Replace('\', '/'))
    }
    foreach ($relative in $required) {
        if (!$entryNames.Contains($relative)) {
            throw "归档缺少文件：$relative"
        }
    }
    [pscustomobject]@{
        archive = $archive
        source = $source
        bytes = (Get-Item -LiteralPath $archive).Length
        entries = $entryNames.Count
        requiredFiles = $required.Count
        created = $true
    } | ConvertTo-Json -Compress
} finally {
    $zip.Dispose()
}
