[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$NativeSourceRoot = (Join-Path $PSScriptRoot 'staging/native-protected'),
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [string[]]$PackageRoot = @('dist/protected', 'dist/release')
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
        $destinationParent = Split-Path -Parent $destination
        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
}

function Write-PackageManifest([string]$Package) {
    $manifest = Join-Path $Package 'RELEASE-MANIFEST.sha256'
    $lines = @(Get-ChildItem -LiteralPath $Package -Recurse -File |
        Where-Object { $_.FullName -ne $manifest } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($Package.Length).TrimStart('\', '/').Replace('\', '/')
            $digest = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            "$digest  $relative"
        })
    [IO.File]::WriteAllLines($manifest, [string[]]$lines, [Text.Encoding]::ASCII)
    return $lines.Count
}

$project = Resolve-FullPath $ProjectRoot
if (!(Test-Path -LiteralPath $project -PathType Container)) {
    throw "工程目录不存在：$project"
}

$nativeSource = Resolve-ProjectPath $NativeSourceRoot $project
Assert-ChildPath $nativeSource $project
$protectionMarker = Join-Path $nativeSource 'native-protection.json'
if (!(Test-Path -LiteralPath $protectionMarker -PathType Leaf)) {
    throw "native 刷新只接受 protect-native.ps1 生成的 protected staging；缺少标记：$protectionMarker"
}
$protectionInfo = Get-Content -LiteralPath $protectionMarker -Raw | ConvertFrom-Json
if ($protectionInfo.kind -ne 'protected-native-candidate' -or
    $protectionInfo.formalPackageApplied -ne $false -or
    [string]::IsNullOrWhiteSpace($protectionInfo.protectedSha256)) {
    throw "native source 不是有效的 protected staging：$protectionMarker"
}

$nativeRoot = Join-Path $nativeSource 'native'
$nativeBuildOutput = Join-Path $nativeRoot "bin\$Configuration\net8.0-windows\win-x64"
$nativeRegistrationOutput = Join-Path $nativeRoot "TsfRegistration.Tool\bin\$Configuration\net8.0-windows\win-x64"
$protectedAssembly = Join-Path $nativeBuildOutput 'ClaudeZhInput.Tsf.dll'
$registrationAssembly = Join-Path $nativeRegistrationOutput 'ClaudeZhInput.Tsf.dll'
$rawAssembly = Join-Path $project "native\bin\$Configuration\net8.0-windows\win-x64\ClaudeZhInput.Tsf.dll"
foreach ($assembly in @($protectedAssembly, $registrationAssembly, $rawAssembly)) {
    if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) {
        throw "native protected/source 缺少核心程序集：$assembly"
    }
}
$protectedHash = (Get-FileHash -LiteralPath $protectedAssembly -Algorithm SHA256).Hash
$registrationHash = (Get-FileHash -LiteralPath $registrationAssembly -Algorithm SHA256).Hash
$rawHash = (Get-FileHash -LiteralPath $rawAssembly -Algorithm SHA256).Hash
if ($protectedHash -ne $protectionInfo.protectedSha256 -or
    $registrationHash -ne $protectionInfo.protectedSha256) {
    throw "protected staging 的主/注册工具核心 DLL 与 marker hash 不一致。"
}
if ($rawHash -ne $protectionInfo.sourceSha256) {
    throw "protected staging 不是当前 native source 生成的版本；请先重新运行 protect-native.ps1。"
}
if ($protectedHash -eq $rawHash) {
    throw 'protected staging 的核心 DLL 与 raw source 相同，拒绝进入发布包。'
}
$mainDestinationRelative = "native\bin\$Configuration\net8.0-windows\win-x64"
$registrationDestinationRelative = "native\TsfRegistration.Tool\bin\$Configuration\net8.0-windows\win-x64"

# These are the runtime files consumed by the installed COM host and by the
# registration tool. Build outputs such as PDBs and test-host artifacts are
# deliberately absent from both lists.
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
    $source = Join-Path $nativeBuildOutput ($relative -replace '/', '\')
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "缺少 native COM host 运行文件：$source"
    }
    if ($relative -match '(?i)(TsfTestHost|Diagnostic|\.pdb$|\.cs$|\.csproj$|\.xaml$)') {
        throw "native 白名单意外包含测试、诊断或源码文件：$relative"
    }
}
foreach ($relative in $registrationFiles) {
    $source = Join-Path $nativeRegistrationOutput ($relative -replace '/', '\')
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "缺少 native 注册工具运行文件：$source"
    }
    if ($relative -match '(?i)(TsfTestHost|Diagnostic|\.pdb$|\.cs$|\.csproj$|\.xaml$)') {
        throw "注册工具白名单意外包含测试、诊断或源码文件：$relative"
    }
}

# A generated COM manifest must expose only the production COM class. This
# catches an accidental diagnostic/test COM type before it reaches either
# share package.
foreach ($manifest in @(
    (Join-Path $nativeBuildOutput 'ClaudeZhInput.Tsf.X.manifest'),
    (Join-Path $nativeRegistrationOutput 'ClaudeZhInput.Tsf.X.manifest')
)) {
    if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) { continue }
    $manifestText = Get-Content -LiteralPath $manifest -Raw
    if ($manifestText -match '(?i)(TsfTestHost|Diagnostic|DiagText)') {
        throw "native COM manifest 仍包含测试或诊断类型：$manifest"
    }
}

foreach ($scriptName in $installScripts) {
    $source = Join-Path $nativeRoot $scriptName
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "缺少正式 native 安装脚本：$source"
    }
    $bytes = [IO.File]::ReadAllBytes((Resolve-FullPath $source))
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "native 安装脚本必须保留 UTF-8 BOM：$source"
    }
}

$packages = @()
foreach ($packageValue in $PackageRoot) {
    $package = Resolve-ProjectPath $packageValue $project
    Assert-ChildPath $package $project
    if (!(Test-Path -LiteralPath $package -PathType Container)) {
        throw "发布包目录不存在：$package"
    }
    $packages += $package
}

$result = @()
foreach ($package in $packages) {
    $packageNative = Join-Path $package 'native'
    if (!(Test-Path -LiteralPath $packageNative -PathType Container)) {
        throw "发布包缺少 native 目录：$packageNative"
    }
    if (!$PSCmdlet.ShouldProcess($package, '刷新 native 运行文件、正式安装脚本并重算 manifest')) {
        continue
    }

    $mainDestination = Join-Path $package $mainDestinationRelative
    $registrationDestination = Join-Path $package $registrationDestinationRelative
    foreach ($destination in @($mainDestination, $registrationDestination)) {
        if (Test-Path -LiteralPath $destination) {
            Remove-Item -LiteralPath $destination -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
    }

    Copy-SelectedFiles $nativeBuildOutput $mainDestination $mainFiles
    Copy-SelectedFiles $nativeRegistrationOutput $registrationDestination $registrationFiles
    foreach ($scriptName in $installScripts) {
        Copy-Item -LiteralPath (Join-Path $nativeRoot $scriptName) `
            -Destination (Join-Path $packageNative $scriptName) -Force
    }

    # End-user packages contain usage instructions and required notices, not the developer IPC contract.
    foreach ($document in @('INTERFACE.md', 'README.md')) {
        $developerDocument = Join-Path $packageNative $document
        if (Test-Path -LiteralPath $developerDocument -PathType Leaf) { Remove-Item -LiteralPath $developerDocument -Force }
    }

    $forbiddenNative = @(Get-ChildItem -LiteralPath $packageNative -Recurse -File |
        Where-Object {
            $_.Extension.ToLowerInvariant() -in @('.pdb', '.cs', '.csproj', '.xaml', '.py', '.pyc', '.pyo') -or
            $_.FullName -match '(?i)(TsfTestHost|Diagnostic|DiagText)'
        })
    if ($forbiddenNative.Count -gt 0) {
        throw "刷新后的 native 目录仍包含测试、诊断、源码或调试文件：$($forbiddenNative.FullName -join ', ')"
    }

    foreach ($scriptName in $installScripts) {
        $destination = Join-Path $packageNative $scriptName
        $bytes = [IO.File]::ReadAllBytes((Resolve-FullPath $destination))
        if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
            throw "复制后的 native 安装脚本丢失 UTF-8 BOM：$destination"
        }
    }

    $manifestLines = Write-PackageManifest $package
    $result += [pscustomobject]@{
        package = $package
        manifest = (Join-Path $package 'RELEASE-MANIFEST.sha256')
        entries = $manifestLines
        nativeSource = $nativeSource
        workerOrWpfRebuilt = $false
    }
}

if ($result.Count -gt 0) {
    $result | ConvertTo-Json -Compress
} else {
    [pscustomobject]@{ changed = $false; whatIf = $true } | ConvertTo-Json -Compress
}
