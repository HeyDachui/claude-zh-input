[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$CandidateRoot = (Join-Path $PSScriptRoot 'staging/native-protected'),
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [switch]$Overwrite
)

$ErrorActionPreference = 'Stop'

function Resolve-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Resolve-ProjectPath([string]$Path, [string]$Project) {
    if ([IO.Path]::IsPathRooted($Path)) { return (Resolve-FullPath $Path) }
    return (Resolve-FullPath (Join-Path $Project $Path))
}

function Assert-ChildPath([string]$Child, [string]$Parent) {
    $childFull = (Resolve-FullPath $Child).TrimEnd('\', '/')
    $parentFull = (Resolve-FullPath $Parent).TrimEnd('\', '/')
    $prefix = $parentFull + [IO.Path]::DirectorySeparatorChar
    if ($childFull.Equals($parentFull, [StringComparison]::OrdinalIgnoreCase) -or
        !$childFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "路径必须位于工程目录内：$childFull"
    }
}

function Copy-SelectedFiles([string]$SourceRoot, [string]$DestinationRoot, [string[]]$RelativePaths) {
    foreach ($relative in $RelativePaths) {
        $source = Join-Path $SourceRoot ($relative -replace '/', '\')
        $destination = Join-Path $DestinationRoot ($relative -replace '/', '\')
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
}

function Find-ReferenceFolder([string]$PackName) {
    $pack = Join-Path $env:ProgramFiles "dotnet\packs\$PackName"
    if (!(Test-Path -LiteralPath $pack -PathType Container)) { return $null }
    $version = Get-ChildItem -LiteralPath $pack -Directory |
        Sort-Object { try { [Version]$_.Name } catch { [Version]'0.0' } } -Descending |
        Select-Object -First 1
    if ($null -eq $version) { return $null }
    $candidate = Join-Path $version.FullName 'ref\net8.0'
    if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    return $null
}

function XmlPath([string]$Value) {
    return [Security.SecurityElement]::Escape($Value.Replace('\', '/'))
}

$project = Resolve-FullPath $ProjectRoot
if (!(Test-Path -LiteralPath $project -PathType Container)) {
    throw "工程目录不存在：$project"
}
$candidate = Resolve-ProjectPath $CandidateRoot $project
Assert-ChildPath $candidate $project
if (Test-Path -LiteralPath $candidate) {
    if (!$Overwrite) { throw "候选目录已存在；如需重建请显式传 -Overwrite：$candidate" }
    [IO.Directory]::Delete($candidate, $true)
}

$nativeRoot = Join-Path $project 'native'
$mainSource = Join-Path $nativeRoot "bin\$Configuration\net8.0-windows\win-x64"
$registrationSource = Join-Path $nativeRoot "TsfRegistration.Tool\bin\$Configuration\net8.0-windows\win-x64"
$candidateMain = Join-Path $candidate "native\bin\$Configuration\net8.0-windows\win-x64"
$candidateRegistration = Join-Path $candidate "native\TsfRegistration.Tool\bin\$Configuration\net8.0-windows\win-x64"
$mainFiles = @(
    'ClaudeZhInput.Tsf.comhost.dll',
    'ClaudeZhInput.Tsf.dll',
    'ClaudeZhInput.Tsf.deps.json',
    'ClaudeZhInput.Tsf.runtimeconfig.json',
    'ClaudeZhInput.Tsf.X.manifest',
    'Data/pinyin.txt',
    'Data/phrase-pinyin-data.LICENSE'
)
$registrationFiles = @(
    'ClaudeZhInput.TsfRegistration.exe',
    'ClaudeZhInput.TsfRegistration.dll',
    'ClaudeZhInput.TsfRegistration.deps.json',
    'ClaudeZhInput.TsfRegistration.runtimeconfig.json',
    'ClaudeZhInput.Tsf.dll',
    'Data/pinyin.txt',
    'Data/phrase-pinyin-data.LICENSE'
)
$installScripts = @('Install-Tsf.ps1', 'install.ps1', 'Uninstall-Tsf.ps1', 'uninstall.ps1')

foreach ($relative in $mainFiles) {
    $source = Join-Path $mainSource ($relative -replace '/', '\')
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "缺少 native 输入：$source" }
    if ($relative -match '(?i)(TsfTestHost|Diagnostic|\.pdb$|\.cs$|\.csproj$|\.xaml$)') {
        throw "native 白名单意外包含测试、诊断或源码文件：$relative"
    }
}
foreach ($relative in $registrationFiles) {
    $source = Join-Path $registrationSource ($relative -replace '/', '\')
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "缺少注册工具输入：$source" }
    if ($relative -match '(?i)(TsfTestHost|Diagnostic|\.pdb$|\.cs$|\.csproj$|\.xaml$)') {
        throw "注册工具白名单意外包含测试、诊断或源码文件：$relative"
    }
}
foreach ($manifest in @(
    (Join-Path $mainSource 'ClaudeZhInput.Tsf.X.manifest'),
    (Join-Path $registrationSource 'ClaudeZhInput.Tsf.X.manifest')
)) {
    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        if ((Get-Content -LiteralPath $manifest -Raw) -match '(?i)(TsfTestHost|Diagnostic|DiagText)') {
            throw "native COM manifest 仍包含测试或诊断类型：$manifest"
        }
    }
}
foreach ($scriptName in $installScripts) {
    $source = Join-Path $nativeRoot $scriptName
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "缺少正式 native 安装脚本：$source" }
    $bytes = [IO.File]::ReadAllBytes((Resolve-FullPath $source))
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "native 安装脚本必须保留 UTF-8 BOM：$source"
    }
}

New-Item -ItemType Directory -Force -Path $candidateMain,$candidateRegistration | Out-Null
Copy-SelectedFiles $mainSource $candidateMain $mainFiles
Copy-SelectedFiles $registrationSource $candidateRegistration $registrationFiles
foreach ($scriptName in $installScripts) {
    Copy-Item -LiteralPath (Join-Path $nativeRoot $scriptName) -Destination (Join-Path $candidate "native\$scriptName") -Force
}

$windowsRef = Find-ReferenceFolder 'Microsoft.WindowsDesktop.App.Ref'
$coreRef = Find-ReferenceFolder 'Microsoft.NETCore.App.Ref'
if (!$windowsRef -or !$coreRef) { throw '缺少 .NET 8 WPF/核心引用程序集，不能执行 native 混淆。' }
$obfuscar = Join-Path $PSScriptRoot '.dotnet-tools\obfuscar.console.exe'
if (!(Test-Path -LiteralPath $obfuscar -PathType Leaf)) { throw "找不到 Obfuscar：$obfuscar" }
$template = Join-Path $PSScriptRoot 'native-obfuscar.xml'
if (!(Test-Path -LiteralPath $template -PathType Leaf)) { throw "找不到 native Obfuscar 配置：$template" }
$config = Join-Path $candidate 'obfuscar.native.xml'
$configText = [IO.File]::ReadAllText($template)
$replacements = @{
    '__MAIN_SOURCE_DIR__' = XmlPath (Resolve-FullPath $mainSource)
    '__CANDIDATE_MAIN_DIR__' = XmlPath (Resolve-FullPath $candidateMain)
    '__WINDOWS_REF__' = XmlPath $windowsRef
    '__CORE_REF__' = XmlPath $coreRef
    '__MODULE_FILE__' = XmlPath (Resolve-FullPath (Join-Path $mainSource 'ClaudeZhInput.Tsf.dll'))
}
foreach ($key in $replacements.Keys) { $configText = $configText.Replace($key, $replacements[$key]) }
[IO.File]::WriteAllText($config, $configText, [Text.UTF8Encoding]::new($false))

& $obfuscar $config '-v:m'
if ($LASTEXITCODE -ne 0) { throw "Obfuscar 退出码为 $LASTEXITCODE" }
$protectedAssembly = Join-Path $candidateMain 'ClaudeZhInput.Tsf.dll'
if (!(Test-Path -LiteralPath $protectedAssembly -PathType Leaf)) { throw 'Obfuscar 未生成 ClaudeZhInput.Tsf.dll' }
Copy-Item -LiteralPath $protectedAssembly -Destination (Join-Path $candidateRegistration 'ClaudeZhInput.Tsf.dll') -Force
# Obfuscar writes a reverse-name map beside the protected assembly by default.
# It is useful during development but would materially reduce the protection
# value of a shareable candidate, so never retain it in this staging root.
$mappingFile = Join-Path $candidateMain 'Mapping.txt'
if (Test-Path -LiteralPath $mappingFile -PathType Leaf) {
    Remove-Item -LiteralPath $mappingFile -Force
}

$sourceHash = (Get-FileHash -LiteralPath (Join-Path $mainSource 'ClaudeZhInput.Tsf.dll') -Algorithm SHA256).Hash
$protectedHash = (Get-FileHash -LiteralPath $protectedAssembly -Algorithm SHA256).Hash
if ($sourceHash -eq $protectedHash) { throw 'native 混淆未改变核心程序集，拒绝生成 protected 候选。' }
if ((Get-FileHash -LiteralPath (Join-Path $candidateRegistration 'ClaudeZhInput.Tsf.dll') -Algorithm SHA256).Hash -ne $protectedHash) {
    throw '注册工具目录中的 protected ClaudeZhInput.Tsf.dll 与主目录不一致。'
}

$forbidden = @(Get-ChildItem -LiteralPath (Join-Path $candidate 'native') -Recurse -File |
    Where-Object {
        $_.Extension.ToLowerInvariant() -in @('.pdb', '.cs', '.csproj', '.xaml', '.py', '.pyc', '.pyo') -or
        $_.Name -match '(?i)^(Mapping|Obfuscar)\.(txt|xml)$' -or
        $_.FullName -match '(?i)(TsfTestHost|Diagnostic|DiagText)'
    })
if ($forbidden.Count -gt 0) { throw "protected native 候选包含禁止文件：$($forbidden.FullName -join ', ')" }

$marker = Join-Path $candidate 'native-protection.json'
@{
    kind = 'protected-native-candidate'
    obfuscator = 'Obfuscar 2.2.50'
    ruleTemplate = 'release-tools/native-obfuscar.xml'
    sourceAssembly = 'native/bin/Release/net8.0-windows/win-x64/ClaudeZhInput.Tsf.dll'
    sourceSha256 = $sourceHash
    protectedSha256 = $protectedHash
    comAbiPreserved = $true
    jsonContractsPreserved = $true
    formalPackageApplied = $false
    sourceFilesIncluded = $false
} | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8

[pscustomobject]@{
    candidate = $candidate
    protectedAssembly = $protectedAssembly
    protectedSha256 = $protectedHash
    sourceSha256 = $sourceHash
    marker = $marker
    formalPackageApplied = $false
    workerOrWpfRebuilt = $false
} | ConvertTo-Json -Compress
