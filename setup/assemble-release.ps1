#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ProtectedDirectory,
    [string]$OutputDirectory,
    [switch]$Overwrite,
    [switch]$Simulate
)

$ErrorActionPreference = 'Stop'
$setupRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $setupRoot 'setup-common.ps1')

function Copy-ClaudeTree {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

$releaseEntryFiles = @(
    '使用说明.txt',
    '验证范围.txt',
    '安装输入法.cmd',
    '打开设置.cmd',
    '卸载输入法.cmd'
)

$resolvedProjectRoot = if ([String]::IsNullOrWhiteSpace($ProjectRoot)) {
    Split-Path -Parent $setupRoot
} else {
    Get-ClaudeFullPath $ProjectRoot
}
$resolvedProtected = if ([String]::IsNullOrWhiteSpace($ProtectedDirectory)) {
    Join-Path $resolvedProjectRoot 'dist\protected'
} else {
    Get-ClaudeFullPath $ProtectedDirectory
}
$resolvedOutput = if ([String]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $resolvedProjectRoot 'dist\release'
} else {
    Get-ClaudeFullPath $OutputDirectory
}

if ((Test-ClaudePathWithin -Child $resolvedOutput -Parent $resolvedProtected) -or
    (Test-ClaudePathWithin -Child $resolvedProtected -Parent $resolvedOutput)) {
    throw "最终分享包目录不能与保护构建目录重叠：$resolvedOutput / $resolvedProtected"
}
if ((Test-ClaudePathWithin -Child $resolvedOutput -Parent $setupRoot) -or
    (Test-ClaudePathWithin -Child $setupRoot -Parent $resolvedOutput)) {
    throw "最终分享包目录不能与 setup 源目录重叠：$resolvedOutput / $setupRoot"
}

$missingEntryFiles = @()
foreach ($entryName in $releaseEntryFiles) {
    $entryPath = Join-Path $resolvedProjectRoot $entryName
    if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
        $missingEntryFiles += $entryName
    }
}
if ($missingEntryFiles.Count -gt 0) {
    throw ('分享包缺少源根目录入口：' + ($missingEntryFiles -join '、'))
}

Assert-ClaudeProtectedPackage -PackageRoot $resolvedProtected
$selfContained = Test-ClaudeSelfContainedPackage -PackageRoot $resolvedProtected
$runtimeInstaller = $null
if (-not $selfContained) {
    $runtimeManifest = Get-ClaudeRuntimeManifest -PackageRoot $resolvedProjectRoot
    $runtimeInstaller = Assert-ClaudeRuntimeInstaller -PackageRoot $resolvedProjectRoot -Manifest $runtimeManifest
}
$vcManifest = Get-ClaudeVCRuntimeManifest -PackageRoot $resolvedProjectRoot
$vcInstaller = Assert-ClaudeVCRuntimeInstaller -PackageRoot $resolvedProjectRoot -Manifest $vcManifest

if (Test-Path -LiteralPath $resolvedOutput -PathType Container) {
    if (-not $Overwrite) {
        throw "最终分享包目录已存在；为避免覆盖请换目录，或显式使用 -Overwrite：$resolvedOutput"
    }
    if (@(Get-ChildItem -LiteralPath $resolvedOutput -Force -ErrorAction SilentlyContinue).Count -gt 0) {
        Write-Host "将原最终分享包暂存后替换：$resolvedOutput"
    }
}

if ($Simulate) {
    Write-Host "[模拟] 保护包完整：$resolvedProtected"
    if ($selfContained) { Write-Host '[模拟] 使用应用自带 .NET 桌面运行时。' }
    else { Write-Host "[模拟] 运行时安装器已校验：$runtimeInstaller" }
    Write-Host "[模拟] VC14 x64 Runtime 安装器已校验：$vcInstaller"
    Write-Host "[模拟] 将输出完整分享包：$resolvedOutput"
    Write-Host ('[模拟] 将包含源根目录入口：' + ($releaseEntryFiles -join '、'))
    Write-Host '[模拟] 将包含 app、worker.exe、phrases.dat、models、commands、native、licenses、setup 和所需运行库；自包含应用不另附 .NET 安装器。'
    exit 0
}

$stageDirectory = New-ClaudeControlledTempDirectory
$backupDirectory = $null
try {
    Copy-ClaudeTree -Source $resolvedProtected -Destination $stageDirectory
    foreach ($entryName in $releaseEntryFiles) {
        Copy-Item -LiteralPath (Join-Path $resolvedProjectRoot $entryName) -Destination (Join-Path $stageDirectory $entryName) -Force
    }
    $publicSetup = Join-Path $stageDirectory 'setup'
    New-Item -ItemType Directory -Force -Path $publicSetup | Out-Null
    # Ship user maintenance entry points, not build scripts or legacy repairs.
    foreach ($name in @('install.ps1','install.cmd','start.ps1','start.cmd','uninstall.ps1','uninstall.cmd','setup-common.ps1','README.md')) {
        Copy-Item -LiteralPath (Join-Path $setupRoot $name) -Destination (Join-Path $publicSetup $name)
    }
    $publicPrerequisites = Join-Path $publicSetup 'prerequisites'
    if ($selfContained) {
        New-Item -ItemType Directory -Force -Path $publicPrerequisites | Out-Null
        Copy-Item -LiteralPath $vcInstaller -Destination $publicPrerequisites
        Copy-Item -LiteralPath (Join-Path $setupRoot 'prerequisites\vc-redist-x64.json') -Destination $publicPrerequisites
    } else {
        Copy-ClaudeTree -Source (Join-Path $setupRoot 'prerequisites') -Destination $publicPrerequisites
    }

    # Re-check after the copy so an incomplete or partially copied output can
    # never be presented as a shareable package.
    Assert-ClaudeProtectedPackage -PackageRoot $stageDirectory
    if (-not $selfContained) { [void](Assert-ClaudeRuntimeInstaller -PackageRoot $stageDirectory -Manifest (Get-ClaudeRuntimeManifest $stageDirectory)) }
    [void](Assert-ClaudeVCRuntimeInstaller -PackageRoot $stageDirectory -Manifest (Get-ClaudeVCRuntimeManifest $stageDirectory))
    foreach ($entryName in $releaseEntryFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageDirectory $entryName) -PathType Leaf)) {
            throw "组包暂存目录缺少源根目录入口：$entryName"
        }
    }

    # The final manifest must also cover installer scripts and user entry files.
    $stageManifest = Join-Path $stageDirectory 'RELEASE-MANIFEST.sha256'
    $stageFull = [IO.Path]::GetFullPath($stageDirectory).TrimEnd('\','/')
    $manifestLines = Get-ChildItem -LiteralPath $stageFull -File -Recurse |
        Where-Object { $_.FullName -ne $stageManifest } | Sort-Object FullName | ForEach-Object {
            (Get-FileHash -LiteralPath $_.FullName).Hash + '  ' + $_.FullName.Substring($stageFull.Length).TrimStart('\','/').Replace('\','/')
        }
    [IO.File]::WriteAllLines($stageManifest,$manifestLines,[Text.UTF8Encoding]::new($true))

    $parent = Split-Path -Parent $resolvedOutput
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    if (Test-Path -LiteralPath $resolvedOutput -PathType Container) {
        $backupDirectory = Join-Path ([IO.Path]::GetTempPath()) ('.ClaudeZhInput-release-backup-' + [Guid]::NewGuid().ToString('N'))
        Move-Item -LiteralPath $resolvedOutput -Destination $backupDirectory
    }
    Move-Item -LiteralPath $stageDirectory -Destination $resolvedOutput
    $stageDirectory = $null

    if ($backupDirectory -and (Test-Path -LiteralPath $backupDirectory)) {
        Remove-Item -LiteralPath $backupDirectory -Recurse -Force
        $backupDirectory = $null
    }
    Write-Host "已组装完整离线分享包：$resolvedOutput"
}
catch {
    if ($backupDirectory -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
        if (Test-Path -LiteralPath $resolvedOutput -PathType Container) {
            Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
        }
        Move-Item -LiteralPath $backupDirectory -Destination $resolvedOutput
        $backupDirectory = $null
    }
    throw "分享包组装未完成，原目录已恢复：$($_.Exception.Message)"
}
finally {
    if ($stageDirectory -and (Test-Path -LiteralPath $stageDirectory)) {
        Remove-ClaudeControlledTempDirectory -Path $stageDirectory
    }
    if ($backupDirectory -and (Test-Path -LiteralPath $backupDirectory)) {
        Remove-Item -LiteralPath $backupDirectory -Recurse -Force
    }
}

