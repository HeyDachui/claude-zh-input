#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [string]$OutputRoot,
    [string]$ModelCacheDirectory,
    [switch]$Overwrite,
    [switch]$CreateProgramZip,
    [switch]$CreateModelZip
)
$ErrorActionPreference='Stop'
if (!$ProjectRoot) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }
. (Join-Path $PSScriptRoot '../setup/combine-model-pack.ps1') -LibraryOnly

function Assert-SplitBuildTarget([string]$Path) {
    $full=Get-SplitFullPath $Path
    if (!(Test-SplitWithin $full $project) -or $full -eq $project -or
        (Test-SplitWithin $full $source) -or (Test-SplitWithin $source $full)) { throw "分包输出必须位于工程内且不得覆盖基线：$full" }
    Assert-SplitAncestors $full
}
function Remove-SplitBuildTree([string]$Path) {
    Assert-SplitBuildTarget $Path
    [void](Get-SplitFiles $Path)
    Remove-Item -LiteralPath $Path -Recurse -Force
}
function Copy-SplitFile([string]$From,[string]$To) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $To) -Force | Out-Null
    Copy-Item -LiteralPath $From -Destination $To
}
function Assert-SplitArchive([string]$Archive,[string]$Root) {
    $expected=Get-SplitFiles $Root
    try { $zip=[IO.Compression.ZipFile]::OpenRead($Archive) }
    catch { throw "ZIP归档无法读取：$Archive；$($_.Exception.Message)" }
    try {
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            Assert-SplitPath $entry.FullName
            if (!$seen.Add($entry.FullName) -or !$expected.ContainsKey($entry.FullName) -or $entry.Length -ne (Get-Item -LiteralPath $expected[$entry.FullName]).Length) { throw 'ZIP 包内路径、文件集或大小不匹配。' }
            $stream=$entry.Open(); $sha=[Security.Cryptography.SHA256]::Create()
            try { $actual=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') }
            finally { $stream.Dispose(); $sha.Dispose() }
            if ($actual -cne (Get-SplitHash $expected[$entry.FullName])) { throw "ZIP 包内 SHA256 不匹配：$($entry.FullName)" }
        }
        if ($seen.Count -ne $expected.Count) { throw 'ZIP 缺少文件。' }
    } finally { $zip.Dispose() }
}
function Publish-SplitArchive([string]$Root,[string]$Archive,[switch]$Stable) {
    Assert-SplitBuildTarget $Archive
    if (Test-Path -LiteralPath $Archive) {
        if (!(Test-Path -LiteralPath $Archive -PathType Leaf)) { throw "归档目标已被目录占用：$Archive" }
        if ($Stable) { Assert-SplitArchive $Archive $Root; return 'reused' }
        if (!$Overwrite) { throw "程序 ZIP 已存在；请换目录或使用 -Overwrite：$Archive" }
    }
    $temporary=$Archive + '.tmp-' + [guid]::NewGuid().ToString('N')
    try {
        $zipStream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $zipWriter=[IO.Compression.ZipArchive]::new($zipStream,[IO.Compression.ZipArchiveMode]::Create,$false)
        try {
            $level=if ($Stable) { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal }
            foreach ($file in (Get-SplitFiles $Root).GetEnumerator()) {
                $entry=$zipWriter.CreateEntry($file.Key,$level)
                $destination=$entry.Open(); $input=[IO.File]::OpenRead($file.Value)
                try { $input.CopyTo($destination) } finally { $input.Dispose(); $destination.Dispose() }
            }
        } finally { $zipWriter.Dispose(); $zipStream.Dispose() }
        Assert-SplitArchive $temporary $Root
        $archiveBackup=$null
        if (Test-Path -LiteralPath $Archive) {
            $archiveBackup=$Archive + '.backup-' + [guid]::NewGuid().ToString('N')
            Assert-SplitBuildTarget $archiveBackup
            [IO.File]::Replace($temporary,$Archive,$archiveBackup)
        } else { [IO.File]::Move($temporary,$Archive) }
        $archiveChanges.Add([pscustomobject]@{path=$Archive;backup=$archiveBackup})
        return 'created'
    } finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force } }
}

$project=Get-SplitFullPath $ProjectRoot; $source=Get-SplitFullPath $SourceRoot
if (!(Test-Path -LiteralPath $project -PathType Container)) { throw '工程目录不存在。' }
if (!$OutputRoot) { $OutputRoot=Join-Path $project 'dist/split' }
$output=Get-SplitFullPath $OutputRoot; Assert-SplitBuildTarget $output
if (!$ModelCacheDirectory) { $ModelCacheDirectory=$output }
$cache=Get-SplitFullPath $ModelCacheDirectory; Assert-SplitBuildTarget $cache
# Only an already complete, audited public package may become a split release.
$verifiedSourceHashes=@{}
$sourceFiles=Assert-SplitManifest $source $verifiedSourceHashes
foreach ($required in @('BUILD-INFO.json','models/translation-llm/runtime.json','app/ClaudeZhInput.exe','worker.exe','phrases.dat','安装输入法.cmd','setup/install.ps1','licenses/README.md','licenses/THIRD-PARTY-NOTICES.md')) {
    if (!$sourceFiles.ContainsKey($required)) { throw "完整分享基线缺少：$required" }
}
$build=Get-Content -LiteralPath $sourceFiles['BUILD-INFO.json'] -Raw -Encoding UTF8 | ConvertFrom-Json
if ($build.version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or $build.userDataIncluded -ne $false -or $build.businessSourceFilesIncluded -ne $false) { throw '基线版本、私人数据或源码声明不合格。' }
$identity=Get-SplitIdentity $build
$runtime=Get-Content -LiteralPath $sourceFiles['models/translation-llm/runtime.json'] -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-SplitIdentityEqual (Get-SplitIdentity @{primaryTranslation=$runtime}) $identity
$noticePath='models/translation-llm/MODEL-NOTICE.txt'
$noticeText=@"
Offline translation model: $($identity['modelId']) $($identity['quantization'])
Original: https://huggingface.co/$($identity['modelId'])
Original revision: $($identity['sourceRevision'])
Quantized GGUF: https://huggingface.co/$($identity['quantizationSource'])
Quantization revision: $($identity['quantizationRevision'])
SHA-256: $($identity['modelSha256'])
Bytes: $($identity['modelBytes'])
The model weights are redistributed unchanged under Apache-2.0.
See licenses/redistribution/Qwen3.5-4B-Apache-2.0.txt for the license.
The input method author does not claim private ownership of the model weights.
This reusable model pack contains no inference runtime.
"@
$noticeBytes=[Text.UTF8Encoding]::new($true).GetBytes($noticeText.Replace("`r`n","`n").Replace("`n","`r`n") + "`r`n")
$noticeHasher=[Security.Cryptography.SHA256]::Create()
try { $noticeHash=([BitConverter]::ToString($noticeHasher.ComputeHash($noticeBytes))).Replace('-','') }
finally { $noticeHasher.Dispose() }
$rows=[ordered]@{}
foreach ($relative in $script:SplitModelFiles) {
    if ($relative -eq $noticePath) { $rows[$relative]=[pscustomobject]@{bytes=$noticeBytes.Length;sha256=$noticeHash}; continue }
    if (!$sourceFiles.ContainsKey($relative)) { throw "基线缺少模型或许可：$relative" }
    $rows[$relative]=[pscustomobject]@{bytes=(Get-Item -LiteralPath $sourceFiles[$relative]).Length;sha256=$verifiedSourceHashes[$relative]}
}
if ($rows[$script:SplitModelPath].bytes -ne [long]$identity['modelBytes'] -or $rows[$script:SplitModelPath].sha256 -cne $identity['modelSha256']) { throw '基线模型大小或SHA256与固定身份不符。' }
$expected=[pscustomobject]@{data=$identity;files=$rows}
$program=Join-Path $output ('ClaudeZhInput-program-' + $build.version)
$modelPack=Join-Path $cache ('ClaudeZhInput-model-' + $identity['modelSha256'].ToLowerInvariant())
foreach ($path in @($program,$modelPack)) { Assert-SplitBuildTarget $path }
if ((Test-SplitWithin $program $modelPack) -or (Test-SplitWithin $modelPack $program)) { throw '程序包与模型包输出不能重叠。' }
if ((Test-Path -LiteralPath $program) -and !$Overwrite) { throw "程序包已存在；请换目录或使用 -Overwrite：$program" }
New-Item -ItemType Directory -Path $output,$cache -Force | Out-Null
$stage=$null; $modelStage=$null; $backup=$null; $modelReused=$false
$programPublished=$false; $committed=$false; $discard=$null
$archiveChanges=[Collections.Generic.List[object]]::new()
$cleanupWarnings=[Collections.Generic.List[string]]::new()
try {
    if (Test-Path -LiteralPath $modelPack) {
        [void](Assert-SplitModelPack $modelPack $expected); $modelReused=$true
    } else {
        $modelStage=Join-Path $cache ('.split-model-stage-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $modelStage | Out-Null
        foreach ($relative in $script:SplitModelFiles) {
            if ($relative -eq $noticePath) { [IO.File]::WriteAllBytes((Join-Path $modelStage $relative),$noticeBytes) }
            else { Copy-SplitFile $sourceFiles[$relative] (Join-Path $modelStage $relative) }
        }
        Write-SplitMetadata (Join-Path $modelStage 'MODEL-PACK.txt') model $identity $rows
        [void](Assert-SplitModelPack $modelStage $expected)
        [IO.Directory]::Move($modelStage,$modelPack); $modelStage=$null
    }
    $stage=Join-Path $output ('.split-program-stage-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($relative in $sourceFiles.Keys) {
        if ($relative -in @($script:SplitModelPath,'RELEASE-MANIFEST.sha256','PROGRAM-PACK.txt','组合模型包说明.txt','组合模型包.cmd','setup/combine-model-pack.ps1','setup/组合模型包.cmd')) { continue }
        Copy-SplitFile $sourceFiles[$relative] (Join-Path $stage $relative)
    }
    [IO.File]::WriteAllBytes((Join-Path $stage $noticePath),$noticeBytes)
    Copy-SplitFile (Join-Path $PSScriptRoot '../setup/combine-model-pack.ps1') (Join-Path $stage 'setup/combine-model-pack.ps1')
    Copy-SplitFile (Join-Path $PSScriptRoot '../setup/组合模型包.cmd') (Join-Path $stage 'setup/组合模型包.cmd')
    Copy-SplitFile (Join-Path $PSScriptRoot '../setup/组合模型包.cmd') (Join-Path $stage '组合模型包.cmd')
    Write-SplitMetadata (Join-Path $stage 'PROGRAM-PACK.txt') program $identity $rows $build.version (Get-SplitHash $sourceFiles['BUILD-INFO.json'])
    $guide=@"
这是程序分包，尚未包含整句离线翻译模型。缺模型时安装器会拒绝继续。
完整离线能力需要程序包和匹配的模型包。模型只需下载一次，可供采用同一模型的后续程序版本复用。

1. 分别解压程序 ZIP 与模型 ZIP，保留各自文件夹。不要将任意 ZIP 内容直接覆盖进程序目录。
2. 将模型文件夹放在程序文件夹旁，名称保持为：
   $(Split-Path -Leaf $modelPack)
3. 在程序文件夹中双击“组合模型包.cmd”。校验需要读取整个模型，可能等待片刻。
4. 看到组合成功后，再运行“安装输入法.cmd”。组合操作本身不安装、不启动服务。

模型包必须保留 MODEL-PACK.txt、models 和 licenses，不能只拷贝一个 GGUF 文件。
也可在 PowerShell 明确指定已完整解压的模型包：
  .\setup\combine-model-pack.ps1 -ModelPackRoot '模型包完整路径'
复用旧的完整离线包时，请明确指定该包；组合程序会先验证其完整清单和同一模型身份：
  .\setup\combine-model-pack.ps1 -ExistingOfflinePackage '旧完整离线包路径'
只验证而不写入时，加 -ValidateOnly。组合失败时不执行安装，并恢复原程序清单。
"@
    [IO.File]::WriteAllText((Join-Path $stage '组合模型包说明.txt'),$guide,[Text.UTF8Encoding]::new($true))
    Write-SplitManifest $stage
    [void](Assert-SplitProgram $stage)
    if (Test-Path -LiteralPath $program) {
        [void](Get-SplitFiles $program)
        $backup=Join-Path $output ('.split-program-backup-' + [guid]::NewGuid().ToString('N'))
        Assert-SplitBuildTarget $backup
        [IO.Directory]::Move($program,$backup)
    }
    [IO.Directory]::Move($stage,$program); $stage=$null; $programPublished=$true
    $programZip=$null; $modelZip=$null; $modelZipStatus='not requested'
    if ($CreateProgramZip -or $CreateModelZip) { Add-Type -AssemblyName System.IO.Compression.FileSystem; Add-Type -AssemblyName System.IO.Compression }
    if ($CreateProgramZip) { $programZip=$program + '.zip'; [void](Publish-SplitArchive $program $programZip) }
    if ($CreateModelZip) { $modelZip=$modelPack + '.zip'; $modelZipStatus=Publish-SplitArchive $modelPack $modelZip -Stable }
    # Commit only after every requested archive succeeds. Cleanup errors must
    # never trigger restoration from a backup that has already been partly removed.
    $committed=$true
    if ($backup) {
        try { Remove-SplitBuildTree $backup; $backup=$null }
        catch { $cleanupWarnings.Add("发布已成功，旧程序备份清理未完成：$backup；$($_.Exception.Message)") }
    }
    foreach ($change in $archiveChanges) {
        if ($change.backup) {
            try { Assert-SplitBuildTarget $change.backup; Remove-Item -LiteralPath $change.backup -Force }
            catch { $cleanupWarnings.Add("发布已成功，旧归档备份清理未完成：$($change.backup)；$($_.Exception.Message)") }
        }
    }
    [pscustomobject]@{program=$program;modelPack=$modelPack;modelPackReused=$modelReused;programZip=$programZip;modelZip=$modelZip;modelZipStatus=$modelZipStatus;modelSha256=$identity['modelSha256'];modelBytes=[long]$identity['modelBytes'];completeOffline=$false;cleanupWarnings=$cleanupWarnings.ToArray();next='组合模型包.cmd，成功后再运行安装输入法.cmd'} | ConvertTo-Json -Compress
} catch {
    $failure=$_
    $recoveryErrors=[Collections.Generic.List[string]]::new()
    if (!$committed) {
        for ($index=$archiveChanges.Count-1; $index -ge 0; $index--) {
            $change=$archiveChanges[$index]
            try {
                Assert-SplitBuildTarget $change.path
                if ($change.backup) {
                    Assert-SplitBuildTarget $change.backup
                    if (Test-Path -LiteralPath $change.path -PathType Leaf) { [IO.File]::Replace($change.backup,$change.path,[System.Management.Automation.Language.NullString]::Value) }
                    else { [IO.File]::Move($change.backup,$change.path) }
                } elseif (Test-Path -LiteralPath $change.path -PathType Leaf) { Remove-Item -LiteralPath $change.path -Force }
            } catch { $recoveryErrors.Add("归档恢复失败，原件保留在 $($change.backup)：$($_.Exception.Message)") }
        }
        try {
            if ($programPublished -and (Test-Path -LiteralPath $program)) {
                $discard=Join-Path $output ('.split-program-discard-' + [guid]::NewGuid().ToString('N'))
                Assert-SplitBuildTarget $discard
                [IO.Directory]::Move($program,$discard)
            }
            if ($backup -and (Test-Path -LiteralPath $backup)) {
                Assert-SplitBuildTarget $backup
                [IO.Directory]::Move($backup,$program); $backup=$null
            }
            if ($discard) { Remove-SplitBuildTree $discard; $discard=$null }
        } catch { $recoveryErrors.Add("程序恢复未完成；完整备份保留在 $backup，新程序保留在 $discard：$($_.Exception.Message)") }
    }
    if ($recoveryErrors.Count) {
        throw ($failure.Exception.Message + "`n" + ($recoveryErrors -join "`n"))
    }
    throw $failure
} finally {
    foreach ($temporaryTree in @($stage,$modelStage)) { if ($temporaryTree -and (Test-Path -LiteralPath $temporaryTree)) { Remove-SplitBuildTree $temporaryTree } }
}
